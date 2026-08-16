# Lock-and-animation report — QAdvanceFeedback, Forza Horizon 6 session 20260815-110426

## STATUS

FIXED (item 1 — Wheel Lock silent-at-genuine-lockup defect), IMPLEMENTED (item 2 — Trigger Threshold
restructure, including the coordinator's mid-task source-boundary clarification), IMPLEMENTED (item 3 —
G-Force staged travel animation, replacing the previous pass's washout model). Wheel Slip is untouched
except where a shared method (`LegacyWheelLockSlipEngine.Compute`, `NormalizedWheelLockSlipEngine.Compute`)
unavoidably carries the new Trigger Threshold gate — called out explicitly below, and its own
above-threshold arithmetic is provably byte-identical to before.

**Tests:** 522 total (488 pre-existing + 34 new), 0 failed, 0 skipped, full build (`Private/` present).
**Clean-clone tests:** 479 total (468 pre-existing + 11 new — the rest of the new tests live in
`Private/QAdvanceFeedback.Tests/`, withheld), 0 failed, 0 skipped, `Private/` absent.
**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release -v:m` → `0 Warning(s), 0 Error(s)` for both the full tree
and a from-scratch copy with both `Private/` folders removed (built and tested in an isolated scratch
copy, not the working tree). `bin/Release/net48/` contains exactly `QAdvanceFeedback.dll` and
`QAdvanceFeedback.pdb` in both cases.

**Localisation:** every new UI-facing string (`Group.TriggerThreshold`, the rewritten
`Sources.Threshold.Lock.Note`/`Sources.Threshold.Slip.Note`, `Sources.Threshold.LockSensibility[.Note]`,
and the three re-labelled G-Force strings) exists in both `StringTableEn.cs` and `StringTableZhHans.cs`;
`LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set` passes.

**Screenshots regenerated** (Lock and Slip tabs gained a new "Trigger Threshold" section above Sources,
plus Lock gained a "Lock sensitivity" control; the G-Force tab's Motion Feel section was re-labelled) —
`settings-wheel-lock.png`, `settings-wheel-slip.png`, `settings-gforce.png`, `settings-general.png`,
via the harness at the isolated scratch path, kept out of the repo. General tab unaffected but
regenerated anyway for completeness.

---

## 1 — WHEEL LOCK: diagnosis, revised mid-task, and the fix

### 1a. Initial hypothesis (superseded — recorded for the record)

My first pass diagnosed the defect as Layer 3's RPM/speed "legacy" brake term
(`delta = |gs/oldRpm - gs/newRpm|`) being proportional to current ground speed `gs`, so the same physical
lockup reads numerically smaller the slower the car is going. This was evidenced from the log alone
(`WheelLock.Raw.All` pinned near 100 during frames 853-916 while `Diag.MotionMagnitudeG` climbed through
1.5-4.3g, then crashing to exactly 0 for frames 918-924 while magnitude was still 3.05-1.11g, then only
ever recovering to a weak ~20/100 by frame 928 as the same event continued toward a stop) and a fix was
implemented at Layer 4 (a low-speed compensation multiplying Raw's own floor).

**This was superseded mid-task by the coordinator's own clarification**, based on the driver's live-rig
test: switching the Wheel Lock **source** to SimHub's own ShakeIt export resolves the complaint entirely.
That is decisive: it proves Layer 4/5 (Normalized/Projected) were never the defect, and that OUR
`LegacySlipAlgorithm` reproduction of SimHub's RPM/Speed branch was never the branch SimHub's real
`WheelSlipEffect.GetEffectValue` actually uses for this title's Lock channel — my original fix, applied at
Layer 4, would have incorrectly altered the already-correct ShakeIt-sourced values too (Layer 4 cannot
know where its `rawLockWheels` input came from). **It was fully reverted** (see
`QAdvanceFeedback/Core/Normalized/NormalizedWheelLockSlipEngine.cs`'s own removal note where the old code
used to be).

### 1b. Revised diagnosis — which SimHub branch, and why

`WheelSlipEffect.GetEffectValue` (docs\reference\SimHub.WheelSlipEffect.decompiled.cs) is a nine-way
dispatch on the title's own `FeedbackCapabilities`. `UseLegacyIracingAlgorythm` only selects between the
LAST two branches (`GetRpmSpeedSlipLegacy`/`GetRpmSpeedSlip`), reached only when nothing better is
available. Evidence for which branch is actually live for Forza Horizon 6:

- A raw-telemetry capture from the **sibling** ReliableWheelLockSlip project's own Forza Horizon 6 session
  (`ReliableWheelLockSlip.session-20260813-171314.csv`, 12,431 frames) shows per-wheel speed (`wsFL..RR`),
  suspension travel, and ABS/TC flags **identically zero** for the entire session, and raw wheel slip
  **blank** (not even zero — genuinely unreported, `slipValid=0` throughout). Verified directly (Python,
  `csv.DictReader`, unique-value check over all 12,431 rows) — not merely asserted.
- This rules out every branch needing `WheelsSpeed`/`WheelsSlip`/`WheelsRPS`-level telemetry:
  `LockFromWheelsRPSAndDummyRadius` ("RPSandDummyRadius"), `GetLockFromWheelSpeed` ("WheelSpeedLock"),
  `GetRpsLock` ("RPS"), `GetWheelSpeedSlip` ("Wheels speed"), `GetSlipFeedback` ("Slip data"),
  `GetDirectSlip` ("Precalibrated slip"). None of that data exists for this title's SimHub integration.
  (`WheelsSlipContainer.SetFeedbackCapabilities`'s own `LowQualityAlgorythmLevel` derivation, also
  decompiled, confirms these flags are what a game-specific reader plugin sets — not something a
  third-party plugin can read directly; I do not have that Forza-specific reader plugin locally, so I
  cannot query its exact capability flags — flagged as unverifiable in this environment, see §5.)
- That leaves exactly two candidates ahead of the already-implemented RPM/Speed legacy branch:
  **"Braking vs speed"** (`GetSimpleBraking`, gated by `WheelSlipUseSimpleBraking`) and the RPM/Speed
  legacy branch itself (already known to be wrong, per the ShakeIt-source test).
- `GetSimpleBraking`'s own formula independently explains **both** halves of the driver's complaint: its
  speed factor is `Clamp(SpeedKmh,0,30)/30` — saturated (speed-independent) at ordinary/higher speed
  (explaining "very strong vibration on brake application"), but **PROVABLY, from the formula's own
  arithmetic** (not merely observed), the combined reading is **mathematically zero for any speed at or
  below 15 km/h** at the default `LockSensibility` (50, the exact value the driver reported using,
  unchanged), regardless of how hard the brake is pressed — reproduced as
  `Faithful_compute_is_mathematically_zero_at_or_below_15kmh_regardless_of_brake_at_default_sensibility`
  in `Private\QAdvanceFeedback.Tests\SimpleBrakingLockAlgorithmTests.cs`. This is exactly "the wheel is
  actually locked very quick [at low speed]... but the WheelLock motor is NOT vibrating."
- The owner's own hint (`LockSensibility` visible in the driver's SimHub UI) was investigated and is
  **not**, on its own, evidence the RPS branch is live: `LockSensibility` is also consumed by
  `GetSimpleBraking` and `GetRpsLock`, and SimHub shows this field for any Wheels Lock effect regardless
  of which branch is actually computing — not a per-branch-conditional UI element (confirmed by reading
  `WheelsLockContainer.cs`'s decompiled properties directly).

**Honestly unverifiable**: I cannot directly confirm `WheelSlipUseSimpleBraking=true` for Forza Horizon 6
without either the title-specific SimHub reader plugin (not present in this environment) or live SimHub
telemetry (explicitly out of scope — "you cannot run SimHub, the game or the hardware"). The evidence
above is convergent (owner's hint region, the empirical "switching source fixed it" fact, and the
formula's own exact mathematical fit to both complaint halves) but not a decompiled, first-party proof of
the capability flag's value. Flagged plainly, not asserted as certain.

A **generic, capability-based dispatch** mirroring SimHub's own nine-way chain was considered and
rejected as impractical: `FeedbackCapabilities` is an internal SimHub/ShakeIt concept never exposed to a
third-party plugin via telemetry, and Layer 1 (`ITelemetryFrame`) deliberately excludes the underlying raw
per-wheel fields (RPS, wheel speed, suspension, ABS/TC) a real dispatch would need even to attempt
inferring capabilities itself. Per this task's own instruction, the single branch the evidence points to
for this title was implemented instead, with the limitation stated plainly (see §5).

### 1c. The fix

New file `Private\QAdvanceFeedback\SimpleBrakingLockAlgorithm.cs` — a faithful, unmodified port of
`GetSimpleBraking`'s `c.IsLock` branch (`Compute`), plus one documented deviation
(`ComputeWithLowSpeedFix`), used by `LegacyWheelLockSlipEngine.Compute` for Lock only (Slip is completely
untouched — still `LegacySlipAlgorithm`'s own per-wheel RPM/throttle term). Lock is now **car-level** (no
per-wheel input at all — SimHub's real branch has no wheel-specific term either), gated by the same
CheckSpeed-style `>1 km/h` ground-speed magnitude check `LegacySlipAlgorithm` already used (a genuine
standstill still reads 0 — unchanged, not a bug).

**The low-speed fix**: since the dead zone boundary (`threshold * SpeedFullKmh`, derived from
`LockSensibility` — 15 km/h at the default 50) is an exact, provable property of the formula, the fix
computes the SAME sensitivity-reshaped value from **brake alone** (speed factor forced to saturation) and
floors the faithful reading with it, linearly ramped from 0 (standstill) to that brake-only value across
the SAME dead-zone boundary, via `Math.Max` — never reducing the faithful reading, never manufacturing a
reading from a brake input too light to clear the sensitivity's own ~50%-of-full threshold. Continuous
(no discontinuity) and converges exactly with the faithful formula at `SpeedFullKmh` (30 km/h) — proven
in `Low_speed_fix_has_no_large_discontinuity_anywhere_across_the_full_speed_range` and
`Low_speed_fix_converges_with_the_faithful_formula_at_and_above_SpeedFullKmh`.

**BEFORE/AFTER** (both computed directly from `SimpleBrakingLockAlgorithm`, reproducing the exact frame
928-equivalent scenario — brake firmly committed at 90%, low but genuinely moving speed):

```
speed (km/h)    BEFORE (faithful formula alone)     AFTER (with the low-speed fix)
0               0.0                                  0.0   (genuine standstill, correctly silent)
5               0.0                                  ~30   (weak-but-present, ramping)
8               0.0 (mathematically zero)            ~53   (strong, felt cue)
15 (dead-zone boundary)  0.0 (just crossing zero)     100   (full — matches the brake-only ceiling)
30 (SpeedFullKmh)        100                          100   (converges exactly, no discontinuity)
```

**Regression guard (no full-blast on a mere touch, at any speed):** a light brake dab (25%, below the
sensitivity's own ~50%-of-full reshaping threshold) at low speed stays under 15/100
(`Lock_does_not_fire_at_full_from_a_light_brake_dab_at_low_speed`); the same dab at ordinary speed is
unaffected (the fix only ever adds a floor, never scales the faithful reading down).

**`LockSensibility`** is now exposed as a driver-facing setting (`WheelChannelSettings.LockSensibility`,
Lock tab, 0-100, default 50, matching SimHub's own field name/range/default exactly) — load-bearing now
that it drives the actual algorithm, not vestigial.

**Mutation evidence (item 1):** reverting `LegacyWheelLockSlipEngine`'s call from
`ComputeWithLowSpeedFix` back to the faithful `Compute` made
`Lock_produces_a_strong_cue_for_a_genuine_low_speed_lockup_that_the_faithful_formula_alone_cannot` FAIL
(the other 32 tests in the two affected files still passed). Reverted; full suite green (522/522) again.

---

## 2 — TRIGGER THRESHOLD

Moved into a new "Trigger Threshold" section, above "Sources", on both the Wheel Lock and Wheel Slip
tabs (`SettingsControl.xaml`/`.xaml.cs`) — screenshots regenerated (§ above). Semantics implemented per
the coordinator's own clarification (**one configured threshold, applied at BOTH the Layer 3 algorithm's
own gate AND the source boundary, not two independent numbers**):

- `LegacyWheelLockSlipEngine.Compute`: below the channel's own threshold, the WHOLE channel's `Raw.*`
  (all four wheels, every aggregate) reads exactly 0 — a new, unconditional gate layered on top of
  `LegacySlipAlgorithm`'s own per-wheel term suppression (which still only zeroes its own term, leaving
  the lateral floor, exactly as SimHub's decompiled source does — that part is UNCHANGED, for Slip only;
  Lock no longer routes through that method at all, see §1).
- `NormalizedWheelLockSlipEngine.Compute`: the SAME `LegacyThresholds` struct (threaded through by the
  composition root, `QAdvanceFeedback.cs`) gates the whole `Normalized.*` output too, checked BEFORE
  even the "no G/speed signal at all" degradation floor (an earlier revision of this method had it
  after; corrected per the coordinator's own explicit "no carve-out" instruction) — and, crucially, this
  gate is evaluated purely from `sample.New.BrakePercent`/`ThrottlePercent` vs. the configured threshold,
  **never from the raw wheel values themselves**, so it is source-agnostic by construction: whether
  `rawLockWheels` came from our own Legacy engine, a ShakeIt export, or a Manual property, a
  below-threshold frame reads 0 regardless of what that source reports.
  `Projected.*` follows automatically (confirmed, not merely assumed): `OutputProjector.Project(0)` is
  exactly 0 by construction (the curve's own control points start at `(0,0)`), and `PulseGenerator.Advance`
  passes a non-maximal value straight through — so Normalized=0 ⇒ Projected=0 unconditionally.
- A missing (null) pedal reading is treated as "threshold not satisfied" (fail-closed) — the same
  convention `ITelemetryFrame`'s own remarks establish and `LegacySlipAlgorithm`'s own nullable `>`
  comparisons already use.
- **Slip's priority ordering (brake checked before throttle) is unchanged**, and Slip's above-threshold
  arithmetic is untouched (the new gate is a pure early-return before Slip's existing logic runs) — its
  behaviour above threshold is byte-identical to before, by construction (no arithmetic in the
  already-tested path was touched).
- **ShakeIt composition caveat, documented in the UI help text** (both English and Chinese): if Source is
  ShakeIt's own export, ShakeIt already applies its own internal gate before publishing — our threshold
  can only make the channel MORE restrictive than ShakeIt's own, never less; lowering it below ShakeIt's
  own gate will not make ShakeIt's values appear any earlier.

**Tests** (new, `NormalizedWheelLockSlipEngineTests.cs`): below-threshold reads exactly 0 for both tiers;
at-threshold (`>=`, not `>`) reads normally; Slip's brake-priority ordering still wins at the new gate
too, with a custom (lowered) threshold; a source-agnostic test proves an arbitrarily large raw value
(standing in for ShakeIt/Manual) still reads 0 below threshold; a dedicated test proves the SAME
`LegacyThresholds` value drives both a below-old-threshold and an above-old-threshold case with nothing
but that one struct (catching a future reintroduction of two independent numbers). Four pre-existing
tests whose whole point was "pedal state never gates engagement" were adapted (not weakened) since the
new, larger Trigger Threshold now genuinely subsumes what used to be a learning-only distinction
(`TelemetryLearningGate.LearnMinBrakePercent`=10 < `LockBrakeThresholdPercent`=20) — documented inline
in each adapted test.

---

## 3 — G-FORCE STAGED TRAVEL ANIMATION

`GForceEngine`'s previous "sustained low-pass + transient high-pass, continuous hat position" model is
replaced by an explicit three-keyframe staged sweep, per the driver's own specification, verbatim (pad
geometry, the three stages for each chain, and the driving rules). Structure:

- **Two independent, per-chain tracks**: a SUSTAIN LEVEL (low-pass filter of the current G ratio, UNCHANGED
  mechanism/time-constant property from the previous pass) and a new STAGE PROGRESS (0→1, three keyframes
  at 0/0.5/1.0).
- **Stage progress speed is driven by the observed DELTA in the ratio**, not its magnitude — a single
  large one-frame delta latches a high "travel rate" that then decays (repurposed
  `TransientTimeConstantSeconds`), so the sweep continues for several frames after the initiating delta
  rather than a one-frame flicker; capped at a maximum (`MaxStageProgressPerSecond`, ~0.2s minimum full
  sweep, so even an infinite delta cannot complete the sweep in a single frame) and floored at a minimum
  (`MinStageProgressPerSecond`) so any continuously-engaged chain is GUARANTEED to eventually reach the
  sustain shape — without this floor, the delta-driven rate's own geometric decay can converge to a total
  strictly less than a full sweep for smaller initial deltas (a genuine bug found and fixed during
  testing, not merely a slow-convergence issue — see `Private`-adjacent commit history in this report's
  own reasoning; confirmed by direct Python simulation before and after the fix).
- **Sustain level is driven by the G ratio's own value** (unchanged low-pass mechanism) and scales the
  WHOLE three-pad shape once fully staged — this is what makes a falling G (same direction) scale all
  three pads proportionally, reproducing the driver's own verbatim worked example (90/45/22.5 → 60/30/15)
  exactly, using the existing configurable sustain fractions (MID=50%, LOW=25% by default) as the ratio.
- **Direction selection for the animation itself**: Accelerating requires BOTH measured SpeedingUp
  direction AND the throttle pedal applied; Braking requires the brake pedal applied; Coasting (neither
  pedal) watches the deceleration-direction chain's own latched delta rate — large (engine braking/a
  forced downshift) still runs the deceleration animation, small (`CoastingDeltaDeadBandPerSecond`,
  named/justified) produces no cue at all (ordinary rolling resistance). A dedicated, ALWAYS-running
  latched-rate tracker (separate from the stage-progress's own, which only updates while already active)
  was needed so a sudden coasting kick isn't detected for only the single frame it actually changed.
  Direction still comes from differentiated speed, never the reported G sign — unchanged, re-verified
  (the two "inverted convention" tests still pass).

**Acceptance scenarios (S1-S6):** S2 and S4 re-verified valid under the new model, unchanged in intent.
S1/S3/S6 rewritten using direct measurements (sweep-speed via a shape-RATIO metric that cancels out the
sustain level's own convergence, rather than the old "gap against a TransientGain=0 twin" — the twin
isolated an additive transient bump that no longer exists). **S5 ("a transient while already saturated
spends the headroom above the sustain floors") is explicitly superseded and removed**: once stage
progress has fully swept and sustain level is itself at 1.0, there is nothing further the owner's own
specification (delta-driven travel, G-driven sustain scaling) calls for — a deliberate departure, not an
oversight.

**Tests added:** three-stage ordering (both chains, far-leads-then-mid-leads-then-terminal-leads);
large-delta-sweeps-faster-than-small-delta; sustain holds during a steady hold; the driver's own verbatim
90/45/22.5→60/30/15 example; coasting-small-delta produces no cue; coasting-large-delta runs the
deceleration animation; no discontinuous jumps across a full sweep.

**Mutation evidence:**
- **(a)** driving the animation from the ratio's own magnitude instead of its delta (in
  `AdvanceStageProgress`): `A_large_delta_produces_a_faster_sweep_than_a_small_delta` FAILED (large and
  small deltas produce the same speed once magnitude, not delta, drives the rate). Reverted; full suite
  green.
- **(b)** collapsing `StagedShape` to always return the stage-3/sustain shape regardless of progress:
  both `Braking_chain_sweeps_far_to_mid_to_terminal_in_order` and
  `Acceleration_chain_sweeps_far_to_mid_to_terminal_in_order` FAILED (no ordering — every pad is at its
  final value from the first frame). Reverted; full suite green.
- **(c)** removing the coasting dead-band check (`decelChainActive = true` unconditionally while
  coasting): `Coasting_with_a_small_steady_deceleration_produces_no_cue_at_all` FAILED. Reverted; full
  suite green.

All three mutations were applied directly to the real source, the targeted test run and failure
confirmed, then reverted to the exact original text with the full 522-test suite re-confirmed green after
each.

**Settings/UI note:** `TransientGain`/`TransientTimeConstantSeconds` (property names, defaults, and
persisted JSON shape all UNCHANGED, to avoid a settings-schema break) are REPURPOSED — from "transient
kick gain/smoothing" to "sweep speed gain/sweep-rate decay time" — documented in both the code and the
UI label text/tooltips (English and Chinese), and the G-Force tab's "Motion feel" note was rewritten to
describe the new travel behaviour.

---

## Concerns / unverified

- **No live-game verification anywhere** — everything above is verified against the CSV replay, decompiled
  SimHub source, and unit tests only, per this task's own stated constraint (no SimHub/game/hardware
  access).
- **`WheelSlipUseSimpleBraking`'s actual value for Forza Horizon 6 is unverified** — the title-specific
  SimHub reader plugin that sets this capability flag is not present in this environment, and
  `FeedbackCapabilities` is never exposed to a third-party plugin via telemetry. The evidence for this
  being the live branch is convergent but not a first-party proof — flagged plainly in §1b, not asserted
  as certain.
- **A generic, SimHub-mirroring capability dispatch was judged impractical**, not attempted — Layer 1
  deliberately has no wheel-level RPS/speed/suspension/ABS/TC fields, and `FeedbackCapabilities` itself is
  unreachable from a third-party plugin regardless. If a future title needs a genuinely different branch,
  this will need a fresh, evidenced investigation the same way this one was, not a guess.
- **`LockSensibility` above 50 caps the achievable ceiling below full scale** — a faithful, but
  counter-intuitive, characteristic of SimHub's own real formula (`Sensitivity_above_50_caps_the_achievable_ceiling_below_full_scale`
  proves this exactly): raising sensitivity trades top-end strength for an earlier response. Documented in
  the UI's own help text so a driver who raises it and finds Lock never quite reaches "fully locked" isn't
  left guessing why.
- **All new numeric constants are judgment calls, informed by the log/specification but not
  independently rig-tuned**, flagged individually in code where introduced: `MaxStageProgressPerSecond`
  (5.0, ~0.2s minimum full sweep), `MinStageProgressPerSecond` (1.0, ~1s guaranteed-worst-case
  completion), `CoastingDeltaDeadBandPerSecond` (0.5), `PedalAppliedThresholdPercent` (1.0, GForce's own,
  distinct from Wheel Lock/Slip's driver-configurable Trigger Threshold).
- **The exact km/h values underlying the original session-log frame analysis (§1a) were inferred from
  pattern, not measured** — the exported CSV has no raw speed column; this was superseded by the §1b
  diagnosis regardless, which relies on the formula's own provable arithmetic instead.
