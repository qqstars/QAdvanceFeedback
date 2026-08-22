# Pipeline exception safety and "is accumulation stuck?" report

Investigation triggered by the owner's report: low sample counts in both ShakeIt's own
calibration and this plugin's Normalized-layer learner, never seen before installing this
plugin, plus AZOM's "mbooster detecting" intermittently stopping - recovered only by a SimHub
restart - most often when switching game. Hypothesis under test: this plugin throws inside a
SimHub-invoked callback and that exception blocks other plugins' load/update behaviour.

## Part A - is SimHub's dispatch exception-safe, and could we break it?

### SimHub's own dispatch (decompiled via ilspycmd against `SimHub.Plugins.dll`)

| Call site | Exception-safe? | Evidence |
|---|---|---|
| `DataUpdate` (per-frame) | **Yes.** | Every plugin call is routed through `private void ExecUpdate(PluginInstance plugin, Action a)`, which wraps `a()` in try/catch, logs via `Logging.Current.Error`, and tracks a per-plugin `ChainedErrors` counter that throttles (not disables) a persistently-throwing plugin after 5 consecutive errors. A throw here cannot abort iteration over the other plugins. |
| `Init` (startup load loop) | **Yes, for the startup path** - but **no** for late/manual enable. | The startup `foreach` wraps `plugin4.Init(this);` in try/catch (`OutOfDatePluginException` and generic `Exception`), logs, shows a MessageBox, and continues. However `internal bool EnablePlugin(Type plugin)` - the path used when a plugin is enabled after SimHub is already running, without a restart - calls `plugin2.Init(this);` **directly, with no try/catch**. A throw there is **not** contained and would abort that enable operation. (QAdvanceFeedback does not implement `IPluginV2`, so the separate unguarded `PluginManagerLoaded` foreach right after the protected `Init` loop does not apply to it.) |
| `End` (unload) | **Yes.** | Per-plugin work runs through `private void EndPlugin(bool finalize, IPlugin plugin)`, which wraps `plugin.End(this)`/`reusable.FinalizePlugin()` in try/catch, logs via `Logging.Current.Warn`, and never rethrows. Both sync and `IAsyncEnd` plugins go through this guarded method. |
| `AttachDelegate` value-provider invocation (property reads) | **Mixed.** | The raw primitive, `PropertyEntry.Evaluate()`, has **no try/catch**: `return (Value as Func<object>)();`. `PluginManager.GetPropertyValue(string name)` (used by the property picker/dashboards-by-name) DOES wrap `value.Evaluate()` in try/catch and returns `null` on exception - safe. `PropertyEntryWrapper.GetValue()` (used by SimHub's own NCalc/formula engine, e.g. ShakeIt-style/dashboard-formula property access) also has **no try/catch**, and its only decompiled caller chain (`NCalcEngineBase.EvaluateWrapper` -> `ParseValueNcalc`) happens to be covered by an *outer* try/catch several frames up - so it is only as safe as that specific caller, not a hardened primitive. Any other caller that reaches `PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()` directly would not be protected. |

**Verdict:** SimHub's per-frame (`DataUpdate`) and shutdown (`End`) dispatch are genuinely
exception-safe by design. `Init` is guarded at normal startup but **not** when a plugin is
enabled after the fact. The property-value-read path is inconsistently guarded at the
primitive level - safe through the two call chains decompiled here, but not hardened as a
primitive. **Not determined:** ShakeIt's own plugin assembly was not present in the reference
`lib`/extracted-install folders available for this investigation and so was not decompiled;
whether ShakeIt's own calibration/effect code reads properties through a guarded or unguarded
path could not be confirmed either way. Given `Evaluate()`/`GetValue()` are themselves
unguarded primitives, it is plausible but unproven that some caller (ShakeIt, AZOM, or
another plugin) hits an unguarded path.

### Our own code - what could throw, and what was fixed

Audited: `QAdvanceFeedback.Init/DataUpdate/End`, `GetWPFSettingsControl`, every `AttachDelegate`
lambda in `PropertyPublisher`, `SettingsControl`'s constructor, `ConfigStore`/`RuntimeStore`
load/flush, the background flush timer, and the reflection wrappers
(`SimHubScriptEditor`, `PropertyPickerLauncher`, `SimHubExpressionEvaluator`,
`MotorsExportAvailabilityProvider`).

Findings and fixes:

1. **`RuntimeStore`'s background flush timer (`FlushTick`) - real gap, fixed.**
   `FlushTick` ran directly on the raw ThreadPool thread the `System.Threading.Timer` uses,
   calling `_cache.SnapshotIfDirty()` with **no try/catch** before handing off to
   `Task.Run(() => WriteAtomic(snapshot))`. An unhandled exception on a raw ThreadPool thread
   crashes the whole host process (SimHub itself) in .NET - exactly the mechanism the brief
   flagged as worth checking, and a plausible shape for "only a SimHub restart recovers it".
   Fixed: `FlushTick`'s entire body is now wrapped in try/catch, logging once per distinct
   fault via the same dedup pattern `DataUpdate` already uses. `WriteAtomic`'s own catch
   (previously `IOException`/`UnauthorizedAccessException` only) was also broadened to a
   trailing `catch (Exception)`, since it now also runs detached inside `Task.Run` and types
   like `ArgumentException`/`PathTooLongException`/`NotSupportedException` (from
   `Directory.CreateDirectory`/`File.WriteAllText`/`File.Move`) do not derive from either
   caught type. Covered by two new tests in `RuntimeStoreTests.cs` using an intentionally
   invalid path segment (`sub|dir`) that raises exactly the previously-uncaught exception
   type, confirming the timer and the process both survive and the store stays usable.

2. **`QAdvanceFeedback.Init` - unguarded, fixed.** The body ran with no try/catch. Per the
   decompiled evidence above, a plugin `Init` thrown from `EnablePlugin` (a driver
   disabling/re-enabling this plugin without restarting SimHub) is **not** caught by SimHub -
   it would abort that operation. `ConfigStore.Load`/`RuntimeStore`'s own constructor were
   already internally safe (they degrade to defaults rather than throwing), but the
   remaining Init work (learner import/migration, `RebuildProjectedEngine`,
   `_publisher.Register`'s ~90 `AttachDelegate` calls) was not defended as a whole. Fixed by
   splitting `Init` into a thin, try/catch-wrapped entry point (`Init`) and the original body
   (`InitCore`); any failure is logged once (unconditionally, like `End`'s own fault logging)
   and swallowed rather than propagated. `DataUpdate`/`End` already null-guard/short-circuit
   whatever a partial `InitCore` failure would leave unset, so the worst case is "this plugin
   does nothing useful this session", never "SimHub cannot finish (re-)loading."

3. **`GetWPFSettingsControl` - unguarded, fixed.** `SettingsControl`'s constructor does real
   work (reads live settings, probes ShakeIt Motors export availability via reflection, wires
   ~40 WPF controls) and was called with no try/catch. If SimHub's settings host iterates
   every enabled plugin's own `GetWPFSettingsControl` while building its tab list, one
   throwing plugin here could plausibly degrade that shared iteration the same way `Init`
   could. Fixed: wrapped in try/catch; on failure, logs once and returns a minimal `Label`
   control ("settings failed to load - see SimHub log for details") instead of letting the
   exception escape.

4. **`DataUpdate`/`End` - already exception-safe**, unchanged. Both already wrap their entire
   body in try/catch with once-per-distinct-fault logging (not gated behind
   `EnableDiagnostics`, correctly, since these are operational errors not tracing).

5. **`AttachDelegate` lambdas in `PropertyPublisher`** - audited, no changes needed. Every
   lambda is a direct read of a fixed-size backing array/field at a compile-time-constant
   index, all populated unconditionally every frame inside `DataUpdate`'s own try/catch -
   there is no code path by which one of these lambdas itself throws.

6. **Reflection wrappers (`SimHubScriptEditor`, `PropertyPickerLauncher`,
   `SimHubExpressionEvaluator`, `MotorsExportAvailabilityProvider`)** - audited, already
   solid: every one of these already catches broadly (`catch (Exception)` around any
   reflection/invocation call, plus a `catch { }` around their own logging so "logging itself
   throwing" cannot escape either), degrades to "unavailable" silently, and logs at most once
   per session/channel. No changes needed here.

7. **`WheelSourceResolver.Resolve`/`Core` engines (`TelemetryLearningGate`,
   `OnlineDistributionLearner`, `GripLearner`, etc.)** - audited. These already finite-check
   every input before using it and are, in any case, called entirely from within
   `DataUpdate`'s own top-level try/catch, so even an unanticipated throw here is contained to
   "this plugin logs one message and skips one frame's output", never propagates to SimHub.

### Files changed for Part A

- `QAdvanceFeedback/QAdvanceFeedback.cs` - `Init` split into a guarded entry point +
  `InitCore`; `GetWPFSettingsControl` now guarded with a fallback control.
- `QAdvanceFeedback/RuntimeStore.cs` - `FlushTick` now fully guarded (dedup-logged, matching
  `DataUpdate`'s own pattern); `WriteAtomic`'s catch broadened to also catch `Exception`
  generically.
- `QAdvanceFeedback.Tests/RuntimeStoreTests.cs` - two new tests proving the timer and a
  synchronous `Flush()` both survive a previously-uncaught exception type without crashing or
  leaving the store unusable.

## Part B - is sample accumulation actually stuck?

### The funnel (replicated gate: `TelemetryLearningGate.IsValid` -> pedal-commitment/trigger
threshold -> `LongitudinalDirectionResolver` direction match -> lateral-isolation check ->
surface-bucket resolution -> `GripLearner.Observe`), replayed against both log sets:

| File / stint | Total frames | Pass validity | Pass pedal/trigger | Pass direction | Observed (replica) | Persisted `Samples` |
|---|---|---|---|---|---|---|
| `211733_ShakeIt` Sauber | 9,706 | 9,694 (99.9%) | 775 (8.0%) | 773 (8.0%) | 179 (1.84%) | 180 (ShakeIt source) |
| `211733_ShakeIt` F1 Generic | 6,703 | 6,303 (94.0%) | 609 (9.1%) | 592 (8.8%) | 269 (4.01%) | 260 (ShakeIt source) |
| `212439_Raw` Sauber | 11,237 | 11,165 (99.4%) | 946 (8.4%) | 944 (8.4%) | 199 (1.77%) | 175 (Raw source) |
| `212439_Raw` F1 Generic | 9,116 | 8,641 (94.8%) | 926 (10.2%) | 926 (10.2%) | 294 (3.23%) | 294 (Raw source) - exact match |
| `122552-withRawWet` | 3,522 | 3,489 | 450 | 449 | 158 (4.5%) | n/a (pre-source-keying era) |
| `122825-WithShakeItWet` | 3,722 | 3,691 | 414 | 412 | 111 (3.0%) | n/a |
| `123120-WithRawDry` | 5,897 | 5,538 | 727 | 726 | 132 (2.2%) | n/a |
| `123345-WithShakeItDry` | 6,952 | 6,343 | 744 | 744 | 181 (2.6%) | n/a |

The replicated "observed" figures land within roughly 5-10% of the actual persisted `Samples`
for every (car, source) key that could be cross-checked (179 vs 180, 269 vs 260, 199 vs 175,
294 vs 294 exact) - the small residual gap is attributable to the replica approximating dt from
wall-clock timestamps rather than SimHub's own frame `Dt`, plus an approximation of the
lateral-isolation/surface-purity checks.

**Note on the brief's own reference figure:** the "~253 samples out of 9,706 frames (~2.6%)"
data point cited in the brief does not match this file's actual persisted count (180, not
253) - "253" traces back to an old doc-comment example elsewhere in
`NormalizedWheelLockSlipEngine.cs` describing a different, earlier session, not this log. The
real, persisted number for the `211733` Sauber/ShakeIt stint is 180/9,706 (~1.85%), which the
replicated funnel matches closely.

**Verdict: no stage rejects more than its own documented threshold would predict.** Validity
passes ~94-100% of frames (menu/pit/replay/teleport frames are rare in these logs); the
pedal-commitment/trigger-threshold stage passes only ~8-12% (braking hard enough to trigger is
genuinely a small fraction of a lap); nearly all pedal-committed frames are already correctly
resolved as "Slowing" (the direction stage costs almost nothing extra); the largest remaining
cut is lateral isolation, which is expected for F1 corners that combine trail-braking with real
lateral G. **Sample accumulation is working as designed** - the low counts are inherent to how
little of a lap is spent in a qualifying braking zone, not a bug in the gate.

### The real bug found: the live diagnostic readout was frozen, independent of real accumulation

Every CSV checked (all eight files, both log formats) showed `Diag.Lock.LearnedPeakG` pinned at
exactly `1` (`GripLearner.SeedPeakG`) and `Diag.Lock.LearnerConfidence` pinned at `0` on **every
single row** - even in the same session where `QAdvanceFeedback.Parameters.json` demonstrably
persisted a real, mature learned peak (3.1-3.98g, well past 200 qualifying samples) for that
exact (game, car, source) key. Root cause, confirmed by reading source:

- `NormalizedWheelLockSlipEngine.ComputeChannel` (the code that actually accumulates) calls
  `learners.Observe(gameId, carId, motion.MagnitudeG, sourceIdentity, observeBucket)` where
  `observeBucket` is always the real `"Sealed"`/`"Loose"` string (never empty).
- `QAdvanceFeedback.cs`'s own `DataUpdate` read the diagnostics back via
  `_normalizedEngine.LockLearners.PublishedPeakG(gameId, carId, lockSourceIdentity)` /
  `.Confidence(gameId, carId, lockSourceIdentity)` - **with no fourth (`surfaceBucket`)
  argument at all**, silently defaulting to the empty string.
- `KeyedGripLearner.MakeKey` folds the surface bucket into the dictionary key, so
  `Observe(..., "Sealed")` and `Confidence(..., "")` build **two different keys**. `Find()`
  (the read-only lookup) has no fallback from an empty bucket to the real one, so the
  diagnostic read never located the entry real accumulation was actually writing to and
  permanently fell through to the seed defaults.
- This was purely a **read-side (diagnostics) bug** - `KeyedGripLearner.ExportAll()` (what
  actually persists to `RuntimeStore`/`Parameters.json`) iterates the dictionary directly with
  no query key involved, so persistence was correct and unaffected throughout. This plausibly
  explains why the owner perceived accumulation as "stuck" even though it was not: the one
  number they could actually watch live (`Diag.Lock.LearnedPeakG`/`LearnerConfidence`) never
  moved, regardless of how much real learning was happening underneath it.

**Fixed:** added `NormalizedWheelLockSlipEngine.LockCurrentSurfaceBucket`/
`SlipCurrentSurfaceBucket`, public accessors that resolve the exact bucket `ComputeChannel`
itself is currently observing under (same purity-threshold logic against the already-exposed
`LockLooseFraction`/`SlipLooseFraction`, null when the surface reading is genuinely ambiguous -
mirroring `ComputeChannel`'s own "teach neither bucket" case). `QAdvanceFeedback.cs`'s
`DataUpdate` now passes these into the `PublishedPeakG`/`Confidence` calls that feed
`Diag.Lock/Slip.LearnedPeakG`/`LearnerConfidence`, so the live readout finds the same entry
real accumulation writes to. Two new regression tests
(`LockCurrentSurfaceBucket_is_the_bucket_real_accumulation_is_actually_written_under`,
`SlipCurrentSurfaceBucket_is_the_bucket_real_accumulation_is_actually_written_under` in
`NormalizedWheelLockSlipEngineTests.cs`) drive 250 qualifying braking/throttle frames through a
fresh engine and assert the old (bucket-less) query still returns the seed/zero while the new,
bucket-aware query returns real, non-seed values - pinning both the bug and the fix.

### CarId/GameId stability

**Stable within every stint, not flickering.** `211733_ShakeIt.csv` and `212439_Raw.csv`:
`Diag.GameId` is constant (`"F12025"`) for the entire file (zero changes); `Diag.CarId` changes
exactly once, at the row matching the real car switch between stints (row 9,706 and 11,237
respectively) - a single clean transition, not repeated flicker. The four
`Common_1_5_e_d` files predate the `Diag.GameId`/`Diag.CarId` columns entirely (the columns are
absent from their header) - an older export format from before car/game-keyed learning existed,
not a stability problem.

No dictionary key derived from car/track identity changes every frame -
`KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, surfaceBucket)` is stable for an entire
stint in both new-format logs. The one genuine key-mismatch found (above) was on the *query*
side of a read-only diagnostic lookup, not in the storage key itself, and does not affect
accumulation.

## Summary

- SimHub's `DataUpdate`/`End` dispatch is exception-safe by design (decompiled and quoted
  above); `Init` is guarded only at normal startup, not on a later manual enable; the
  `AttachDelegate`/property-read primitive itself is unguarded and only as safe as whichever
  caller happens to wrap it - ShakeIt's own code could not be decompiled/verified either way.
- Two real gaps were found and fixed in our own code: the background flush timer's callback
  could throw unguarded on a raw ThreadPool thread (a plausible process-crash mechanism), and
  both `Init` and `GetWPFSettingsControl` ran unguarded despite being SimHub-invoked entry
  points that (per the decompiled evidence) are not universally protected by SimHub itself.
  All four SimHub-invoked entry points (`Init`, `DataUpdate`, `End`, `GetWPFSettingsControl`)
  and the background timer are now defensively wrapped, logging once per distinct fault.
- Sample accumulation is **working as designed** - the funnel numbers match the persisted
  counts closely, and no stage rejects more than its own threshold would predict. The
  "accumulation looks stuck" perception is explained by a separate, now-fixed bug: the live
  `Diag.Lock/Slip.LearnedPeakG`/`LearnerConfidence` diagnostic readout was querying the wrong
  dictionary key and was permanently frozen at its seed values, independent of how much real
  learning had actually happened.
- `Diag.GameId`/`Diag.CarId` are stable within every stint in both log sets that carry those
  columns; no flickering key was found.
- **Could not be determined:** whether ShakeIt's own plugin code (assembly not available in
  either reference-assembly location) reads properties through SimHub's guarded
  (`GetPropertyValue`) or unguarded (`PropertyEntry.Evaluate`/`PropertyEntryWrapper.GetValue`)
  path, and therefore whether AZOM's reported "mbooster detecting stops working after a game
  switch" symptom is actually caused by this plugin, by ShakeIt/AZOM's own code hitting an
  unguarded primitive, or by something unrelated. The owner-reported symptom could not be
  reproduced from the artifacts available to this investigation (logs, Parameters.json,
  decompiled reference assemblies) - hardening this plugin's own entry points removes it as a
  suspect and is worthwhile regardless, but this report cannot claim to have proven or
  disproven the AZOM connection specifically.

## Verification

- `dotnet build QAdvanceFeedback.sln` - Build succeeded, 0 Warning(s), 0 Error(s), single
  output DLL (`QAdvanceFeedback/bin/Debug/net48/QAdvanceFeedback.dll`).
- `dotnet test QAdvanceFeedback.sln` - Passed: 786, Failed: 0, Skipped: 0 (up from 782 before
  this task; 4 new tests added: 2 for the background-timer exception guard, 2 for the
  surface-bucket diagnostics fix).
