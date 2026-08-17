# Architecture

This document describes the layer model behind `QAdvanceFeedback` and maps every implementation
file to the layer it belongs to. It exists so a contributor (or a future version of the author) can
find "which file owns this behaviour" without re-deriving the whole design from the code.

## The layer model

Telemetry flows through five layers plus two independent subsystems (G-force, and settings/
persistence). Each layer publishes its own tier of properties (except Layers 1-2, which are
internal) and depends only on the layer(s) below it - never sideways, never upward.

### Layer 1 - Telemetry interface

**What it does:** defines a game-agnostic shape for one frame of telemetry
(`ITelemetryFrame`/`TelemetryFrame`) and one sample (`ITelemetrySample`/`TelemetrySample`, current +
previous frame + elapsed time). Every reading is independently nullable, and that is load-bearing:
null means "this title did not supply this value", never a real zero.

**Must NOT depend on:** anything. This is the bottom of the stack - no SimHub types, no other
layer's types.

### Layer 2 - SimHub adapter

**What it does:** the only place allowed to know SimHub's own type names
(`GameData`/`StatusDataBase`/`FeedbackData`/`PluginManager`/`FeedbackCapabilities`). Maps SimHub's
live telemetry onto Layer 1's shape (`SimHubTelemetryAdapter`), and separately captures a raw
per-wheel diagnostics snapshot (`RawWheelTelemetrySnapshot`, gated by capability flags so a title's
genuine zero is never confused with "does not support this channel" - see
`RawWheelTelemetryBuilder`).

**Must NOT depend on:** nothing above it. It may reference SimHub/GameReaderCommon types (it is the
one place allowed to), but Layer 3 and above never reference SimHub types back.

### Layer 3 - Raw calculator

**What it does:** `QAdvanceFeedback.Core.RawCalculator` - turns one telemetry sample into the
published `WheelLock.Raw.*`/`WheelSlip.Raw.*` properties. `WheelSlipBranchSelector` (in `Core`,
not `RawCalculator`, since it is pure boolean priority over public capability flags and reveals no
formula) picks which signal shape a title supports; `RawCalculatorEngine` dispatches to the matching
formula and owns every stateful learner. Unnormalised by design - a reading of "40" here means a
different thing in different cars; that is fixed one layer up.

**Must NOT depend on:** SimHub types (enforced by the test project link-compiling this folder into a
plain net8.0 assembly with no SimHub package reference - a SimHub dependency creeping in here breaks
the test build immediately), or anything in Layer 4/5/G-force/Settings.

### Layer 4 - Normalized

**What it does:** `QAdvanceFeedback.Core.Normalized` - combines Layer 3's per-wheel shape with a
car-relative severity learned from speed/throttle/brake/G alone (`GripLearner`/`KeyedGripLearner`,
keyed per game+car+source, with a surface-keyed and per-source-scale extension), so the published
bands mean the same thing in an arcade car pulling 4g as in a sim car pulling 1.2g.

**Must NOT depend on:** SimHub types, or Layer 5/G-force. May depend on Layer 3's output shape
(`Corners`, `LegacyWheelLockSlipResult`) and Layer 1 (`ITelemetrySample`).

### Layer 5 - Projected

**What it does:** `QAdvanceFeedback.Core.Projection` - pushes Layer 4's output through a driver-
editable monotone curve (`MonotoneCubicCurve`/`OutputProjector`) and an optional pulse-at-maximum
stage (`PulseGenerator`). This is what a driver should bind a shaker to.

**Must NOT depend on:** SimHub types. May depend on Layer 4's output shape.

### G-Force

**What it does:** `QAdvanceFeedback.Core.GForce` - an independent channel set (not derived from
Layers 3-5 at all) modelling a washout-style split between a sustained level and a rate-driven
transient, plus per-game/per-car learned maxima (`GForceMaxLearner`).

**Must NOT depend on:** Layers 3/4/5 or SimHub types. (The Wheel Lock/Slip "shake" integration reads
Layer 5's output as an input to G-Force's own amplitude, which is the one deliberate exception -
see `GForceEngine.Compute`'s own remarks.)

### Settings / persistence

**What it does:** `QAdvanceFeedback.Settings` (settings POCOs + the WPF settings UI) and
`ConfigStore`/`RuntimeStore`/`Core.Runtime` (JSON persistence for configuration and learned state).
Reads/writes plain doubles, enums and strings - never a live SimHub reference baked into a
persisted object.

**Must NOT depend on:** SimHub types for anything that needs to be unit-tested (the WPF control
itself is the one part of this subsystem that necessarily does, and is accordingly the one part not
unit-tested - see `ApplyDirtyStateTests.cs`'s own remarks).

## SimHub-dependent vs. pure/testable

This boundary is what makes almost this entire plugin unit-testable without a running SimHub
process:

- **SimHub-dependent** (references `SimHub.Plugins`/`GameReaderCommon`, cannot be constructed
  outside a live SimHub host): `QAdvanceFeedback.cs` (the plugin composition root),
  `SimHubTelemetryAdapter.cs`, `MotorsExportAvailabilityProvider.cs`, `PropertyPickerLauncher.cs`,
  `SimHubScriptEditor.cs`, `SimHubExpressionEvaluator.cs`, `WheelSourceResolver.cs`,
  `Settings/SettingsControl.xaml(.cs)`.
- **Pure/testable** (plain C#, no SimHub reference at all): everything under `Core\` (Layers 1/3/4/5,
  G-Force), plus the settings POCOs and `ConfigStore`/`RuntimeStore` (which take logging as plain
  `Action<string>` delegates rather than a direct SimHub logger reference).

The test project (`QAdvanceFeedback.Tests`) enforces this by link-compiling the pure files directly
as source into a net8.0 assembly with zero SimHub package references, rather than referencing the
built net48 plugin DLL - a SimHub dependency creeping into a file that is supposed to be pure breaks
the test build immediately, not just a runtime assumption nobody checks.

## File-by-file map

### `QAdvanceFeedback\` (composition root, Layer 2, SimHub-facing helpers)

| File | Layer | Purpose |
|---|---|---|
| `QAdvanceFeedback.cs` | composition root | The `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` entry point - wires every layer together each frame. |
| `SimHubTelemetryAdapter.cs` | 2 | Maps SimHub's `GameData` onto Layer 1's `TelemetrySample`; captures the raw diagnostics snapshot. |
| `ITelemetryAdapter.cs` | 2 (contract) | The interface `SimHubTelemetryAdapter` implements. |
| `MotorsExportAvailabilityProvider.cs` | Settings-adjacent | Adapts a live `PluginManager` to `MotorsExportAvailabilityResolver` for the settings UI's inline note. |
| `WheelSourceResolver.cs` | Layer 4 input | Resolves one of `WheelChannelSettings`'s source fields (plain property, JavaScript or NCalc) to a live 0-100 reading. |
| `PropertyPickerLauncher.cs` / `SimHubScriptEditor.cs` / `SimHubExpressionEvaluator.cs` | Settings-adjacent | SimHub-reflection helpers for the settings UI's picker/script-editor buttons and expression evaluation. |
| `PropertyPublisher.cs` | publish boundary | Registers and updates every published SimHub property from every layer's own output. |
| `CsvExportWriter.cs` | diagnostics | Writes every published property to a CSV file when "Export session to CSV" is on. |
| `ConfigStore.cs` / `RuntimeStore.cs` | persistence | JSON load/save for settings and for learned runtime state. |

### `QAdvanceFeedback\Core\` (Layer 1 + shared primitives)

| File | Layer | Purpose |
|---|---|---|
| `ITelemetryFrame.cs` / `TelemetryFrame.cs` | 1 | One frame of game-agnostic telemetry. |
| `ITelemetrySample.cs` / `TelemetrySample.cs` | 1 | Current + previous frame + elapsed time. |
| `Corners.cs` | shared | Four-wheel value struct, fixed FL/FR/RL/RR index order. |
| `ClampMath.cs` | shared | Publish-boundary clamping (`To0100`/`To01`) and safe-conversion helpers. |
| `MathHelpers.cs` | shared (Layer 3 formulas) | Clamp/Map/Offset/piecewise-map remapping helpers. |
| `AggregationWeights.cs` / `Aggregator.cs` / `WheelAggregate.cs` | shared (Layers 3/4/5) | The physically-motivated axle/side blend that turns four per-wheel values into Front/Rear/Left/Right/All. |
| `ILegacyWheelLockSlipEngine.cs` / `LegacyWheelLockSlipResult.cs` / `WheelLegacyResult.cs` / `LegacyThresholds.cs` | 3 (contract) | The public contract `RawCalculatorEngine` implements, and the driver-configurable pedal thresholds that gate it. |
| `WheelSlipBranchNames.cs` / `WheelSlipBranchSelector.cs` | 3 (selection) | The diagnostic branch-name constants and the pure capability-priority selector. |
| `RawWheelTelemetrySnapshot.cs` / `RawWheelTelemetryBuilder.cs` | 2/3 boundary | The per-wheel raw telemetry + capability snapshot Layer 3's dispatch reads, and its null-vs-zero gating logic. |
| `IValueDistributionLearner.cs` | 3 (contract) | The learner contract `StreamingPercentileLearner` implements. |
| `OnlineDistributionLearner.cs` | 4 (KeyedScaleLearner support) | A separate streaming mean/variance learner used by the per-source scale calibration mechanism. |
| `PublishedPropertyNames.cs` / `AllPublishedProperties.cs` | publish boundary | Every published property name, product and diagnostic. |
| `TelemetryLearningGate.cs` | 4/G-Force | Shared "is this frame valid evidence for a cross-frame learner" gate (pit/replay/session-restart). |
| `AccelerationUnits.cs` | 2 | m/s² <-> G conversion, used once at the SimHub-facing edge. |

### `QAdvanceFeedback\Core\RawCalculator\` (Layer 3 concrete engine)

| File | Purpose |
|---|---|
| `RawCalculatorEngine.cs` | The `ILegacyWheelLockSlipEngine` implementation - dispatches per frame, owns every stateful learner/filter. |
| `BrakeSpeedSlipModel.cs` | Pedal+speed+RPM-derived per-wheel Lock/Slip model (the branch used when no wheel-level telemetry exists). |
| `BrakingVsSpeedModel.cs` | Car-level pedal+speed-only Lock/Slip model, plus the low-speed fix. |
| `DispatchBranchFormulas.cs` | The remaining per-branch formulas (wheel rotation, wheel speed, precalibrated slip, learned distributions, wheel-speed delta). |
| `WheelRotationLockFilter.cs` | Per-wheel EMA-smoothed lock estimate from wheel rotation rate vs. ground speed. |
| `StreamingPercentileLearner.cs` | The concrete `IValueDistributionLearner` - a bucketed running histogram (mean + nearest-rank percentile). |

### `QAdvanceFeedback\Core\Normalized\` (Layer 4)

| File | Purpose |
|---|---|
| `NormalizedWheelLockSlipEngine.cs` / `NormalizedWheelLockSlipResult.cs` | The Layer 4 engine and its published result shape. |
| `GripLearner.cs` / `KeyedGripLearner.cs` / `GripLearnerKeyMigration.cs` | The car-relative learned-peak reference, keyed per game+car+source(+surface), with migration for older persisted key shapes. |
| `KeyedScaleLearner.cs` | Per-source scale calibration, anchored to a shared physical reference. |
| `SourceIdentity.cs` | Computes a stable composite key from a channel's four Source/ScriptType fields. |
| `SurfaceLooseFraction.cs` | Continuous sealed/loose surface blend weight. |
| `LongitudinalDirectionResolver.cs` | Resolves Slowing/SpeedingUp/Unknown from differentiated ground speed. |
| `AchievedMotion.cs` | Degradation-tier G-magnitude resolution used by diagnostics. |

### `QAdvanceFeedback\Core\Projection\` (Layer 5)

| File | Purpose |
|---|---|
| `ProjectedWheelLockSlipEngine.cs` / `ProjectedWheelLockSlipResult.cs` | The Layer 5 engine and result shape. |
| `OutputProjector.cs` / `MonotoneCubicCurve.cs` / `PiecewiseCurve.cs` / `ProjectorSettings.cs` / `ProjectorAnchorEditor.cs` | The driver-editable curve and its settings/UI-editing helper. |
| `PulseGenerator.cs` / `PulseSettings.cs` | The optional pulse-at-maximum stage. |

### `QAdvanceFeedback\Core\GForce\`

| File | Purpose |
|---|---|
| `GForceEngine.cs` / `GForceOutput.cs` / `GForcePublishedNames.cs` | The washout-style G-force engine and its published 8-channel output. |
| `GForceMaxLearner.cs` | Per-game/per-car learned acceleration/braking maxima with outlier rejection. |
| `GForceShake.cs` | The "Integrate Wheel Lock and Slip" shake modulation. |

### `QAdvanceFeedback\Core\MotorsExport\`

| File | Purpose |
|---|---|
| `MotorsExportPropertyNames.cs` | SimHub's own ShakeIt Motors export property-name shape (must match SimHub's real API - see the clean-room restructure report's ShakeIt-purge section). |
| `MotorsExportAvailabilityResolver.cs` | Pure "are all four wheels' exported properties usable right now" check. |

### `QAdvanceFeedback\Core\Localization\`

| File | Purpose |
|---|---|
| `Strings.cs` / `StringTableEn.cs` / `StringTableZhHans.cs` | The settings UI's own string table (English/Simplified Chinese). |

### `QAdvanceFeedback\Core\Runtime\`

| File | Purpose |
|---|---|
| `RuntimeDocument.cs` / `RuntimeCache.cs` | The persisted-learned-state document shape and its in-memory dirty-tracked cache. |

### `QAdvanceFeedback\Settings\`

| File | Purpose |
|---|---|
| `QAdvanceFeedbackSettings.cs` | The root settings object (Lock/Slip/GForce/General). |
| `WheelChannelSettings.cs` | One channel's (Lock or Slip) sources, aggregation weights, thresholds, curve, pulse. |
| `GForceSettings.cs` | G-Force tab's settings + learned-maxima import/export. |
| `GeneralSettings.cs` | Diagnostics/CSV-export toggles. |
| `SourceMode.cs` / `ScriptType.cs` / `SourceButtonMode.cs` | Small enums backing the Sources section. |
| `DefaultWheelSources.cs` | Builds the shipped default Manual-mode source text (a plain reference to Layer 3's own Raw property). |
| `ApplyDirtyState.cs` | Tracks whether the settings UI has unsaved edits, for the Apply button's enabled state. |
| `SettingsControl.xaml` / `SettingsControl.xaml.cs` | The one WPF settings control (four tabs). |

## Settings screenshot capture rule (standing rule)

`docs\images\settings-*.png` (linked from both READMEs' "Screenshots" section) are rendered by a
throwaway, out-of-repo WPF harness (not part of this solution/tests) that loads the built
`QAdvanceFeedback.dll`, instantiates `Settings\SettingsControl.xaml(.cs)` standalone, and renders it
to PNG per tab. The Apply/Restore button row is a `DockPanel.Dock="Bottom"` sibling of `MainTabs` in
`SettingsControl.xaml` - it sits OUTSIDE the `TabControl`, so a per-tab capture never includes it no
matter which tab is selected.

The capture rule, by tab (apply this to every future regeneration without needing to be told again):

- **Wheel Lock, Wheel Slip, G-Force** - these three are tall. Capture ONLY the selected `TabItem`'s
  content (its `ScrollViewer`'s content element), excluding the tab strip above and the button row
  below, so the whole tab's settings fit in one image with nothing clipped.
- **General** - short enough that nothing is lost by including the chrome. Capture the FULL
  `SettingsControl` instead: tab strip, the General tab's content, AND the Apply/Restore row.

In both cases, measure/arrange the render target at its own full natural extent (height =
`PositiveInfinity` on `Measure`, then an explicit `Arrange` at the resulting `DesiredSize`) rather
than accepting whatever height the hosting preview window happens to impose - the ScrollViewer's
viewport clips tall content, and the DockPanel's fill child stretches to fill an oversized host
window, leaving a dead gap above the button row, if you skip the explicit re-Arrange step.

Output filenames (note `settings-gforce.png`, NOT `settings-g-force.png` - the harness derives the
name from the tab header text and needs a rename on the G-Force one to match the README links):
`settings-wheel-lock.png`, `settings-wheel-slip.png`, `settings-gforce.png`, `settings-general.png`.

Full rationale, verification evidence and pixel dimensions from the pass that established this rule:
`docs\screenshot-capture-rule.md`.

## Where "Private" used to be

Everything under `Core\RawCalculator\` plus `SimHubTelemetryAdapter.cs` used to live in a withheld,
gitignored `Private\` folder outside both projects, with a reflection-based factory
(`AlgorithmFactory`/`PrivateTypeResolver`) resolving them at runtime and falling back to inert stubs
(`InertTelemetryAdapter`/`InertLegacyWheelLockSlipEngine`) when absent. That split, and the machinery
behind it, is gone - see `docs\clean-room-restructure-report.md` for the full history and rationale.
