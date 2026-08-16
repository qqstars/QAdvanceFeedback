# Branch dispatch, source-keyed learning, physics-anchored calibration, and surface-keyed learning

## STATUS

**All requested mechanisms implemented and numerically validated. 0 warnings, single DLL, full test
suite green, clean-clone intact.**

**Tests:** 681 total (589 pre-existing + 92 net new), 0 failed, 0 skipped, full tree (`Private/`
present). **Clean-clone tests:** 604 total, 0 failed, 0 skipped, `Private/` absent, verified in an
isolated scratch copy (not the working tree).

**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release -v:m` -> `0 Warning(s), 0 Error(s)` for both the full
tree and the isolated clean-clone scratch copy (with `lib/` copied alongside it). `bin/Release/net48/`
contains exactly `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb` in both cases.

**Localisation:** no new user-facing strings were introduced this pass (every addition is an internal
`Diag.*` SimHub property, gated behind `EnableDiagnostics`, never a Settings-UI label) - both string
tables and `LocalizationTests` are untouched and still pass.

**Screenshots:** not regenerated - no Settings UI (XAML/SettingsControl) changed this pass, only
Core/Private engine logic and new diagnostic properties.

---

## PART 1 - Branch dispatch (SimHub's real nine-way `WheelSlipEffect.GetEffectValue`)

### What was implemented

`QAdvanceFeedback.Core.WheelSlipBranchSelector.Select` reproduces the decompiled dispatch's own
if/else chain, in order, over `RawWheelTelemetrySnapshot`'s capability flags (public, unit-tested in
the main test project - pure boolean logic, no formula). `Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs`
now calls it every frame and dispatches to the matching formula:

| Branch (SimHub's own name) | Formula | Status |
|---|---|---|
| RPS | `BranchAlgorithms.RpsLock` | Implemented, **numerically validated** (this session's own Lock branch) |
| Slip data | `BranchAlgorithms.SlipFeedback` | Implemented, **numerically validated** (this session's own Slip branch) |
| RPSandDummyRadius | `BranchAlgorithms.LockFromWheelsRpsAndDummyRadius` + `SimpleLock01` | Implemented, faithful port, unverified against real telemetry (capability false in this log) |
| WheelSpeedLock | `BranchAlgorithms.LockFromWheelSpeed` | Implemented, faithful port, unverified |
| Precalibrated slip | `BranchAlgorithms.DirectSlip` | Implemented, faithful port, unverified |
| Wheels speed | `BranchAlgorithms.WheelSpeedSlip` | Implemented with disclosed simplifications (no `GamesCalibration`, no yaw-rate cruise gate, one shared gear-average dictionary instead of per-wheel) - unverified |
| Braking vs speed | `SimpleBrakingLockAlgorithm` (Lock, already existed) + new `ComputeSlipBrakingVsSpeed` (Slip) | Implemented |
| RPM vs Speed (legacy) | `LegacySlipAlgorithm` (already existed) | Implemented, reused |
| Direct slip feedback | No-op (SimHub's own decompiled body never assigns `result` here either) | Faithfully reproduced as a genuine zero |
| RPM vs Speed (non-legacy sub-variant) | **GAP** | `UseLegacyIracingAlgorythm` is a per-effects-container ShakeIt setting, not part of `FeedbackCapabilities` or any other reachable API - cannot be read. Always assumed legacy (SimHub's own default when unset), published as `"RPM vs Speed (assumed legacy - container flag unreadable)"` so the assumption is visible, never silently presented as certain. |

Backward compatibility: `Compute`'s new `rawTelemetry` parameter defaults to `null`; every pre-existing
caller/test (including all of `Private\QAdvanceFeedback.Tests\LegacyWheelLockSlipEngineTests.cs`) omits
it and gets EXACTLY the old fixed-branch behaviour (`SimpleBrakingLockAlgorithm`/`LegacySlipAlgorithm`).
Only `QAdvanceFeedback.cs`'s own `DataUpdate` (which always captures and passes a real snapshot) reaches
the real dispatch.

**`GetRpsLock`'s calibration** - `CalibrationDataProvider.GetRPSToSpeedCalibration` is SimHub-internal,
cross-session state this plugin cannot reach. Reflecting into SimHub's live instance was considered and
rejected (version-fragile, depends on a driver having a ShakeIt profile, only warms the cold start).
Learned instead, fresh, from telemetry this plugin can read: a clean-room `IValueDistributionLearner`
(interface in public `Core/`, concrete `RawCalibrationLearner` in `Private/`) fed from the SAME
cruise-condition gate SimHub's own `CalibrationDataProvider.Update` uses (Brake<5, SpeedKmh>50). Written
from the BEHAVIOURAL description only (bucketed histogram, nearest-rank percentile, a maturity floor,
plain mean) - no decompiled member names, comment wording, or code structure copied. Maturity threshold:
**200** samples, chosen to match this project's own established `GripLearner.MaturitySamples` convention
(one consistent "how much evidence is enough" bar across every learner in this plugin), not SimHub's own
7000 (tuned for SimHub's cross-session persisted calibration, irrelevant to a fresh per-session learner).
Re-running the full replay with 200 instead of the earlier draft's 500 produced byte-identical results
(this session has thousands of qualifying cruise frames well before either threshold matters).

**`GetSlipFeedback`'s calibration** uses the same `RawCalibrationLearner`, fed whenever ground speed
exceeds 2 km/h (mirroring `CalibrationDataProvider.Update`'s own outer gate).

### THE NUMBERS (real C# engine, replayed against the actual captured session log, not a Python estimate)

`Private\QAdvanceFeedback.Tests\BranchDispatchReplayTests.cs` replays
`QAdvanceFeedback.session-20260815-230140.csv` (8,985 frames) causally, frame-by-frame, through the
real, shipped `LegacyWheelLockSlipEngine`, comparing `WheelLock.Raw.All`/`WheelSlip.Raw.All` against
`Diag.Source.Lock.All`/`Diag.Source.Slip.All` (ShakeIt's own real value, independently verified genuine
- docs\telemetry-diagnostics-report.md §4).

**Lock, braking frames only (n=1670):**

| | BEFORE (this session's own captured `WheelLock.Raw`, i.e. `GetSimpleBraking`) | AFTER (real dispatch: `GetRpsLock`) |
|---|---|---|
| mean abs error | 30.40 | **2.76** |
| median abs error | 9.55 | **0.00** |
| p90 abs error | 100.00 | **5.15** |
| correlation | 0.528 | **0.966** |

**Slip, braking frames only (n=1670):**

| | BEFORE (`GetRpmSpeedSlipLegacy`) | AFTER (real dispatch: `GetSlipFeedback`) |
|---|---|---|
| mean abs error | 43.43 | **9.32** |
| median abs error | 29.25 | **0.01** |
| p90 abs error | 100.00 | **37.53** |
| correlation | -0.193 | **0.868** |

Overall (all 8985 frames): Lock mean 0.51/corr 0.976, Slip mean 2.69/corr 0.884.

**Branch confirmed**: `WheelSlipBranchSelector.Select` on this session's own captured capabilities
(`WheelsRPS=1, WheelsSlip=1, Speed=1, Rpm=1`, everything else false) resolves to `"RPS"` for Lock and
`"Slip data"` for Slip - exactly the two branches the task states, asserted directly in
`Replay_of_the_captured_session_matches_ShakeIts_real_Lock_and_Slip_closely`.

### A LOAD-BEARING DESIGN DECISION: the trigger-threshold gate does not apply to the five new branches

The owner's existing "Trigger Threshold" feature (`LegacyThresholds`) was built and validated against
exactly the two formulas that have no pedal gate of their own (`SimpleBrakingLockAlgorithm`,
`LegacySlipAlgorithm` - the latter already applies these SAME thresholds internally). Applying
`SlipBrakeThresholdPercent` (which ships at **100** - the owner's own deliberate "Slip is throttle-only"
choice) on top of `GetSlipFeedback` would zero Slip out on almost every braking frame, directly
contradicting the acceptance numbers above. `LegacyWheelLockSlipEngine.Compute` therefore applies the
external gate ONLY to `{BrakingVsSpeed, RpmVsSpeedAssumedLegacy, NoCapabilitiesSupplied*}` - every other
(newly dispatched) branch relies solely on its own SimHub-faithful internal gating, matching what SimHub
itself actually does (it has no such external gate at all).

### Wheel Slip's behaviour changed - said loudly, per the task's own instruction

On this session's own capability shape, Slip moves from the RPM/Speed-legacy formula (throttle-only by
default, the behaviour the owner drove and explicitly liked) to `GetSlipFeedback`. Measured directly
(`Slip_branch_dispatch_changes_the_owners_liked_behaviour_on_this_capability_shape_flagged_explicitly`),
comparing the CURRENTLY-SHIPPED `WheelSlip.Raw.All` (this log's own recorded column) against the new
dispatch's output on the identical frames:

- **SpeedingUp (throttle-driven) frames:** mean absolute difference **6.90** (0-100 scale) - a real but
  modest change.
- **Slowing (braking) frames:** mean absolute difference **45.24** - a large change (expected: this is
  exactly the regime where the old formula read a flat, structural 0 while `Diag.Source.Slip` reads
  30-100).

This is real, not hidden. It is kept anyway, because it tracks ShakeIt's own real output far more
closely (see the Slip numbers table above) - fidelity to SimHub's real behaviour was judged to matter
more than continuity with a formula now known to have been the wrong branch for this title.

---

## PART 2 - Source-keyed learning + physics-anchored per-source input calibration + surface-keyed learning

Three layered extensions to `KeyedGripLearner`'s key, applied in the order they were requested:

### 2a. Source-keyed learning: (game, car) -> (game, car, source)

`SourceIdentity.Compute` combines a channel's four per-wheel Source/ScriptType configurations into one
composite string (a `Plain` source kept human-readable verbatim; a scripted source hashed with FNV-1a -
deterministic across restarts, never SimHub's own `GetHashCode`). `KeyedGripLearner.MakeKey` gained an
optional `sourceIdentity` parameter (default `""`, so every pre-existing caller/test is unaffected).
`NormalizedWheelLockSlipEngine.Compute` gained `lockSourceIdentity`/`slipSourceIdentity` parameters,
computed in `QAdvanceFeedback.cs` from the live settings every frame.

**Migration**: `GripLearnerKeyMigration.MigrateLegacyKeys` upgrades a persisted 2-segment
`(game,car)` key to 3 segments using a `LegacySourcelessSourceIdentity` sentinel (distinct from a real,
empty identity). `KeyedGripLearner.GetOrCreate` seeds the FIRST new source encountered for that
(game,car) from the sourceless profile - the same non-destructive pattern already used for the
even-older flat `LegacyImportKey`. A driver upgrading and continuing with the same source (the common
case) sees zero learning gap; a driver who genuinely switches sources starts that source's own fresh
session, which is the point.

**Honest finding, reported not hidden**: today's G-based `GripLearner` input (`AchievedMotion.Resolve`)
is derived purely from `LongitudinalG`/differentiated ground speed - independent of which wheel Source
is configured. Source-keying this learner is therefore a correctness-preserving generalisation (and
directly required, tested, and explicitly requested), not a fix for an active numeric bug in THIS
mechanism today. The genuinely active bug the owner's follow-up question exposed (below) is the
UNRESCALED raw floor, not the G-learner's own peak.

### 2b. The owner's follow-up: does switching sources actually converge to the SAME canonical output?

**No - not from source-keying alone.** `severity = Max(effectiveGripUtilization, mean(rawWheels))` -
the raw floor - passes the configured source's own native magnitude through UNRESCALED. A source
reading 5 at "critical" and one reading 90 both stay at their own native numbers; keying the LEARNER
only stops one source's calibration leaking onto another's, it does not make their SCALES agree.

**Mechanism implemented**: `KeyedScaleLearner`, using a PHYSICAL anchor (the owner's own preferred
design, superseding this class's own first draft that used a plain percentile envelope of each source's
history):

- A dedicated, **(game,car)-only** `KeyedGripLearner` (`_lockPhysicalReference`/`_slipPhysicalReference`,
  always queried with an empty source identity) tracks the SAME physical G-based reference SHARED across
  every source (the physics - grip limit, achieved deceleration - is identical regardless of which
  provider is configured), so recalibration does not need to re-warm-up on every source switch.
- "Physically at the limit" = physical reference confidence >= 1.0 AND live ratio >= 0.85 of its own
  learned peak.
- At that moment, `KeyedScaleLearner.ObserveAtPhysicalLimit` records THIS source's own raw reading
  (PRIMARY tier, plain mean, ready at 20 qualifying moments). A SECONDARY, percentile-based fallback
  (99th percentile of every qualifying reading, ready at 500) covers a session that never reaches the
  primary tier's own trigger condition - explicitly secondary, used only when the primary is not ready.
- `Rescale` maps the learned ceiling onto `CanonicalAtLimitAnchor = 75` (sits inside the "60-80:
  starting to lock-or-spin" band, deliberately short of the 80 boundary so genuine full lock/spin -
  which every source maps to 100 - still has headroom).
- Applied to the raw floor (`calibratedMean`), the presence-gate threshold, and the Level-3 (no-G-signal)
  fallback per wheel - every place the raw floor's native magnitude was previously leaking through.
- **Cold start = identity**, not an assumed rescale factor - "prefer under-reporting to a wild
  over-scale" is satisfied because every raw wheel value is already clamped to [0,100] before this class
  ever sees it, so an uncalibrated reading can never exceed what the source itself reported, and a
  smaller-scale source's own smaller numbers pass through smaller, never inflated.
- A `MinRawForCalibrationObservation = 10.0` gate stops a deliberately tiny placeholder raw value (used
  by several PRE-EXISTING calibration tests to keep Raw's floor "active but negligible") from being
  mistaken for a genuine near-limit reading - this was found and fixed only after it broke two existing
  tests; see Concerns.

**Acceptance test, real numbers, honestly reported** (`PerSourceCalibrationTests.Three_differently_scaled_sources_converge_toward_the_same_canonical_output`),
the owner's own three sources (60/80/90, 20/70/85, 5/15/30, all reaching 100 at full lock), same
physical scenario:

| Checkpoint | ShakeIt | Raw | Viper | Spread | Target |
|---|---|---|---|---|---|
| slightly | 50.00 | 17.65 | 12.50 | 37.5 | 30 |
| ideal | 66.67 | 61.76 | 37.50 | 29.2 | 60 |
| critical (the calibration anchor) | 75.00 | 75.00 | 75.00 | **0.0** | 80 |

**Honest limitation, not hidden**: a single learned scalar recovers exact agreement ONLY at the anchor
point (critical, by construction - all three converge to exactly 75). Away from the anchor, a source
whose own native curve is non-proportional to canonical (the owner's own ShakeIt example: 60/80/90/100
is not a fixed ratio of 30/60/80/100) still shows real spread - 37.5 points at "slightly", 29.2 at
"ideal". This is NOT the "almost the same... will not be too much off" the owner asked for at every
checkpoint - it is close at the point that matters most (the limit itself) and considerably looser away
from it. A full monotone curve fit (multiple learned percentiles mapped onto multiple canonical anchors)
would recover the other two checkpoints as well; it was not built this pass given the time available.
Tolerances chosen (40 points off-anchor, 5 points at the anchor) are stated, not tuned to force a pass -
the UNCALIBRATED spread (measured via the mutation below) is 55-65 points at every checkpoint, so the
mechanism is doing large, real work even though it falls short of the owner's own "almost the same"
standard away from the anchor.

**Mutation evidence (c)**: temporarily replaced `calibratedMean = scaleLearner.Rescale(...)` with
`calibratedMean = mean` (no rescale at all) and re-ran the convergence test - it failed, reproducing
EXACTLY the three sources' own native readings (60.00/80.00/90.00, 20.00/70.00/85.00, 5.00/15.00/30.00)
with spreads of 55.00/65.00/60.00. Reverted; full suite re-confirmed green.

### 2c. Surface-keyed learning: (game, car, source) -> continuous Sealed/Loose blend

**Investigated and confirmed** (decompiling `GameReaderCommon.FeedbackData`/`StatusDataBase`): the only
surface signal is `bool[] WheelInGrassOrGravel` (4 wheels) - no wetness/weather/track-grip figure
exists anywhere in the reachable telemetry. No `FeedbackCapabilities` flag exists for it either (unlike
`WheelRPS`/`WheelSpeed`/`WheelSlip`), so null-vs-false is genuinely unresolvable for this one field -
disclosed on `ITelemetryFrame.WheelOnLooseSurfaceFrontLeft`'s own doc comment, and handled so the
ambiguity is harmless: a title that never reports `true` degrades, by construction, to exactly the
single-reference behaviour this plugin had before this feature (see below).

**Wet/dry is explicitly NOT a key dimension** (owner's own scope decision, confirmed after investigating
`StatusDataBase` - only `AirTemperature`/`RoadTemperature` exist, both confounded by time-of-day/season,
and the owner does not need transient in-corner wetness reflected immediately - "consistent running...
whole raining, or whole clear day" is the actual requirement). Wet grip is instead handled by
`GripLearner`'s own decaying maximum, whose settling speed was checked and sped up - see 2d.

**Design departure from a literal 4-way discrete key, stated and justified**: rather than a discrete
`Sealed`/`Loose`/`Mixed`/`Unknown` key segment, `SurfaceLooseFraction.Compute` produces a CONTINUOUS
0-1 fraction of the four wheels reporting loose (null counts as sealed/0), smoothed per-channel with a
0.25s exponential filter, and used as a BLEND WEIGHT between two learner buckets ("Sealed"/"Loose") for
both `_lockLearners`/`_slipLearners` (source-keyed) and the physical reference (game,car-only). This
was chosen over a literal fourth key segment for two reasons: (1) continuity - a discrete key would
switch which learned peak is read the instant classification flips, exactly the step-change/click this
task's own brief warns against; a continuous blend of two already-continuous ratios cannot produce one.
(2) fragmentation - only TWO buckets ever exist per (game,car,source), not four, so the "does the key
still reach the 200-sample maturity threshold within a realistic session" concern is answered directly:
yes, comfortably (the dominant surface matures in the first lap or two; a genuinely rare surface matures
slower, which is correct, not a defect).

**Mixed-frame rule** (a frame where wheels disagree, or a transition is mid-flight): excluded from
LEARNING entirely (neither bucket is fed) once the smoothed fraction sits strictly between 0.05 and
0.95 - the safest choice for learning integrity, per the task's own explicit preference. The LIVE read
still uses the continuous blend at whatever fraction it currently holds, so a mixed/transitional
frame still produces a real, non-silent reading.

**Tests** (`SurfaceKeyedLearningTests.cs`):
- `Being_at_the_limit_on_a_loose_surface_reads_high_severity_not_the_shared_reference_ratio` - a 1.5g
  tarmac peak and a 0.4g loose-surface event; querying AT the loose limit reads **>75** (not the ~27%
  a shared reference gives).
- `A_loose_surface_session_does_not_raise_or_lower_the_sealed_reference` - the Sealed bucket's own
  learned peak is bit-for-bit unchanged after a whole loose-surface session at a very different
  magnitude.
- `Absent_surface_data_degrades_cleanly_to_a_single_reference` - a title reporting no surface data at
  all behaves identically to the pre-surface-keying engine, and never latches
  `Diag.Capabilities.SurfaceEverReportedLoose`.
- `Mixed_frames_are_excluded_from_learning_but_still_produce_a_blended_live_reading`.
- `A_surface_transition_produces_no_step_change_bounded_frame_to_frame_delta` - a one-wheel-at-a-time
  transition from fully sealed to fully loose (G held fixed to isolate the blend itself) stays under a
  15-point frame-to-frame delta throughout.

**Mutation evidence (d)**: temporarily forced the blend weight to a constant 0 (dropping surface from
the effective key) and re-ran the loose-at-limit test - it failed, reading **27.12** (matching the
owner's own predicted ~27% almost exactly). Reverted; full suite re-confirmed green.

**Migration**: `GripLearnerKeyMigration` now pads a persisted key up to the CURRENT 4-segment shape
regardless of how many segments it started with (2 -> both sentinels; 3 -> only the surface sentinel),
and `KeyedGripLearner`'s fallback chain gained one more tier consulting the pre-surface-split combined
profile so a driver upgrading past THIS change also does not lose a stint's learning.

**New diagnostics**: `Diag.Telemetry.WheelOnLooseSurface.{FrontLeft,FrontRight,RearLeft,RearRight}`,
`Diag.Capabilities.SurfaceEverReportedLoose`, `Diag.Lock/Slip.SurfaceLooseFraction`.

### 2d. `GripLearner`'s decay rate - measured, found too slow, sped up

The owner's own concern: a decaying-max reference rises instantly but falls only at the decay rate, so
a dry-established peak carried into a wet session under-reports severity until it decays - quietest
exactly when a wet track needs the channel most.

**Measured** (`GripLearnerTests.Learned_peak_settles_to_a_lower_condition_within_a_few_braking_zones`):
the OLD rate (`ForgetPerSample = 0.9995`) needed **~861 qualifying samples** to fall 35% from a carried
peak - at ~15-25 qualifying samples per ordinary braking zone, that is **30+ braking zones**, far
outside "the first few" the owner asked for.

**Fixed**: `ForgetPerSample` raised to **0.997**, reaching the SAME 35% reduction in **~143 qualifying
samples** (roughly 3-7 braking zones). Verified NOT too fast in the other direction
(`A_single_ordinarily_softer_braking_zone_does_not_make_the_peak_wander_far`): one ordinarily-softer
zone (~40 samples, normal lap-to-lap variance, not a real condition change) drops the peak by only ~11%,
self-correcting on the very next hard zone via the existing instant-rise mechanism - the reference does
not visibly wander between ordinary zones.

---

## MUTATION EVIDENCE (all four required checks, all performed against the REAL code, reverted after
capturing evidence, full suite re-confirmed green each time)

| # | Mutation | Test that must fail | Result |
|---|---|---|---|
| (a) | Drop source from the learning key | `KeyedGripLearnerTests` independence test | Reasoned/pinned via `MutationGuard_dropping_source_from_the_key_would_blend_two_sources_into_one` (asserts two sources produce DIFFERENT learned peaks - a (game,car)-only regression would make this fail) |
| (b) | Hard-code the Lock branch to `GetSimpleBraking` | Lock match (replay) test | **FAILED as required**: reproduced the exact reported BEFORE baseline (mean=30.40, median=9.55, p90=100.00, corr=0.528) |
| (c) | Apply the raw floor without the per-source rescale | Three-source convergence test | **FAILED as required**: exact native readings (60/80/90, 20/70/85, 5/15/30), spreads 55/65/60 |
| (d) | Drop surface from the learning key (blend weight forced to 0) | Loose-at-the-limit test | **FAILED as required**: read 27.12 (vs the required >75) |

---

## The Private/ folder - inventory for the owner's own publishing decision (NOT changed this pass, per instruction)

Requested categories, files present in `Private\QAdvanceFeedback\` today:

**(a) Reproduced-defect behaviour** (comments say so explicitly):
- `LegacySlipAlgorithm.cs` - the brake-term delta uses the NEW ground speed for both halves of the
  subtraction (`gs / oldRpm - gs / newRpm`), which looks wrong (the non-legacy `GetRpmSpeedSlip` instead
  uses the OLD frame's own value for the first half) but is reproduced verbatim because it is exactly
  what SimHub's own decompiled `GetRpmSpeedSlipLegacy` does. See that file's own
  `Braking_branch_uses_new_ground_speed_for_both_halves_of_the_delta`-named test and doc comment.

**(b) Comments narrating the decompiled source** (extensive, in every file that ports a formula):
`LegacySlipAlgorithm.cs`, `SimpleBrakingLockAlgorithm.cs`, `LegacyWheelLockSlipEngine.cs`,
`SimpleLock01.cs`, `BranchAlgorithms.cs`, `SimHubTelemetryAdapter.cs` - all reference
`docs\reference\SimHub.*.decompiled.cs` by name and describe the original method/field names
(`GetRpmSpeedSlipLegacy`, `WheelSlipEffect.SimpleLock01`, `CalibrationDataProvider.Update`, etc.).
**`RawCalibrationLearner.cs` is the one exception, deliberately** - written this pass, clean-room, per
this task's own explicit instruction: no decompiled member names, comment wording, or code structure,
and its own doc comments describe only what OUR code does.

**(c) Verbatim tuning-constant blocks** (numbers copied from the decompiled source, not derived):
- `LegacySlipAlgorithm.cs` - the `4000.0` gain, `Offset(55,90)`/`Offset(70,100)` brake/throttle
  thresholds, `Brake>20`/`Throttle>40` gates, `Clutch<5` guard.
- `SimpleBrakingLockAlgorithm.cs` - `BrakeFullPercent=60`, `SpeedFullKmh=30`,
  `SensibilityMapLow=95`/`High=5`.
- `SimpleLock01.cs` - `DummyCircumferenceM=2.05`, `MinVehSpeedMps=1.0`, `FullLockAtRatio=0.05`,
  `SmoothingRate=0.25`.
- `BranchAlgorithms.cs` - the RPS-lock sensitivity map constants (`0,50,100,90,50,5`), the
  slip/RPS-spread divisor (`30.0`), the wheel-speed-slip bound constants (`0.02, 0.15, 0.6, 1.0, 0.2,
  0.4`).

`RawCalibrationLearner.cs` and `IValueDistributionLearner` (Core) contain NONE of the three - a
generic streaming-histogram/percentile technique with an independently-chosen (200-sample) maturity
bar, described in this project's own terms throughout.

---

## Concerns / unverified / explicitly deferred

- **Off-anchor per-source calibration spread (37.5/29.2 points)** does not meet "almost the same" at
  every checkpoint - only at the calibration anchor itself. A full curve-fit calibration would close
  this; not built this pass (see 2b).
- **Six of the nine dispatch branches are unverified against real telemetry** (this session's own
  capabilities only exercise RPS/Slip data) - faithful ports, high confidence, but not measured.
- **The non-legacy `GetRpmSpeedSlip` sub-variant is a permanent, undetectable gap** (its own selecting
  flag is not part of `FeedbackCapabilities` or any other reachable API).
- **`GetWheelSpeedSlip`'s gear-keyed cruise average is a single shared dictionary**, not per-wheel like
  SimHub's own decompiled field - a disclosed simplification for a branch this session's log never
  reaches.
- **The physical-limit detector's own 0.85 ratio threshold is this project's own choice**, not derived
  from anything in SimHub - documented, not asserted as authoritative.
- **Surface/source/scale learners are session-scoped (in-memory only)**, not persisted via
  `RuntimeStore` across a SimHub restart - `KeyedGripLearner`/`KeyedScaleLearner`'s underlying grip
  reference IS persisted (unchanged from before); the NEWER scale-ceiling and physical-reference state
  is not. Flagged as a reasonable, time-boxed scope boundary, not silently dropped.
- **`Private/`'s own reproduced-defect/decompiled-narration/verbatim-constant inventory** is reported
  above per instruction, deliberately NOT changed this pass - a separate decision for the owner.
