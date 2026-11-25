# Architecture

**English** · [简体中文](architecture.zh-Hans.md)

> ## MAINTENANCE RULE — binding, not a suggestion
>
> **This document MUST be updated whenever any algorithm or mechanism described below changes, and
> whenever the core file structure changes.** The file-by-file map and every algorithm description in
> this document exist to be trusted at face value by the next reader — human or AI agent — without
> them re-deriving the design from the source tree first. If you (a contributor, or an AI agent acting
> on this repository) change what a file does, move a file, add a file, remove a file, or change how an
> algorithm works, **updating this document is part of that change, not a follow-up task that can be
> skipped or deferred.** A pull request/commit that changes behaviour or file structure without a
> matching update here should be treated as incomplete. Before trusting anything below as ground truth,
> verify the file-by-file map against the actual directory listing rather than assuming it is current —
> and if you find it is not, fix it as part of whatever else you are doing, not as an afterthought.

This document describes the layer model behind `QAdvanceFeedback` and maps every implementation
file to the layer it belongs to. It exists so a contributor (or a future version of the author) can
find "which file owns this behaviour" without re-deriving the whole design from the code.

## Subsystem algorithms — quick reference

This table mirrors the one in the project [`README.md`](../README.md#4-technical-details). Each
subsystem links to its own "how it works and why" section further down this document.

| Subsystem | Core algorithms / mechanisms | What it's for |
|---|---|---|
| [**Wheel Lock Raw / Wheel Slip Raw**](#wheel-lock-raw--wheel-slip-raw-how-it-works-and-why) | Reproduces SimHub's own legacy-iRacing RPM/speed-derived lock and slip formula exactly, dispatched per title across several branch-specific models selected by capability flags, then combined per wheel-group by a Max/Min axle blend plus a front/rear weighted blend. | The faithful, unnormalised reproduction of SimHub's own well-known algorithm — the common reference point everything else in this plugin builds on. |
| [**Wheel Lock/Slip Normalizer**](#wheel-lockslip-normalizer-how-it-works-and-why) | Rescales Raw's per-wheel shape against a per-(game, car, source) learned physical-grip reference (a deliberately slow-converging EMA), cross-calibrated per source via a scale learner anchored to rare "at the physical limit" moments, blended between live and persisted evidence by a dispersion-weighted cold/warm mechanism. | Makes "80" mean the same thing — "at the measured grip limit" — in every car, instead of a number whose meaning drifts with how grippy that car happens to be. |
| [**Wheel Lock/Slip Projector**](#wheel-lockslip-projector-how-it-works-and-why) | Pushes the normalized 0–100 value through a driver-editable five-anchor curve, smoothed with monotone cubic interpolation, plus an optional pulse-at-maximum stage. | Turns "how severe is this, numerically" into "exactly how this should feel" — the property tier meant to be bound to a shaker. |
| [**G-Force**](#g-force-how-it-works-and-why) | A washout-style split between a sustained G level and a rate-driven transient, mapped onto a 3-stage pad chain via partition-of-unity piecewise-linear functions; per-game/per-car maxima learned via a trimmed-pool robust estimator over a real-time rolling window; an optional wheel lock/slip/angle shake superimposed on top, spread across the pads by one of four selectable modes and shaped by one of three selectable feelings. | Gives a seat pad a continuous, directional sense of braking/accelerating/cornering load, independent of and complementary to the wheel channels. |
| [**Slip Angle**](#slip-angle-how-it-works-and-why) | Native per-wheel tyre slip angle from a per-game raw-data table (auto-promoted, unit auto-detected by episode voting), else a body-slip fallback; one combined Raw.All with front/rear equalisation; a per-(game, car, source, axle, surface) grip-curve learner whose Max is taught only by corroborated traction crossings, guarded against crashes; three surface buckets; borrowing tiers; a monotone 15/30/60/80/100 normalizer (15 at the derived Start angle); a drift attenuator state machine. | Tells the driver when the tyres enter the ideal slip-angle window and when they pass it, without buzzing through a held drift - and feeds G-Force's shake. |

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

**Slip Angle inputs:** `SlipAngleRawSources.cs` holds the per-game raw-data table (paths, unit, transform),
built ONLY from `QAdvanceFeedback.SlipAngleSource.json` (1.2.1, `SlipAngleSourceFile.cs`; see "Sources" in the
Slip Angle section), and `SlipAngleRawSourceReader` reads it through a value-lookup delegate, so the
table itself is SimHub-free and link-compiled into the tests. `SimHubTelemetryAdapter` adds the body
inputs Slip Angle needs to `ITelemetryFrame` (forward local velocity, yaw rate, `IsAirborne` from
`IsFlying`, `WheelsOnKerbs`, vertical G, a surface-grip hint).

### Layer 3 - Raw calculator

**What it does:** `QAdvanceFeedback.Core.RawCalculator` - turns one telemetry sample into the
published `WheelLock.Raw.*`/`WheelSlip.Raw.*` properties. `WheelSlipBranchSelector` (in `Core`,
not `RawCalculator`, since it is pure boolean priority over public capability flags and reveals no
formula) picks which signal shape a title supports; `RawCalculatorEngine` dispatches to the matching
formula and owns every stateful piece. Unnormalised by design - a reading of "40" here means a
different thing in different cars; that is fixed one layer up.

**Fidelity contract (1.0.7.0):** this layer's job is to reproduce what SimHub's ShakeIt Motors plugin
publishes, so that a driver switching between the two feels the same thing. Since 1.0.7.0 the
calibration machinery is a faithful port of SimHub's own rather than a work-alike - see
`Core\RawCalculator\Calibration\`. Concretely: the histogram, its adaptive bucket ladder, the
500-positive-sample percentile gate, the `Math.Max(1.0, Max * 0.9) * pct / 100` pre-maturity fallback,
the `track;car;metric` key, the 7000-point feed cap, and the fixed 0.25 blend against a shipped preset
are all SimHub's, verified against the shipped `SimHub.Plugins.dll`. **Two deliberate deviations
remain, both documented at their call site:** the smoothing filters are dt-corrected (SimHub applies a
fixed per-frame rate, so its time constant varies with a title's telemetry rate - ours is identical at
60Hz and frame-rate independent elsewhere), and the legacy-vs-non-legacy sub-choice of the last branch
is not exposed by any capability, so the legacy variant is always assumed.

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

**Slip Angle** is a sibling stack, not a third channel inside this engine: `Core\SlipAngle\`
holds its Raw calculator (Layer 3 role), its gate, its window learner, normalizer and surface classifier
(Layer 4 role) and the per-frame processor that strings them together. It shares four cores with
Lock/Slip that live here in `Core\Normalized\` - `CrossingGate`, `CorroboratedLevelEstimator`,
`ColdStartTierResolver` and `ImpactGuard` - see "Shared cores" below.

### Layer 5 - Projected

**What it does:** `QAdvanceFeedback.Core.Projection` - pushes Layer 4's output through a driver-
editable monotone curve (`MonotoneCubicCurve`/`OutputProjector`) and an optional pulse-at-maximum
stage (`PulseGenerator`). This is what a driver should bind a shaker to.

**Must NOT depend on:** SimHub types. May depend on Layer 4's output shape.

Slip Angle's Layer 5 is `SlipAngleProjectedEngine` (the same `OutputProjector`/`PulseGenerator` with
the `ProjectionChannel.SlipAngle` preset, `ColdStartScale`) plus the `DriftAttenuator` state machine.

### G-Force

**What it does:** `QAdvanceFeedback.Core.GForce` - an independent channel set (not derived from
Layers 3-5 at all) modelling a washout-style split between a sustained level and a rate-driven
transient, plus per-game/per-car learned maxima (`GForceMaxLearner`).

**Must NOT depend on:** Layers 3/4/5 or SimHub types. (The Wheel Lock/Slip "shake" integration reads
Layer 5's output as an input to G-Force's own amplitude, which is the one deliberate exception -
see `GForceEngine.Compute`'s own remarks. Since the Slip Angle work the same exception covers the final projected
Slip Angle, read only when "Integrate Slip Angle" is on.)

### Settings / persistence

**What it does:** `QAdvanceFeedback.Settings` (settings POCOs + the WPF settings UI) and
`ConfigStore`/`RuntimeStore`/`Core.Runtime` (JSON persistence for configuration and learned state).
Reads/writes plain doubles, enums and strings - never a live SimHub reference baked into a
persisted object.

**Must NOT depend on:** SimHub types for anything that needs to be unit-tested. The WPF views are the
one part that necessarily does; since the full-MVVM page (2026-11-20) they hold no rules at all - every
rule of the settings page lives in WPF-free view models under `Ui\ViewModels` (net8-tested), and
`MvvmRuleTests` keeps the views wiring-only (see "UI: MVVM (standing rule)").

## Algorithm details — how each subsystem works and why

### Wheel Lock Raw / Wheel Slip Raw: how it works and why

`RawCalculatorEngine` (Layer 3) is the one place this plugin reproduces SimHub's own legacy-iRacing
wheel lock/slip formula — studied via decompilation of the shipped `SimHub.Plugins.dll` so the
arithmetic matches exactly, not reverse-engineered from guesswork. A single title does not always
expose the same shape of wheel telemetry (some expose real per-wheel rotation rate, some only
pedal/speed/RPM), so `WheelSlipBranchSelector` picks, per frame, which of several branch-specific
formulas in `DispatchBranchFormulas.cs`/`BrakeSpeedSlipModel.cs`/`BrakingVsSpeedModel.cs`/
`WheelRotationLockFilter.cs` applies — purely a boolean priority over the capability flags
`RawWheelTelemetryBuilder` captured for that title, never a hidden formula choice. This selection
exists so a title with rich per-wheel telemetry gets a more precise per-wheel reading, while a title
with only pedal/speed/RPM still gets a usable, car-level approximation, rather than the plugin
requiring one specific telemetry shape from every game.

Both channels gate on pedal position before the underlying algorithm engages at all (`LegacyThresholds`,
owner-configurable, deliberately deviating from SimHub's own hard-coded values) — Wheel Lock triggers
above a brake-pedal threshold; Wheel Slip checks a (disabled-by-default) brake threshold first, then a
throttle threshold, mirroring SimHub's own undivided algorithm which does not distinguish Lock from Slip
internally.

Once each of the four wheels has a 0–100 reading, `Aggregator`/`AggregationWeights` combine them into
`Front`/`Rear`/`Left`/`Right`/`All` with a physically-motivated, two-stage weighted blend rather than a
symmetric average — because weight transfer is the dominant real effect a wheel-lock/slip cue should
reflect: under braking, load shifts forward, so the front wheels carry the grip and matter most; under
power, the driven wheels are the ones that spin.

- **Axle blend:** `Front = Max(FL,FR)×WMax + Min(FL,FR)×WMin` (same shape for `Rear`) — order-independent;
  it doesn't matter which physical wheel on the axle is the stronger one.
- **Side/car blend:** `Left = FL×WFront + RL×WRear`, `Right = FR×WFront + RR×WRear`,
  `All = Front×WFront + Rear×WRear` — order-dependent; front is always front.
- **Wheel Slip only, by default:** a floor (`result = Max(result, Max(participating wheels)×SlipFloorFactor)`)
  so a single strongly-spinning wheel is never averaged away to nothing.

Both blend stages are simple weighted sums, which keeps the whole pipeline continuous end-to-end — a
bare `Math.Max` would also avoid a value jump, but produces a much larger *slope* discontinuity (a
felt "click") at the crossover than a weighted blend does. All five weights are independently
configurable per channel and are deliberately **not** forced to sum to 1 — a driver who wants an
amplified or attenuated combined reading gets exactly that, not a silently "corrected" one. Full
derivation and the continuity proof: `docs/aggregation-report.md`.

### Wheel Lock/Slip Normalizer: how it works and why

`NormalizedWheelLockSlipEngine` (Layer 4) exists because Raw's own 0–100 reading is unnormalised by
design — "40" means something different in a car that only ever grips to 1.2g than in one that grips to
4g. Layer 4 fixes this by learning, per **game + car + source**, the physical peak the car actually
reaches, and using that learned peak as the reference a Raw reading is rescaled against.

- **`GripLearner`/`KeyedGripLearner`** hold that learned physical-grip reference as a deliberately
  slow-converging EMA. The slow convergence is load-bearing, not an oversight: a fast learner would
  treat a single spike (a collision, a brief lock-up) as evidence of the car's real limit; the pinned
  regression test `A_cold_start_never_publishes_higher_than_the_source_across_a_synthetic_braking_event`
  exists specifically to guard this — a faster-converging estimator was evaluated (a
  `RobustBandEstimator`-backed swap, see `docs/robust-auto-gforce-report.md` §3 and
  `docs/cold-start-convergence-report.md`) and rejected for this exact reason: it converges to a
  constant, non-limit signal fast enough to make ordinary, non-limit driving misfire as "at the
  physical limit."
- **`KeyedScaleLearner`** cross-calibrates per source (since different source modes/expressions can
  report on different native scales) anchored only to rare, independently-detected "at the physical
  limit" moments — not a raw noisy stream — which is part of why it remains outlier-resistant without
  needing the robust estimator either. `CanonicalAtLimitAnchor` (the canonical value a physical-limit
  reading rescales to) is **80** (`docs/anchor-rescale-report.md` — rescaled from 75, chosen to
  coincide exactly with the Projector's own top ("Max Grip") curve anchor input). That report also
  fixed a real bug in the primary tier's own confidence ramp: `ColdWarmBlend.ConcaveHotWeight` is a
  product of a count term (which reaches exactly 1.0 at `CalibrationConfidenceScaleSamples`) and a
  dispersion term (`DispersionQuality`, strictly below 1.0 for any nonzero coefficient of variation —
  i.e. any real driving session), so the product never actually reached full trust no matter how much
  MORE evidence accumulated, permanently leaving a genuinely-at-the-limit reading off-anchor for any
  realistically-noisy source. `KeyedScaleLearner` now floors the primary tier's own weight to 1.0 once
  `primary.Count >= CalibrationConfidenceScaleSamples`, matching that constant's own documented
  contract, WITHOUT changing `ColdWarmBlend` itself (so `GripLearner`'s own use of it, and every one of
  its already-tuned thresholds, is unaffected).
- **`ColdWarmBlend`** decides, every frame, how much to trust this session's own live evidence versus a
  persisted prior-session value for the same key, weighted by the live evidence's own **dispersion**
  (coefficient of variation), not merely its sample count — a noisy session converges toward "trust the
  persisted/cold value" even if it has accumulated many samples, while a tight, repeatable session earns
  trust quickly even from a handful of samples. Both factors are smooth/saturating, so there is no
  sample-count or dispersion threshold at which the live blend jumps.
- **`SurfaceLooseFraction`** blends the learned reference across sealed/loose surface conditions
  continuously rather than switching between two fixed references.
- **What may teach SMax: the crossing gate, and the 95 hard limit (2026-11-19).** At-limit evidence
  reaches `ObserveAtPhysicalLimit` only through the shared `CrossingGate`. A crossing is the source
  rising while G falls. What it teaches is the snapshot at the grip peak of that one rise, and the
  walk-back to that peak only steps onto frames that themselves rose from their predecessor, so the
  flat frame a rise started from is never the snapshot. Every frame of the 1 s hold window teaches that
  snapshot and never its own live value; a crossing with an unusable snapshot is rejected rather than
  falling back to the live runaway. Every taught basis, every learned / published / persisted ceiling,
  every imported value and every typed Manual SMax or Custom reference is at most
  **95** (`KeyedScaleLearner.MaxLearnedCeiling`): a source at 100 is a locked or spinning wheel, never
  the grip limit. The defect this closed (SMax 100/90/70 from the post-crossing runaway) and the corpus
  numbers are in `docs/slip-smax-crossing-gate-design.md` §10.

Measured directly against seven real telemetry logs (`docs/cold-start-convergence-report.md`), the
current ramp already converges as fast as the data safely supports — shortening it measurably trades
away margin against transient over-reporting, which is the risk this design is built to avoid.

### Key Data Points: manual SMax/S90/S75, and when they apply (1.0.7.0)

`Settings\KeyDataPointSettings.cs`, `Core\Normalized\ManualOverrideGate.cs`.

**Learning is never conditional.** `AutoGenerate` selects which numbers reach the output; it does not
gate observation. `ObserveAtPhysicalLimit`/`ObserveGeneral` and the anchor learner's corner buffering
all run before the manual block in `ComputeChannel`, unconditionally. Three things follow: the
`[Learned Value: xx.x]` hint is meaningful in manual mode, toggling back to Auto is instant, and a
manual value can never corrupt what the learner knows.

**Values are keyed by SLOT = (mode, game, source).**

```
global mode   ->  "global|src:<sourceIdentity>"
per-game mode ->  "game:<gameId>|src:<sourceIdentity>"
```

The source is always part of the key because a number that suits the ShakeIt source does not suit our own
Raw — the two have different scales. The game is part of it only in per-game mode; omitting it is what
"global" means. One dictionary holds both namespaces, so global -> per-game -> global returns the
original global numbers untouched.

**The gate.** `ManualOverrideGate` withholds a manual value until that context has BOTH finished cold
start (`KeyedScaleLearner.CeilingHandoverConfidence` >= 0.95) AND accumulated 30 seconds of driving.
Only frames where the car is moving are credited, and any single frame longer than 0.5s is clamped, so
a pause, a breakpoint or an alt-tab cannot satisfy it.

**The one-time seed lives in the plugin's frame loop**, not in the settings page - it has to happen
whether or not the page is open. When a slot first becomes eligible, the learned values are written in
and `ConfigStore.Save` is called directly (not `ApplySettings`, which also rebuilds the projected engine
- wrong thing to do mid-frame). Because the latch is per slot, a never-played game or a newly selected
source seeds again on its own. The plugin bumps a revision counter (`KeyDataRevision`) and raises
`SettingsPageStateChanged`, so the settings page (and any ShakeIt panel) reloads the seeded slot.

**What the page and the panels show - one display rule, also after a restart (2026-11-19).**
`KeyDataPanelModel.LearnedKeyData` is the only source of "learned" key data for the settings page (its
`KeyDataPageViewModel` reads it through the host on every `PluginStateChanged`, once a second while
driving), both ShakeIt panels and the one-time seed: the ceiling the engine itself divides by,
`KeyedScaleLearner.PublishedCeiling`, for the EXACT persist key that `KeyedGripLearner.MakeKey` builds
(game, car, source, surface). Lock's S90/S75 come from its learned physical anchor ratios (seeded at
0.90/0.70); Slip's are 0.90/0.70 of SMax. With SimHub on the menu after a restart there is no game
running, so the plugin uses the last (game, car) it showed key data for
(`RuntimeDocument.LastKeyDataGame/Car`). If `QAdvanceFeedback.Parameters.json` holds that exact key, its
persisted SMax/S90/S75 are shown; if it does not, the cold-start default is shown. Never the value of
another game or car. (The former "best value for this source across every game and car" lookup was
removed for exactly that reason.) The learned anchor ratios themselves now persist: the runtime cache
used to copy only the legacy anchor fields, so S90/S75 fell back to 0.90/0.70 after every restart.
The page's Lock Auto S90/S75 come from the same rule (physical ratios) as the panel's, and what the page
shows in Manual with nothing stored is the source's default (`KeyDataPanelModel.Resolve`), as on the
panel.

**Slip Angle follows the same rule (2026-11-20, `docs\slip-angle-design.md` §9.4).**
`Core\SlipAngle\SlipAngleKeyDataModel` is the Slip Angle twin: `LearnedWindow` = the exact (game, car,
source identity for the current identity tier, surface) key's published window capped for the source
(20° native / Custom, 30° car angle), null when the learner holds no such key - never a borrowed window;
`Shown` = Auto: learned else default, Manual: stored slot else default; the one-time Manual seed writes
exactly the learned window shown (`SlipAngleDisplayRuleKeyData`). The tab, the ShakeIt panel and the seed
all use it. For the menu after a restart the plugin persists the last Slip Angle context in
`RuntimeDocument` - `LastSlipAngleGame`, `LastSlipAngleCar`, `LastSlipAngleTier` (the identity tier's
`SourceNativeness` name), next to `LastKeyDataGame/Car` - and restores it at Init after importing the
learners (`SlipAngleKeyDataContext`), so the exact key is formed with no game running. Older files have
none of these fields: the default is shown until the first drive.

**Defaults are per source type**, resolved through `KnownSourceColdStartReference.Classify`: Lock
85/75/60 and Slip 75 for our Raw and for the ShakeIt source (looked up by source TYPE: a ShakeIt
identity carrying an algorithm fingerprint gets the same shipped defaults). An UNKNOWN source (a script, an NCalc
expression) deliberately gets no default at all - there is no honest guess for a signal whose scale has
never been measured, so such a channel keeps publishing learned values until a slot is seeded.

**Max-Grip-Only / Best Point Only** hides the two lower anchors and keeps them derived at 0.90/0.70 of
the top one, so switching to the three-point mapping never finds them stale or out of order. Slip
derives them always - it has no native 90%/75% grip measurement - which is why it ships Best Point Only.

**The manual-mode reset** (`KeyDataPageViewModel.ResetCommand` on the page) writes the shipped defaults for a source
`KnownSourceColdStartReference.Classify` recognises, marking the slot seeded so the one-time learned
seed cannot immediately overwrite what the driver just asked for. For an UNKNOWN source there is no
default to write, so it calls `KeyDataPointSettings.ClearSlot` instead - values and seeded latch both -
which re-arms the one-time seed and leaves the boxes empty until real evidence arrives. Persists
immediately, like the other reset actions on the page.

**Slip is no longer forced to Max-Grip-Only under Auto** (owner's change of mind, v1.0.8): Auto derives
S90/S75 from the learned SMax by a fixed percentage, so the three-point mapping is honest on either
channel, the selector is enabled in both modes, and both channels ship the three-point mapping. (The
former `EnforceSlipPatternForAutoMode` had become a no-op and went with the old page.)

**The two curve graphs** (`CurveEditorViewModel` supplies the data - curve, markers, source graph
points; `Ui\Views\NormalizedCurveGraph` / `GraphFurniture` draw them) follow the page's draft values, so
they track unapplied edits. The left one is normalized -> projected, with dashed markers at the canonical normalized positions of the key
data points (S75 -> 30, S90 -> 60, SMax -> 80 - the four-range curve's own knot table), or SMax alone
under Max-Grip-Only. The right one is source -> projected end to end; it has no closed form because the
key data points and the projector compose, so it is sampled every 5 and joined. Both share the same grid:
a 25%-black rule every 20% of height, the 0% rule solid black as a baseline, and 3px ticks every 20% of
width.

**Restore never discards them.** `QAdvanceFeedbackSettings.RestoreDefaults` carries both channels'
`KeyDataPoints` (all slots, plus the Auto/Per-Game switches) across the reset; the three channel-level
source resets only ever touch source fields.

### Wheel Lock/Slip sources: ShakeIt Built-in, the per-channel Raw fallback, internal learning (2026-11-19)

Design: `docs/shakeit-source-design.md` (ShakeIt source) and `docs/internal-source-learning-design.md`;
summary of what shipped: `docs/shakeit-source-report.md`.

**ShakeIt Built-in reads ShakeIt's algorithm only through our native effect.** There is no export-property
path any more (the ShakeIt Motors export profile, `MotorsExportAvailabilityProvider` and
`MotorsExportAvailabilityResolver` are gone; `docs/shakeit-export-guide.md` is retired). Every
QAdvanceFeedback ShakeIt effect (WheelLock, WheelSlip, the four G-Force effects, Clutch Bite, Slip Angle)
calls `ShakeItBuiltInTap.Sample` when ShakeIt processes it. The tap stamps a heartbeat on every call (in
game or not) and, in game, evaluates hidden copies of ShakeIt's `WheelsLockContainer` /
`WheelsSlipContainer` with the game data ShakeIt hands it, publishing per-channel values plus ShakeIt's
own `GetIsSupportedOnCurrentGame(caps)` answer for lock and for slip.

**Detection** (`Core\ShakeItEffects\ShakeItSourceAvailability.cs`, `Integration\ShakeIt\ShakeItProfileInspector.cs`):
- *Set up* = at least one of our effects is effectively enabled (its own switch and every parent group)
  in the current profile of either ShakeIt plugin (Bass Shakers, Motors), read through SimHub's public API
  by a 1 Hz UI-thread `DispatcherTimer` (each plugin guarded on its own; health `ShakeItProfile`), AND
  the heartbeat is ≤ 2 s old.
- *Driving* = game running, not paused, not in a menu, and ShakeIt's own FeedbackData gate.
- One state machine per channel: **NotSetUp / SetUpIdle / Supported / NotSupported**. Supported needs
  fresh tap values while driving. NotSupported comes from ShakeIt reporting the game unsupported (sticky
  until a game switch) or from 3 s of continuous stale driving (recovers on the first fresh values —
  ruling R41). A 0.5 s-debounced pair (`ShakeItSourceStateBroadcaster`) is published as
  `QAdvanceFeedback.ShakeItSourceState` and raises `SettingField.SourceAvailability`.

**Per-channel resolution** (`Core\ShakeItEffects\ShakeItSourceResolution.cs`, from the live state each
frame): Supported + fresh tap → the tap's values under the **canonical ShakeIt identity**
(`ShakeItSourceResolution.ShakeItIdentity`: the legacy export property names as four Plain sources,
byte-identical to the pre-2026-11-19 key, plus the algorithm fingerprint). Anything else (not set up, idle
in game, not supported, stale) → Raw's values under **`RawSourceFallback.RawIdentity`** for all four wheels
of that channel — never per wheel, and never Raw values under the ShakeIt key. Viper's blocked route is
the same Raw route. `QAdvanceFeedback.ShakeItSourceBlocked(channel, isLock)` (published state; true only
on a NotSetUp / NotSupported verdict, never while idle) is the page's shadow input, alongside
`ViperSourceBlocked`. A preset source (ShakeIt or Viper) applied while its channel is shadowed stores its
own preset rows (`WheelChannelSettings.KeepPresetSourcesWhileShadowed`).

**UI.** The ShakeIt option is listed when the state is not NotSetUp, or it is the shown or saved
source (marked "ShakeIt Built-in (not set up)" while not set up; a hidden option is also disabled).
Notes: "Not set up — add a QAdvanceFeedback effect to the active ShakeIt profile. Using Raw." and "Not
supported in this game — using Raw.", only while ShakeIt is the shown source. Rules in
`Ui\ViewModels\ShakeItSourceViewModels.cs` (`ShakeItSourceUi`, `ShakeItSourceStatusViewModel`); the
panels read the state through the optional host capability `IShakeItSourceStateHost`; the page re-samples
on the Runtime `SourceAvailability` broadcast, so it follows changes at the menu.

**ShakeIt algorithm block** (`Settings\ShakeItAlgorithmSettings.cs` on `WheelChannelSettings.ShakeItAlgorithm`,
rules in `Core\ShakeItEffects\ShakeItAlgorithm.cs`, VM `ShakeItAlgorithmViewModel`; shown only while the
source is ShakeIt Built-in, on the tab and the panel, synced through five `SettingsHub` fields):
- Lock: legacy iRacing algorithm (on), ShakeIt lock sensibility (50, 0–100, step 5, snapped everywhere),
  lock-only (on). Slip: legacy iRacing algorithm (on), mute slip while lock is active (off).
- Hidden filters follow our thresholds: Lock brake filter = round(Lock brake threshold); Slip
  UseBrakeFilter = brake threshold < 100 AND throttle threshold > 0; Slip UseThrottleFilter = throttle
  threshold > 0 (at throttle 0 our OR gate is always open, so both filters are off).
- Mute is emulated: all four Slip wheels read 0 on a frame where any wheel of our hidden Lock value is > 0
  (no 100 ms hold).
- Fingerprint appended to the ShakeIt identity: `#alg=legacy:X,sens:N,lockOnly:X` (Lock),
  `#alg=legacy:X,mute:X` (Slip); empty at the defaults. Shipped key-data defaults are looked up by
  source type (`ShakeItAlgorithmFingerprint.BaseIdentity` strips it); manual slots, Auto and learning
  are keyed with it. The data thread builds an immutable `ShakeItAlgorithmSnapshot` each frame (reused
  when unchanged) and hands it to the tap (`ShakeItBuiltInTap.Configure`). This happens every frame
  BEFORE the in-session gate, so a change made at the menu reaches ShakeIt's thread before the first
  in-game frame.
- **Tap snapshot stamping.** The tap stamps each frame's values with the snapshot they were computed under
  (`ShakeItTapValues.Algorithm`). The snapshot holds the algorithm settings and the threshold filters.
  `ShakeItSourceResolution.TapComputedUnder` (a reference compare) is part of "tap fresh". So a tap from
  an older snapshot counts as not fresh, and the channel reads Raw under the Raw key until ShakeIt
  produces values under the new settings. After a pedal-threshold change this lasts a frame or so.
  Old-settings values therefore never reach the new fingerprinted key.

**Internal-source learning rule** (`NormalizedWheelLockSlipEngine`, `docs/internal-source-learning-design.md`):
- Every source learns only under its own key. **Plugin Internal (`PluginInternalSourceIdentity.For(isLock)`,
  equal to `RawSourceFallback.RawIdentity`) learns every frame**, selected or not: when another source is
  active, an internal mirror repeats every learning step for the internal key from Layer 3 Raw with its own
  crossing gate and at-limit basis. When the internal key is the active one it is fed once.
- The "configured source went quiet" relative fallback compares against the internal key. The former
  always-fed hidden key `__layer3_raw_fallback__` is retired; `InternalSourceKeyMigration` merges it into
  the internal key once on load (internal empty → adopt, both present → keep internal, hidden dropped).
- Shared, non-source-keyed state (physical g reference, surface support, Slip Angle surface classifier)
  follows car motion and the internal source, observed once per frame.
- On a change of a channel's active identity the shared crossing gate, the shared at-limit basis and the
  fallback divergence are cleared, and the previous source's Lock-anchor run is closed under its own
  game/car (not when that previous source is the internal key).
- Slip's cross-channel cold-start borrow from Lock pairs only the same source type
  (`CrossChannelSourcePairing`): internal ↔ internal always, ShakeIt ↔ ShakeIt and Viper ↔ Viper by
  type, Custom / mixed none. The pairing inputs are an immutable, Volatile-published snapshot.
- A Custom channel whose four plain rows are our Raw properties (`RawSourceFallback.MatchesRaw`) is Plugin
  Internal. One whose rows are the legacy ShakeIt export names produces the ShakeIt identity string and so
  shares the ShakeIt key. A Custom channel that reproduces any shipped preset never applies the Custom
  cold-start reference. `ApplyCustomColdStart` skips it when `RawSourceFallback.MatchesShippedPreset`
  matches the Raw identity, the canonical ShakeIt identity (fingerprint stripped) or the shipped Viper NCalc
  preset. So a Custom cold-start SMax never reaches the Raw, ShakeIt or Viper key.

### Wheel Lock/Slip Projector: how it works and why

`ProjectedWheelLockSlipEngine` (Layer 5) is the one property tier meant to be bound to hardware. It
exists to separate "how severe is this, numerically" (Layer 4's job) from "exactly how this should
feel" (this layer's job), so a driver can retune the feel without touching the learning underneath it.

- **`OutputProjector`/`MonotoneCubicCurve`** implement a five-anchor curve (Start/Powerful/Ideal/
  Max Grip/End — the first anchor was renamed from "Slightly" in the v1.0.6.9 rework,
  `docs/v1068-rework-report.md`, once the Normalized 30/60 anchors were verified: near-30 now marks the
  start of a POWERFUL brake/throttle application — good enough, but not yet ideal; holding 30–60 gives a
  good result; holding 60–80 gives the ideal result) — each anchor independently editable in both its
  input position and its output strength — smoothed with monotone cubic interpolation specifically so
  the output can never *decrease* as the input rises. A plain (non-monotone) spline can overshoot and
  dip between anchors, which a driver would feel as the shaker easing off at the exact moment things are
  getting worse; a piecewise-linear curve avoids the dip but reads noticeably kinked. Monotone cubic
  interpolation is the mechanism that gets a smooth curve without ever sacrificing the "never eases off
  while getting worse" guarantee. The shipped default anchor positions (30/60/80/100) were verified
  numerically to put "at the limit" near 75–80 and "fully locked/spinning" at exactly 100
  (`docs/refinements-report.md`).
  RESCALED to exactly 80 (`docs/anchor-rescale-report.md`): `KeyedScaleLearner.CanonicalAtLimitAnchor`
  moved from 75.0 to 80.0, made to COINCIDE EXACTLY with this curve's own top ("Max Grip") anchor input
  (already 80 for both channels' shipped Curve preset - see `WheelChannelSettings.CreateLockDefaults`/
  `CreateSlipDefaults`), and a structural cap on the primary tier's own confidence ramp was fixed (see
  `KeyedScaleLearner`'s own remarks) so a genuinely-at-the-limit reading now actually converges to 80,
  not just a constant of that name — that pass also renamed this anchor from "Critical" to "Max Grip"
  throughout the UI to describe what it now means: AT the measured limit, not past it.
- **Per-setpoint flatten ranges (`ProjectorSettings.SlightlyFlattenRange`/`ModerateFlattenRange`/
  `CriticalFlattenRange`, defaults 3/2/2).** Each of the three named anchors gets its own driver-editable
  half-width; `OutputProjector.AcceptSetpointWithFlatten` inserts up to two HIDDEN control points at
  `setpointInput ± range`, with outputs nudged only 20% of the way toward a straight line to that side's
  real neighbouring anchor (`FlattenBleedFraction`) — this is what turns a sharp corner at each anchor
  into a brief, near-flat plateau instead. **A range of 0 omits both hidden points entirely** rather than
  creating them at a zero offset — a zero-offset point is not equivalent, since duplicate/near-duplicate
  x-values perturb the monotone-cubic fit's own computed tangents even though the points coincide with
  the anchor; a regression test asserts the range-0 curve is bit-identical to the pre-flatten-range curve
  at the original 30/60/80 inputs. Each range is independently clamped to at most half the distance to
  whichever real neighbour sits on that side, so two adjacent plateaus can never cross or overlap even at
  extreme settings — at the shipped 62/78 Ideal/Max Grip thresholds (see below) the Ideal-Max Grip gap is
  16, so either range independently clamps at 8 once pushed past that, letting the two plateaus meet
  exactly at the midpoint (70) but never cross. Flattening is skipped entirely under the Linear preset
  (which must stay an exact straight line).
- **Ideal/Max Grip curve-input thresholds moved 60/80 → 62/78** (paired with the flatten ranges of 2
  above) so each plateau's own EDGE — not the anchor itself — lands exactly on the shared 60/80 band
  boundary: `62 - 2 = 60`, `78 + 2 = 80`. This is a projection-layer-only offset; Raw, Normalized, every
  learner and `KeyedScaleLearner.CanonicalAtLimitAnchor` (80) are all untouched — Normalized's own key
  points remain 60 and 80, and the essential coupling test now asserts the top anchor's own **plateau
  edge** (`CriticalInput + CriticalFlattenRange`), not the raw threshold directly, coincides with
  `CanonicalAtLimitAnchor`. The curve-editor labels ("Powerful (30)", "Ideal (60)", "Max Grip (80)") show
  this Normalized band value, not the 62/78 threshold the "raw value" column displays — a deliberately
  static parenthesised number, not generated from the threshold field, so it keeps meaning "this is where
  the plateau reaches the named band" even though the editable threshold sits elsewhere. WheelLock's own
  Max Grip anchor OUTPUT is a separate, independently-configured number from the threshold move described
  here — **as of 1.0.6.0 it is 60, not 80** (see "1.0.6.0 changes" below); the anchor *input* positions
  (62/78) described in this bullet are unaffected either way.
- **Configurable Start/End outputs (`ProjectorSettings.StartOutput`/`EndOutput`, defaults 0/100)**
  replace what used to be hard-fixed values. Both are a CONTINUOUS floor/ceiling, not a step: every input
  at/below `StartInput` reads exactly `StartOutput`, and every input at/above `EndInput` reads exactly
  `EndOutput`. A non-zero `StartOutput` is therefore a permanent baseline hum for the entire time the
  channel is engaged (the pedal trigger threshold still gates engagement itself), not merely a raised
  floor on the ramp. Any conflict between a configured Start/End output and a named anchor's own output
  (e.g. `StartOutput` set above the first anchor, or `EndOutput` set below the last) is resolved by the
  SAME non-decreasing clamp every control point already goes through — never rejected or thrown, and
  documented/tested for all four combinations. The Cold-Start Device-Feel Scale's amplitude divisor
  (`ColdStartScale.ApplyAmplitudeScale`) deliberately stays an absolute 100 regardless of a configured
  `EndOutput` — it measures "how large is this shake" against the device's own absolute 0-100 scale, not
  a driver-capped ceiling.
- **`PulseGenerator`/`PulseSettings`** implement the optional pulse-at-maximum stage — alternating
  between 100 and a configurable minimum instead of holding flat, for a driver who wants a sustained
  lockup/spin to read as more urgent than a static buzz. The 200 ms (5 Hz) minimum half-cycle gap is
  enforced by the plugin itself, not just the settings UI, so a hand-edited config file cannot sneak in
  a faster pulse.

### 1.0.6.0 changes (`docs/release-1060-report.md`)

Version stamped `1.0.6.0` (sorts below the pre-release branches by design — the owner's own explicit
choice, not a downgrade). Full detail and the acceptance-replay evidence live in
`docs/release-1060-report.md`; summarised here per this file's own "must be kept in sync" rule:

- **`NormalizePattern` (`MaxGripOnly`/`Mapping`, `Core/Normalized/NormalizedWheelLockSlipEngine.cs`) —
  Wheel Lock only, no Wheel Slip equivalent.** `Mapping` (the default) is the four-range S75/S90/Max-Grip
  severity formula this plugin has shipped since 1.0.6.8; `MaxGripOnly` discards the four-range curve's
  value for published severity and falls back to plain `calibratedMean`, matching this plugin's original,
  simpler behaviour. Threaded into `ComputeChannel` via a `useFourRangeForSeverity` flag on the Lock call
  site only — under `MaxGripOnly` the four-range curve is still BUILT and S75/S90/SMax still LEARN (all
  three keep being persisted to `QAdvanceFeedback.Parameters.json` either way), only the published
  severity ignores it, and `LockFourRangeCurveActive` reports `false` so a driver/diagnostic can tell
  which formula is actually live. Surfaced in the settings UI as a **Normalize Pattern** dropdown at the
  top of Wheel Lock's own section (now titled **"Output data and shaping"**, renamed from "Output
  shaping"), with the Slightly/Ideal anchor labels and their help text live-switching between "Powerful
  (30)"/"Perfect (60)" (Mapping) and "Slightly (30)"/"Ideal (60)" (Max-Grip Only, restoring this
  plugin's original pre-rework wording verbatim) — the Max Grip (80) label is identical in both modes.
  Wheel Slip keeps a single, fixed "Slightly (30)"/"Ideal (60)"/"Max Grip (80)" label set always (an
  earlier rework pass had incorrectly pointed Slip at the same shared string keys Lock's own Mapping-mode
  rename touched — split into per-channel/per-mode keys here to fix that).
- **Wheel Lock's own Max Grip curve OUTPUT changed 80 → 60** (`ProjectorSettings.CriticalOutput`'s own
  field initialiser and the Lock branch of `ApplyPreset(Curve)`) — the owner's explicit request ("it will
  shake too strong when reaches the best braking force"), now matching the sibling 1.0.6.2 pre-release
  branch's own value. At the time of this change Wheel Slip's own three outputs were unchanged (Slightly
  10 / Ideal 35 / Max Grip 75 — see the follow-up bullet below for a later revision of the Max Grip
  figure). This single change shifts several plateau-edge numbers documented earlier in this section (e.g.
  the Ideal plateau's own upper edge at input 64 moved from 31.25 to 30.75) — see the pinned regression
  test `OutputProjectorTests.Curve_default_plateau_numbers_lock_channel_match_the_measured_report_table`.
- **Wheel Slip's own Max Grip curve OUTPUT further softened 75 → 70** (same field, Slip branch of
  `ApplyPreset(Curve)`) — owner-confirmed after an explicit follow-up question, direct response to the
  owner's in-game report that with the ShakeIt source, Wheel Slip "shakes much harder than using Raw."
  Wheel Slip's other two outputs (Slightly 10 / Ideal 35) and Wheel Lock's own Max Grip output (60, see
  the bullet above) are unaffected — only Slip's own Max Grip ceiling moved. Slip's severity FORMULA
  itself is deliberately untouched (still 1.0.6.3's own, see below) — this is an output-curve ceiling
  change only. See `OutputProjectorTests.Curve_default_plateau_numbers_slip_channel_match_the_measured_report_table`
  and `WheelChannelSettingsTests.Slip_defaults_ship_the_owners_shared_band_boundaries_with_a_gentler_curve`
  for the updated pinned numbers.
- **Slip's published severity reverted to 1.0.6.3's own formula** — a prior pass had let a ΔG-collapse
  term influence Slip's published number; that formula is now unified to a single expression,
  `(lockFourRangeSeverityConfigured ?? calibratedMean) * (1 - fallbackWeight) + (lockFourRangeSeverityFallback
  ?? calibratedRawFallback) * fallbackWeight`, which algebraically reduces to plain `calibratedMean` for
  Slip (the Lock-only fields are always null on that call site) — bit-identical to 1.0.6.3's own Slip
  output. `ComputeDeltaGCollapseSeverity` is still called with its result discarded, keeping that
  diagnostic alive without letting it affect the published number.
- **S75/S90 fallback ratios refined**: `S90FallbackRatioOfSmax = 0.750 * 1.125 = 0.84375`,
  `S75FallbackRatioOfSmax = 0.40`, applied read-time-only via `RatioOfSmaxFallback`/
  `TryBuildLockRangeCurveWithFallback` — never written back into `LockAnchorLearner`'s own persisted
  state. A determinism bug was also fixed here: a key could previously flip between the fallback curve
  and plain `calibratedMean` across repeat queries with no new evidence in between.
- **Overflow audit completed**: `GripLearner.AdaptivePeakState.RaiseHits`/`LowerHits`,
  `LockAnchorLearner`'s accepted/rejected/hit counters, and `StreamingPercentileLearner`'s observation and
  per-bucket counts (fed every telemetry frame at 60 fps — the highest-frequency counter in the plugin)
  all now saturate at a fixed cap (1,000,000) instead of risking a signed-int wraparound over a long
  enough uptime, while the values they feed keep learning past that cap via a decaying-mean formula. A
  genuine remaining gap found during this pass: `OnlineDistributionLearner.MaxSamples` (7000) documented
  itself as enforced "at the call site" but no call site actually checked it — `KeyedScaleLearner`'s own
  `ObserveAtPhysicalLimit`/`ObserveGeneral` fed it unconditionally, so its internal histogram had no real
  ceiling. Fixed by wiring the gate into both call sites.
- **General tab now shows the running assembly's own file version** (read from `FileVersionInfo` —
  never a hand-typed literal; since 2026-11-20 `PluginPageHost.PluginVersion`, shown by
  `GeneralViewModel`), so it can never drift from the DLL actually loaded.
- **`tools/screenshot-harness` had two real bugs fixed**: it computed `docs/images` as the output
  directory but then wrote every PNG to a bin-directory-local `screenshot-out` folder instead, so
  re-running it never actually updated the committed screenshots; and it named the G-Force capture
  `settings-g-force.png`, contradicting this very file's own "Settings screenshot capture rule" below
  (`settings-gforce.png`, no hyphen). Both fixed; screenshots regenerated and hash-verified as part of
  this release.

### G-Force: how it works and why

`GForceEngine` (an independent subsystem, not derived from Layers 3–5) is modelled on classical
washout/motion-cueing rigs: it separates the STEADY level a driver is holding from the MOTION of
getting there, because a rig cueing acceleration needs both — the current g, and how fast it got there
— to feel physically honest.

- **Travel/position model.** Braking and accelerating are modelled as two independent, non-negative
  "travel" signals, each combining a magnitude term (`|G| / maxG`, clamped to [0,1] — the energy
  present) with a rate-of-change term (how fast G is rising or falling) — so a *rising* G pushes the
  felt sensation further along its pad chain than the same *static* G would, and a *falling* G recedes
  it back proportionally to how fast it's dropping. Each travel signal maps onto its own 3-stage pad
  chain (braking: Back Low → Bottom Rear → Bottom Front; accelerating: Bottom Rear → Back Low → Back
  Top) via piecewise-linear "hat" functions that form a partition of unity (sum to exactly 1 at every
  point), which is what keeps the sweep continuous — no step change on any pad as travel moves from 0
  to 1. The magnitude term (not the travel/position term) is what scales the total output energy, so a
  genuine 0g frame is exactly 0 on every pad regardless of position.
- **`GForceMaxLearner`/`RobustBandEstimator`** learn each game+car's own maximum g (for AUTO mode) by
  sorting recent samples descending, excluding the top ~5% as likely outliers, taking a band of what
  remains sized to roughly 10% of the remainder (with a guaranteed minimum pool width of 10), and
  blending that pool's own max and mean (75%/25%) into the estimate — "very close to the largest value
  in the pool, but still influenced by the average." This runs over a genuine 2-minute real-time
  rolling window with no minimum-sample gate (`TryEstimate` only fails at zero samples), and a candidate
  above the current max is only promoted once a second, similar reading confirms it, so a single
  collision spike is never mistaken for the car's genuine peak. Full specification and the measured
  case for why this estimator suits G-force (but was evaluated and declined for the Normalizer's own
  learners, for reasons specific to each) is in `docs/robust-auto-gforce-report.md`.
- **`GForceShake`** optionally superimposes an alternating left/right shake on top of the plain G-force
  level whenever the Wheel Lock/Slip Projected value for that side is non-zero — the shake's *width*
  grows with how hard the wheel is currently locking/slipping, while its *centre* stays anchored to the
  plain G-force value, so enabling the feature never causes a jump. This is the one deliberate exception
  to "G-Force does not depend on Layers 3–5" — see `GForceEngine.Compute`'s own remarks.

  **Band placement.** `band = level × contribution`, `half = band / 2`, and the wave is centred on the
  level. A band that would leave 0–100 is **shifted, not squashed**, by the single clamp
  `effectiveCentre = Clamp(centre, half, 100 − half)` — one expression covering all three cases (fits;
  top overflows; bottom underflows), so the band's *width* is always preserved. Only a band wider than
  the whole range (`half > 50`) cannot be placed by any shift; there the centre pins to 50 and the
  output is clamped instead.

  **One drive, one oscillator — both load-bearing (1.0.8).** The drive is always
  `Math.Max(lockContribution, slipContribution)`, computed once *before* any mode branching, and there
  is exactly one `_shakePhaseSeconds` shared by all eight pads and both wheel signals. Lock-driven and
  slip-driven shaking are therefore in phase **by construction** — when one is at its maximum so is the
  other — and a handover between them changes only the band's width, never the wave's position. No mode
  routes lock to one set of pads and slip to another; the apparent routing is emergent, since
  `band = level × contribution` leaves a channel at level 0 silent. `GForceEngineShakeModeTests` guards
  both properties, and both guards were mutation-checked (`Max`→`Min`: 12 failures; a 21 ms per-channel
  phase offset: 2 failures, including the pads-swing-together guard).

  **`ShakeApplyMode` (1.0.8)** selects what each channel's band is computed *from* — never how lock and
  slip combine:

  | Mode | Band source | Travel | Lateral bias | Default scale / trigger |
  | --- | --- | --- | --- | --- |
  | `HigherOfGForceOrLockSlip` (**shipped default**, listed first) | that channel's own level, with the CEILING raised by the wheel | centred on the level | applied | 1.2 / 60 |
  | `PerChannel` (all pre-1.0.8 behaviour) | that channel's own level | centred on the level | applied | 1.5 / 5 |
  | `AllChannelsGForce` | the **active chain's terminal** level, applied to all eight | centred on the level | applied | 1.3 / 60 |
  | `AllChannelsLockSlip` | `100 × contribution` — G-force ignored entirely | **from zero** | **not** applied | 1.0 / 30 |

  **The trigger split** (5 vs 60 vs 30, revised in v1.0.9) follows from what a low wheel value DOES in
  each mode. On a per-channel band a trace of lock is a trace of extra width on an animation that was
  already there, so it can be admitted early. On an all-channels band it is a floor under all eight pads
  at once. And in the combined mode it lifts every channel's CEILING — a different route to the same
  permanent background buzz the threshold exists to stop, which is why seat time moved that mode from 5
  to 60 rather than leaving it with per-channel's value.

  `AllChannelsGForce` picks its terminal by **direction** (braking → BottomFront, accelerating →
  BackTop), not by whichever wheel signal is larger: slip while braking would otherwise select BackTop,
  whose level is 0 under braking, silencing the shake exactly when it was wanted. The terminal channel's
  output is consequently identical to `PerChannel`'s. `AllChannelsLockSlip` drops the lateral bias
  because the mode's whole point is an output depending on nothing but lock/slip.

  **`HigherOfGForceOrLockSlip` is PerChannel with a raised ceiling (REDEFINED 2026-09-07).** It runs
  the PerChannel path outright — same centre, same band, same "both pads follow the stronger side"
  lateral rule — and changes exactly one thing:

  ```
  low  = PerChannel's low, UNCHANGED
  high = Min(100, Max(PerChannel's high, 100 × contribution))
  ```

  So the minimum is always identical to PerChannel's and only the ceiling is lifted by the wheel. It
  previously travelled from a zero floor on one band shared by all eight pads, which discarded the
  per-channel G-force shape entirely; the point of the redefinition is that a quiet wheel now leaves
  that animation intact with a little width on top, while a wheel past its own limit pushes every
  channel's ceiling to 100 and the shake takes over regardless of G-force. `GForceShake.ApplyRange`
  exists for this: the two ends now come from different places and can no longer be expressed as one
  centre plus one band.

  **Only `AllChannelsLockSlip` still travels from zero.** Its band *is* the wheel value, so a centred
  excursion would make a full-lock shake dip only to half strength and read as a loud buzz rather than a
  shake. The three others keep a centred excursion, because there the band is a modulation *of* an
  existing level that must not jump when the shake starts. `UsesLockSlipWave` narrowed accordingly.

- **The wave is a sine with holds at both extremes (1.0.8, `GForceShake.SineHoldWave`).** One pad's
  normalised position over a cycle of length `1/f`, for a hold fraction `h`:

  | Fraction of the cycle | Shape |
  | --- | --- |
  | `[0, h/4)` | held at MAX |
  | `[h/4, h/4 + (1−h)/2)` | half-cosine, MAX → MIN |
  | `[…, … + h/2)` | held at MIN |
  | `[…, … + (1−h)/2)` | half-cosine, MIN → MAX |
  | `[1 − h/4, 1)` | held at MAX |

  Three properties are load-bearing:
  - **The period is always `1/f`.** The holds are a share *of* the cycle, not an addition to it, so
    "10 Hz" keeps meaning ten full Max-Min-Max travels per second at every hold setting. Higher hold
    reads *sharper*, never slower.
  - **The two MAX holds are half-length each** (`h/4 + h/4`) while the MIN, passed through once, gets
    `h/2`. Both extremes are therefore held for the same total `h/2` — which is exactly what makes the
    shape symmetric, and a cycle both start and end at the maximum.
  - **At `h = 0` this is exactly a cosine**, with no flats at all. `h` is capped at 90%, not 100%, since
    zero-length ramps would demand an instantaneous square the hardware cannot follow.

  This replaced a trapezoid (linear ramps) plus a separate quadrature copy of it. The trapezoid's
  sustain-tracking phase offset and the quadrature term are both gone; `SineHoldWave` is the only wave
  in the plugin, and the two pads differ only in *when* they run it.

- **`ShakeFeeling` — how the pair relates (1.0.8, `GForceShake.FeelingPair`).** One wave, three
  offsets between the leader and the follower:

  | Feeling | Offset | Hold |
  | --- | --- | --- |
  | `OppositePhase` (**shipped default**) | half a period | as configured |
  | `SamePhase` | none — both pads identical | as configured |
  | `Blending` | `h/4` of the period (an eighth of the cycle at its fixed hold) | **pinned at 0.5**, setting ignored |

  `Blending`'s offset is the leader's own opening MAX hold, so at phase 0 the follower sits exactly at
  the top of its descent while the leader is still holding — both start high, one already dropping.
  Because that character is *defined* in terms of the hold, `Blending` pins its own (`EffectiveHold`)
  and the UI hides the hold control for it rather than letting a setting silently do nothing.

  **Picking a feeling SETS `ShakeFrequencyHz`** to `DefaultShakeFrequencyFor` — **5 Hz for
  `OppositePhase`, 10 Hz for the other two** (revised after the owner's seat time; the first cut had the
  split the other way round). Owner's explicit instruction: *"set the frequency as 10HZ
  (Even the user override to their own frequency value) … if enabled 'Blending' … set the Shake
  Frequency as 5HZ instead."* Discarding a hand-tuned value is the requested behaviour, not a side
  effect. It fires from the dropdown's `SelectionChanged` only, never from the load path, so a fresh
  install keeps whatever it shipped with until a feeling is actually picked.

  **The shipped default frequency is DERIVED from `DefaultShakeFeeling`**, not written out, so a fresh
  install and Restore-defaults both land on the frequency the shipped feeling would itself choose
  (today: OppositePhase → **5 Hz**). It was a literal 5.0 for a while, which meant a fresh install
  opened showing "Opposite phase" at 5 Hz and jumped to 10 the instant the driver touched the dropdown
  — the same drift class the two shake scales had.

  **The hold is NOT defaulted per feeling in the same way.** The two phase-locked feelings share the one
  configured value; `Blending` takes no default at all — it *pins* `BlendingHoldFraction` (0.5) and the
  UI hides the spinner, so there is no per-feeling hold value to hand the driver.

  **Picking a `ShakeApplyMode` on the G-Force tab RESETS the shake scales and the trigger** to that
  mode's own defaults (owner, **2026-09-07**, as `GForceShakeViewModel.Mode` and
  `GForceSettings.DefaultShakeScaleFor` cite it). The `Mode` setter of `Ui\ViewModels\GForceShakeViewModel`
  writes `DefaultShakeScaleFor(mode)` into the Wheel Lock, Wheel Slip **and Slip Angle** scales and
  `DefaultShakeTriggerFor(mode)` into "Start shaking above" on a driver's pick only - never on `Load`, so a
  driver's persisted values show as saved. A feeling pick likewise rewrites the frequency
  (`DefaultShakeFrequencyFor`) and hides the hold for Blending. The `GForceSettings.ShakeApplyMode`
  property itself does not touch the scales; only the tab's mode pick does (the ShakeIt G-Force panels
  have no mode control). The per-mode scales are **1.2** (`HigherOfGForceOrLockSlip`, the shipped
  default), **1.5** (`PerChannel`), **1.3** (`AllChannelsGForce`), **1.0** (`AllChannelsLockSlip`); the
  triggers are 60 / 5 / 60 / 30.

  History, kept because the reasoning is still true: on 2026-09-06 the owner had this reset REMOVED,
  since `contribution = scale × wheel/100` is computed identically in all four modes (only what it
  *multiplies* differs - the pad's level in the G-force modes, the full 0–100 range in the wheel-driven
  ones, so at 1.5 a wheel-driven mode saturates from wheel 67 up). On 2026-09-07 seat time produced a
  different scale and trigger per mode, and a per-mode table is only reachable if picking the mode
  applies it - so a hand-tuned value IS overwritten again, deliberately. (An earlier revision of this
  document still described the 2026-09-06 state; the code has followed 2026-09-07 throughout.)

  **`PadRange` no longer applies a `PeakExcursionFactor`.** The sine+hold wave spans a full 0..1 for
  every feeling and every hold, so a pad's travel *is* the band. The old factor existed because the
  quadrature blend genuinely shrank the excursion — which is also why a nominal band of 70 only ever
  swung 35 wide at the shipped blend.

  A shake starting from real silence still **opens at whichever of its four corners is furthest from
  the pads' current values**, so it announces itself instead of fading in. `PhaseForCorner` finds the
  phase that lands there by **searching 720 phases across the cycle** rather than deriving it in closed
  form: with three feelings, a variable hold, and `reversed`, no single offset expression is correct
  for all of them, and not every corner is even reachable (`SamePhase` can only sit at `BothLow` or
  `BothHigh`), so the search returns the closest attainable phase. `ChooseStartCorner` scores the four
  corners by **total absolute** travel — absolute, not signed, because a pad can sit *outside* the coming
  range under a cornering bias, where a signed term goes negative and ranks a corner as worse than doing
  nothing. The corners are scored at their real output values (`CornerOutputs`), and the reference band
  is mode-dependent: the wheel-driven modes' band comes from the wheel value alone, so using the pads'
  current level there (0 after a silence with no G-force) would collapse every corner onto one point.

  The traversal direction (`reversed`, which swaps which side leads) alternates between consecutive
  shakes so a stop-start reads as one rhythm; on the very first shake of a session there is nothing to
  alternate from, so the side with the *shorter* travel leads.

- **The sweep re-arms mid-brake, on a fast RISE only (2026-09-06).** `AdvanceStageProgress` used to
  reset progress ONLY when the chain went inactive, so trailing a little brake down a straight let the
  sweep finish and every later stab found progress already at 1 — the owner's report was "even with a
  small dec value for a while, a quick dec later will NOT be triggered". Three rules now govern the
  re-arm:
  - **Only when nothing is running** (`stageProgress >= 1.0`). A sweep in progress is never cut short;
    that would read as a stutter rather than a new event.
  - **Only on a rise.** The re-arm delta is SIGNED, so dec-G falling away fast — lifting off the brake —
    triggers nothing. The travel *rate* still uses the magnitude, so a release still sweeps out at a
    matching speed.
  - **A self-scaling threshold**, `RetriggerRatioRatePerSecond = MaxStageProgressPerSecond ×
    RetriggerStrictness` — in RATIO per second, not raw g. In absolute terms that is
    `maxG / sweepDuration × strictness`, which is the owner's own derivation: a low-max-G car (gentler
    stops, smaller absolute deltas) needs a proportionally smaller delta to earn its animation, while a
    high-max-G car is not retriggered by every small stab. `RetriggerStrictness` is a **driver-facing
    setting** (2026-09-07 — the one number here that only seat time can settle), default **1.2**,
    clamped to 0.1–5.0, **0.5 since v1.0.9** (it shipped at 1.2; seat time asked for a readier re-arm).
    At the shipped 0.2 s fastest sweep 1.2 reads as "the pedal moved far
    enough to cover the car's whole braking range in 167 ms". Higher is stricter; the floor is not 0,
    because a strictness of zero means a threshold of zero, which re-arms on any rise at all — the
    runaway the setting exists to prevent. `RetriggerRatioRatePerSecond` is therefore computed, not
    stored, so a mid-session Apply takes effect on the very next frame.

  A re-armed sweep also **resets the animation peak**, or the new animation would be scaled by the old
  event's high-water mark and a second, gentler stab would read as loud as the first.

- **The opening pad HOLDS at its peak (`StartHoldFraction` = 0.25).** The owner's reading of the old
  shape was right: the far pad was at its peak for a single instant at `p = 0` and only ever fell from
  there, while the MIDDLE pad rises into its peak and then falls away from it — so the middle pad spent
  roughly twice as long near maximum as the pad that opens the animation. The opening keyframe is now
  frozen for the first quarter of the sweep and the three-keyframe travel plays out across the
  remaining three quarters, which both gives the far pad a real plateau and delays the whole travel
  slightly, as asked.

- **Both pads follow the stronger side while shaking (`ShakePadPair`).** The lateral bias used to
  multiply each pad *after* the shake. Under a real cornering load that left the two ranges
  non-overlapping — measured 75.9–100 against 32.5–51.4, so the loud side **never changed**: zero swaps
  per cycle, no alternation at all, just two unequal wobbles at different heights. Taking
  `level × Max(leftFactor, rightFactor)` for both pads restores two swaps per cycle. The cornering cue is
  suppressed for the duration of the shake and returns the instant it stops — an accepted trade, on the
  grounds that at the limit "you are locking" outranks "you are turning right".

- **Lateral is a FRICTION CIRCLE, not a multiplier (1.0.8).** It used to be `pad × (1 ± gain·bias)`
  against a hard-coded `LateralReferenceG` of 1.6 g that no setting could reach. Two failures: under hard
  braking the strong side was already at 100, so there was no headroom to lean into and all the asymmetry
  had to come from the weak side dropping; and it destroyed the shake's alternation (see the
  stronger-side rule below). It is now additive headroom:

  ```
  combined = sqrt(rLong² + rLat²)
  level_ch = rLong × that channel's own staged shape        (terminal shape = 1.0)
  boost_ch = (combined − rLong) × that channel's lateral split
  L / R    = clamp(level_ch ± boost_ch)
  ```

  `GForceLateralFrictionTests` reproduces the owner's worked examples end-to-end: 72% brake with 70%
  lateral → combined 100.42%, headroom 28.4 → Bottom Front 86.2/57.8, Bottom Rear 57.3/14.7, Back Low
  46.4/0.

  Six per-channel splits, **inverted against the longitudinal emphasis on purpose** — braking
  50/75/100 (front/rear/low), acceleration 100/75/50 (rear/low/top). The loudest pad takes the least
  cornering, so a trail brake reads as the cue travelling back and to one side rather than everything
  getting louder in place. A pad the active chain does not drive gets a split of 0. With no longitudinal
  G at all the **last active chain's** splits apply (owner's decision), so the cue does not jump as
  longitudinal G fades mid-corner.

  `LateralReferenceG` and `LateralBiasGain` are **gone**, superseded by the new Maximum-cornering-G
  setting and the six splits. Three output scales (accel/brake/lateral, default 100%) attenuate their own
  component and never `combined`, so the circle stays an honest physics quantity.

- **The learned maxima, and the floor under them (1.0.8).** All three axes share one
  `RobustBandEstimator` — 8 g hard reject, 2-minute rolling window, skip the top 5%, pool the next 10% of
  the remainder (min 10 samples), report `0.75 × pool max + 0.25 × pool mean`. **Not a literal
  percentile**, though it lands near P95: on a real F1 2025 log, raw p95 was 2.589 g and the estimator
  gave 2.510 g. Lateral additionally has no direction to gate on — accel and decel are two different
  maxima sharing one axis, whereas cornering left and right are the same grip question — so it observes
  `|latG|` on every valid frame and keeps whichever is highest, regardless of what the car was doing
  longitudinally.

  **`MinLearnedAccelMaxG` / `MinLearnedDecelMaxG` / `MinLearnedLatMaxG` = 0.5 g**, and the floor actually
  applied is **`Min(that constant, the driver's own Fixed*MaxG)`** (`FloorFor`). Applied to the LEARNED
  value only — a hand-typed `Fixed*MaxG` is left as typed, and with no evidence the fixed default already
  governs. So the floor covers exactly one case: real but implausibly low evidence, i.e. a session with
  almost no cornering (or braking), which otherwise learns ~0.1 g and makes the cue saturate on the
  slightest input.

  The `Min` matters: a driver who typed something BELOW 0.5 has said explicitly that values that low are
  wanted on that axis, so the floor steps aside rather than overriding them. Typing 1.5 leaves the floor
  at 0.5, and a learner converging on 0.3 is held at 0.5; typing 0.2 lowers the floor to 0.2, and the
  same learner is allowed all the way down to 0.3.

  **AUTO ALWAYS OPENS AT THE TYPED VALUE AND STEPS TOWARD THE MEASURED ONE, IN EITHER DIRECTION.**
  `MaxRamp` seeds `_lastPublished` from `Fixed*MaxG` on its very first call, then converges on the
  learner's estimate — comparing `|target − lastPublished|`, so it is direction-agnostic by construction.
  Typed 1.5 with a real maximum of 1.2 walks down to 1.2; typed 0.8 with a real 2.4 walks up. Changes
  under 25% apply immediately, larger ones ramp over 2 s, and a ramp already in flight continues on
  elapsed time alone rather than re-checking the threshold (which would let a converging ramp snap).

  **WHY AN ABSOLUTE FLOOR IS CORRECT, not an overestimate.** The objection was that it misreports
  genuinely low-grip content: a car on snow pulls ~0.4 g and a truck perhaps 0.2 g, so normalising them
  against 0.5 makes flat-out effort read below full scale. The owner's answer settles it — **that is the
  right outcome.** A real truck does not produce a strong G-force transition, and a seat pad should not
  pretend otherwise; below roughly half a g there is no forceful event to report. This is a deliberate
  trade against pure per-vehicle normalisation: above the floor the cue means "at THIS car's limit",
  below it, it means "not much force here". Three separate constants, equal today, because the axes have
  different physics (braking and cornering are grip-limited; acceleration is power-limited and spans
  ~0.2 g for a laden truck to ~1.5 g for an F1 launch).

  **A MINIMUM-OBSERVATION THRESHOLD WAS TRIED ON LATERAL AND REMOVED.** The reasoning is worth keeping,
  because the idea is tempting: without a direction gate, straight-line frames padded the pool and slid
  the rank-5%–14.5% window down, measurably lowering the estimate (2.500 → 2.689 g, +7.6% with a 0.15 g
  threshold). But sweeping the threshold from 0 to 0.8 g was **monotonic with no knee** — the estimate
  climbs all the way (+24% at 0.8 g), because trimming the bottom always slides the pool window up. That
  makes it a *feel* knob deciding how high in the distribution the reference sits, with no derivable
  correct value, rather than a noise filter. Its effect on the output was small anyway (~4 points of
  split across the whole range) and in the direction of caution, since a higher learned max means a
  smaller `rLat` and a NARROWER split. The floor handles the case that actually mattered and is far more
  predictable, so the threshold was dropped rather than tuned.

  The longitudinal axes never needed the threshold either: measured on the same log, 0.15 g moved decel
  by +0.7% and accel by +0.9%, because their direction gate already excludes the idle frames.

- **Why a pure pan could not be felt, and why the blend became a dropdown (1.0.8).** Originally the
  shake was 100% differential: `L = c + h·w`, `R = c − h·w`, so `L + R = 2c` and the pair's *total*
  never moved. Two transducers are uncorrelated, so their powers add and felt magnitude goes as
  `sqrt(L² + R²)` — which a pure pan barely disturbs. Measured on a real session log: the pan swung
  **62** points peak-to-peak while felt magnitude swung **13**, a modulation depth of **23%**, against a
  theoretical ceiling of **29%** even at a saturated 100/0 swing. The shake was present, large, and
  imperceptible.

  The first fix was a **Both-sides blend (%)** spinner routing share `m` of the band through a
  quadrature copy of the wave. It worked — felt depth rose from ~11% to ~40% at `m = 0.5` — but it was
  the wrong *control*: only three points on that slider were distinct to feel, the mechanism cost a
  `PeakExcursionFactor` that halved a pad's travel, and the hold setting had to be cancelled out
  towards the middle (`EffectiveSustain = configured × 2 × |0.5 − m|`) because the quadrature term
  supplied a dwell of its own.

  So the slider became `ShakeFeeling`'s three named choices, and the quadrature term, the excursion
  factor, and the sustain-cancelling all went away with it: two copies of one `SineHoldWave` at
  different phases reproduce all three feelings, and each spans the full band. The retired mechanism is
  recorded here because the *measurement* behind it still stands — `OppositePhase` is deliberately the
  subtlest of the three feelings for exactly the reason above, and a driver who cannot feel it should
  be pointed at `SamePhase` or `Blending`, not at a bigger scale.

  One detail survived the rewrite: **the final L/R clamp is unconditional**, because arithmetic on the
  wave can land a few ULPs outside 0–100 (`100.00000000000003`).

  The setting key and engine property outlived the mechanism for a while as **write-only state** —
  `ApplyTo` kept pushing `ShakeBlendPercent` into `GForceEngine.ShakeBlend` and nothing ever read it
  back, so a persisted blend silently did nothing. Both are now removed; an old config file simply
  carries an ignored key.

- **The Test Effect panel (1.0.8; since 2026-11-20 the shared `GForceTestViewModel` hosted by
  `GForcePageViewModel`).** A drag-a-ball G-force pad and a Lock/Slip bar that push synthesised frames
  through a **private `GForceEngine` instance** owned by the settings page, so a driver can set ShakeIt
  levels without provoking a real lock-up on track.

  **It publishes to the real properties.** Driving only its private engine moved the on-screen readout
  and nothing else: with no game running `DataUpdate` returns before publishing anything, so
  `QAdvanceFeedback.GForce.*` read null and ShakeIt — the entire reason the panel exists — got nothing.
  `QAdvanceFeedback.PublishTestEffect` writes through the same `PropertyPublisher.UpdateGForce` the live
  path uses (its properties are pull-delegates over that array, so a write lands with or without a
  running game) and sets a flag that makes `DataUpdate` skip its own publish. Passing null clears the
  override; the toggle going off, the 10 s auto-off and hiding the G-Force tab (`GForcePageViewModel`'s
  `Deactivate`) all do that, or a closed page would pin the outputs forever.

  **Pad orientation** (owner, 2026-09-06): the ball marks where the load is thrown, so **up is braking,
  down is accelerating, and dragging left means turning right**. Both pad axes are negated on the way
  into the telemetry frame.

  Two further properties are the whole point of it, and both are guarded:
  - **Every G-Force setting on the page applies, live — into a SCRATCH object.** The test runs on a
    throwaway copy of the page DRAFT's G-Force settings (unapplied edits included, Wheel Lock and Slip
    scales too) and applies it to its own engine, never to the plugin's live settings. (The original
    page once called its whole save-to-settings method here, which pushed every uncommitted edit into
    the running plugin: an Apply nobody asked for.) An edit made while the test runs reaches it.
  - **Nothing about it is ever saved** (owner). Its state lives only in `GForceTestViewModel`, which is
    not part of the draft, so it can neither mark the page dirty nor be written by Apply
    (`GForcePageViewModelTests.The_test_effect_runs_on_the_unapplied_page_values_and_never_marks_dirty`).
  - **Off means nothing at all.** With the toggle off the controls are collapsed, no labels render, and
    no simulated frame is produced — the panel must not be able to drive a driver's motors while it is
    switched off.

  The **preview graph** in the shake section is the same idea at rest: it calls `FeelingPair` directly
  to draw one second of both pads at the current settings. Neither surface reimplements the wave; both
  render what the plugin itself would output.

- **The trigger gate and the shake's rhythm (1.0.8, `GForceEngine.AdvanceShake`).** Four rules, all in
  service of the shake reading as one continuous rhythm rather than a signal that restarts whenever the
  wheel value wobbles:
  1. **Threshold** (default 5, on the *unscaled* `Max(lock, slip)` so it means the same number the
     driver sees on the wheel tabs - raising a scale never makes the shake start earlier). Below it
     nothing starts, and this acts as a SOFT OFF-SWITCH for the whole integration: every mode then
     publishes exactly what it would with `IntegrateWheelLockAndSlip` false - plain G-force, no
     shake - including the two wheel-driven modes, which have nothing left to be louder than.
  2. **One rhythm.** Once running the phase only advances; a changing wheel value moves the band's width
     and nothing else.
  3. **Finish the cycle.** On dropout the shake runs to the end of the cycle it had begun. Computed as an
     explicit remainder, **not** `Ceiling(elapsed) × period` — the phase accumulates by repeated `+= dt`,
     so a dropout landing on a cycle boundary reads as `5.9999…` and `Ceiling` returns the boundary the
     shake is already standing on, ending the release instantly and cutting off exactly the gesture this
     exists to complete.
  4. **Re-arming inside that tail keeps the beat.** Only a shake starting from true silence realigns.

  During the tail the *last above-threshold* contribution is held rather than the current sub-threshold
  one, so the gesture completes at the size it was being felt at instead of collapsing as it finishes.

- **Settings-panel "Auto detected" readout — stale-snapshot fix.** `SettingsControl.RefreshGForceLearnedText`
  used to be invoked only at construction (`LoadFromSettings`) and when the Accel/Decel mode combo's own
  selection actually changed — never on a timer. Traced end-to-end: `GForceSettings.SetCurrentGameAndCar`/
  `ObserveAccelG`/`ObserveDecelG`/`GetLearnedMax`/`TryGetCurrentAccelAutoDetected`/
  `TryGetCurrentDecelAutoDetected` all key off the identical `(gameId, carId)` pair, and
  `EffectiveAccelMaxG`/`EffectiveDecelMaxG` — the values that actually feed the live G-derived severity
  path — are queried fresh every telemetry frame; none of that was ever stale or mismatched (confirmed
  byte-for-byte identical across every 1.0.6.x build back to 1.0.6.5). Only this settings-panel TEXT was
  a one-shot snapshot, so a driver who opened the panel before driving (correctly seeing "no data yet")
  and left it open never saw it update even after real evidence existed — **purely cosmetic**, never a
  key/channel/learner mismatch, and never anything the live behavioural path read. Fixed with a
  one-second refresh - since 2026-11-20 the `ITicker` timer of `GForcePageViewModel`, running only while
  the G-Force tab is shown (hiding it stops the timer, showing it restarts it and refreshes at once).

### Slip Angle: how it works and why

Design: `docs\slip-angle-design.md` (with its post-spike revisions). Telemetry survey:
`docs\slip-angle-spike-report.md`. Build record, rulings and deviations: `docs\slip-angle-report.md`.

**What it is for.** In tarmac racing the fastest technique keeps the tyres' slip angle just under the
angle of peak lateral grip. SimHub's built-in "Traction loss" effect is car-level body sideslip with no
threshold and no learning, so it only reports a slide that has already happened. Slip Angle is a third
wheel channel built on the same pattern as Wheel Lock / Wheel Slip (Raw -> Normalized -> Projected, a
settings tab, a native ShakeIt effect, a Test Effect). It rises as the tyres enter their ideal window,
peaks once the car goes past the limit, and eases off while the driver holds a deliberate drift, so a
WRC stage or a drift run does not buzz at full strength all the way through. It is ONE output channel
(`SlipAngle.Projected.All`). It has no per-wheel or per-axle routing.

**Pipeline, one frame** (`Core\SlipAngle\SlipAngleFrameProcessor.Process`, called from
`QAdvanceFeedback.SlipAngle.cs` after Lock/Slip Projected and before G-Force, so G-Force reads this
frame's angle). The order is pinned by tests:

1. yaw-sign observe (`YawSignDetector`);
2. gate (`SlipAngleGate.Step`);
3. source (native, fallback or custom);
4. `SlipAngleRawCalculator.Compute` with the PREVIOUS frame's k (k comes from the learner that this
   frame's Raw.All then feeds);
5. `ImpactGuard`;
6. surface classifier on Raw.All;
7. `KeyedSlipAngleLearner.Observe`;
8. learned window, maturity and tier;
9. manual key data gate and one-time seed;
10. `SlipAngleNormalizer`;
11. `SlipAngleProjectedEngine`;
12. `SaveIfDue`.

The processor and its learner belong to the data thread: the learner's getters can re-prior a key, so
they mutate. Everything the UI, the CSV and the ShakeIt effect read is an immutable
`SlipAngleFrameResult` / `SlipAngleStatus` captured once per frame. The plugin file has its own
try/catch, which reports `HealthSubsystems.SlipAngle` and publishes the empty result, so a Slip Angle
fault never stops Lock/Slip/G-Force. After 300 consecutive good running frames the entry is cleared
again (`Core\Health\HealthRecoveryTracker`), so a one-off fault does not leave a permanent message.

#### Sources: native, computed, fallback, custom

- **Plugin Internal, native tier.** `SlipAngleRawSources.cs` (project root, Layer 2 but SimHub-free and
  link-compiled into the tests) is a per-game table: four full SimHub property names (FL, FR, RL, RR, any
  raw-index remap already applied), the unit and the transform.
  **Since 1.2.1 the table has no built-in rows: it is built only from `QAdvanceFeedback.SlipAngleSource.json`**
  (`SlipAngleSourceFile.cs`):
  - The solution root's copy is an embedded resource (`SlipAngleSourceCompiled`). It seeds
    `PluginsData\Common\QAdvanceFeedback.SlipAngleSource.json` when that file is missing, and is what the page's
    Restore default goes back to.
  - `SlipAngleSourceStore` loads the Common file at Init and again when the running game changes (a hand edit
    applies without a SimHub restart; a deleted file is created again), and puts the parsed
    `SlipAngleSourceCatalog` in use (`SlipAngleRawSourceTable.Use`, a volatile swap). The reader drops its
    per-game missing/failed verdicts when the table revision changes.
  - A file that does not parse, or has an incomplete row, is refused whole and never overwritten: the compiled
    copy is used for the session, Health (`SlipAngleSourceFile`) says why, and the page's editor is off.
  - `id` names the games: `Game1|Game2`, wildcards `*` / `?`, or `regex:...` to the end of the id
    (`SlipAngleGameIdPattern`); an exact name wins over a pattern, else the first matching row; an exact row with
    nothing else is "no source". On save, a game whose settings equal another row's joins that row's id, and a game
    a pattern covers with the same settings gets no row (`SlipAngleSourceStore.Place`).
  - Surface: `surfaceValues` translates the row's `surface` values (sealed / loose / lowGrip) in the reader
    (`SlipAngleInput.WheelSurface`); without it, the processor uses SimHub's `WheelOnLooseSurface*` flag
    (`SurfaceBucketMap.FromSimHubLoose`: loose at 3+ wheels, tarmac at 0-1 only after loose was seen this session);
    else Unknown, held.
  - Each row: `id`, `slipAngle` (four names), optional `unit` (degrees / radians / ratio1 /
    ratio100; absent = detected), `transform` (direct / atanOfTangent / atan2PatchVelocity) with `longitudinal`,
    `surface`, `provisional`, `verticalAcceleration`, `note`. Unknown fields are kept on save. The shipped units are
    the ones the code stated before 1.2.1: LFS, Drift21, rF2, LMU, the NASCAR family radians; KartKraft degrees; the
    Forza games ratio1. NOTHING GAME-SPECIFIC IS HARD-CODED any more (owner, 2026-11-24): the config's unit lists
    (`SlipAngleSourceUnits`) start empty (the KartKraft / Forza defaults are gone - `SettingsHub.SlipAngleHardCodedUnit`
    is the row's `unit`), and SimHub's vertical acceleration is read only for a row with `verticalAcceleration: true`
    (Rennsport, and an `IRacing` row holding only that flag) - `SlipAngleRawSourceTable.ReportsVerticalAcceleration`.
  - The settings page's Slip Angle tab edits the current game's row ("Per-wheel tyre angle when available"):
    Telemetry Type (None / Per-Wheel Angle / Lateral + Longitudinal Velocity), the property boxes, Has Per-Wheel
    Surface Telemetry and Has Vertical Acceleration Data (`SlipAngleSourceEdit`), written by the page's Apply
    (`SlipAngleTabViewModel.ApplyTelemetrySource` -> `SlipAngleSourceStore.Save`). "Restore this game's source" shows
    only for a game the compiled copy has a row for (`SlipAngleSourceStore.HasCompiledDefault`), is enabled while the
    file's row (or the typed edit) differs, and writes the compiled row AT ONCE - no Apply, like the page's other
    restores.
  - **Match levels and units** (owner, 2026-11-24): a game gets the row whose best term matches it best - exact
    single name, name in a list, `?` wildcard, `*` wildcard, regex - then file order (`SlipAngleGameIdPattern.Level`);
    a save always names the game in the first row with its settings. Per-Wheel Angle's **Source Unit** is the row's
    `unit` (saved by Apply); a velocity row is always radians with no `unit`. **Source Type for this game** is the
    game's own override in the plugin config (never the JSON), defaulting to the JSON's; a JSON unit wins over the
    config lists.
  - **One source** (owner, 2026-11-24): the Slip Angle Custom source (`SourceMode` / `CustomSources`, the frame
    request's Custom fields, the Current Source dropdown) was removed; Plugin Internal is the only source.
  - **Why the car angle is used** (`SlipAngleSourceState` on `SlipAngleInput` -> frame result -> `SlipAngleStatus`;
    `SlipAngleLiveText.WarningText`): no source, Checking (a source is read but not promoted yet), NotReported (none
    of its properties for `MissingVerdictFrames` while moving), or CarAngleChosen (no warning).
  - **Angle source** (`SlipAngleChannelSettings.AngleSource`, hub field `SlipAngleAngleSource`): "Always car angle"
    makes the reader read no game's tyre field (`SlipAngleRawSourceReader.AlwaysCarAngle`), keys the key data by the
    car-angle identity, and hides the per-wheel source and the unit dropdown on the tab and the ShakeIt panel.

  The shipped file lists only games where the spike found the field:
  - **Native (direct)**, a per-wheel slip angle field: ACC, Assetto Corsa Rally, Assetto Corsa EVO,
    F1 23/24/25, Forza (FM7, FM8, FH4, FH5, FH6), Live For Speed (a tangent, converted with atan),
    Wreckfest 2, Project Motor Racing, Drift21 (radians), KartKraft, Bus Bound, Rennsport.
  - **Native (computed)**: `atan2(lateral patch velocity, |longitudinal ground velocity|)`. This covers
    rFactor 2 and Le Mans Ultimate, and provisionally the NASCAR Heat 4/5 / Tony Stewart / SRX reader
    family (its terrain-speed semantics are unproven).

  `SlipAngleRawSourceReader` reads the table through a delegate, so the paths are never resolved from
  SimHub types in Core. A path that is missing for the session gets a "path missing" verdict and is not
  read again.
- **Native promotion** (`SlipAngleNativeSupport`). It uses the same promotion pattern as
  `KeyedTelemetrySupport`: evidence promotes, nothing demotes within a session. A game counts as native
  only after 5 s of moving frames (≥ 30 km/h) whose four values are finite, plausible (≤ 45°) and not
  constant. **The 45° block (R14):**
  - on a row with a pinned unit, a frame above 45° is neutral;
  - on a row whose unit is unverified, a frame above 45° counts toward a 1 s block only while the unit
    is still undetermined and |lat G| < 0.3 g (garbage, not a drift).
- **Unit auto-detection by episode voting (R6 → R16).** Only Drift21 (radians), LFS (tangent) and the
  computed rows have a known unit. For every other native field the unit is detected per game:
  - An episode is a contiguous run of ≥ 0.3 s at |lat G| ≥ 0.8 g that ends when |lat G| < 0.5 g.
  - An episode votes "degrees" if at least 70% of its frames show a magnitude ≥ 1.0, else "radians".
  - The unit is decided after at least 3 episodes with at least 2/3 agreement.
  - An undetermined unit means no promotion. So an unverified game needs three hard corners before it
    goes native.

  Accepted residual risk (R17): two spins plus one corner in the first three episodes can vote the
  wrong unit for one session. The unit, the vote counts and the block state are published as
  diagnostics.
- **Fallback tier.** This is the rear axle only:
  `α_rear = |atan2(Vlat, max(Vfwd, 1 m/s))|`, from `LocalVelocity`. The design's `− b·r` yaw term was
  DROPPED after the spike (R12): `YawChangeVelocity`'s sign is not uniform across readers, so the term
  could add instead of subtract. The learned window absorbs the scale difference. Every game outside
  the native table uses it: Assetto Corsa, AMS2, iRacing (unverified), RaceRoom, the Codemasters
  rally/F1 ≤ 22 titles, EA WRC, GT7 and the rest.
- **Custom.** Four per-wheel source fields in degrees (plain / NCalc / JavaScript), learned under their
  own `SourceIdentity`. ShakeIt Built-in and Viper are not offered, because neither provides a slip
  angle.
- **Plugin Internal always learns (2026-11-19, `docs/internal-source-learning-design.md`).** Plugin
  Internal's raw angle (native or fallback) is computed and learned under its own key **every frame**,
  also while Custom is selected. The shared surface classifier is stepped once per frame from Plugin
  Internal's angle and the internal key's curves, whatever is selected; Custom learns only while it is
  selected, into that same blend, under its own key. Only the selected source publishes.
- Native and fallback are **different source identities** (`NativeIdentity` / `FallbackIdentity`).
  Their scales differ, so they never share a learned window.
- **The orange fallback warning** is driven by `SlipAngleStatus` (R19). It shows on the tab and the
  ShakeIt panel when the source is Plugin Internal and the debounced tier is Fallback. Tier Unknown
  with no Raw.All is a plain status ("No slip-angle data from this game") and shows no warning.
- **Key-data identity tier (`SlipAngleIdentityTier`).** No game / pause / menu clears the debounced
  tier to Unknown at once - right for the warning, but Unknown maps to the Native slot. The key data
  the hub, the tab and the ShakeIt panel read and write is therefore chosen by a separate value,
  `CurrentSlipAngleIdentityTier`: the last in-session CONFIRMED tier for the current game. It is kept
  across pause / menu / a fault, follows a confirmed (debounced) tier change while running, and resets
  on a game switch. So an edit made while paused on a fallback game lands in the Fallback slot.

#### Yaw sign learning (`YawSignDetector`, R12)

In a steady corner `lat G ≈ k · V · r / g`. Each game's yaw-rate sign and lateral-axis sign are their
own conventions, so only their RELATIVE sign `k ∈ {+1, −1}` can be learned:

- A frame votes when the car is grounded, |V| ≥ 30 km/h, |lat G| ≥ 0.3 g and |V·r| ≥ 0.1 g.
- Votes are collected in 3 s windows of voting time. A window decides k at a 70% share.
- Once k is decided, flipping it needs 80%.
- Until k is known, and on frames with |dVlat/dt| > 0.15 g (transients the steady-state relation does
  not cover), the processor passes a null yaw to `ImpactGuard`. The yaw-consistency check is then
  skipped.

#### Four wheels → one Raw.All (`SlipAngleRawCalculator`)

1. **Axle α** = the mean of the axle's two |α| values. A wheel on a kerb (`WheelsOnKerbs`), an
   unreadable Custom wheel, or a lifted wheel (≤ 0.05° while its partner reads ≥ 2°; owner, 2026-11-22) is
   left out, and the other wheel stands alone.
2. **Equalisation:** `α_F′ = α_F × k` (k below). Front and rear tyres peak at
   different angles, so a plain blend would judge one axle against the other's window. Rescaling the
   front onto the rear's scale lets the single window learned on Raw.All act as the rear window, and
   each axle is effectively judged against its own peak.
3. **Blend:** `Raw.All = Max(α_F′, α_R) × W_Max + Min(α_F′, α_R) × W_Min`. The defaults are 0.85 /
   0.15, editable under "Wheel combination". The weights are not forced to sum to 1.
4. **One axle only (R18):** if only one axle is available in native mode, Raw.All is that axle (front
   scaled by k). In fallback mode Raw.All is α_R.

How k is formed:

- k is the median rear / front axle angle over the frames where the car is near its limit (owner, 2026-11-22;
  `SlipAngleBalanceEstimator`): speed-band-scaled lateral G at or above 85% of the cornering 98th percentile (trusted
  after 120 cornering frames; spikes above 6 g ignored), pulled toward 1 until 120 such frames
  (`k = exp(w ln median)`, `w = n / 120`). Never taught by a frame the learners refuse (pit, replay, restart,
  airborne, landing wobble, bad frame time), by contact (the impact verdict, light or heavy), by a slide (the body
  angle), by an axle beyond the source's angle cap (a spin) or a front under 0.3 deg. Per game, car, source and
  surface. A surface with no k evidence of its own STARTS from the same game, car and source's k on another surface,
  else from exactly 1 - at once, not eased over (owner, 2026-11-22; `SlipAngleBorrowRules.KScope`); its own evidence
  then takes over (`k = exp(w ln median + (1 - w) ln start)`); never another car or game. Saved with the bucket's All
  key (single values), so a restart carries on. Clamped to **0.3**-2.0 (was 0.5; F1 measures 0.29-0.46) and slewed at
  0.2 per second once the surface has evidence of its own. A source
  without both axles (fallback, a one-axle Custom source) never feeds or uses it. Replayed on the owner's logs it
  reached F1's 0.43 within the first hard corner (26.3 -> 27.1 s) and held 0.39-0.49, and held 1.00-1.10 on FH6. It
  replaces `Best_rear / Best_front` of the confirmed windows, which needed both axles' peaks confirmed: on F1 the rear
  crossed once in eight minutes, so k stayed 1 all session.
- With surface weights, the buckets' k values are blended in the log domain.

#### Gating and the landing wobble (`SlipAngleGate`)

- **Trigger By:** Brake, Throttle or Any (default Any), with thresholds brake ≥ 5%, throttle ≥ 10%.
  While the gate is closed the published value is 0, but the drift state machine keeps stepping, so
  re-opening mid-slide is consistent.
- **Speed:** the output fades in linearly from 10 to 20 km/h. Learning needs ≥ 30 km/h, a plausible
  `dt`, and `TelemetryLearningGate` (no pit, replay or restart). Pedals are NOT required for learning.
- **Airborne = `IsFlying > 0` only (R5).** `FlightIsOnGround` is never written for car games, so
  "`FlightIsOnGround == false` means airborne" would gate every car title. While airborne the output
  is 0, nothing is learned, and the drift attenuator is reset.
- **Landing wobble:** `wobble = min(1, flight / MaxFlight) × MaxWobble` (defaults **MaxFlight 1000 ms /
  MaxWobble 500 ms**, swapped 2026-11-19 — `docs/slip-angle-design.md` §9.7; a 200 ms flight now gives a
  100 ms wobble).
  During the wobble learning is frozen, the output is multiplied by a smoothstep fade from 0 to 1, and
  the drift timers are held at reset.
- **Small hops (R7):** a frame with |vertical G − 1 g| > 0.5 g, plus the 200 ms after it, is excluded
  from learning. This works only where `VerticalAcceleration` is populated (iRacing, Rennsport).
  Elsewhere hops are covered by `IsFlying` and by `ImpactGuard`'s vertical-jump check.

#### The window learner: grip curve, crossing-taught Max, confirmation

There is one `SlipAngleWindowLearner` per key, with three targets per context:

- **front** and **rear** (internal; only their Best is used, for k);
- **all** (its window IS the published key data).

The learners are fed after equalisation, so Raw.All learns in the "rear-equivalent" degrees it is
applied in.

**The grip curve (`GripCurveBins`).** Bins are 0.25° wide over 0–30° (120 bins). Each bin keeps a
decaying P90 of |lat G| (a quantile step scaled by the bin's own spread, memory ≈ 500 of that bin's
observations).

- The first 20 samples of a bin go to a seed buffer, and the bin's P90 is seeded EXACTLY from them.
  This removes first-sample bias.
- A bin updates **only when visited**, so later cautious driving never erodes a confirmed peak.
- A bin counts toward the curve from 20 samples. Smoothing is a 3-bin max of median.
- `peakG` for Best and Min is the highest bin **below Max** (R20; the spec's step 3 said "over all
  bins").
- **Best** is the midpoint of the contiguous plateau where P90 ≥ 97% of the peak.
- **Min** is where the rising side first reaches 90% of the peak. If the rising side is not yet
  populated, Min = Best × 5/8.
- The shape check calls the curve "still rising at its top" when the top bin is within 99% of the peak
  and ≥ 2% of the peak above the curve 1° lower.

**Max is taught only by traction crossings** (like Lock/Slip SMax), through a `CrossingGate` with
`CrossingGateParameters.SlipAngle`: ceiling 45°, rise 0.5°, lateral-G fall −0.05 g, 1 s hold,
collapse test off, no saturated-wheel guard (R2), walk-back 0.10, non-crossing weight 0. The walk-back
stays inside the rise and a crossing with an unusable snapshot is rejected, exactly as for Lock/Slip.
**Learned-Max cap (2026-11-19):** 20° for a native tyre slip angle, 30° for the car-angle fallback
(Custom: 20°), applied to every taught α, snapshot, prior scale and published / persisted / borrowed
window, and to a typed Max (`SlipAngleWindowLearner.MaxCapDegFor`, `SlipAngleWindow.CappedAt`). The 45°
gate ceiling and plausibility bound are unchanged: only α within 45° teaches, but every α is mapped, and
anything above End (1.5 × Max) is 100.

- **Candidate:** a frame is a candidate when |lat G| ≥ 85% of the curve's `peakG` and the
  curve-plausibility check has held on every frame of the gate's trend. That check requires G ≤ 115% of
  the curve's expected G at that α (skipped where the curve is empty) and ≤ 115% of `peakG`. Nothing is
  a candidate while the curve is cold.
- **Event lifecycle:**
  - Detection opens a PENDING event.
  - After 5 more qualified frames its corroboration is DECIDED.
  - A corroborated event teaches its onset snapshot (never the live α) on every remaining frame of the
    hold.
  - The event COMPLETES only when the hold expires naturally. Only then is it counted, put in the
    recent-snapshot ring, and eligible for confirmation and as a corroborator.
  - Any excluded frame during the hold (impact, wobble, yaw residual, gating) DISCARDS the event, and
    whatever it taught is **rolled back** to the state saved at the decision.
- **Corroboration with a level epoch (R21, R22, R23).** The Max level is a
  `CorroboratedLevelEstimator` in degrees: cap 45, jump 15, tolerance 0.30 / 0.15, floor 0.2°, cooldown
  3 / 40, base 0.10, level floor 0.5°. One 1 s hold is about 60 observations, so an event may teach only
  when an earlier, SEPARATE completed event corroborates it. The corroborator must:
  - lie within the band (30% when raising, 15% when lowering, never narrower than 0.2°);
  - lie on the **same side** of the current level by more than the floor;
  - have completed **after the level last moved** by more than the floor. This is the "level epoch",
    and it is persisted.

  The cluster that set Max, scatter included, can therefore never corroborate a move away from it. A
  single fake crossing can neither raise nor lower Max; two separate new crossings on the same side can.
  With no level yet, any earlier in-band event counts. So the first crossing alone never teaches Max.
- **Peak confirmation, latched.** Confirmation is checked only when an event completes. The peak is
  confirmed when all of these hold:
  - ≥ 3 recent completed events lie pairwise within 15% (lowest ≥ 85% of highest);
  - the curve is not still rising at its top;
  - a Max level exists.

  Once confirmed it stays confirmed, and the flag is persisted.
  - **Unconfirmed:** the published window is the prior (borrowed, else shipped 5 / 8 / 10°) times a
    raise-only `PriorScale`. When grip is still rising at an α above the published Best, the window is
    scaled up so Best equals that α. A driver who stops at 5° therefore never gets Max = 5°.
  - **Confirmed:** Max is the corroborated level, and Best and Min come from the curve.
- **Crossing gate off (R29, R30).** `SlipAngle.CrossingGateEnabled` (default on) works exactly like the
  Lock/Slip switches. With it off, runs past the threshold are taught through the same snapshot,
  rollback and 5-frame delay line. The ring and epoch are committed only on a natural end, and a
  shape-only confirmation needs ≥ 2 runs.

**Crash and contact protection** (owner: "the same or similar pattern as Lock/Slip"). These checks gate
every learner input: crossings and Max, the curve, `peakG`, maturity and the surface inference. They
NEVER touch the output, so a crash that really throws the car into a slide is still felt.

1. `ImpactGuard` (`ImpactGuardParameters.SlipAngle`):
   - a frame with |G| > 8 g on any axis, or a non-finite G, teaches nothing;
   - a frame-to-frame jump > 3 g on lateral, longitudinal or vertical G is an impact. It invalidates
     the previous 5 frames (`RetroInvalidate`), blocks learning for 1.5 s, resets the gate and
     discards a live hold;
   - the yaw-consistency residual `|lat G − k·V·r/g|` must stay ≤ 0.3 g.
2. **The 5-frame retro delay line.** Learnable frames reach the curve, the maturity counts, the prior
   raise and the Best history only 5 frames late. `RetroInvalidate` drops what is still in the line, so
   the frames just before an impact never teach. `Export` is side-effect-free and omits those frames.
3. **Rollback** of a discarded crossing event (above).
4. The curve-plausibility check (above).
5. Corroboration (above).

The owner's named scenario is a pinned regression test: true Max 8°, the car is hit at 4°, then α keeps
rising while G ramps down.

**Maturity, which drives output scale and handover.** It does not depend on confirmation, so a driver
who stays under the limit still matures and their manual triple still kicks in. It is
`clamp01(min(observations / 50, corners / 3))`:

- an observation is a learnable frame with |lat G| ≥ 70% of `peakG`, counted at most 10 per second;
- a corner is a rise above 70% followed by a fall below 40%.

A car matures within its first two to three near-limit corners. Maturity feeds `ColdStartScale` with
`ColdStartTierFloors` exactly as Lock/Slip, so a cold car with nothing to borrow caps a full reading at
50 until it matures. **Window quality** is coverage × stability, capped at 0.6 (a min, not a multiplier)
while unconfirmed. It is used for display and the cold/warm weight only.

**Learning never stops**, as for Lock/Slip. Every learnable frame keeps feeding all three learners, in
Auto and Manual, after maturity and confirmation. Counters saturate at the shared overflow cap.

#### Surface buckets (`SurfaceBucketClassifier`, `SurfaceBucketMap`)

There are three buckets: **sealed**, **loose** (gravel, dirt, grass, sand) and **low-grip** (snow,
ice). Each has its own front/rear/all learners and its own maturity, because WRC runs one car on
several surfaces in one stage.

- **Game-reported surface (R13).** It is used only where the spike documents what the values mean:
  - **rFactor 2** `mSurfaceType`: Dry → sealed, Grass/Dirt/Gravel → loose, Kerb → sealed. Wet and
    Special are Unknown.
  - **GT7** `surfaceType`, by NAME only (the spike documents the names, not their numbers; a numeric
    value is not mapped): tarmac → sealed, dirt/sand/grass → loose, snow → low-grip, kerb → sealed.

  The four wheels are a majority vote. Every other raw surface (LMU, F1, AMS2, PCars, rF1 family, ETS2,
  BeamNG, ...) is read and exposed, but not mapped.
- **Otherwise the surface is UNKNOWN and never guessed (owner, 2026-11-22: "If you cannot get the surface
  information, then assume the surface as unknown. NO NEED TO GUESS").** The bucket is not switched: a game that
  reports no surface (FH6, F1, AC, ...) keeps all its learning in the starting (sealed) bucket; the diagnostic
  source reads `Unknown`. The inference from grip it replaces (lateral G against the sealed curve at the same angle)
  flipped FH6 between sealed and loose every 2-5 s on tarmac and ended on low-grip - turn-in, trail-braking and exits
  read below the steady-state curve - so the learning was scattered over three buckets: the LED dropped to red as
  each new bucket started, its value kept moving, and the tarmac window stayed low. On load, the Loose / Low-grip
  buckets of such a game (windows, k, speed groups) are dropped - only the guessing could have made them - and the
  next save writes the file without them (`SurfaceBucketMap.IsGuessedBucket`).
- **Switching:** a 1 s hysteresis on the reported surface (an Unknown frame restarts it), then the weights blend
  linearly over 1.5 s.
  Learning goes to the active bucket. Publishing blends the buckets' windows component-wise.
- **Lateral G per speed group** (`SlipAngleSpeedBands`) is kept per game, car, source and surface and learned on
  every surface (owner, 2026-11-22; was per game and car, sealed only), never borrowed: gravel's lateral G is
  typically 60-80% of tarmac's, so tarmac's groups would keep gravel from ever reaching 85% of the band peak, and
  replaying the owner's F1 / FH6 logs with fully learned groups from the start did not shorten the learning at all.
  Groups saved per game and car move to that car's Sealed key for the Plugin Internal source on first use.
- **A car switch starts the classifier again (owner, 2026-11-22),** as a game switch always did
  (`SlipAngleFrameProcessor.Process` resets its frame state when the game or the car changes): the new car
  starts on sealed. (With the guessing then in place, a new car had no curve to be judged against and never left the
  previous car's last verdict - on FH6 a second car learned only into low-grip. Learned data is not touched.)

#### Borrowing tiers, the cold-reference latch, the persisted published window (`KeyedSlipAngleLearner`)

- **Keys** are built only through `ColdStartTierResolver.Key`, with the target folded into the source
  segment: `game|#|car|#|source/target|#|surface`. So "same source" means "same source and target", and
  a rear window is never borrowed for a front key.
- **THE BORROWING RULES ARE IN ONE PLACE (owner, 2026-11-22): `SlipAngleBorrowRules`.** Each parameter has one
  scope there; to change a rule, change its scope:

  | Parameter | May borrow from |
  |---|---|
  | Starting window, slip-ratio source (0-1 / 0-100) | same game and source: any car, any surface |
  | Starting window, angle source (degrees, radians, car angle, Custom) | same game, car and source - another surface only |
  | k | same game, car and source - another surface only |
  | Lateral G per speed group | never |

  Never across games. The tyre compound is read by no game reader, so it is not part of any rule. A key's own
  saved learning (a restart) is not borrowing. A file saved before the rules has any unconfirmed key whose prior
  was borrowed re-resolved under them on load.
- **Cold reference, latched per key.** When a key's learner is created, its borrowing tier is resolved among the
  keys the rule allows:
  - Tier 4: same game and car, other surface;
  - Tier 3: same game, other car (slip-ratio sources only);
  - Tier 1: shipped default.

  **A lender needs learning of its own and some confidence, not a confirmed peak (owner, 2026-11-22; FH6 never
  confirms one, so it never lent).** Within a tier the most confident lender wins. What it lends is weighted by
  that confidence: start = shipped + confidence x (lender's window - shipped), per component - 30% confident moves
  the start 30% of the way, 100% takes the lender's window. That start becomes BOTH this key's prior (so the
  raise-only rule works against it) and its cold reference. Until the key learns anything of its own it follows
  the lender on every read (the start moves as the lender's confidence grows); a key restored from the file keeps
  its saved window. The LED is half-and-half meanwhile (right half the lender's confidence, left half red). Until this key confirms, a strictly better tier appearing
  re-priors it (`SetPrior`, with the raise expressed in absolute degrees). On the frame the key itself
  confirms, the cold reference is latched to the window it was publishing just before, and it is never
  swapped again in that session.
- **Hot weight.** A confirmed key publishes `ColdWarmBlend.Blend(cold, learned, w)` per component.
  `w` starts at 0 and is slewed toward `ConcaveCountConfidence(sessionCrossings, 10) ×
  clamp01(quality / 0.5)`. The window is therefore continuous across the confirmation frame, and it
  reaches the pure learned window after 10 session crossings at quality ≥ 0.5.
- **Restart.** `QAdvanceFeedback.Parameters.json` gains `SlipAngleLearners` (`RuntimeDocument`
  version 12). It stores each key's learner state, its LAST PUBLISHED window and its cold tier. Import
  makes the published window the cold reference with `w = 0`, so a restart with no new driving
  publishes exactly what was last published. `SaveIfDue` exports on the data thread at most once per
  interval and hands fresh objects to `RuntimeStore`, whose timer thread never touches the learner.

#### Key data and the normalizer (`SlipAngleKeyDataSettings`, `SlipAngleNormalizer`)

- **One Min / Best / Max triple, on Raw.All.** It uses the `KeyDataPointSettings` slot model
  (mode, game, source) with Auto / Manual and Global / Per-Game. The Auto / PerGame flags are
  **per source**, like `KeyDataPointSettings` (R24; the spec described a single pair).
- Values are in degrees, step 0.1, range 0.5–45. Validation: `0 < Min < Best < Max`, gaps ≥ 0.2°.
- The shipped cold default is **5 / 8 / 10°** (End 15°), for native and fallback alike.
- Under Auto the editors are read-only on the tab AND the ShakeIt panel (the same VM state), and show
  the learned, borrowed or default window.
- A Manual triple applies only once `ManualOverrideGate` is satisfied (30 s moving AND handover
  confidence ≥ 0.95, R28). The manual slot is seeded once from the learned window, as Lock does.
- **N(α):** a monotone cubic through (0,0), (Start,15), (Min,30), (Best,60), (Max,80), (End,100),
  End = 1.5 × Max, clamped to 100 for published values.
- **Start value (2026-11-19, `docs/slip-angle-design.md` §8).** `SlipAngleWindow.Start` = ⌊0.5 × Min⌋
  whole degrees, derived (never persisted or edited). When it is 0 (Min < 2°) the Start knot is
  **omitted** and the curve runs (0,0) → (Min,30) as before. `SlipAngleNormalizer.SourceFor` (the
  inverse) bisects through the same knots. The tab and the ShakeIt panel show a read-only line
  "Start: 2° (50% of Min)" (`SlipAngleStartText`), or "Start: — (Min below 2°)"; the tab's left graph
  gets a dashed Start marker at 15 only while the window has a Start.
- **Nᵤ(α):** the same below End. Beyond End it extends the Max→End segment linearly, +20 points per
  0.5 × Max. Only the drift state machine uses it, so a trend above End (250% → 180% of Max) is still
  visible.

#### Projection and drift (`SlipAngleProjectedEngine`, `DriftAttenuator`)

- **Preset (`ProjectionChannel.SlipAngle`):**
  - anchor inputs 30 / 60 / 80, smooth ranges 3 / 6 / 3 (owner, 2026-11-22; was 30 / 62 / 78, 3 / 2 / 2);
  - outputs 10 / 20 / 60 (Min → 10%, Best → 20%, Max → 60%; Best was 30%);
  - Start **15** → 0 (2026-11-19: the projector's Start input sits on the normalizer's Start knot, so
    an angle below Start projects to 0), End 100 → 100. The "Linear" preset keeps the current Start, so it
    draws a 15 → 100 line.

  The anchors sit exactly where the learned Best and Max map (60 and 80), so Best and Max read exactly 20 and
  60, each in the middle of its own plateau. "Restore all defaults" and choosing the Curve preset give this
  curve; a saved config still holding the previous shipped curve, unedited, moves to it on load, and any
  curve the driver changed is kept. Pulse-at-max is available and off by default.
- **Order:** project → cold-start maturity scale → drift attenuation → × landing fade × speed fade →
  pedal gate → pulse.
- **The drift attenuator** (pure, allocation-free, dt-correct):

  ```
  Normal ──(sNᵤ > DriftStart for Hold, rise in the hold window ≤ StabilityRange)──▶ Drifting
  Drifting:   factor = 1 − (1 − To)·S(t),  S(t) = t^γ/(t^γ+(1−t)^γ);  out = Min(factor × calcPeak, calc)
  Drifting ──(sNᵤ ≤ peak − SwingMargin for TrendConfirm)──▶ Recovering (locked = factor × calcPeak)
  Recovering: out = Min(locked, calc)
  Recovering ──(sNᵤ ≥ trough + SwingMargin for TrendConfirm)──▶ Normal
  Recovering ──(sNᵤ < 30)──▶ Normal
  Drifting / Recovering ──(sNᵤ ≤ 80 and throttle > ref + Rise, or throttle ≥ Level)──▶ Normal
  ```

  Owner rules of 2026-11-20 (`docs/slip-angle-design.md` §9.7):
  - **Drift start** (`SlipAngleDriftStart`): the angle `Max(1.2 × Max, Min(30°, 1.5 × Max))` as its Nᵤ,
    never below 80 (Max 15° → 22.5° = End).
  - **Locked output:** `calcPeak` is the highest calc since the drift began, so a falling angle is
    never scaled again: the eased value is held until calc drops below it.
  - **Throttle clear:** at or below Max, `ref = Min(ref, throttle)`; the flag clears on
    `throttle > ref + ThrottleRiseClear` (20) or `throttle ≥ ThrottleLevelClear` (90). The frame
    processor passes the frame's throttle; the Test Effect passes none.

  sNᵤ is Nᵤ through an EMA (`SmoothingTime`). The first sample after construction or Reset seeds the
  EMA directly. The defaults are:

  | Hold | Stability | To | Duration | γ | Smoothing | Swing | Confirm | Throttle rise | Throttle level |
  |---|---|---|---|---|---|---|---|---|---|
  | 250 ms | 20 pts | 40% | 120 ms | 2.0 | 50 ms | 8 pts | 80 ms | 20 pts | 90% |

  - **The raw/smoothed escalation rule (R25, final review).** The rise inside the hold window is
    `max(young raw rise, smoothed rise)`, each being the largest upward excursion in the window. The
    young raw rise only STARTS from samples taken while the EMA is young (< 3τ since it was seeded at
    construction / Reset / landing): a freshly seeded EMA lags a spin's onset and would under-read it.
    Once the EMA is warm only the smoothed rise counts, so raw frame-to-frame noise (washboard, rumble
    strips) cannot block a held drift. With τ = 0 raw and smoothed are the same series. Smoothing
    still governs entering (> 80) and both trend confirmations. A rise above `StabilityRange` is an
    escalation (a spin): the attenuator stays Normal at full output. The window slides, so a slide
    that stabilises later is still caught.
  - **No escalation exit from Drifting (R26).** Once drifting, only the confirmed-decrease path leaves.
  - **Robustness:** `dt` is clamped to `min(0.1 s, HoldSeconds / 2)` (so even the 0.05 s minimum hold
    needs two frames), and a bad dt ages timers by 1/60 s.
  - **Gaps:** `SlipAngleFrameProcessor.NoteGap` (no game / pause / menu) resets the attenuator, like
    `Configure` and a game switch.
  - **Caller contract:** airborne and the landing wobble call `Reset()`.

  The owner's worked example is pinned: a drift at 80 eases to 32; released 100 ms into the ramp with
  factor 0.8 and calc 80, the output is 64.

  One known consequence, recorded as an owner follow-up in the report: with the defaults, a spin
  slower than about 80 pts/s is classed as a drift.

#### Published properties, ShakeIt effect, Test Effect

- **Product properties** (appended after every earlier name):
  - `SlipAngle.Raw.All` (degrees, null when unavailable, never a fake 0);
  - `SlipAngle.Normalized.All`;
  - `SlipAngle.Projected.All` (final: attenuated, faded, pulsed).
- **33 `Diag.SlipAngle.*` diagnostics** are appended at the end of the diagnostics and CSV columns:
  - per-wheel α, per-axle α, k and the per-axle learned Best;
  - the source tier, the drift state and factor, and the Min/Best/Max window;
  - maturity, window quality, borrowing tier, wobble remaining and pedal gate;
  - the impact flag, exclusion remaining and yaw residual;
  - the surface bucket, its weights and its source;
  - the unit, the unit-undetermined flag, the unit votes and the native block.
- **`QAdvanceFeedbackSlipAngleContainer`**:
  - the type name is frozen; the display name is "Slip Angle" (R37: no QAdvanceFeedback prefix on
    effect names);
  - category QAdvanceFeedback, ONE placement;
  - default gain 75%, the same as Lock/Slip (R38); tone 40 Hz low, 25 Hz high, no noise;
  - it plays the data-thread `SlipAngle.Projected.All` and returns 0 once that frame is older than
    500 ms (`SlipAngleEffectOutput`).

  Its curve slot is the read-only Raw.All degrees → Projected graph (`SlipAngleGraphView`). The panel
  has Trigger By and its thresholds, Auto/Manual, the Min/Best/Max editors, the fallback warning, a
  status line and the Test Effect. It is synced two-way with the tab through `SettingsHub`.
- **Test Effect** (`SlipAngleTestViewModel` / `SlipAngleTestEngine`):
  - one slider for Raw.All in degrees, 264 px wide (fits the ShakeIt panel), 0 → 3 × Max;
  - a "100%" tick at its middle (End);
  - it runs the real normalization, projector and `DriftAttenuator` (no learner, no pedal gate, no
    landing logic, full confidence) and publishes through `TestEffectSession`, with the 10 s auto-off.

  The dot sits at x = `Min(slider, End)`, y = the actual pulse-free output. While drifting it eases
  from Max toward To, which is the owner's own "slight drift" rule (80 → 32, R39). During the test,
  Raw and Normalized read 0 or null, as they do for the Lock/Slip test.

#### G-Force integration ("Integrate Slip Angle")

- **Settings:** `GForceSettings.IntegrateSlipAngle` defaults **on**, for fresh installs AND for
  upgrades: a settings file without the field loads true (R31). `WheelSlipAngleShakeScale` defaults to
  `DefaultShakeScaleFor(mode)`, like the Lock and Slip scales. Both are on the G-Force tab and the
  G-Force ShakeIt panels (hub field `IntegrateSlipAngle`). The engine's own property defaults to false;
  `ApplyTo` sets it.
- **Per frame**, on unscaled values:

  ```
  gate  = (IntegrateWheelLockAndSlip && Max(lock, slip) ≥ T) || (IntegrateSlipAngle && angle ≥ T)
  drive = Max over the enabled contributions of lock × sL, slip × sS, angle × sA
  ```

  The input is `SlipAngle.Projected.All` (already drift-attenuated) from the same frame. Everything
  downstream is unchanged: modes, wave, feeling, tail and re-arm. The mode strings read
  "lock/slip/angle", and the `ShakeApplyMode` enum names stay (they are persisted).
- **The G-Force Test Effect** gains a Slip Angle bar.

### Shared cores (extracted behaviour-preservingly)

Four pieces are shared between Lock/Slip and Slip Angle, so the protection logic exists only once.
Each of the three extractions is proven **bit-identical** against a verbatim frozen copy of the
pre-extraction code in `QAdvanceFeedback.Tests\Frozen\`. Those copies are deliberate test oracles
(R1), not duplication to remove.

| Core | Extracted from | Lock/Slip use | Slip Angle use | Proof |
|---|---|---|---|---|
| `Core\Normalized\CrossingGate` + `CrossingGateParameters` (`TeachingWeightMode`: Fixed / MultiplyConfidence) | `SlipCrossingGate`, `LockCrossingGate` (now thin named wrappers with the shipped parameters; their call sites, settings and tests are unchanged) | `.Slip` / `.Lock` shipped sets | `.SlipAngle` (45° ceiling, 0.5° rise, no collapse test, no saturated-wheel guard) | `CrossingGateEquivalenceTests`: 200 seeds × 3000 frames per wrapper (spikes, NaN/zero dt, saturated wheels, back-to-back crossings, hold edges) plus exact-threshold boundary streams, against `FrozenSlipCrossingGate` / `FrozenLockCrossingGate` |
| `Core\Normalized\CorroboratedLevelEstimator` + `CorroborationParameters` | `GripLearner`'s adaptive-peak state and `ApplyEvidenceWeightedObservation` (persisted hit-field JSON names unchanged) | `CorroborationParameters.GripLearnerDefaults` (8 g, 3 g, 0.30 / 0.15, floor 0.05, cooldown 3 / 40, base 0.10) | Max estimator in degrees (cap 45, jump 15, floor 0.2°, level floor 0.5°) | `CorroboratedLevelEstimatorEquivalenceTests`: 200 seeds × 5000 values at the defaults, non-default caps, confidence schedules, and `GripLearner` public state, against `FrozenGripLearnerCorroboration` |
| `Core\Normalized\ColdStartTierResolver` | `KeyedScaleLearner`'s borrowing-tier scan | `KeyedScaleLearner.ResolveTier` | `KeyedSlipAngleLearner` (target folded into the source segment) | `ColdStartTierResolverEquivalenceTests`: 300 seeds × 600 operations, the shipped learner and the bare resolver, against `FrozenColdStartTierResolution` |
| `Core\Normalized\ImpactGuard` + `ImpactGuardParameters` | new | not wired (`ImpactGuardParameters.Disabled` exists, so turning it on later is a one-parameter decision) | `.SlipAngle` (8 g, 3 g jump, 5 retro frames, 1.5 s, 0.3 g yaw) | `ImpactGuardTests`. No frozen copy exists because there was no earlier code |

Every pre-existing Lock/Slip, `GripLearner` and `KeyedScaleLearner` test passes unedited. The Phase 3
checkpoint recorded Lock/Slip as bit-identical, with the suite green.

### Lock/Slip additions: Test Effect group sliders, fallback warning

- **Group sliders (`Settings\TestGroupSliders`, `KeyDataNormalizationInverse`).** The Lock/Slip Test
  Effect used to place its dot at `x = Aggregate(sources)`, `y = Project(Aggregate(Normalize(wheels)))`.
  Normalization is non-linear, so the dot left the curve whenever the wheels differed. The panel now
  shows group sliders by routing:
  - Mono: **Single Channel**;
  - FrontRear: **Front / Rear Channel**;
  - LeftRight: **Left / Right Channel**;
  - Corners: none.

  The four wheel sliders stay in every routing.
  - A group value is computed, never stored: `N⁻¹(Aggregate(N(FL..RR), weights).<group>)`. `N` is
    exactly `KeyDataNormalization.Normalize` with the key data the graph shows. `N⁻¹` bisects for the
    SMALLEST source reaching the target; a target ≥ N(100) returns 100.
  - Dragging a group sets its member wheels through the VM setters, and the group values are
    recomputed once afterwards.
  - The dot is x = the group value, y = the projection from the four wheels. A property test asserts
    that it lies on the curve over random spreads, weights and key data.
  - If the combination weights do not sum to 1, a group slider can settle slightly off where it was
    released. The help text says so.
- **Fallback warning (`Core\BranchNativeness`, `Core\NativenessBroadcaster`,
  `Ui\ViewModels\FallbackWarningViewModel`).**
  - `BranchNativeness` classifies the Raw engine's selected Lock/Slip branch:
    - Native: WheelSpeedLock / RPS / RPSandDummyRadius for Lock; Precalibrated slip / Slip data /
      Wheels speed for Slip.
    - Fallback: Braking vs speed, the RPM-vs-speed variants, and the no-capabilities defaults.
    - Unknown: Direct slip feedback, No branch matched.
  - `NativenessBroadcaster` publishes a new pair only after it has been stable for 0.5 s of data-thread
    time, so a flickering branch never broadcasts. It clears immediately when no game is running.
    `SettingField.SourceAvailability` stays notification-only. The VMs read nativeness from plugin
    properties through `IPanelHost` (R9).
  - The orange label ("Not natively supported by this game — using the fallback estimate") shows on the
    Wheel Lock / Wheel Slip tab and the matching ShakeIt panel, ONLY when the source is Plugin Internal.
    It is hidden with no game running, so no screenshot taken offscreen shows it.

## SimHub-dependent vs. pure/testable

This boundary is what makes almost this entire plugin unit-testable without a running SimHub
process:

- **SimHub-dependent** (references `SimHub.Plugins`/`GameReaderCommon`, cannot be constructed
  outside a live SimHub host): `QAdvanceFeedback.cs` (the plugin composition root),
  `SimHubTelemetryAdapter.cs`, `PropertyPickerLauncher.cs`,
  `SimHubScriptEditor.cs`, `SimHubExpressionEvaluator.cs`, `WheelSourceResolver.cs`,
  `QAdvanceFeedback.SlipAngle.cs` / `QAdvanceFeedback.Clutch.cs` (partials of the composition root),
  `ViperAvailabilityProvider.cs`, everything under `Integration\ShakeIt\`; the WPF views in
  `Ui\Views\` are not SimHub-typed but are WPF, so the net8 tests do not compile them either;
  `Settings/SettingsControl.xaml(.cs)` (the thin page host), `QAdvanceFeedback.SettingsPage.cs` and
  `Settings/NumericEditor.xaml(.cs)`.
- **Pure/testable** (plain C#, no SimHub reference at all): everything under `Core\` (Layers 1/3/4/5,
  G-Force, Slip Angle, Clutch), `Ui\Mvvm\` and `Ui\ViewModels\`, `SlipAngleRawSources.cs`, plus the
  settings POCOs and `ConfigStore`/`RuntimeStore` (which take logging as plain `Action<string>`
  delegates rather than a direct SimHub logger reference).

The test project (`QAdvanceFeedback.Tests`) enforces this by link-compiling the pure files directly
as source into a net8.0 assembly with zero SimHub package references, rather than referencing the
built net48 plugin DLL - a SimHub dependency creeping into a file that is supposed to be pure breaks
the test build immediately, not just a runtime assumption nobody checks.

## File-by-file map

Verified against the directory listing on 2026-11-20 (branch `feature/slip-angle`). Every `.cs` /
`.xaml` file under `QAdvanceFeedback\` appears below.

### `QAdvanceFeedback\` (composition root, Layer 2, SimHub-facing helpers)

| File | Layer | Purpose |
|---|---|---|
| `QAdvanceFeedback.cs` | composition root | The `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` entry point - wires every layer together each frame. |
| `QAdvanceFeedback.SlipAngle.cs` | composition root (partial) | Slip Angle composition: reads the Layer 2 inputs, calls `SlipAngleFrameProcessor` once per running frame (after Lock/Slip Projected, before G-Force), publishes the plain result, keeps the UI snapshots, the debounced tier (warning) and the identity tier (key-data slot), persists the learners; own try/catch (`HealthSubsystems.SlipAngle`). |
| `QAdvanceFeedback.Clutch.cs` | composition root (partial) | Clutch Bite: resolves the clutch, computes/publishes `Clutch.Bite.Output`, learns the bite point. |
| `QAdvanceFeedback.SettingsPage.cs` | composition root (partial) | The settings page: ONE `SettingsPageViewModel`, created on the first `GetWPFSettingsControl` and disposed in `End()`; each view SimHub asks for is a thin `SettingsControl` over it. `SettingsPageStateChanged` replaces every push into the page; `ManualGateReadyForDisplay`. |
| `SimHubTelemetryAdapter.cs` | 2 | Maps SimHub's `GameData` onto Layer 1's `TelemetrySample`; captures the raw diagnostics snapshot; supplies the Slip Angle body inputs. |
| `ITelemetryAdapter.cs` | 2 (contract) | The interface `SimHubTelemetryAdapter` implements. |
| `SlipAngleRawSources.cs` | 2 (pure) | The per-game Slip Angle raw-data table (paths, unit, transform, raw surface paths), built only from the source file, and `SlipAngleRawSourceReader`, which reads it through a lookup delegate into `SlipAngleInput`. SimHub-free, link-compiled into the tests. |
| `SlipAngleSourceFile.cs` | 2 (pure) | `QAdvanceFeedback.SlipAngleSource.json` (1.2.1): the row/document types, the checked `SlipAngleSourceCatalog`, the compiled-in copy (`SlipAngleSourceCompiled`) and `SlipAngleSourceStore` (create, reload on a game change, the page's save, broken-file fallback). Newtonsoft only, link-compiled into the tests. |
| `KnownSourceColdStartReference.cs` | Layer 4 support | Which of the two input sources this plugin has measured cold-start evidence for, from a channel's composite `SourceIdentity`. |
| `ViperAvailabilityProvider.cs` / `ViperStore.cs` | Settings-adjacent / persistence | Live check for viper4gh's CalcLngWheelSlip plugin (faults under health `ViperAvailability`); read/write of `QAdvanceFeedback.Viper.json`. |
| `WheelSourceResolver.cs` | Layer 4 input | Resolves one of `WheelChannelSettings`'s source fields (plain property, JavaScript or NCalc) to a live 0-100 reading. The ShakeIt Built-in source never goes through it (it reads the tap, or falls back to Raw per channel). |
| `PropertyPickerLauncher.cs` / `SimHubScriptEditor.cs` / `SimHubExpressionEvaluator.cs` | Settings-adjacent | SimHub-reflection helpers for the settings UI's picker/script-editor buttons and expression evaluation. |
| `PropertyPublisher.cs` | publish boundary | Registers every published SimHub property (`Register`/`AttachTier`/`AttachTierNullable`) - the SimHub-`IPlugin`/`AttachDelegate`-dependent half of the class; net48-only. |
| `PropertyPublisher.State.cs` | publish boundary | The SimHub-free half of the same `partial class`: every backing field, every `Update*` setter (incl. `UpdateSlipAngle`), every `*Snapshot` accessor, and `SnapshotAllValuesForCsv` - link-compiled into the test project so a CSV header/row mismatch is caught. |
| `CsvExportWriter.cs` | diagnostics | Writes every published property to a CSV file when "Export session to CSV" is on. |
| `ConfigStore.cs` / `RuntimeStore.cs` | persistence | JSON load/save for settings and for learned runtime state (incl. `SaveSlipAngleLearners`, an in-memory cache the timer thread only snapshots). |
| `ShakeItCalibrationJson.cs` / `ShakeItPrecalibrationConverter.cs` | persistence | Short-name serialisation rules for Layer 3's ShakeIt calibration types; the one-shot SimHub precalibration import. |

### `QAdvanceFeedback\Core\` (Layer 1 + shared primitives)

| File | Layer | Purpose |
|---|---|---|
| `ITelemetryFrame.cs` / `TelemetryFrame.cs` | 1 | One frame of game-agnostic telemetry (the Slip Angle work adds the Slip Angle body inputs: forward local velocity, yaw rate, airborne, kerbs, vertical G, surface-grip hint). |
| `ITelemetrySample.cs` / `TelemetrySample.cs` | 1 | Current + previous frame + elapsed time. |
| `Corners.cs` | shared | Four-wheel value struct, fixed FL/FR/RL/RR index order. |
| `ClampMath.cs` | shared | Publish-boundary clamping (`To0100`/`To01`) and safe-conversion helpers. |
| `MathHelpers.cs` | shared (Layer 3 formulas) | Clamp/Map/Offset/piecewise-map remapping helpers. |
| `AggregationWeights.cs` / `Aggregator.cs` / `WheelAggregate.cs` | shared (Layers 3/4/5) | The physically-motivated axle/side blend that turns four per-wheel values into Front/Rear/Left/Right/All. |
| `ILegacyWheelLockSlipEngine.cs` / `LegacyWheelLockSlipResult.cs` / `WheelLegacyResult.cs` / `LegacyThresholds.cs` | 3 (contract) | The public contract `RawCalculatorEngine` implements, and the driver-configurable pedal thresholds that gate it. |
| `WheelSlipBranchNames.cs` / `WheelSlipBranchSelector.cs` | 3 (selection) | The diagnostic branch-name constants and the pure capability-priority selector. |
| `BranchNativeness.cs` | 3 (classification) | Pure: is the selected Lock/Slip branch a native game signal, a speed/RPM fallback estimate, or unknown - drives the orange fallback warning. |
| `NativenessBroadcaster.cs` | publish boundary | Holds the published Lock/Slip nativeness pair, notifying only after 0.5 s of stability (data-thread time); clears at once with no game. |
| `RawWheelTelemetrySnapshot.cs` / `RawWheelTelemetryBuilder.cs` | 2/3 boundary | The per-wheel raw telemetry + capability snapshot Layer 3's dispatch reads, and its null-vs-zero gating logic. |
| `IValueDistributionLearner.cs` | 3 (contract) | The learner contract `StreamingPercentileLearner` implements. |
| `OnlineDistributionLearner.cs` | 4 (KeyedScaleLearner support) | A separate streaming mean/variance learner used by the per-source scale calibration mechanism. |
| `PublishedPropertyNames.cs` / `AllPublishedProperties.cs` | publish boundary | Every published property name, product and diagnostic (Slip Angle's appended at the end). |
| `TelemetryLearningGate.cs` | 4/G-Force/Slip Angle | Shared "is this frame valid evidence for a cross-frame learner" gate (pit/replay/session-restart). |
| `AccelerationUnits.cs` | 2 | m/s² <-> G conversion, used once at the SimHub-facing edge. |
| `RobustBandEstimator.cs` | shared (G-Force) | Index-based pool estimator used by `GForceMaxLearner` - see docs\robust-auto-gforce-report.md. |
| `ColdWarmBlend.cs` | shared (Layer 4 / Slip Angle) | The dispersion-weighted cold/warm persistence blend shared by `GripLearner`, `KeyedScaleLearner` and the Slip Angle published window. |
| `KeyedTelemetrySupport.cs` | 2/3 boundary | Per-game sustained-evidence promotion of `WheelOnLooseSurfaceFrontLeft` support; never demotes. |

### `QAdvanceFeedback\Core\RawCalculator\` (Layer 3 concrete engine)

| File | Purpose |
|---|---|
| `RawCalculatorEngine.cs` | The `ILegacyWheelLockSlipEngine` implementation - dispatches per frame, owns every stateful learner/filter. |
| `BrakeSpeedSlipModel.cs` | Pedal+speed+RPM-derived per-wheel Lock/Slip model (the branch used when no wheel-level telemetry exists). |
| `BrakingVsSpeedModel.cs` | Car-level pedal+speed-only Lock/Slip model. A faithful port of SimHub's `GetSimpleBraking`; the former "low-speed fix" was removed in 1.0.7.0. |
| `DispatchBranchFormulas.cs` | The remaining per-branch formulas (wheel rotation, wheel speed, precalibrated slip, calibrated distributions, wheel-speed delta). |
| `WheelRotationLockFilter.cs` | Per-wheel EMA-smoothed lock estimate from wheel rotation rate vs. ground speed, dt-corrected. |
| `StreamingPercentileLearner.cs` | A bucketed running histogram. No longer on the Raw hot path since 1.0.7.0. |

### `QAdvanceFeedback\Core\RawCalculator\Calibration\` (the ShakeIt calibration port, 1.0.7.0)

Every type here is a port of the SimHub type of the same name. They exist so Layer 3 and ShakeIt
produce the same numbers from the same inputs.

| File | Purpose |
|---|---|
| `ICalibrationData.cs` | The contract shared by a live calibration and a shipped preset; `GetPercentile` never returns null. |
| `CalibrationData.cs` | The live histogram: adaptive bucket ladder, positives-only counter, memoised percentiles, running-maximum fallback. |
| `PreloadedCalibrationData.cs` | A shipped per-game preset, blended with live evidence at a fixed 0.25. |
| `CalibrationDataProvider.cs` | Owns every calibration, keyed `track;car;metric`. |
| `GameCalibrationBounds.cs` | The three per-game wheel-speed-delta bounds. |
| `TimeMovingAverage.cs` | The per-gear wheel-speed-delta reference (reconstructed from usage). |

### `QAdvanceFeedback\Core\Normalized\` (Layer 4, and the shared cores)

| File | Purpose |
|---|---|
| `NormalizedWheelLockSlipEngine.cs` / `NormalizedWheelLockSlipResult.cs` | The Layer 4 engine and its published result shape. |
| `CrossingGate.cs` | **Shared core (Slip Angle work):** the parameterised crossing gate (`CrossingGateParameters` `.Lock` / `.Slip` / `.SlipAngle`, `TeachingWeightMode`). Window opening is proven bit-identical to the pre-extraction gates; the taught basis deliberately differs since 2026-11-19 (walk-back stays inside the rise, no live fallback, `LearnCeiling` 95 for Lock/Slip) - see `docs\slip-smax-crossing-gate-design.md` §10. |
| `SlipCrossingGate.cs` / `LockCrossingGate.cs` | Thin named wrappers over `CrossingGate`; their public constants are authoritative (`CrossingGateParameters.LockShipped` / `SlipShipped` are built from them) - still two classes so either side can be disabled alone (`docs\slip-smax-crossing-gate-design.md`). |
| `CorroboratedLevelEstimator.cs` | **Shared core (Slip Angle work):** the evidence-weighted, corroborated level estimator (`CorroborationParameters`, `GripLearnerDefaults`) extracted from `GripLearner`; used in g by Lock/Slip and in degrees by the Slip Angle Max. |
| `ColdStartTierResolver.cs` / `ColdStartTier.cs` | **Shared core (Slip Angle work):** the borrowing-tier resolver extracted from `KeyedScaleLearner` (Tier 4 other surface / 3 other car / 2 other game / 1 none), and the tier enum + `ColdStartTierFloors`. |
| `ImpactGuard.cs` | **Shared core (Slip Angle work, new):** crash/contact protection (plausibility cap, jump detector with retro invalidation and a timed exclusion, yaw-consistency residual); on for Slip Angle, not wired for Lock/Slip (`ImpactGuardParameters.Disabled`). |
| `GripLearner.cs` / `KeyedGripLearner.cs` / `GripLearnerKeyMigration.cs` | The car-relative learned-peak reference, keyed per game+car+source(+surface), with migration for older persisted key shapes. |
| `KeyedScaleLearner.cs` | Per-source scale calibration, anchored to a shared physical reference; Slip's cross-channel borrow goes through a resolver (`SetCrossChannelLockIdentityResolver`). |
| `PluginInternalSourceIdentity.cs` | The internal (Plugin Internal) key per channel, equal to `RawSourceFallback.RawIdentity`; the engine feeds it every frame. |
| `InternalSourceKeyMigration.cs` | Pure one-time load migration of the retired `__layer3_raw_fallback__` key into the internal key (scale, anchors, grip). |
| `CrossChannelSourcePairing.cs` | Pure: which Lock key a Slip key may borrow cold-start evidence from (same source type only). |
| `LockAnchorLearner.cs` | Wheel Lock only: learns the source-space S75/S90 reference points of the four-range mapping. |
| `ManualOverrideGate.cs` | The kick-in gate for a manual key data set (30 s moving + handover confidence ≥ 0.95); reused as-is by Slip Angle. |
| `SourceIdentity.cs` | Computes a stable composite key from a channel's four Source/ScriptType fields. |
| `SurfaceLooseFraction.cs` | Continuous sealed/loose surface blend weight (Lock/Slip). |
| `LongitudinalDirectionResolver.cs` | Resolves Slowing/SpeedingUp/Unknown from differentiated ground speed. |
| `AchievedMotion.cs` | Degradation-tier G-magnitude resolution used by diagnostics. |

### `QAdvanceFeedback\Core\SlipAngle\` (the Slip Angle stack)

| File | Purpose |
|---|---|
| `SlipAngleInput.cs` | One frame's raw-data inputs (native per-wheel angles, kerbs, raw surface values); null = unavailable, never zero. |
| `SlipAngleUnit.cs` | Degrees / radians / tangent / unverified. |
| `SlipAngleNativeSupport.cs` | Per-game native promotion (5 s of finite, plausible, non-constant moving frames), the 45° block, and unit detection by episode voting; diagnostics for unit/votes/block. |
| `YawSignDetector.cs` | Learns the per-game relative yaw sign k ∈ {+1, −1} from lateral G vs V·r, with hysteresis. |
| `SlipAngleGate.cs` | Trigger By (brake/throttle/any) and thresholds, speed fade 10→20 km/h, learn ≥ 30 km/h, airborne (`IsFlying`), landing wobble S-curve, 200 ms hop exclusion; `SlipAngleGateSettings`, `SlipAngleGateVerdict`. |
| `SlipAngleRawCalculator.cs` / `SlipAngleRawResult.cs` | Per-wheel → axle mean (kerb excluded) → front × k → Max/Min blend into Raw.All; fallback body slip. |
| `GripCurveBins.cs` | 0.25° bins over 0–30° with a decaying per-bin P90 of lateral G (seeded exactly from the first 20), plateau/Best/Min and the still-rising shape check; persisted state. |
| `SlipAngleWindow.cs` | A Min/Best/Max window in degrees (End = 1.5 × Max), the derived Start (⌊0.5 × Min⌋, null when Min < 2°), validation, shipped 5/8/10. |
| `SlipAngleStartText.cs` | The read-only "Start: N° (50% of Min)" / "Start: — (Min below 2°)" line for the tab and the panel. |
| `SlipAngleWindowLearner.cs` | One key's learner: candidate filter, crossing-taught Max with corroboration and the level epoch, retro delay line and rollback, latched peak confirmation, raise-only prior, maturity, window quality, Export/Import. |
| `KeyedSlipAngleLearner.cs` | One learner per (game, car, source/target, surface); borrowing through `ColdStartTierResolver`, the per-key cold-reference latch, the hot-weight blend, k, maturity/tier, the persisted published window, `SaveIfDue`. |
| `SurfaceBucketClassifier.cs` | Sealed / loose / low-grip from the game's own surface only (`SurfaceBucketMap`: rF2 and GT7); otherwise Unknown, never switched or guessed; 1 s hysteresis, 1.5 s blend. |
| `SlipAngleNormalizer.cs` | N(α) (clamped) and Nᵤ(α) (unclamped past End), with the (Start, 15) knot; the inverse `SourceFor`. |
| `SlipAngleDriftStart.cs` | The drift start angle `Max(1.2 × Max, Min(30°, 1.5 × Max))` and its Nᵤ (never below 80). |
| `ISlipAngleKeyData.cs` | What the processor needs from the persisted key data (Core keeps no dependency on Settings). |
| `SlipAngleFrameProcessor.cs` | The pure per-frame pipeline in its pinned order. |
| `SlipAngleIdentityTier.cs` | The tier that picks the key-data identity (Native / Fallback slot): last in-session confirmed tier for the current game, kept across pause / menu, reset on a game switch. |
| `SlipAngleKeyDataModel.cs` | THE Slip Angle key-data display rule (Lock/Slip's): the exact key's capped window (`LearnedWindow`), the default, what the boxes show (`Shown`), the seed of what is shown (`TrySeed`, `SlipAngleDisplayRuleKeyData`), and the display context (`SlipAngleKeyDataContext`, persisted last game/car/tier for the restart display). |
| `SlipAngleFrameResult.cs` / `SlipAngleStatus.cs` | Immutable per-frame values for the publisher/CSV/UI, and the UI status (tier, warning, texts). |
| `SlipAnglePublishedNames.cs` | The three product names and the 33 appended diagnostics, in CSV order. |

### `QAdvanceFeedback\Core\Projection\` (Layer 5)

| File | Purpose |
|---|---|
| `ProjectedWheelLockSlipEngine.cs` / `ProjectedWheelLockSlipResult.cs` | The Layer 5 engine and result shape. |
| `OutputProjector.cs` / `MonotoneCubicCurve.cs` / `PiecewiseCurve.cs` / `ProjectorSettings.cs` / `ProjectorAnchorEditor.cs` | The driver-editable curve and its settings/UI-editing helper (`ProjectionChannel.SlipAngle` preset added for Slip Angle). |
| `PulseGenerator.cs` / `PulseSettings.cs` | The optional pulse-at-maximum stage. |
| `ColdStartScale.cs` | The cold-start device-feel amplitude scale (seven-anchor curve, tier floors), shared by Lock/Slip and Slip Angle. |
| `SlipAngleProjectedEngine.cs` | Slip Angle Layer 5: project → maturity scale → drift → landing/speed fade → pedal gate → pulse. |
| `DriftAttenuator.cs` / `DriftSettings.cs` | The Normal/Drifting/Recovering state machine and its tunables (ranges, sanitise). |

### `QAdvanceFeedback\Core\GForce\`

| File | Purpose |
|---|---|
| `GForceEngine.cs` / `GForceOutput.cs` / `GForcePublishedNames.cs` | The washout-style G-force engine and its published 8-channel output (Slip Angle work: `IntegrateSlipAngle`, `WheelSlipAngleShakeScale`, `slipAngleAll0100`). |
| `GForceMaxLearner.cs` | Per-game/per-car learned maxima via `RobustBandEstimator` over a 2-minute real-time window. |
| `GForceShake.cs` | The shake modulation: band placement (shift-not-squash) and `SineHoldWave`. |
| `ShakeFeeling.cs` | Opposite phase (shipped default), same phase, or blending. |
| `ShakeApplyMode.cs` | How the shake is spread across the eight pads (see the mode table above). |

### `QAdvanceFeedback\Core\Clutch\`

| File | Purpose |
|---|---|
| `ClutchBiteEngine.cs` / `ClutchBiteCurve.cs` / `ClutchBiteDetector.cs` / `ClutchBiteLearner.cs` / `ClutchLearningParameters.cs` | Clutch Bite (docs\clutch-bite-design.md): clutch resolution, the bite S-curve, the bite detector, the per-key learner and its tunables. |

### `QAdvanceFeedback\Core\ShakeItEffects\` (native ShakeIt effects, SimHub-free half)

| File | Purpose |
|---|---|
| `NativeEffects.cs` | The native effect list and per-effect defaults (gain 75% for Lock/Slip/Slip Angle, 100% otherwise). |
| `ToneDefaults.cs` | Starting tone values per effect (Slip Angle 40 / 25 Hz). |
| `EffectRouting.cs` | WheelLock/WheelSlip routing (Mono / FrontRear / LeftRight / Corners). |
| `FrameSnapshot.cs` / `SlipAngleEffectOutput.cs` | The data-thread frame snapshot with its 500 ms staleness rule; what the Slip Angle effect plays. |
| `GraphPoint.cs` | A graph canvas point. |
| `TestAwareOutputGate.cs` / `TestEffectSession.cs` | When an effect may output (in game, or while its own test runs); one test at a time, 10 s auto-off. |
| `ShakeItTapPropertyNames.cs` / `ShakeItTapValues.cs` | SimHub's own built-in lock/slip values read through a native effect (diagnostics + the ShakeIt source), with per-channel game support and the game id. |
| `ShakeItSourceAvailability.cs` | The per-channel ShakeIt source state machine (NotSetUp / SetUpIdle / Supported / NotSupported), its 0.5 s broadcaster, and the testable profile-inspection logic (`IShakeItProfileReader`, `ShakeItProfileInspection`). |
| `ShakeItSourceResolution.cs` | Pure per-channel routing (Configured / ShakeIt built-in / Raw fallback), a frame's values + identity, and the canonical ShakeIt identity. |
| `ShakeItAlgorithm.cs` | ShakeIt algorithm defaults, threshold → filter mapping, fingerprint (`#alg=`), mute emulation, immutable inputs/snapshot. |

### `QAdvanceFeedback\Core\Viper\`

| File | Purpose |
|---|---|
| `ViperAvailabilityResolver.cs` / `ViperDocument.cs` / `ViperPropertyNames.cs` / `ViperSupportedGames.cs` | viper4gh CalcLngWheelSlip as a Lock/Slip source: availability, persisted notes, property names, supported games. |

### `QAdvanceFeedback\Core\Health\` (resilience model support)

| File | Purpose |
|---|---|
| `HealthRegistry.cs` | The pure registry every guarded boundary reports into from inside its own catch block. |
| `HealthEntry.cs` | One registry entry: subsystem, severity, localization key, detail, first time, count, SimHub-compatibility flag. |
| `HealthSeverity.cs` | `Degraded`/`Failed`. |
| `HealthSubsystems.cs` | The fixed subsystem names: `ScriptEditor`, `PropertyPicker`, `ExpressionEvaluator`, `ViperAvailability` (renamed from `ShakeItExport` 2026-11-19), `ShakeItEffects`, `ShakeItProfile` (new 2026-11-19), `ClutchBite`, `CapabilityDetection`, `ConfigPersistence`, `RuntimePersistence`, `CsvExport`, `BackgroundFlush`, `TelemetryAdapter`, `PropertyPublish`, `Init`, `Shutdown`, `SettingsUi`, `SlipAngleSource`, `SlipAngle`. |
| `HealthRecoveryTracker.cs` | Clears one subsystem's entry after N consecutive good frames (Slip Angle: 300). |
| `SafeCall.cs` | The wrapper every published property's value provider runs through. |

### `QAdvanceFeedback\Core\MotorsExport\`

| File | Purpose |
|---|---|
| `MotorsExportPropertyNames.cs` | The legacy ShakeIt Motors export property-name shape. Never read any more: it builds the canonical ShakeIt source identity and supplies `WheelSuffixes`. (`MotorsExportAvailabilityResolver.cs` was removed 2026-11-19 with the export path.) |

### `QAdvanceFeedback\Core\Localization\`

| File | Purpose |
|---|---|
| `Strings.cs` / `StringTableEn.cs` / `StringTableZhHans.cs` | The UI string table (English/Simplified Chinese); parity enforced by `LocalizationTests`. |
| `StringTable{En,ZhHans}.Areas.cs` + `.Page` / `.LockSlip` / `.Curve` / `.GForce` / `.General` / `.Panels` / `.SlipAngle.cs` | Per-area partial string files (each area's keys in its own file, the same area in both languages); `.Areas.cs` adds them all. |

### `QAdvanceFeedback\Core\Runtime\`

| File | Purpose |
|---|---|
| `RuntimeDocument.cs` / `RuntimeCache.cs` | The persisted-learned-state document and its in-memory dirty-tracked cache. Version 11 (1.0.7.0) added Layer 3's ShakeIt calibration; **version 12 (Slip Angle) adds `SlipAngleLearners`** (per key: learner state, last published window, cold tier). Older files load cleanly. Since 2026-11-19 the save snapshot also keeps the grip learners' `RaiseCandidateG/Hits`, `LowerCandidateG/Hits` and `QuietStreak`. Restart display context (additive): `LastKeyDataGame/Car` (Lock/Slip) and `LastSlipAngleGame/Car/Tier` (Slip Angle). |
| `AtomicFile.cs` | The single shared atomic-replace write path for every file this plugin owns. |

Layer 3's calibration is written with **short JSON names** (`mx`, `v`, `s`, `c`, `p`, `mm`, `cf`,
`lo`, `hi`, `ll`) because it is the largest section of that file and is rewritten on a timer while
driving. The naming lives in `ShakeItCalibrationContractResolver` at the project root, not as
attributes on the Core types, so `Core\` keeps its no-serialiser rule. That resolver also **omits**
`AutoCalibrationData` (a live object reference the provider re-points every frame) and the derived
`IsReady`/`Completion` getters. Changing a short name is a breaking file change - do it only with a
Version bump.

### `QAdvanceFeedback\Settings\`

| File | Purpose |
|---|---|
| `QAdvanceFeedbackSettings.cs` | The root settings object (Lock/Slip/SlipAngle/GForce/Clutch/General); `RestoreDefaults` carries key data across. |
| `WheelChannelSettings.cs` / `WheelSourceSet.cs` | One Lock/Slip channel's sources (archived per source mode), weights, thresholds, curve, pulse, `ShakeItAlgorithm`; `KeepPresetSourcesWhileShadowed`. |
| `ShakeItAlgorithmSettings.cs` | The ShakeIt algorithm block's persisted settings (Lock: legacy, sensibility, lock-only; Slip: legacy, mute) and `Snapshot`. |
| `SlipAngleChannelSettings.cs` | The Slip Angle channel: source (Plugin Internal / Custom), gate, weights, key data, curve, pulse, drift, crossing-gate switch. |
| `SlipAngleKeyDataSettings.cs` | Slip Angle Min/Best/Max per slot (mode, game, source), per-source Auto/PerGame flags, one-time seed latch, validation. |
| `GForceSettings.cs` | G-Force settings + learned-maxima import/export (Slip Angle work: `IntegrateSlipAngle`, `WheelSlipAngleShakeScale`; per-mode `DefaultShakeScaleFor`/`DefaultShakeTriggerFor`). |
| `GForceReadout.cs` / `GForceTestFrame.cs` | The max-G readout label; one G-Force Test Effect frame through the real engine (with the Slip Angle bar). |
| `ClutchBiteSettings.cs` | Clutch Bite settings. |
| `GeneralSettings.cs` | Diagnostics/CSV-export toggles. |
| `SourceMode.cs` / `ScriptType.cs` / `SourceButtonMode.cs` / `ShakeItSourceOptions.cs` / `RawSourceFallback.cs` | Source enums, which sources the ShakeIt panel offers (ShakeIt only while set up or selected), the Raw fallback when a source cannot work (`RawIdentity`), `MatchesRaw` (Custom rows equal to Raw's) and `MatchesShippedPreset` (an identity equal to the Raw, ShakeIt (fingerprint stripped) or shipped Viper preset; such a Custom channel gets no Custom cold start). |
| `DefaultWheelSources.cs` | The shipped default source text for Plugin Internal mode. |
| `KeyDataPointSettings.cs` / `KeyDataPointDefaults.cs` / `KeyDataPanelModel.cs` | Lock/Slip manual SMax/S90/S75 per slot, shipped defaults, the panel rules. |
| `KeyDataNormalization.cs` / `KeyDataNormalizationInverse.cs` | A source value normalized by key data; its bisection inverse (`N⁻¹`, smallest source reaching a target) for the group sliders. |
| `LockSlipTestEngine.cs` / `TestBalls.cs` / `TestGroupSliders.cs` | The Lock/Slip Test Effect calculation, its ball placement, and the group slider maths (dot always on the curve). |
| `SlipAngleTestEngine.cs` | The Slip Angle Test Effect calculation (real normalizer, projector and drift attenuator). |
| `SourceToProjectedGraph.cs` / `SlipAngleSourceToProjectedGraph.cs` | The "Source → Projected" graphs (Slip Angle: Raw.All degrees 0 → End). |
| `SettingsHub.cs` / `ConfigFileWatcher.cs` | Two-way settings sync between the page and the ShakeIt panels (Slip Angle fields: Slip Angle trigger/thresholds/key data/projector, `IntegrateSlipAngle`; the five ShakeIt algorithm fields; nativeness and ShakeIt state in `SourceAvailability`, which the wheel tabs re-sample on); `SwitchSource`, THE one source switch used by the hub and the page's Source combo alike; the backup on-disk watcher. |
| `NumericEditor.xaml` / `NumericEditor.xaml.cs` | The text box + −/+ editor used everywhere on the page. |
| `SettingsControl.xaml` / `SettingsControl.xaml.cs` | The control `GetWPFSettingsControl` returns: a thin host whose content is `Ui\Views\SettingsPageView` (`x:Name="Page"`), code-behind exactly `InitializeComponent(); DataContext = viewModel;` (MvvmRuleTests). The old 3,651-line code-behind, its partials (`.Sync`, `.GForceTest`, `.GForceShake`, `.SlipAngle`, `.Clutch`, `.ShakeItSource`), `ApplyDirtyState.cs` and `PageFieldRefreshPlan.cs` were deleted on 2026-11-20 (`docs\full-mvvm-report.md`). |

### `QAdvanceFeedback\Ui\` (MVVM, see "UI: MVVM" below)

| File | Purpose |
|---|---|
| `Mvvm\ObservableObject.cs` / `RelayCommand.cs` / `IUiDispatcher.cs` / `ITicker.cs` / `LeadingColumns.cs` / `IActivatable.cs` | Building blocks; WPF-free label-column maths; `Activate`/`Deactivate` for a view model whose view comes and goes. |
| `ViewModels\SettingsDraft.cs` | The page's draft / baseline / live model, merged at JSON-leaf level (`IsDirty`, `AdoptExternal` - ShakeIt wins -, `Apply` - edited leaves only, in place -, `ReplaceDraft`, `Reloaded` with the changed paths). |
| `ViewModels\SettingsPageViewModel.cs` / `SettingsPageContext.cs` / `PageSectionViewModel.cs` | The page: draft, hub subscription for its whole life, Apply / Restore all defaults, dirty + banner, the tab view models; the context every section shares (draft, host, dispatcher, ticker, dialogs, clipboard, `Guard` / `ReportFailure`); the section base (`Edit<T>` writes the draft, a reload re-reads every property). |
| `ViewModels\ISettingsPageHost.cs` / `IWheelChannelPageHost.cs` / `IKeyDataPageHost.cs` / `IGeneralPageHost.cs` / `PageServices.cs` | What the page needs from the plugin: `ISettingsPageHost` (`ApplySettings`, `CreateDefaultSettings`, `PluginStateChanged`, `LogError`), the optional area hosts, and the composite `IPluginSettingsPageHost`; `IDialogService`, `IClipboard` (and the unused `IPropertyPicker` / `IScriptEditor`). |
| `ViewModels\WheelChannelPageViewModel.cs` / `TriggerThresholdViewModel.cs` / `SourcesViewModel.cs` / `SourceRowViewModel.cs` / `CustomReferenceViewModel.cs` / `AggregationViewModel.cs` | The Wheel Lock / Wheel Slip tab (one class, `IsLock`): trigger thresholds, sources (one switch path, shadow, Viper game button, ShakeIt state and algorithm block), the four source rows, Custom's cold-start reference, wheel combination. |
| `ViewModels\KeyDataPageViewModel.cs` / `CurveEditorViewModel.cs` / `PulseViewModel.cs` | The tab's key data (panel rules, immediate save with the 700 ms debounce, never Apply), curve editor + both graphs' data, pulse. |
| `ViewModels\GForcePageViewModel.cs` / `GForcePageAxisViewModel.cs` / `GForcePageShakePreview.cs` | The G-Force tab: maximum-G rows and readouts (1 s while shown), splits, feel, direction, the hosted `GForceShakeViewModel`, the preview data, the hosted Test Effect. |
| `ViewModels\GeneralViewModel.cs` / `HealthListViewModel.cs` | The General tab (diagnostics / CSV switches, ShakeIt import, version) and its health list. |
| `ViewModels\IPanelHost.cs` / `ITestEffectHost.cs` / `PanelViewModelBase.cs` | What panel/test VMs need from the plugin (Slip Angle work: nativeness, Slip Angle snapshots, `PublishSlipAngleTest`); the counted activate/deactivate base. |
| `ViewModels\FallbackWarningViewModel.cs` | The orange Lock/Slip fallback warning (Plugin Internal + fallback branch only). |
| `ViewModels\ShakeItSourceViewModels.cs` | `IShakeItSourceStateHost` (optional host capability), `ShakeItSourceUi` (pure list/label/note rules), `ShakeItSourceStatusViewModel`, `ShakeItAlgorithmViewModel`. |
| `ViewModels\WheelChannelEffectViewModel.cs` / `KeyDataViewModel.cs` / `LockSlipTestViewModel.cs` | Wheel Lock/Slip panel, its key data, its Test Effect (group sliders). |
| `ViewModels\SlipAngleTabViewModel.cs` / `ProjectorEditorViewModel.cs` | The Slip Angle settings tab; one projection-anchor row (Lock tab rules). |
| `ViewModels\SlipAngleEffectViewModel.cs` / `SlipAngleTestViewModel.cs` | The Slip Angle ShakeIt panel; its Test Effect (one 0 → 3 × Max slider, "100%" tick). |
| `ViewModels\GForceEffectViewModel.cs` / `GForceTestViewModel.cs` | G-Force panel; G-Force Test Effect (with the Slip Angle bar). |
| `ViewModels\ClutchViewModels.cs` | Clutch Bite tab/panel and its test. |
| `ViewModels\GForceShakeViewModel.cs` | The G-Force tab's shake block: both Integrate switches, the shared settings and their enabled state, the three scales, the mode-pick scale/trigger reset and the feeling-pick frequency rewrite; load/save into `GForceSettings`. |
| `Views\WheelChannelEffectView.xaml(.cs)` / `GraphView.xaml(.cs)` | Wheel Lock/Slip panel and curve-slot graph. |
| `Views\SlipAngleTabView.xaml(.cs)` | The Slip Angle settings tab, laid out like the Wheel Lock tab. |
| `Views\SlipAngleEffectView.xaml(.cs)` / `SlipAngleGraphView.xaml(.cs)` | The Slip Angle ShakeIt panel and its curve-slot graph (with the Test Effect). |
| `Views\GForceEffectView.xaml(.cs)` / `GForceTestView.xaml(.cs)` | G-Force panel and Test Effect view. |
| `Views\ClutchBiteView.xaml(.cs)` / `ClutchGraph.cs` | Clutch Bite view and graph. |
| `Views\NormalizedCurveGraph.cs` / `GraphFurniture.cs` | The bindable Normalized → Projected curve plot; the shared gridlines/ticks/dashed key-point markers. |
| `Views\TestControls.cs` / `TrackAlignment.cs` / `LabelColumnSync.cs` / `WpfInfrastructure.cs` | Test drag targets; pure-layout attached behaviours; the WPF `IUiDispatcher`/`ITicker`. |
| `Views\ViewBehaviors.cs` / `WpfPageServices.cs` | The attached behaviours `ViewModelLifetime.Activate` and `EventCommand.Loaded` / `DropDownOpened`; the WPF `IDialogService` (MessageBox) and `IClipboard` (a locked clipboard swallowed). |
| `Views\SettingsPageView.xaml(.cs)` | The page shell: `MainTabs` (the old tab names, one ScrollViewer per tab), the unapplied banner, Apply / Restore all defaults, the old page's implicit styles. |
| `Views\WheelChannelPageView.xaml(.cs)` / `WheelChannelKeyDataView.xaml(.cs)` / `WheelChannelCurveView.xaml(.cs)` / `WheelChannelPulseView.xaml(.cs)` | The Wheel Lock / Wheel Slip tab (one view, used twice; one SharedSizeGroup pair at tab level) and its key data, curve and pulse parts. |
| `Views\GForcePageView.xaml(.cs)` / `GForcePageShakePreviewGraph.cs` / `GeneralPageView.xaml(.cs)` | The G-Force tab and its shake preview graph; the General tab. |

### `QAdvanceFeedback\Integration\ShakeIt\` (native ShakeIt effects, SimHub-facing half)

| File | Purpose |
|---|---|
| `QAdvanceFeedbackWheelChannelContainers.cs` / `QAdvanceProjectionFilter.cs` | WheelLock/WheelSlip effects (own routing) and their pass-through filter with the read-only graph. |
| `QAdvanceFeedbackSlipAngleContainer.cs` | The Slip Angle effect (frozen type name, display "Slip Angle", one placement, gain 75%). |
| `QAdvanceFeedbackGForceContainers.cs` / `QAdvanceFeedbackClutchBiteContainer.cs` | G-Force pad-pair effects; Clutch Bite effect. |
| `PluginPageHost.cs` | The running plugin as the settings page sees it (`IPluginSettingsPageHost`): forwards to the plugin's own members (source blocking, Viper document, learned display rules, manual gate, Apply, ShakeIt import, health, version) and owns the SimHub picker / script editor / evaluator. |
| `QAdvanceFeedbackEffect.cs` / `ShakeItEffectSupport.cs` / `ShakeItBuiltInTap.cs` / `PluginPanelHost.cs` | One effect output; shared helpers; SimHub's own lock/slip tap (heartbeat, game support, hidden containers configured from the algorithm snapshot, mute emulation); the live plugin as the panels see it (implements `IShakeItSourceStateHost`). |
| `ShakeItProfileInspector.cs` | Reads both ShakeIt plugins' current profiles at 1 Hz on the UI thread (`SimHubShakeItProfileReader<T>`) for the "set up" check. |

### `QAdvanceFeedback.Tests\Frozen\` (test oracles)

| File | Purpose |
|---|---|
| `FrozenSlipCrossingGate.cs` / `FrozenLockCrossingGate.cs` / `FrozenGripLearnerCorroboration.cs` / `FrozenColdStartTierResolution.cs` | Verbatim copies of the pre-extraction shipped code. The equivalence tests run identical randomised streams through these and the shared cores and require bit-identical results. Never edit them to make a test pass. |

## UI: MVVM (standing rule)

Owner's project-wide rule (2026-11-17). It applies to all UI: the plugin settings page and the native
ShakeIt effect panels.

- **View models hold the state and the rules.** They implement `INotifyPropertyChanged` (base:
  `Ui\Mvvm\ObservableObject`). Actions are commands (`Ui\Mvvm\RelayCommand`).
- **Views bind to them:** one-way, two-way and command bindings, with dependency properties where a
  control needs them.
- **Code-behind is wiring only:** exactly `InitializeComponent(); DataContext = viewModel;`. Lifetime
  and the few events with no bindable property are attached behaviours declared in XAML
  (`Ui\Views\ViewBehaviors.cs`): `v:ViewModelLifetime.Activate="True"` (Loaded -> `Activate`,
  Unloaded -> `Deactivate` on an `IActivatable` DataContext), `v:EventCommand.Loaded="{Binding X}"`,
  `v:EventCommand.DropDownOpened="{Binding X}"`. No XAML event-handler attributes. `MvvmRuleTests`
  enforces this over every view, the page host `Settings\SettingsControl.xaml(.cs)` included (and that
  the old `SettingsControl.*.cs` partials stay gone).
- **Forbidden:** handling an event and then setting another control's property (visibility, enabled
  state, text, selection) from it. Expose a view-model property and bind to it instead.
- **Allowed view-side code:**
  - a custom control's own rendering and input handling (the graph, the G-ball pad), exposed through its
    dependency properties;
  - small attached behaviours for pure layout (aligning a toggle's switch track to a column).
- **Threading and time:**
  - View models change bound state only on the UI thread, through `Ui\Mvvm\IUiDispatcher`.
  - Timers and the current time come from `Ui\Mvvm\ITicker`, so debounces, refreshes and auto-off are
    unit-testable.

Folders:
- `Ui\Mvvm` (building blocks) and `Ui\ViewModels` are WPF- and SimHub-free, so the net8 test project
  compiles and tests them.
- `Ui\Views` holds the XAML views, the custom controls and the WPF implementations of `IUiDispatcher` and
  `ITicker`.

**The plugin settings page follows it in full (2026-11-20, `docs\full-mvvm-plan.md`, report
`docs\full-mvvm-report.md`).** Every tab and every native ShakeIt panel is MVVM; new or touched UI
follows the rule from the start.

View-model tree (all in `Ui\ViewModels`, WPF-free):

```
SettingsPageViewModel          one per plugin: created on the first GetWPFSettingsControl, disposed in End()
 |                             owns SettingsDraft, the hub subscription, Apply / Restore, dirty + banner
 |- LockTab, SlipTab           WheelChannelPageViewModel (isLock): Trigger, Sources (+ 4 SourceRows),
 |                             CustomReference, KeyData (KeyDataPageViewModel), Aggregation, Curve, Pulse
 |- SlipAngleTab               SlipAngleTabViewModel (null without an ISlipAngleTabHost)
 |- GForceTab                  GForcePageViewModel: axes, shake (GForceShakeViewModel), preview, Test Effect
 |- ClutchTab                  ClutchBiteViewModel with the learning parameters (null without an IClutchHost)
 `- GeneralTab                 GeneralViewModel + HealthListViewModel
```

**Draft / apply model (`SettingsDraft`).** Live = the plugin's settings object; Draft = a deep copy the
page edits; Baseline = the live state the draft was last synchronised with. Compared at JSON-leaf level,
so a new setting needs no table entry:
- dirty = any leaf where Draft differs from Baseline (one fact behind the Apply button and the banner);
- every hub event from any origin, and showing the page, adopts each leaf where Live differs from
  Baseline into Draft and Baseline - **the ShakeIt side wins and shows at once**, even over an unapplied
  page edit of the same field; other unapplied edits stay;
- Apply writes only the leaves edited on the page into Live, in place (so it can never revert a change
  made elsewhere), then `ApplySettings()` - the plugin applies, saves and broadcasts once;
- Restore all defaults = `QAdvanceFeedbackSettings.RestoreDefaults` on a copy of live (key data and Custom
  sources carried across), applied at once;
- what applies immediately (key data, Viper game list, the ShakeIt import, the clutch learning
  parameters, the Slip Angle key data) writes Live directly; the draft adopts it, the page stays clean.

The subscription lives as long as the page view model, so a change made before the page was ever shown,
or while it was hidden, is on the page when it opens (the owner-reported defect of the old page, which
listened only while loaded).

**Hosts and services.** View models never touch SimHub or WPF: they reach the plugin through
`ISettingsPageHost` (`ApplySettings`, `CreateDefaultSettings`, `PluginStateChanged`, `LogError`, plus
`IPanelHost`) and cast to the optional area interfaces they need (`IWheelChannelPageHost`,
`IKeyDataPageHost`, `IGeneralPageHost`, `ISlipAngleTabHost`, `IClutchHost`, `IShakeItSourceStateHost`) -
a test fake implements only its area. The plugin implements all of them at once through
`IPluginSettingsPageHost` (`Integration\ShakeIt\PluginPageHost`). The plugin never pushes into the page:
it raises `SettingsPageStateChanged` (game change, the 1 Hz learned readout, a key data seed, ShakeIt /
nativeness changes) and the view models re-read through the same display rules as the ShakeIt panels.
Modal questions and the clipboard are `IDialogService` / `IClipboard` (WPF implementations in
`Ui\Views\WpfPageServices.cs`); a failed page action is logged to SimHub, recorded in the health
registry and shown on the General tab at once (`SettingsPageContext.Guard`).

**Per-tab lifetime.** A tab's subscriptions and timers run only while it is shown (`IActivatable`), and
showing it catches up: the wheel tabs' plugin-state / availability listening and 1 s fallback warning,
the G-Force readout timer (hiding the tab also stops its Test Effect), the General tab's health re-read.

**Strings.** Each area's UI strings live in its own partial string file
(`Core\Localization\StringTable{En,ZhHans}.<Area>.cs`), so parallel work never edits the same table.

## Settings screenshot capture rule (standing rule)

`docs\images\settings-*.png` (linked from both READMEs' "Screenshots" section) are rendered by
`tools\screenshot-harness\` (a small, persisted, re-runnable WPF console project - see its own
`ScreenshotHarness.csproj` header comment for exact build/run steps and its `lib\` dependency).
It is deliberately NOT part of `QAdvanceFeedback.sln` (keeps the shipped single-DLL/0-warning build
untouched), but it is a real, committed project now, not a throwaway/out-of-repo scratch harness -
see `docs\screenshot-styling-report.md` for why that changed and how the harness merges MahApps'
real resource dictionaries so the rendered PNGs match SimHub's actual dark theme instead of default
WPF/Aero chrome. It loads the built `QAdvanceFeedback.dll` (via a `ProjectReference`), asks the plugin
for its page exactly as SimHub does (`GetWPFSettingsControl`: the thin `SettingsControl` over
`Ui\Views\SettingsPageView`, `x:Name="Page"`), and renders it to PNG per tab. The Apply/Restore
button row is a `DockPanel.Dock="Bottom"` sibling of `MainTabs` in `SettingsPageView.xaml` - it sits
OUTSIDE the `TabControl`, so a per-tab capture never includes it no matter which tab is selected.
After selecting a tab the harness runs the dispatcher for 600 ms before capturing, so MahApps'
ToggleSwitch thumbs (set by binding after load) are at rest. `--out <dir>` writes a comparison set
elsewhere instead of `docs\images`.

The capture rule, by tab (apply this to every future regeneration without needing to be told again):

- **Wheel Lock, Wheel Slip, Slip Angle, G-Force, Clutch** - these are tall. Capture ONLY the selected `TabItem`'s
  content (its `ScrollViewer`'s content element), excluding the tab strip above and the button row
  below, so the whole tab's settings fit in one image with nothing clipped.
- **General** - short enough that nothing is lost by including the chrome. Capture the FULL
  `SettingsControl` instead: tab strip, the General tab's content, AND the Apply/Restore row.
- **Native ShakeIt effects (1.2.0)** - `docs\images\shakeit-wheellock|wheelslip|gforce|clutch|slipangle.png`
  (plus `shakeit-wheellock-test` - the Lock Test Effect on Front/Rear routing, showing the group
  sliders - and `shakeit-clutch-test`) and their
  `.zh-Hans.png` twins (rendered with `Strings.Culture` = zh-Hans; `README.zh-Hans.md` links the
  twins, `README.md` the English ones), by `tools\screenshot-harness\ShakeItEffectCaptures.cs`. Each is
  exactly what QAdvanceFeedback adds to ShakeIt's effect editor: the effect's settings panel, plus
  for WheelLock/WheelSlip/Slip Angle the read-only projection graph from ShakeIt's response-curve slot, with
  12 px padding. ShakeIt's own surrounding editor (gain, frequencies, channel mapping) cannot be
  rendered offscreen: SimHub's `EffectsContainerBase` constructor needs `WoteverLocalization.dll`,
  which only a SimHub install has. So the panels get an unconstructed container whose routing
  field is set to the default (one channel).

In both cases, measure/arrange the render target at its own full natural extent (height =
`PositiveInfinity` on `Measure`, then an explicit `Arrange` at the resulting `DesiredSize`) rather
than accepting whatever height the hosting preview window happens to impose - the ScrollViewer's
viewport clips tall content, and the DockPanel's fill child stretches to fill an oversized host
window, leaving a dead gap above the button row, if you skip the explicit re-Arrange step.

Output filenames (note `settings-gforce.png`, NOT `settings-g-force.png` - the harness derives the
name from the tab header text and needs a rename on the G-Force one to match the README links):
`settings-wheel-lock.png`, `settings-wheel-slip.png`, `settings-slip-angle.png`, `settings-gforce.png`,
`settings-clutch.png`, `settings-general.png`.

Full rationale, verification evidence and pixel dimensions from the pass that established this rule:
`docs\screenshot-capture-rule.md`.

## Resilience model and the health registry (standing rule)

This plugin is a third party sharing a live SimHub process with every other enabled plugin, ShakeIt,
and dashboards - a fault in our own code must never propagate into SimHub's own dispatch or another
plugin's. Full decompiled evidence for which SimHub entry points are/aren't exception-safe by design
lives in `docs\pipeline-exception-safety-report.md`; this section is the durable summary plus the
health-registry design that surfaces a degrade to the driver instead of leaving it invisible.

**Boundaries hardened, end to end:**

- Every `IPlugin`/`IDataPlugin`/`IWPFSettingsV2` entry point (`Init`, `DataUpdate`, `End`,
  `GetWPFSettingsControl`) is wrapped in its own top-level try/catch, logging once per distinct fault
  (never per frame) and never rethrowing - `Init` in particular matters because SimHub's own
  `EnablePlugin` (the late/manual-enable path) calls it with NO try/catch of its own (decompiled and
  confirmed).
- Every published SimHub property (`PropertyPublisher.Register`'s `AttachDelegate` calls) is wrapped
  through `PropertyPublisher.AttachSafe` -> `Core.Health.SafeCall.Value`, so an individual value
  provider throwing degrades to SimHub's own "no value" for that ONE property rather than propagating
  into whichever dashboard/ShakeIt effect/other plugin happens to be reading it - `PropertyEntry.
  Evaluate()`/`PropertyEntryWrapper.GetValue()` are themselves unguarded SimHub primitives (decompiled),
  so this plugin cannot rely on SimHub to catch a throwing provider for it.
- Every reflection wrapper into an undocumented SimHub internal (`SimHubScriptEditor`,
  `PropertyPickerLauncher`, `SimHubExpressionEvaluator`) resolves once, caches the result, and degrades
  permanently to "unavailable" for the rest of the session on any failure - never retries and throws
  again next frame/next click. `SimHubTelemetryAdapter.CaptureRawTelemetry`'s own `GetFeedbackCapabilities`
  call (a real API, not reflection, but an equally undocumented-shape SimHub dependency) is guarded the
  same way.
- All file I/O (`ConfigStore`, `RuntimeStore`, `CsvExportWriter`) degrades to defaults/stops recording
  rather than throwing, for a missing, corrupt, locked or permission-denied file.
- `RuntimeStore`'s background flush `Timer` callback (`FlushTick`) is the single most dangerous class
  here: an unhandled exception directly on that raw ThreadPool thread can terminate the whole SimHub
  process in .NET Framework. It (and `WriteAtomic`, which now also runs detached inside `Task.Run` off
  that thread) is fully guarded with a broad trailing `catch (Exception)`.
- The settings UI's construction is covered by `GetWPFSettingsControl`'s own guard; every page action
  after that (Apply, Restore all defaults, the key data reset, the script-editor/property-picker action
  button, a hub-driven reload) runs inside `SettingsPageContext.Guard` - the old page's `SafeUiAction`
  kept whole: a SimHub log line (`ISettingsPageHost.LogError`), a Degraded `SettingsUi` health entry and a
  health-list refresh; nothing upstream of a WPF binding or command invoked well after construction would
  otherwise catch a throw. The attached behaviours run their commands inside the views' own guard.
- Pathological telemetry (NaN/infinity/negative or enormous `dt`/null `GameData`/`NewData`/`OldData`/
  missing car or game ids) is guarded at both ends: `DataUpdate`'s own null/state checks short-circuit
  before Core is ever reached, and every Core engine independently finite-checks its own inputs (see
  `AbsentTelemetryTests`/`DtNormalizationTests`/`ClampMathTests` and friends) - either guard alone would
  already prevent a throw, so this is deliberately redundant rather than a single point of failure.

**The health registry (`QAdvanceFeedback.Core.Health`):** a small, pure, SimHub-free registry
(`HealthRegistry`, `HealthEntry`, `HealthSeverity`, `HealthSubsystems`) that every guarded boundary
above reports into from inside its own catch block - never proactively, never "I'm fine" on every
frame, which is what keeps "no entries at all" the healthy state. Each entry carries the subsystem name,
a severity (`Degraded`/`Failed`), a localization KEY for a short driver-readable "what this means for
you" (resolved through `Strings.Get` at display time, never baked in as English), the raw exception
detail (for a bug report, deliberately unlocalized), when it first occurred, and whether the likely root
cause is a SimHub update having moved/renamed/reshaped something this plugin depends on
(`IsSimHubCompatibilityIssue`) - the ONE case the owner asked to be named plainly rather than shown as an
opaque failure. Reporting the SAME subsystem again (e.g. a value provider that keeps throwing every
frame) mutates the one existing entry's timestamp/occurrence count instead of growing the registry -
this is what makes "log once, not per frame" hold even under a persistent fault.

**Settings UI surface (General tab, "Plugin health" group):** invisible/one-line ("All systems normal -
nothing to report.") when `HealthRegistry.Snapshot()` is empty, so it adds no clutter in the normal
case. Otherwise, one bold warning line per degraded subsystem - a driver-readable subsystem name plus
its impact text, in orange for `Degraded` and firebrick for `Failed` - and, for any entry flagged as a
SimHub-compatibility issue, an appended plain-language "this feature needs an update for your SimHub
version" rather than a raw exception. A "Copy details for a bug report" button (shown only when there is
something to report) copies every entry's technical detail (subsystem, severity, timestamps, occurrence
count, exception text) to the clipboard for the owner to paste into an issue. Read when the page view
model is built, each time the General tab is shown, and again after any failed page action
(`SettingsPageContext.ActionFailed`), so a fault during a click is reflected immediately without
needing to reopen the tab (`GeneralViewModel` / `HealthListViewModel`).

**Known unguarded path, stated plainly:** SimHub's own `PluginManager.GetPropertyValue` and the
NCalc/formula-engine call chain that reaches `PropertyEntryWrapper.GetValue()` are themselves confirmed
exception-safe/safe-in-practice by decompilation (see the pipeline-exception-safety report) - this
plugin does not and cannot patch SimHub's own primitives. If some OTHER caller (a different plugin,
ShakeIt's own internals) reaches `PropertyEntry.Evaluate()`/`PropertyEntryWrapper.GetValue()` directly
without SimHub's own wrapping, that remains genuinely unguarded - outside this plugin's reach to fix,
and not claimed as fixed here.

## Where "Private" used to be

Everything under `Core\RawCalculator\` plus `SimHubTelemetryAdapter.cs` used to live in a withheld,
gitignored `Private\` folder outside both projects, with a reflection-based factory
(`AlgorithmFactory`/`PrivateTypeResolver`) resolving them at runtime and falling back to inert stubs
(`InertTelemetryAdapter`/`InertLegacyWheelLockSlipEngine`) when absent. That split, and the machinery
behind it, is gone - see `docs\clean-room-restructure-report.md` for the full history and rationale.

## Mock host (`tools\mock-host`)

A stand-alone WPF app (net48) that runs the real plugin without SimHub. Unlike `tools\screenshot-harness`, it IS part
of `QAdvanceFeedback.sln`, listed first so Visual Studio starts and debugs it on F5. The plugin's own DLL build is
unchanged; the mock host only adds its exe. It has four tabs:
- **Plugin Settings:** `GetWPFSettingsControl`.
- **ShakeIt Effects:** **Add Effect** for the eight native effects, each effect's own `EditControl` and graph,
  saved to `PluginsData\Common\MockHost.ShakeItProfile.json`.
- **Game Simulation:** game id, car, pedals, speed, G, yaw, per-wheel speed / slip / surface flags, the game's
  reported capabilities, and any SimHub property. It is fed to `DataUpdate` at 60 Hz on a background thread.
- **Language:** English or Simplified Chinese for the plugin's page and editors (rebuilt at once; the page's one view
  model is disposed and created again).

The plugin's files go to `<exe folder>\PluginsData\Common` (SimHub's relative storage path).

SimHub's `PluginManager` and ShakeIt containers are created without their constructors, which need
SimHub-only DLLs. `MockGameManager` supplies `FeedbackCapabilities`. Init's ShakeIt profile inspector, which
needs GongSolutions.WPF.DragDrop, is finished with an empty inspector.

`MockHost.exe --capture <folder> [seconds]` saves every tab, and every added effect, as a PNG.

Full description, files and limits: `docs\mock-host.md`.
