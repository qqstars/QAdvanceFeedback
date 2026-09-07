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
| [**G-Force**](#g-force-how-it-works-and-why) | A washout-style split between a sustained G level and a rate-driven transient, mapped onto a 3-stage pad chain via partition-of-unity piecewise-linear functions; per-game/per-car maxima learned via a trimmed-pool robust estimator over a real-time rolling window; an optional wheel lock/slip shake superimposed on top, spread across the pads by one of four selectable modes and shaped by one of three selectable feelings. | Gives a seat pad a continuous, directional sense of braking/accelerating/cornering load, independent of and complementary to the wheel channels. |

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

The source is always part of the key because a number that suits a ShakeIt export does not suit our own
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
source seeds again on its own. The plugin bumps a revision counter so an open settings page reloads.

**Defaults are per source type**, resolved through `KnownSourceColdStartReference.Classify`: Lock
85/75/60 and Slip 75 for our Raw and for a ShakeIt export. An UNKNOWN source (a script, an NCalc
expression) deliberately gets no default at all - there is no honest guess for a signal whose scale has
never been measured, so such a channel keeps publishing learned values until a slot is seeded.

**Max-Grip-Only / Best Point Only** hides the two lower anchors and keeps them derived at 0.90/0.70 of
the top one, so switching to the three-point mapping never finds them stale or out of order. Slip
derives them always - it has no native 90%/75% grip measurement - which is why it ships Best Point Only.

**The manual-mode reset** (`ResetKeyDataPoints`) writes the shipped defaults for a source
`KnownSourceColdStartReference.Classify` recognises, marking the slot seeded so the one-time learned
seed cannot immediately overwrite what the driver just asked for. For an UNKNOWN source there is no
default to write, so it calls `KeyDataPointSettings.ClearSlot` instead - values and seeded latch both -
which re-arms the one-time seed and leaves the boxes empty until real evidence arrives. Persists
immediately, like the other reset actions on the page.

**Slip is forced to Max-Grip-Only under Auto** (`EnforceSlipPatternForAutoMode`), and its selector is
disabled while Auto is on. Slip has no native 90%/75% measurement, so its two lower anchors are always
derived; offering the three-point mapping while the plugin generates the numbers itself would present
derived values as measured ones. Manual mode re-enables the choice, because the driver is then supplying
all three. Lock measures all three and is untouched.

**The two curve graphs** (`RenderGraphDecorations`, `RenderKeyPointMarkers`,
`RenderSourceToProjectedGraph`) are drawn in code rather than declared, so they track live settings. The
left one is normalized -> projected, with dashed markers at the canonical normalized positions of the key
data points (S75 -> 30, S90 -> 60, SMax -> 80 - the four-range curve's own knot table), or SMax alone
under Max-Grip-Only. The right one is source -> projected end to end; it has no closed form because the
key data points and the projector compose, so it is sampled every 5 and joined. Both share the same grid:
a 25%-black rule every 20% of height, the 0% rule solid black as a baseline, and 3px ticks every 20% of
width.

**Restore never discards them.** `QAdvanceFeedbackSettings.RestoreDefaults` carries both channels'
`KeyDataPoints` (all slots, plus the Auto/Per-Game switches) across the reset; the three channel-level
source resets only ever touch source fields.

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
- **General tab now shows the running assembly's own file version** (`SettingsControl`'s
  `GetRunningAssemblyFileVersion`, reading `FileVersionInfo` — never a hand-typed literal), so it can
  never drift from the DLL actually loaded.
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
  | `HigherOfGForceOrLockSlip` (**shipped default**, listed first) | that channel's own level, with the CEILING raised by the wheel | centred on the level | applied | 1.3 / 5 |
  | `PerChannel` (all pre-1.0.8 behaviour) | that channel's own level | centred on the level | applied | 1.5 / 5 |
  | `AllChannelsGForce` | the **active chain's terminal** level, applied to all eight | centred on the level | applied | 1.3 / 30 |
  | `AllChannelsLockSlip` | `100 × contribution` — G-force ignored entirely | **from zero** | **not** applied | 1.0 / 30 |

  **The trigger split** (5 vs 30) follows from what a low wheel value DOES in each mode. On a
  per-channel band a trace of lock is a trace of extra width on an animation that was already there, so
  it can be admitted early; on an all-channels band it is a floor under all eight pads at once, which at
  a low threshold reads as exactly the permanent background buzz the threshold exists to stop.

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

  **Switching `ShakeApplyMode` does NOT rewrite the two scales** (owner, 2026-09-06). It used to, on the
  reasoning that a scale meant a different thing in each mode. It does not:
  `contribution = scale × wheel/100` is computed identically in all four modes, and the Lock and Slip
  scales are applied to their own channels individually before the engine takes the larger. Only what
  that contribution *multiplies* differs — the pad's own level in the two G-force modes, the full 0–100
  range in the two wheel-driven ones — so at 1.5 a wheel-driven mode reaches a full-width band from
  wheel 67 up. That is a saturation point worth knowing, not a change of meaning, and not grounds for
  overwriting a hand-tuned value. Both scales ship at **1.5** in every mode.

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
    clamped to 0.1–5.0. At the shipped 0.2 s fastest sweep the default reads as "the pedal moved far
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

- **The Test Effect panel (1.0.8, `SettingsControl.GForceTest.cs`).** A drag-a-ball G-force pad and a
  Lock/Slip bar that push synthesised frames through a **private `GForceEngine` instance** owned by the
  settings page, so a driver can set ShakeIt levels without provoking a real lock-up on track.

  **It publishes to the real properties.** Driving only its private engine moved the on-screen readout
  and nothing else: with no game running `DataUpdate` returns before publishing anything, so
  `QAdvanceFeedback.GForce.*` read null and ShakeIt — the entire reason the panel exists — got nothing.
  `QAdvanceFeedback.PublishTestEffect` writes through the same `PropertyPublisher.UpdateGForce` the live
  path uses (its properties are pull-delegates over that array, so a write lands with or without a
  running game) and sets a flag that makes `DataUpdate` skip its own publish. Passing null clears the
  override; both the toggle going off and the page unloading must do that, or a closed page would pin
  the outputs forever.

  **Pad orientation** (owner, 2026-09-06): the ball marks where the load is thrown, so **up is braking,
  down is accelerating, and dragging left means turning right**. Both pad axes are negated on the way
  into the telemetry frame.

  Two further properties are the whole point of it, and both are guarded:
  - **Every G-Force setting on the page applies, live — into a SCRATCH object.** Each simulated frame
    calls `SaveGForceToSettings` into a throwaway `GForceSettings` and then `ApplyTo(_testEngine)`, so
    the panel is fed from the page's *current* state (Wheel Lock and Slip scales included) without
    touching the plugin's live settings. It used to call the whole of `SaveToSettings()`, which writes
    straight into `_plugin.Settings` — so merely switching the panel on pushed every uncommitted edit
    on the page into the running plugin: an Apply nobody asked for. Extracting the G-Force half into a
    method that takes a target is what made a scratch object possible.
  - **Nothing about it is ever saved** (owner). Its controls are read by neither `SaveToSettings` nor
    `LoadFromSettings`, and `WireDirtyTracking` skips them by the `GForceTest` name prefix — by prefix,
    not a list, for the same reason the sweep itself is reflective. `SettingsControlDirtyTrackingTests`
    guards all three.
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
  lightweight one-second `DispatcherTimer` in `SettingsControl`, started in the constructor and stopped
  on `Unloaded`.

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
| `PropertyPublisher.cs` | publish boundary | Registers every published SimHub property (`Register`/`AttachTier`/`AttachTierNullable`) - the SimHub-`IPlugin`/`AttachDelegate`-dependent half of the class; net48-only. |
| `PropertyPublisher.State.cs` | publish boundary | The SimHub-free half of the same `partial class`: every backing field, every `Update*` setter, every `*Snapshot` accessor, and `SnapshotAllValuesForCsv` - split out so this half (where a CSV header/row column-count mismatch could be introduced) can be link-compiled into the test project and exercised directly. |
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
| `RobustBandEstimator.cs` | shared (G-Force) | Index-based pool estimator (exclude the top outliers, take a band of what remains, blend the band's own max/mean) used by `GForceMaxLearner` for the auto max-G reference - see docs\robust-auto-gforce-report.md. Evaluated for the Normalized-layer `GripLearner`/`KeyedScaleLearner` too; not adopted there (see that report for the measured reason). |
| `ColdWarmBlend.cs` | shared (Layer 4 support) | The dispersion-weighted cold/warm persistence mechanism shared by `GripLearner` and `KeyedScaleLearner` - weights this session's own live evidence against a persisted prior value by the live evidence's coefficient of variation, not sample count alone, so a noisy session converges toward trusting the persisted value instead of overwriting it by volume. |
| `KeyedTelemetrySupport.cs` | 2/3 boundary | Per-GAME-only (not per-car) detection of whether a title genuinely supports the one telemetry field with no matching SimHub capability flag (`WheelOnLooseSurfaceFrontLeft`) - promotes a game to "supported" only after sustained `true` evidence, and never demotes it once promoted, within a session or across a restart. |

### `QAdvanceFeedback\Core\RawCalculator\` (Layer 3 concrete engine)

| File | Purpose |
|---|---|
| `RawCalculatorEngine.cs` | The `ILegacyWheelLockSlipEngine` implementation - dispatches per frame, owns every stateful learner/filter. |
| `BrakeSpeedSlipModel.cs` | Pedal+speed+RPM-derived per-wheel Lock/Slip model (the branch used when no wheel-level telemetry exists). |
| `BrakingVsSpeedModel.cs` | Car-level pedal+speed-only Lock/Slip model. A faithful port of SimHub's `GetSimpleBraking`; the former "low-speed fix" was removed in 1.0.7.0 (see that file's own remarks and the measured divergence table). |
| `DispatchBranchFormulas.cs` | The remaining per-branch formulas (wheel rotation, wheel speed, precalibrated slip, calibrated distributions, wheel-speed delta). |
| `WheelRotationLockFilter.cs` | Per-wheel EMA-smoothed lock estimate from wheel rotation rate vs. ground speed - SimHub's `SimpleLock01`, with dt-corrected smoothing. |
| `StreamingPercentileLearner.cs` | A bucketed running histogram (mean + nearest-rank percentile). No longer on the Raw hot path since 1.0.7.0 - superseded by `Calibration\CalibrationData`. |

### `QAdvanceFeedback\Core\RawCalculator\Calibration\` (the ShakeIt calibration port, 1.0.7.0)

Every type here is a port of the SimHub type of the same name, decompiled from the shipped
`SimHub.Plugins.dll`. They exist so Layer 3 and ShakeIt produce the same numbers from the same inputs,
rather than merely the same formulas.

| File | Purpose |
|---|---|
| `ICalibrationData.cs` | The contract shared by a live calibration and a shipped preset. Note `GetPercentile` returns a plain `double` and never null - that is the whole point. |
| `CalibrationData.cs` | The live, accumulating histogram: adaptive bucket ladder, positives-only point counter, memoised percentiles, and the running-maximum fallback that keeps it answering from the first sample. |
| `PreloadedCalibrationData.cs` | A shipped per-game preset: `MeasuredMaximum x CorrectionFactor x pct/100`, blended with live evidence at a fixed 0.25 - permanently, not as a ramp. `GetAverage` deliberately throws. |
| `CalibrationDataProvider.cs` | Owns every calibration, keyed `track;car;metric`. Pools all four wheels into ONE `Slip` calibration while splitting `RPSToSpeed` by axle - SimHub's own scoping, and the reason the earlier per-wheel Lock learners were retired. |
| `GameCalibrationBounds.cs` | The three per-game wheel-speed-delta bounds that sit alongside the presets in SimHub's own file. |
| `TimeMovingAverage.cs` | The per-gear wheel-speed-delta reference. The ONE type here reconstructed from usage rather than decompiled - it lives in `WoteverCommon`, which this project does not ship; see its own remarks for why that cannot matter at this call site. |

### `QAdvanceFeedback\Core\Normalized\` (Layer 4)

| File | Purpose |
|---|---|
| `NormalizedWheelLockSlipEngine.cs` / `NormalizedWheelLockSlipResult.cs` | The Layer 4 engine and its published result shape. |
| `SlipCrossingGate.cs` / `LockCrossingGate.cs` | The crossing gate that decides which at-the-limit moments are allowed to teach SMax (1.0.8). DELIBERATELY TWO CLASSES, not one shared one: the owner asked for the same mechanism on both channels with separate implementations, so either side can be disabled alone. Full specification in `docs\slip-smax-crossing-gate-design.md`. |
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
| `GForceMaxLearner.cs` | Per-game/per-car learned acceleration/braking maxima via `RobustBandEstimator` over a 2-minute real-time window, no minimum-sample gate. |
| `GForceShake.cs` | The "Integrate Wheel Lock and Slip" shake modulation: band placement (shift-not-squash) and the 1.0.8 `SineHoldWave` - one sine with a hold at each extreme, period always 1/f. |
| `ShakeFeeling.cs` | How the two pads of a pair relate while shaking (1.0.8) - opposite phase (shipped default), same phase, or blending. Replaces the retired "Both-sides blend (%)" spinner; `Blending` pins its own 50% hold and the UI hides the hold control for it. |
| `ShakeApplyMode.cs` | How that shake is spread across the eight pads (1.0.8) - per-channel, all-channels-following-G-force, all-channels-lock/slip-only, or **higher-of-G-force-or-lock/slip** (the shipped default): each pad travels from zero up to whichever cue is louder, so neither can mask the other. The two wheel-driven modes use a ZERO FLOOR (the wheel value is the whole travel); the two G-force-centred ones keep shaking around the pad's current level. |

### `QAdvanceFeedback\Core\Health\` (resilience model support)

| File | Purpose |
|---|---|
| `HealthRegistry.cs` | The small, pure, SimHub-free registry every guarded boundary reports into from inside its own catch block - never proactively, never "I'm fine" on every frame. |
| `HealthEntry.cs` | One registry entry: subsystem name, severity, a localization key, raw exception detail, first-occurred time, occurrence count, and whether the likely cause is a SimHub compatibility issue. |
| `HealthSeverity.cs` | The `Degraded`/`Failed` severity enum. |
| `HealthSubsystems.cs` | The fixed set of subsystem name constants every reporting site uses, so the same subsystem reporting again mutates one entry instead of growing the registry. |
| `SafeCall.cs` | The `SafeCall.Value` wrapper `PropertyPublisher.AttachSafe` routes every published property's value provider through, so one provider throwing degrades to "no value" for that property only. |

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
| `RuntimeDocument.cs` / `RuntimeCache.cs` | The persisted-learned-state document shape and its in-memory dirty-tracked cache. Version 11 (1.0.7.0) added Layer 3's own ShakeIt calibration, the converted shipped presets and per-game bounds, and the source-file timestamps that make the start-up re-import cheap - all in the SAME `QAdvanceFeedback.Parameters.json` as the Normalizer's learned state. |

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
| `QAdvanceFeedbackSettings.cs` | The root settings object (Lock/Slip/GForce/General). |
| `WheelChannelSettings.cs` | One channel's (Lock or Slip) sources, aggregation weights, thresholds, curve, pulse. |
| `GForceSettings.cs` | G-Force tab's settings + learned-maxima import/export. |
| `GeneralSettings.cs` | Diagnostics/CSV-export toggles. |
| `SourceMode.cs` / `ScriptType.cs` / `SourceButtonMode.cs` | Small enums backing the Sources section. |
| `DefaultWheelSources.cs` | Builds the shipped default source text for Plugin Internal mode (a plain reference to Layer 3's own Raw property). |
| `KeyDataPointSettings.cs` | Manual SMax/S90/S75 (Perfect/Great/Good for Slip), stored per slot = (mode, game, source); shipped per-source-type defaults; validation. |
| `ApplyDirtyState.cs` | Tracks whether the settings UI has unsaved edits, for the Apply button's enabled state. |
| `SettingsControl.xaml` / `SettingsControl.xaml.cs` | The one WPF settings control (four tabs). Its dirty tracking is a REFLECTIVE sweep over the generated `x:Name` fields, not an enumerated list - the enumerated list had fallen 52 controls behind the XAML, leaving Apply greyed out after editing any of them. |
| `SettingsControl.GForceTest.cs` | The shake preview graph and the Test Effect panel (1.0.8): a drag-a-ball G-force pad and Lock/Slip bar feeding a private `GForceEngine`, re-fed from the page's live state each frame so every G-Force setting (the Lock/Slip scales included) applies. Produces nothing at all while its toggle is off. |

## Settings screenshot capture rule (standing rule)

`docs\images\settings-*.png` (linked from both READMEs' "Screenshots" section) are rendered by
`tools\screenshot-harness\` (a small, persisted, re-runnable WPF console project - see its own
`ScreenshotHarness.csproj` header comment for exact build/run steps and its `lib\` dependency).
It is deliberately NOT part of `QAdvanceFeedback.sln` (keeps the shipped single-DLL/0-warning build
untouched), but it is a real, committed project now, not a throwaway/out-of-repo scratch harness -
see `docs\screenshot-styling-report.md` for why that changed and how the harness merges MahApps'
real resource dictionaries so the rendered PNGs match SimHub's actual dark theme instead of default
WPF/Aero chrome. It loads the built `QAdvanceFeedback.dll` (via a `ProjectReference`), instantiates
`Settings\SettingsControl.xaml(.cs)` standalone, and renders it to PNG per tab. The Apply/Restore
button row is a `DockPanel.Dock="Bottom"` sibling of `MainTabs` in `SettingsControl.xaml` - it sits
OUTSIDE the `TabControl`, so a per-tab capture never includes it no matter which tab is selected.

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
- The settings UI's constructor is covered by `GetWPFSettingsControl`'s own guard; its top-level
  `Button.Click` handlers (Apply, Restore all defaults, per-source reset, the script-editor/property-
  picker action button) are each wrapped in `SettingsControl.SafeUiAction`/`SafeUiActionAsync` - nothing
  upstream of a WPF event handler invoked well after construction would otherwise catch a throw.
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
count, exception text) to the clipboard for the owner to paste into an issue. Refreshed exactly once, at
the end of the settings control's constructor (`SettingsControl.RefreshHealthUi`, called after every
reflection wrapper this control uses has already been force-resolved by the constructor's own earlier
wiring), and again after any `SafeUiAction`/`SafeUiActionAsync` catch so a fault during a click is
reflected immediately without needing to reopen the tab.

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
