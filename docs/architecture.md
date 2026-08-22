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
| [**G-Force**](#g-force-how-it-works-and-why) | A washout-style split between a sustained G level and a rate-driven transient, mapped onto a 3-stage pad chain via partition-of-unity piecewise-linear functions; per-game/per-car maxima learned via a trimmed-pool robust estimator over a real-time rolling window; an optional wheel lock/slip left/right shake superimposed on top. | Gives a seat pad a continuous, directional sense of braking/accelerating/cornering load, independent of and complementary to the wheel channels. |

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
| `GForceMaxLearner.cs` | Per-game/per-car learned acceleration/braking maxima via `RobustBandEstimator` over a 2-minute real-time window, no minimum-sample gate. |
| `GForceShake.cs` | The "Integrate Wheel Lock and Slip" shake modulation. |

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
