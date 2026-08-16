# Raw-telemetry diagnostics for identifying the live Wheel Lock branch

## STATUS

**Diagnostics added, diagnostics-gated, default-published-set unchanged, all tests green.**
No algorithm, default, or behaviour was changed anywhere in this pass - only new `Diag.Telemetry.*`/
`Diag.Capabilities.*` properties, all behind the existing `EnableDiagnostics` gate and all included in
the CSV export exactly like every other `Diag.*` property already is.

**Tests:** 581 total (568 pre-existing + 13 net new), 0 failed, 0 skipped, full build (`Private/`
present). **Clean-clone tests:** 536 total (523 pre-existing + 13 net new), 0 failed, 0 skipped,
`Private/` absent, verified in an isolated scratch copy (not the working tree).
**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release -v:m` -> `0 Warning(s), 0 Error(s)` for both the full
tree and the isolated clean-clone scratch copy (with `lib/` copied alongside it).
`bin/Release/net48/` contains exactly `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb` in both cases.

---

## §0 - `[DoNotExpose]` retraction reconfirmed before touching anything

Per `docs\raw-match-rootcause-report.md` §2c: `GameReaderCommon.Feedback.FeedbackData.WheelRPS`/
`WheelSpeed`/`WheelSlip` are ordinary public `double[]` properties. `[DoNotExpose]` (decompiled
directly again this pass) is a plain, empty marker class with no CLR semantics - it only affects
whether SimHub's OWN property/expression picker lists a member; it has zero effect on a plugin
reading the field directly. Re-decompiled `GameReaderCommon.FeedbackData` this pass (already present
at `docs\reference\GameReaderCommon.FeedbackData.decompiled.cs`) confirms the exact shape:

```csharp
[DoNotExpose] public double[] WheelRPS   { get; set; } = new double[4];
[DoNotExpose] public double[] WheelSlip  { get; set; } = new double[4];
[DoNotExpose] public double[] WheelSpeed { get; set; } = new double[4];
```

These fields are readable. This pass captures them.

---

## §1 - `FeedbackCapabilities` IS reachable at runtime (the single most direct branch signal)

Decompiled `GameReaderCommon.GameData` (new, this pass): carries no capabilities reference of its
own. Decompiled `GameReaderCommon.IGameManager` (new, this pass): has a plain public method,
`FeedbackCapabilities GetFeedbackCapabilities(bool contextual = false)`. Decompiled
`SimHub.Plugins.PluginManager` (already in `docs\reference\` from a previous pass): has a plain
public property, `IGameManager GameManager { get; }`. Chaining these -
`pluginManager.GameManager?.GetFeedbackCapabilities()` - is therefore a fully public, reachable path
from this plugin's own `DataUpdate(PluginManager pluginManager, ref GameData data)` to the EXACT
`FeedbackCapabilities` object `WheelSlipEffect.GetEffectValue`'s own decompiled dispatch chain reads
(`docs\reference\SimHub.WheelSlipEffect.decompiled.cs`, lines 106-162) to pick between its nine
Lock/Slip branches.

**This was published, not skipped as unreachable** - see §3. The relevant `FeedbackCapabilities`
booleans, decompiled and confirmed public this pass (`GameReaderCommon.Feedback.FeedbackCapabilities`):
`WheelsSlip`, `WheelsRPS`, `WheelsSpeed`, `WheelsSlipDirectMode`, `WheelSlipUseSimpleBraking`,
`DetectLockFromWheelsSpeed`, `DetectLockFromWheelsRPSAndDummyRadius`, `Speed`, `RPM`, `GameFamily`,
and `WheelSlipCalibrationProvider?.SlipScale` (a `double?`) - named and ordered here exactly as
`WheelSlipEffect.GetEffectValue`'s own `if`/`else if` chain checks them.

One minor, flagged nuance: `GetFeedbackCapabilities` takes an optional `contextual` parameter
(default `false`); this capture calls it with the default (no-contextual) overload, matching the
simplest/most general reading a plugin would naturally take. Whether the `contextual: true` variant
ever differs in a way that matters for Lock/Slip branch selection was not chased further (out of
scope for a diagnostics-only pass) - flagged as an open, low-priority question, not silently assumed
away.

---

## §2 - Null vs 0 (the thing that must not be gotten wrong again)

`FeedbackData.WheelRPS`/`WheelSpeed`/`WheelSlip` all default to a REAL `double[4]` of zeros
(`= new double[4]`) regardless of whether the running title ever actually populates them. This means
the array's own contents can **never**, by themselves, distinguish "genuinely stationary/zero" from
"this title does not supply this channel at all" - exactly the ambiguity a previous pass's now-
retracted "architecturally impossible" argument could have walked into for a different (title-level,
not architecture-level) reason, had it had this telemetry without a safeguard.

The fix: gate every per-wheel reading on its own `FeedbackCapabilities` flag
(`WheelsRPS`/`WheelsSpeed`/`WheelsSlip`) before ever trusting the array - a **known-false** capability
AND an **unknown** capability (couldn't be reached this frame) both suppress the reading to `null`;
only a **known-true** capability lets the real array contents (including a genuine `0.0`) through.
This logic lives in a new, pure, SimHub-free class, `QAdvanceFeedback.Core.RawWheelTelemetryBuilder`
(`QAdvanceFeedback\Core\RawWheelTelemetryBuilder.cs`), so it is unit-tested directly
(`QAdvanceFeedback.Tests\RawWheelTelemetryBuilderTests.cs`, 11 tests) rather than only ever
exercisable via a live SimHub session. Tests cover: real readings pass through; genuine zeros pass
through as real zeros (not null); a known-false capability suppresses even non-zero-looking array
contents to null; a known-false capability with an all-zero array is the exact confusion scenario,
suppressed to null; an unknown (null) capability is treated exactly as conservatively as false; a
null/too-short array defends to null rather than throwing even when the capability says yes; the
three channels (RPS/Speed/SlipRatio) gate independently; capability metadata passes through verbatim;
and a completely unreachable `FeedbackCapabilities` object leaves every capability AND every gated
per-wheel field null.

The SimHub-facing side, `Private\QAdvanceFeedback\SimHubTelemetryAdapter.cs`'s new
`CaptureRawTelemetry`, is a thin map (reads `FeedbackData`/`FeedbackCapabilities`, hands plain
primitives to `RawWheelTelemetryBuilder.Build`) - the same "Layer 2 maps, Layer 1 holds the logic"
split this project already uses for `TelemetryFrame`/`ToFrame`.

---

## §3 - Exact new columns (what the next capture will contain)

All gated behind `Settings.General.EnableDiagnostics` (published to SimHub only when on, same as
every existing `Diag.*` property) and included in the CSV export unconditionally whenever
`ExportCsv` is on (same as every existing diagnostic - the CSV header is always
`ProductNames() + DiagnosticNames()`, independent of `EnableDiagnostics`). Full SimHub property name
adds the usual `QAdvanceFeedback.` prefix (e.g. `QAdvanceFeedback.Diag.Telemetry.WheelRPS.FrontLeft`);
the CSV header uses the bare name shown below, exactly like the existing `Diag.Source.*` columns do.

**Car-level (11)** - read straight off this frame's already-computed `ITelemetryFrame`
(`sample.New`), not recomputed - these are the exact same values Layer 3 already consumes, only newly
EXPOSED:
```
Diag.Telemetry.GroundSpeedKmh
Diag.Telemetry.SpeedKmh
Diag.Telemetry.GroundSpeedMps
Diag.Telemetry.Rpm
Diag.Telemetry.Gear
Diag.Telemetry.BrakePercent
Diag.Telemetry.ThrottlePercent
Diag.Telemetry.ClutchPercent
Diag.Telemetry.LateralLocalVelocity
Diag.Telemetry.LongitudinalG
Diag.Telemetry.LateralG
```

**Per-wheel (12)** - new capture, gated per §2, FrontLeft/FrontRight/RearLeft/RearRight only (no
Front/Rear/Left/Right/All aggregate - no aggregation makes sense for a raw per-wheel physical reading
the way it does for this plugin's own computed Lock/Slip channels):
```
Diag.Telemetry.WheelRPS.FrontLeft / .FrontRight / .RearLeft / .RearRight
Diag.Telemetry.WheelSpeed.FrontLeft / .FrontRight / .RearLeft / .RearRight
Diag.Telemetry.WheelSlipRatio.FrontLeft / .FrontRight / .RearLeft / .RearRight
```
(`WheelSlipRatio`, not `WheelSlip` - deliberately, so it is never confused with this plugin's own
already-computed `WheelSlip.*` published channel; this is the RAW ratio from `FeedbackData.WheelSlip`.)

**Capabilities (11)** - named/ordered exactly as `WheelSlipEffect.GetEffectValue`'s own dispatch
checks them (§1):
```
Diag.Capabilities.WheelsSlip
Diag.Capabilities.WheelsRPS
Diag.Capabilities.WheelsSpeed
Diag.Capabilities.WheelsSlipDirectMode
Diag.Capabilities.WheelSlipUseSimpleBraking
Diag.Capabilities.DetectLockFromWheelsSpeed
Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius
Diag.Capabilities.Speed
Diag.Capabilities.Rpm
Diag.Capabilities.GameFamily
Diag.Capabilities.WheelSlipCalibrationProviderSlipScale
```

**34 new names total.** The default published set (62 product names) is byte-for-byte unchanged -
`AllPublishedPropertiesTests.Product_names_total_exactly_62_and_are_all_distinct` and
`Diagnostics_off_publishes_only_the_62_product_names` both still pass unmodified, and a new test,
`Diagnostics_off_never_publishes_the_raw_wheel_telemetry_or_capabilities_diagnostics`, asserts none
of the 34 new names leak into the default (diagnostics-off) set.

With these columns, fitting the three remaining candidate Lock branches against a real captured
session becomes direct:
- `LockFromWheelsRPSAndDummyRadius` needs `Diag.Telemetry.WheelRPS.*` + `Diag.Telemetry.GroundSpeedMps`
  (`SimpleLock01.Compute(wheelRps, vehicleSpeedMps, ...)`).
- `GetLockFromWheelSpeed` needs `Diag.Telemetry.WheelSpeed.*` + `Diag.Telemetry.GroundSpeedKmh`/
  `Diag.Telemetry.SpeedKmh` (its own `?? Speed` fallback).
- `GetRpsLock` needs `Diag.Telemetry.WheelRPS.*` + `Diag.Telemetry.SpeedKmh` (its own internal
  calibration average is NOT reachable from plugin code - flagged, see Concerns).
- `Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius`/`DetectLockFromWheelsSpeed`/`WheelsRPS`
  will show DIRECTLY which of the three the running title/SimHub build actually dispatches to,
  removing the need to infer it from timing/uniformity evidence at all.

---

## §4 - `Diag.Source.*` verified as genuinely ShakeIt's own value in the shakeit-mode log

Two independent checks, both confirming the same conclusion:

**Code trace.** `Diag.Source.*` is filled from `lockSources`/`slipSources` in `QAdvanceFeedback.cs`'s
`DataUpdate`, which come from `WheelSourceResolver.Resolve(pluginManager, source, scriptType,
fallback)`. In "Plain" mode (`docs\shakeit-export-guide.md`'s documented ShakeIt-mode setup) this
calls `pluginManager.GetPropertyValue(source)` where `source` is
`ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.<Wheel>` / `...WheelSlip.IRacing.<Wheel>` - a property
in a COMPLETELY DIFFERENT plugin's own namespace (SimHub's ShakeIt Motors plugin), computed entirely
by SimHub's own `WheelSlipEffect`/ShakeIt engine, not by this plugin. It is architecturally
impossible for this to silently echo this plugin's own `WheelLock.Raw`/`WheelSlip.Raw` unless the
driver's Source field is literally left blank (which falls back to Raw, by design, and is exactly
what the `raw` log's own settings did instead).

**Log evidence.** Directly compared `WheelLock.Raw.All`/`WheelSlip.Raw.All` against
`Diag.Source.Lock.All`/`Diag.Source.Slip.All` in both provided CSVs
(`QAdvanceFeedback.session-20260815-210223-raw.csv`, `QAdvanceFeedback.session-20260815-210421-
shakeit.csv`, one directory above this project):

```
raw.csv     (1539 frames): Raw != Source on 0 frames        (Source resolves to our own Raw, as expected)
shakeit.csv (1989 frames): Raw != Source on 112 Lock frames, 1325 Slip frames (max abs diff 100.0)
```

In the shakeit log, `Diag.Source.*` diverges from our own `Raw` on a large fraction of frames (up to
the full 0-100 range) - this is only possible if `Diag.Source.*` is reading a genuinely different,
independently-computed value, i.e. ShakeIt's own real output. **Confirmed: the shakeit-mode log's
`Diag.Source.*` is genuinely ShakeIt's own value, not our own Raw echoed back** - the error figures in
`docs\raw-match-rootcause-report.md` (mean 36.0/median 10.0/p90 100/corr 0.42 on braking frames for
Lock) are measured against a real, independent ShakeIt reading and are trustworthy as stated.

---

## §5 - Per-wheel telemetry availability: NOT determinable from the two existing logs

Both existing CSVs were captured **before** this diagnostic existed - neither header contains any
`Diag.Telemetry.*`/`Diag.Capabilities.*` column (confirmed directly: `head -1` on both files lists
108 columns, none matching `Diag.Telemetry`/`Diag.Capabilities`). **This pass cannot say whether the
title used for those two sessions genuinely supplies `WheelRPS`/`WheelSpeed`/`WheelSlip` or not** -
that is exactly the gap this task closes for the NEXT capture, not a retroactive answer for the
sessions already on disk. Stated plainly rather than guessed: if the next capture's
`Diag.Capabilities.WheelsRPS`/`WheelsSpeed` come back `false` (or the gated `Diag.Telemetry.WheelRPS.*`
columns come back entirely empty across the whole session), THAT would be the direct, data-backed
version of the earlier "architecturally impossible" claim - correct for a title-level reason (this
specific title/game doesn't supply the channel) rather than the retracted architecture-level reason
(`docs\raw-match-rootcause-report.md` §2c). Either outcome settles the question the three previous
passes could only narrow.

---

## Exact files changed

- `QAdvanceFeedback\Core\RawWheelTelemetrySnapshot.cs` (new) - SimHub-free DTO.
- `QAdvanceFeedback\Core\RawWheelTelemetryBuilder.cs` (new) - SimHub-free, unit-tested gating logic.
- `QAdvanceFeedback\ITelemetryAdapter.cs` - added `CaptureRawTelemetry(GameData, PluginManager)`.
- `QAdvanceFeedback\InertTelemetryAdapter.cs` - implements it, returns `RawWheelTelemetrySnapshot.Empty`.
- `Private\QAdvanceFeedback\SimHubTelemetryAdapter.cs` - implements it for real (thin map onto
  `RawWheelTelemetryBuilder.Build`).
- `QAdvanceFeedback\Core\AllPublishedProperties.cs` - 34 new names added to `DiagnosticNames()` only;
  `ProductNames()` untouched.
- `QAdvanceFeedback\PropertyPublisher.cs` - new backing fields, `AttachDelegate` calls (inside the
  existing diagnostics gate), `UpdateRawTelemetry(...)`, `SnapshotAllValuesForCsv()` extended.
- `QAdvanceFeedback\QAdvanceFeedback.cs` - one new call, `_adapter.CaptureRawTelemetry(...)` +
  `_publisher.UpdateRawTelemetry(...)`, right after `sample` is computed in `DataUpdate`.
- `QAdvanceFeedback.Tests\RawWheelTelemetryBuilderTests.cs` (new, 11 tests).
- `QAdvanceFeedback.Tests\AllPublishedPropertiesTests.cs` - 2 new tests.

No algorithm, formula, threshold, or default was touched anywhere in this diff.

---

## MUTATION EVIDENCE

Temporarily added `yield return "Diag.Telemetry.GroundSpeedKmh";` to
`AllPublishedProperties.ProductNames()` (i.e. published unconditionally, outside the diagnostics
gate) and re-ran `AllPublishedPropertiesTests`:

```
FAILED Diagnostics_off_publishes_only_the_62_product_names: Expected 62, Actual 63
FAILED Diagnostics_off_never_publishes_the_raw_wheel_telemetry_or_capabilities_diagnostics:
       Assert.DoesNotContain() - "Diag.Telemetry.GroundSpeedKmh" found in the diagnostics-off set
FAILED Product_names_total_exactly_62_and_are_all_distinct: Expected 62, Actual 63
```

Reverted to the exact original (no unconditional line); all 11 `AllPublishedPropertiesTests` and the
full 581-test suite are green again (confirmed above).

---

## Concerns / unverified

- **`GetRpsLock`'s internal calibration average** (`ICalibrationData.GetAverage()`, from
  `CalibrationDataProvider.GetRPSToSpeedCalibration`) is SimHub-internal state this plugin cannot
  reach - `GetRpsLock` can still be fit qualitatively against `Diag.Telemetry.WheelRPS.*` +
  `Diag.Telemetry.SpeedKmh`, but its own rolling average term will need to be estimated from the
  session's own WheelRPS/Speed data rather than read directly.
- **`GetFeedbackCapabilities(contextual: true)` vs the default `false`** used here was not chased
  further (§1) - flagged, not chased, as out of scope for a diagnostics-only pass.
- **Per-wheel telemetry availability on the title used for the two existing logs is unknown** (§5) -
  by design; this pass adds the diagnostic, it does not retroactively answer that question.
- **No live-game/SimHub/hardware verification anywhere** - everything above is verified against
  decompiled SimHub source (new: `GameReaderCommon.GameData`/`IGameManager`/`FeedbackCapabilities`/
  `WheelSlipProvider`, all newly decompiled this pass and cross-checked against the already-decompiled
  `SimHub.WheelSlipEffect.decompiled.cs`), the two existing CSV replays (log-level cross-check in
  §4), and unit tests only, per this task's own stated constraint (no SimHub/game/harness available).
