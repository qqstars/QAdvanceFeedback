# Resilience hardening + health registry report

## STATUS: DONE

Comprehensive exception-safety audit of every SimHub-facing boundary (Part 1), a new pure/testable
health registry that every hardened boundary reports into (Part 2), a settings-UI surface for it, and
verification including mutation evidence (Part 3). No signal/algorithm behaviour was changed anywhere -
this is resilience and diagnostics work only.

## Part 1 - boundaries audited and hardened

Starting point: `docs\pipeline-exception-safety-report.md` (read first, per the brief) had already
fixed two real hazards (`RuntimeStore.FlushTick`'s unguarded background-timer callback, and unguarded
`Init`/`GetWPFSettingsControl`) and audited everything else as already solid. This task's job was to
extend that audit to full coverage and add observability, not to re-discover those two.

**Boundaries hardened this pass** (see `QAdvanceFeedback\docs\architecture.md`'s new "Resilience model
and the health registry" section for the durable summary):

1. **Every `AttachDelegate` value-provider lambda individually**
   (`QAdvanceFeedback\PropertyPublisher.cs`) - all ~90 delegates (Raw/Normalized/Projected tiers,
   G-force, every `Diag.*`) now funnel through a new `AttachSafe` helper -> `Core.Health.SafeCall.Value`,
   which catches any exception the provider throws, records it in `HealthRegistry` (once, not per
   frame) and returns SimHub's own "no value" instead of propagating. Audited beforehand: none of these
   lambdas can currently throw (all are direct reads of a fixed-size backing array/field at a
   compile-time-constant index) - this is deliberate prevention against a future regression (e.g. an
   array/name-list length mismatch), not a fix for an existing bug, per the brief's "every one must be
   incapable of throwing."
2. **`SimHubTelemetryAdapter.CaptureRawTelemetry`** - `pluginManager?.GameManager?.
   GetFeedbackCapabilities()` was a real (non-reflection) but undocumented-shape SimHub dependency,
   called with no guard, violating `ITelemetryAdapter`'s own documented "must never throw" contract.
   Wrapped in `SafeGetFeedbackCapabilities`, reporting to `HealthRegistry` (`CapabilityDetection`,
   flagged as a SimHub-compatibility issue) and degrading to the pre-existing "capabilities null"
   fallback path.
3. **Reflection wrappers** (`SimHubScriptEditor`, `PropertyPickerLauncher`, `SimHubExpressionEvaluator`)
   - already resolve-once/cache/degrade-permanently/log-once (audited, unchanged structurally). Added a
   `HealthRegistry.Report` call alongside each existing `LogOnce`: a resolution failure (type/member not
   found) is flagged `IsSimHubCompatibilityIssue = true`; a later invocation failure (already resolved,
   broke while showing/evaluating) is not.
4. **`MotorsExportAvailabilityProvider.SafeGet`** - distinguished a genuine `GetPropertyValue` exception
   (now reported to `HealthRegistry` as `ShakeItExport`) from the resolver simply concluding "not
   configured yet" (the common, expected, NOT-a-fault case already surfaced via the existing inline UI
   note) - only the former is a fault worth recording.
5. **`ConfigStore`/`RuntimeStore`/`CsvExportWriter`** file I/O - every existing catch clause (load/save,
   `WriteAtomic`'s three catches, `LoadFromDisk`'s two, CSV `Start`'s two and `WriteRow`'s one) now also
   reports to `HealthRegistry` (`ConfigPersistence`/`RuntimePersistence`/`CsvExport`), in addition to the
   existing `logWarning` delegate. Missing/corrupt/locked/permission-denied files already degraded to
   defaults/stopped recording before this task; this task adds visibility, not new safety.
6. **`RuntimeStore.FlushTick`** (background timer) - already guarded from the prior pass; added
   `HealthRegistry.Report` (`BackgroundFlush`) inside its own catch.
7. **`QAdvanceFeedback.cs`'s four SimHub-invoked entry points** (`Init`, `DataUpdate`, `End`,
   `GetWPFSettingsControl`) - already guarded from the prior pass; each catch now also reports to
   `HealthRegistry` (`Init` as `Failed`, `DataUpdate`->`TelemetryAdapter` and `End`->`Shutdown` and
   `GetWPFSettingsControl`->`SettingsUi` as `Degraded`/`Failed` as appropriate).
8. **Settings UI event handlers - a genuine NEW gap found and fixed.** `SettingsControl`'s constructor
   was already covered by `GetWPFSettingsControl`'s own try/catch, but its top-level `Button.Click`
   handlers (Apply, Restore all defaults, per-source "Reset to default", and the async script-editor/
   property-picker action button) ran completely unguarded - nothing upstream of a WPF event handler
   invoked well after construction would have caught a throw there. Added `SafeUiAction`/
   `SafeUiActionAsync` wrappers around all four, reporting to `HealthRegistry` (`SettingsUi`) and
   refreshing the health section immediately on a caught fault.
9. **Pathological telemetry** (NaN/infinity/negative or enormous `dt`/null `GameData`/`NewData`/
   `OldData`/missing ids) - audited, already solid at both ends (`DataUpdate`'s own null/state guard,
   plus every Core engine's own finite-checks - see `AbsentTelemetryTests`/`DtNormalizationTests`/
   `ClampMathTests` and the many other existing tests already covering this). No behavioural change; no
   gap found.
10. **`WheelSourceResolver`/reflection `FindType` helpers** - re-audited, unchanged; already correct
    (confirmed `PluginManager.GetPropertyValue` is itself decompiled-safe, per the pipeline-exception-
    safety report, so `WheelSourceResolver`'s narrower catch clause is sufficient).

## Part 2 - health registry and UI surface

**`QAdvanceFeedback\Core\Health\`** (pure, SimHub-free, link-compiled into the test project via the
existing `Core\**\*.cs` wildcard):

- `HealthSeverity.cs` - `Info` (reserved, unused today) / `Degraded` / `Failed`.
- `HealthSubsystems.cs` - stable string-constant identifiers for the ten subsystems the brief named
  (script editor, property picker, expression evaluator, ShakeIt export reads, capability detection,
  config load/save, runtime/parameters load/save, CSV export, background flush, telemetry adapter) plus
  four more this audit added on top (`PropertyPublish`, `Init`, `Shutdown`, `SettingsUi`).
- `HealthEntry.cs` - subsystem, severity, a localization KEY (not rendered text) for the driver-readable
  impact, the raw exception detail (deliberately unlocalized - for a bug report), `IsSimHubCompatibilityIssue`,
  first/last-occurred timestamps, occurrence count.
- `HealthRegistry.cs` - a static, lock-protected dictionary keyed by subsystem. `Report(...)` creates an
  entry on first report and mutates the SAME entry (timestamp/count/detail only) on every repeat - this
  is what makes "log once, not per frame" hold even for a value provider that throws every single frame.
  `Snapshot()` returns a point-in-time copy; `IsHealthy` is true only when nothing has ever reported.
- `SafeCall.cs` - the generic value-provider guard `PropertyPublisher.AttachSafe` uses for every
  published property.

**UI surface** (`Settings\SettingsControl.xaml`/`.xaml.cs`, General tab, new "Plugin health" group,
localized via `Group.Health`/`Health.*` keys):

- **Healthy state (the common case):** one line, "All systems normal - nothing to report." - no button,
  no clutter. This is what the regenerated `docs\images\settings-general.png` now shows.
- **Degraded state:** one bold warning line per degraded subsystem - a localized subsystem name plus its
  localized impact text ("what still works, what to do"), colored orange for `Degraded` / firebrick for
  `Failed`. **The critical case:** any entry flagged `IsSimHubCompatibilityIssue` gets an appended plain-
  language sentence - "This feature needs an update for your SimHub version." - rather than an opaque
  failure. A "Copy details for a bug report" button (visible only when there is something to report)
  copies every entry's raw technical detail (subsystem, severity, first/last-seen timestamps, occurrence
  count, exception text) to the clipboard, deliberately unlocalized English since it exists for the owner
  to paste into an issue, not for the driver to read as prose.
- Refreshed once, at the very end of the constructor (after every reflection wrapper the control uses has
  already been force-resolved by the constructor's own earlier wiring, so nothing is missed on first
  open), and again immediately after any `SafeUiAction`/`SafeUiActionAsync` catch.
- Verified visually with a throwaway harness run (`...\scratchpad\qpreview\VerifyHealthDegraded.cs`, not
  part of the repo) that pre-seeds three synthetic faults before constructing the control - confirmed the
  healthy state, the degraded state (both `Degraded`/orange and `Failed`/firebrick rows), the
  SimHub-update-needed sentence, and the copy button all render correctly.

**Localization:** 33 new keys added to both `StringTableEn.cs` and `StringTableZhHans.cs` (group header,
all-good line, copy-details button/confirmation, the SimHub-update-needed sentence, 14 subsystem display
names, 14 impact messages). The Chinese table was edited via a Python script
(`...\scratchpad\health_zh_source.py` + `apply_health_zh.py`) that holds the source Chinese text as a
UTF-8 Python literal (written via the file-write tool, never a bash heredoc) and emits `\uXXXX`-escaped
C# string literals into `StringTableZhHans.cs`, matching that file's existing encoding convention
throughout. `LocalizationTests`' key-parity test (English/zh-Hans have exactly the same key set, no
blank values) passes.

## Part 3 - verification

### Tests

- Full suite: **799 passed, 0 failed, 0 skipped** (up from 786 at the start of this task - 13 net new:
  8 in `HealthRegistryTests.cs`, 4 in `SafeCallTests.cs`, 1 new fact in `RuntimeStoreTests.cs`; three
  more existing tests - one each in `ConfigStoreTests.cs`/`RuntimeStoreTests.cs` (x2) - gained an extra
  `HealthRegistry` assertion rather than becoming new facts, and `CsvExportWriterTests.cs` gained one new
  fact for a directory-as-path fault).
- `HealthRegistryTests.cs` - reporting once creates one entry with the given fields; reporting the same
  subsystem 500 times still yields exactly one entry (proves "log once, not per frame" at the registry
  level); the first-reported severity/impact-key/compatibility-flag stick even if later reports differ;
  null/empty subsystem is a safe no-op; `Clear`/`Snapshot` behave as documented. Every subsystem name used
  is a fresh `Guid`, not a real `HealthSubsystems` constant, so these tests are independent of whatever
  else in the assembly reports concurrently (xunit runs different test classes in parallel by default).
- `SafeCallTests.cs` - a non-throwing provider passes its value through untouched and reports nothing; a
  throwing provider never propagates, returns the fallback, and records exactly one entry; a provider
  that keeps throwing every "frame" (200 simulated calls) still only ever occupies one entry.
- `RuntimeStoreTests.cs`/`ConfigStoreTests.cs`/`CsvExportWriterTests.cs` - the pre-existing invalid-path/
  corrupt-file fault-injection tests now also assert the matching `HealthRegistry` entry appears; a new
  `A_corrupt_runtime_file_is_recorded_in_the_health_registry_and_still_degrades_to_a_fresh_document` test
  and a new `Starting_at_a_path_that_is_actually_a_directory_is_recorded_in_the_health_registry_and_does_not_throw`
  test were added for RuntimeStore/CsvExportWriter respectively.
- **Reflection-wrapper degrade tests, a real limitation stated plainly:** `SimHubScriptEditor`/
  `PropertyPickerLauncher`/`SimHubExpressionEvaluator`/`SimHubTelemetryAdapter` all reference
  `SimHub.Plugins`/`SimHub.Logging`/WPF types and target net48 - they cannot be link-compiled into the
  net8.0 test project (confirmed previously, documented in `PropertyPublisherStructureTests.cs`'s own
  remarks: referencing a net48 SimHub assembly from the net8.0 test host breaks test discovery for the
  whole assembly). Their "resolve-once/cache/degrade-permanently" logic was verified by direct code
  reading (unchanged structurally from the already-solid prior audit) and their NEW `HealthRegistry.Report`
  call sites were verified the same way. The underlying degrade-and-report MECHANISM they all share
  (catch -> report once -> return a safe fallback) is what `SafeCallTests`/`HealthRegistryTests` actually
  exercise end-to-end against the real classes. This is a pre-existing, architecture-documented
  limitation of this codebase's SimHub/non-SimHub split, not something introduced by this task.

### Mutation evidence

1. **Value-provider guard** (`Core.Health.SafeCall.Value`) - temporarily replaced its body with a bare
   `return provider();` (no try/catch) and re-ran `SafeCallTests`: **3 of 4 tests failed**, each with the
   simulated `InvalidOperationException` propagating uncaught out of `SafeCall.Value` instead of the
   fallback being returned (confirmed via the actual test-runner stack traces). Restored the guard from a
   scratchpad backup; full suite re-confirmed green (799/799) afterward.
2. **Timer/background-flush guard** (`RuntimeStore.WriteAtomic`) - temporarily removed its entire
   try/catch (reverting to the shape that existed before the FIRST prior-task fix) and re-ran
   `RuntimeStoreTests`: **2 tests failed** -
   `Flush_with_an_invalid_path_does_not_throw_synchronously_either` failed with the real
   `System.IO.IOException` escaping synchronously out of `Flush()`, and
   `An_invalid_flush_path_does_not_crash_the_background_timer_or_the_process` failed its `HealthRegistry`
   assertion (collection empty - nothing was ever caught to report). Restored from a scratchpad backup;
   full suite re-confirmed green (799/799) afterward.
   - **Caveat found during this exercise, worth recording:** under the net8.0 test host (this project's
     test TFM), `Directory.CreateDirectory`/`File.WriteAllText` throw `IOException` for the "sub|dir"
     invalid-path-segment fixture, NOT the `ArgumentException` the original prior-task report's comments
     describe - .NET Core dropped the .NET Framework-era pre-flight invalid-character validation that
     used to throw `ArgumentException` before ever reaching the OS call. This means the plain
     `catch (IOException)` clause alone already covers this specific fixture under net8.0, and only
     removing the WHOLE try/catch (not just the trailing generic `catch (Exception)`) reproduces a
     failure in this test host. The broadened generic catch this task's predecessor added still matters
     for the real net48 production DLL (where that old ArgumentException-throwing behaviour is what
     actually runs), but this test suite cannot independently prove that net48-specific difference from
     a net8.0 test host - flagged rather than silently assumed correct.

### Screenshots

Only `docs\images\settings-general.png` changed - it now shows the new "Plugin health" group
("All systems normal - nothing to report.") beneath the existing General settings, in the healthy state
a fresh install starts in. Regenerated via the existing out-of-repo harness
(`...\scratchpad\qpreview\Program.cs`, unchanged) against the rebuilt Release DLL, following the standing
per-tab capture rule in `docs\architecture.md` (General: full control, chrome included). Verified visually
before copying into the repo.

Wheel Lock, Wheel Slip and G-Force tabs were not touched by this task and were re-rendered only to
confirm no regression - their content is unchanged (same groups/fields, same font metrics); their PNGs in
`docs\images\` were left as-is, not regenerated/replaced.

## Known unguarded path (stated plainly, not silently omitted)

SimHub's own `PluginManager.GetPropertyValue` and the NCalc/formula-engine call chain that reaches
`PropertyEntryWrapper.GetValue()` are confirmed exception-safe/safe-in-practice by decompilation (see
`docs\pipeline-exception-safety-report.md`) - this plugin neither can nor does patch SimHub's own
primitives. If some OTHER caller (a different plugin, or ShakeIt's own internals) reaches
`PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()` directly without going through SimHub's own
wrapping, that path remains genuinely unguarded - outside this plugin's reach to fix, and not claimed as
fixed here. This was already flagged, unchanged, in the prior report; this task did not find a way to
close it and does not claim to.

## Concerns

1. The reflection-wrapper/adapter classes' new `HealthRegistry.Report` call sites are verified by direct
   code reading and by the shared `SafeCall`/`HealthRegistry` mechanism's own tests, not by a dedicated
   automated test against the real `SimHubScriptEditor`/`PropertyPickerLauncher`/
   `SimHubExpressionEvaluator`/`SimHubTelemetryAdapter` classes themselves - see Part 3's "real limitation
   stated plainly" above for why (net48-only, cannot link-compile into the net8.0 test project).
2. The net8.0-vs-net48 exception-type difference found while producing the timer-guard mutation evidence
   (IOException vs. the originally-assumed ArgumentException for the "sub|dir" fixture) means this test
   suite can prove the GUARD exists and matters in principle, but cannot independently prove the exact
   net48-production exception type it was written to catch - flagged above, not silently assumed.
3. `SettingsControl`'s own event-handler guard (`SafeUiAction`/`SafeUiActionAsync`) is, like the rest of
   the WPF settings control, not unit-tested (architecture.md's own long-standing "the WPF control itself
   is the one part of this subsystem not unit-tested" limitation) - verified by code reading and the
   visual harness run only.

## Build / test confirmation

- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**.
  `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` (single-DLL
  distribution intact).
- `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj`: **Passed! - Failed: 0, Passed: 799,
  Skipped: 0, Total: 799** (up from 786 before this task).
