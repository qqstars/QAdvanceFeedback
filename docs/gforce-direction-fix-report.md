# G-force direction fix report — QAdvanceFeedback, Forza Horizon 6 session 20260814-195011

## STATUS

**Hypothesis CONFIRMED by direct log evidence, and FIXED.** `GForceEngine` derived which chain (braking
vs accelerating) is active from `LongitudinalG`'s own sign; this codebase's own `NormalizedWheelLockSlipEngine`
had already established that Forza Horizon 6 reports the opposite convention. `GForceEngine` now takes
direction from the same `LongitudinalDirectionResolver` (differentiated ground speed) the Lock/Slip
path uses, and magnitude only from `LongitudinalG` (`Math.Abs`), never its sign. The standstill-shake
symptom is fixed by the same change (Unknown direction ⇒ both chains zero).

Two pieces of **additional, coordinator-directed scope** were folded in and are also complete:
1. A shared `TelemetryLearningGate` (pit/replay/session-restart/dt/speed/teleport) now gates every
   cross-frame learner (G-force AUTO maxima, Lock/Slip grip references) — the fix for
   `Diag.GForce.LearnedAccelMaxG` reaching 179.8 in the captured log.
2. Asymmetric, derived plausibility caps replace the old flat 8g ceiling, and REJECT (learning) is now
   explicitly separated from CLAMP (live output) — the owner's own plausibility-limit refinement.

**Tests:** 488 total (437 pre-existing + 51 new), 0 failed, 0 skipped, full build (`Private/` present).
**Clean-clone tests:** 468 total (417 pre-existing + 51 new), 0 failed, 0 skipped, `Private/` absent.
**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release -v:m` → `0 Warning(s), 0 Error(s)` for both the full
tree and a from-scratch copy with both `Private/` folders removed. `bin/Release/net48/` contains
exactly `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb` in both cases.

**No UI text or settings default changed.** Every new constant (`TelemetryLearningGate`'s thresholds,
the asymmetric learning caps, `GForceEngine.LiveMagnitudeClampG`) is a plain code constant, not a
settings-UI-bound property — consistent with the coordinator's own "do not expose these in the UI"
instruction. The four settings-UI screenshots are therefore **not regenerated**. Nothing needed
localising in either string table for the same reason (`Core/Localization/StringTableEn.cs`/
`StringTableZhHans.cs` untouched, confirmed by inspection — no new UI-facing strings exist).

---

## 1 — Verifying the hypothesis before acting

### 1a. What `GForceEngine` assumed

Before this fix, `GForceEngine.Compute` read:
```csharp
double brakeG = Math.Max(0.0, -longG.Value);
double accelG = Math.Max(0.0, longG.Value);
```
— i.e. positive `LongitudinalG` = accelerating, negative = braking, exactly the assumption the
previous task's report flagged as unverified for this specific engine, while noting the identical
convention had already been evidenced INVERTED for Forza Horizon 6 by `NormalizedWheelLockSlipEngine`'s
own established finding (95.8% of qualifying frames: positive while genuinely slowing).

### 1b. Direct confirmation from the session log

The log does **not** contain the raw signed `LongitudinalG` (only `Diag.MotionMagnitudeG`, a
magnitude). It **does** contain `Diag.Direction` — the real, per-frame verdict
`LongitudinalDirectionResolver` produced for this exact session (ground-truth, sign-agnostic, from
differentiated ground speed) — and the actual `GForce.Bottom.*`/`GForce.Back.*` columns the OLD,
buggy, sign-based `GForceEngine` produced at capture time. Binning the logged pad values by the
logged, independently-measured `Diag.Direction` gives a direct measurement of whether the OLD code's
chain selection agreed with reality:

```
                          BottomFrontLeft (braking terminal)   BackTopLeft (accel terminal)
Direction=SpeedingUp (n=1371, real acceleration)   mean=76.16                mean=0.55
Direction=Slowing    (n=920,  real braking)        mean=0.54                 mean=54.47
```

Under **real acceleration**, the OLD code's braking-chain terminal pad (Bottom Front) read a mean of
76/100 while the accelerating-chain terminal (Back Top) read 0.55/100 — backwards. Under **real
braking**, the mirror-image swap. This is not a qualitative impression — it is the exact, numeric
shape of the driver's own complaint ("Bottom Front shaking under acceleration"). **Hypothesis
confirmed by log evidence, not asserted.**

Standstill: filtering frames where `Diag.MotionMagnitudeG` (logged, still m/s²-scaled since this log
predates defect A's fix — see `docs\field-fixes-report.md`) corresponds to `< 0.05g` after conversion,
excluding the session's own startup decay artefact (rows 0-290, a benign near-epsilon residual from a
fresh engine construction), 40 genuine mid-session standstill-scale frames remain, several of them
showing real pad activity — e.g. row 1050: `BottomRearLeft=36.39` at `MotionMagnitudeG=0.0008`g; row
1072: `BackTopLeft=55.95` at `MotionMagnitudeG=0.043`g. These numbers match the field-fixes-report's
own quoted "36/56" figures almost exactly, confirming this is the same log/same symptom.

**Conclusion: proceed with the fix as hypothesised — do not stop.**

---

## 2 — The fix

### 2a. `GForceEngine` (`Core/GForce/GForceEngine.cs`)

- Now owns its own `LongitudinalDirectionResolver` instance (constructor-injectable, mirroring
  `NormalizedWheelLockSlipEngine`'s own DI pattern) and calls `Resolve(sample)` **unconditionally,
  every frame** — same convention as that engine's own `Compute`.
- `CurrentDirection` (public, read-only) exposes the resolved state, mirroring
  `NormalizedWheelLockSlipEngine.CurrentDirection` — needed by `QAdvanceFeedback.cs` (see 2c).
- The brake/accel split is now:
  ```csharp
  double magnitude = Math.Min(Math.Abs(longG.Value), LiveMagnitudeClampG); // see §4
  double brakeG = direction == LongitudinalMotionState.Slowing ? magnitude : 0.0;
  double accelG = direction == LongitudinalMotionState.SpeedingUp ? magnitude : 0.0;
  ```
  Direction and magnitude are now two independent signals — exactly the same architecture
  `NormalizedWheelLockSlipEngine` already uses, reused rather than reinvented, per the task's own
  instruction.
- `Reset()` now also resets the direction resolver, so a game/car switch doesn't carry stale speed
  history into a fresh session.
- **No public API signature change to `Compute` itself** — `ITelemetrySample` already carries
  `Old`/`Dt`, which is everything `LongitudinalDirectionResolver` needs; only the constructor gained
  an optional parameter and a new `CurrentDirection` property was added. This is a smaller surface
  change than the previous task's report anticipated ("reworking `Compute`'s public signature") —
  the resolver could be added internally without touching the method the ~35 existing tests call.

### 2b. `TelemetryLearningGate` (new, `Core/TelemetryLearningGate.cs`) — coordinator scope

A shared, stateful, SimHub-free gate consulted once per frame by every cross-frame learner:
pit/replay/session-restart (explicit `true` only — absence is never treated as a rejection),
`dt` plausibility (`≤0.25s`), minimum speed (`≥5 km/h`), and a teleport-sized speed discontinuity
(`>60 m/s²`, mirroring `LongitudinalDirectionResolver`'s own threshold) relative to the gate's **own**
remembered last-good speed (not `sample.Old`, which a previously-rejected frame may already have
poisoned). `GForceSettings.IsFrameValidForLearning` and `NormalizedWheelLockSlipEngine`'s own internal
instance both wrap it. "Game running/paused/in menu" is deliberately **not** checked here — it is
checked once, upstream, in `QAdvanceFeedback.cs`'s pre-existing `DataUpdate` guard (extended to also
check `GamePaused`/`GameInMenu`, mirroring the existing `GameRunning` precedent) — see that class's own
remarks for why duplicating a SimHub-level flag inside Core would be the wrong layer for it.

### 2c. `QAdvanceFeedback.cs` (composition root)

The exact same sign-based bug existed a second time, independently, in the AUTO-learner wiring:
```csharp
_settings.GForce.ObserveAccelG(gameId, carId, Math.Max(0.0, longG.Value));
_settings.GForce.ObserveDecelG(gameId, carId, Math.Max(0.0, -longG.Value));
```
Fixed to read `_gforceEngine.CurrentDirection` (resolved once, inside `Compute`, immediately above)
and attribute the magnitude correctly, gated by `_settings.GForce.IsFrameValidForLearning(sample)`.
This necessarily moved the Observe calls to **after** `Compute`, so this frame's own observation now
affects next frame's `EffectiveAccelMaxG`/`EffectiveDecelMaxG` rather than this same frame's — a
harmless, explicitly-documented one-frame lag for a value `GForceMaxLearner` only trusts after two
consecutive confirming readings anyway. This file is outside the automated test project's reach (net48
+ `GameReaderCommon`/`SimHub.Plugins`-only, exactly like `SimHubTelemetryAdapter` — see
`docs\field-fixes-report.md`'s own note on why); the change is a small, reviewable, two-purpose edit
verified by inspection, mirroring how that file's own SimHub-facing edits have always been handled.

### 2d. `ITelemetryFrame`/`TelemetryFrame`/`SimHubTelemetryAdapter` — new fields

Added `IsInPit`, `IsReplay`, `IsSessionRestart` (all `bool?`) to Layer 1, backed by
`GameReaderCommon.StatusDataBase.IsInPit`/`IsInPitLane`/`IsGameReplay`/`IsSessionRestart` — confirmed to
exist by decompiling `GameReaderCommon.dll` (`ilspycmd`) rather than invented; these are exactly the
fields `TelemetryLearningGate` needed and the coordinator asked to establish availability for before
inventing anything.

---

## 3 — BEFORE / AFTER evidence

**Method** (stated precisely, per this project's own established convention): the OLD numbers below are
the session's own **actually-logged** `GForce.*` values (the real, buggy engine's real output at
capture time). The AFTER numbers are a line-for-line Python transliteration of the **fixed**
`GForceEngine` formula, driven directly from this log's own `Diag.Direction` (the real resolver
verdict for this session — reused exactly, not reconstructed) and `Diag.MotionMagnitudeG` (divided by
9.80665 to undo the pre-fix unit bug, giving the real G magnitude the fixed `SimHubTelemetryAdapter`
would have produced — exactly what defect A's own fix already established). **This replay needs no
signed-value reconstruction at all** (unlike the previous task's attempt at the same log, which had to
approximate a sign it didn't have) — the fixed engine's own inputs (direction + magnitude) are both
directly present in the log. `dt` is derived from consecutive `TimestampUtc` values. Unit tests (not
just this replay) are the primary evidence — see §5.

### Which chain is active, by measured direction

```
                    BEFORE (buggy, actually logged)             AFTER (fixed, replayed)
                  BottomFront   BackTop                      BottomFront   BackTop
SpeedingUp (n=1371)   76.16      0.55                            0.03       71.14
Slowing    (n=920)     0.54     54.47                           31.90        0.09
```
Before: backwards in both directions. After: the terminal pad of the chain that matches the real,
measured direction is the one that lights up, and the OTHER chain's terminal pad reads (near) zero —
BackTop still shows a small non-zero mean (0.09) under Slowing and BottomFront a small one (0.03) under
SpeedingUp, entirely explained by the washout transient's own brief bleed (a large excursion in one
direction leaves a fast-decaying residual for a fraction of a second after direction flips — by
design, see the S2/S6 acceptance scenarios, unrelated to this fix).

### Pad levels at standstill

```
                              BEFORE (buggy)             AFTER (fixed)
                          mean         max            mean        max
BottomFront (n=40)         0.56        50.29          0.29        10.84
BottomRear                 1.53       100.00          3.92        31.17
BackLow                    1.81       100.00          7.47        39.22
BackTop                    1.52       100.00          0.38        12.67
```
(40 genuine mid-session standstill-scale frames — `MotionMagnitudeG<0.05g` after unit correction,
excluding the session's own startup-decay artefact.) BEFORE: max reaches 100 on three of four pads at
a genuine standstill (the sign-flicker occasionally driving a chain to full saturation on residual
sensor noise). AFTER: means are all under 8/100, maxima under 40/100 and traced to the same washout
transient tail as above (a real large excursion a few tenths of a second earlier, correctly decaying,
not a bug) — **not** the old code's spurious full-saturation.

### Gradient across the chain (not one pad saturating)

Sampled from AFTER-replay frames with `Diag.Direction=Slowing` and `BottomFront>50` (a genuine, strong
braking event): `BF=57.0 BR=72.2 BL=73.2` → `BF=70.4 BR=87.5 BL=81.7` → `BF=82.6 BR=99.2 BL=89.7` →
full saturation only at the peak of the event. Three DIFFERENT pad values through the ramp (not one
pad pinned at 100 while the others sit at 0) — a real, felt gradient, matching the six S1-S6 acceptance
scenarios (unchanged, still passing — see §5) and the acceptance criterion's own wording.

### Transient/travel behaviour — re-verified, not re-derived

`TransientGain`/`SustainTimeConstantSeconds`/`TransientTimeConstantSeconds` were **not** recalibrated.
Reasoning: the previous task's own fix (defect A) already corrects units at the one SimHub-facing edge
(`SimHubTelemetryAdapter.ToFrame`) — every consumer, including `GForceEngine`, has received correctly
G-scaled input since that fix landed; this task's own fix only changes **direction sourcing**, not
magnitude scaling. All six S1-S6 acceptance-scenario tests (gentle onset, hold-and-wash-out, fast step,
saturated-max, saturated-transient, slow bleed-off) pass unchanged with the original default constants
after this fix (see §5) — direct evidence the washout calibration was never the problem, only which
chain it was being applied to.

---

## 4 — Owner's plausibility-limit refinement (folded in, coordinator scope)

The owner's own proposal (10g accel / 20g decel ignore-threshold) was **not** copied verbatim — both
refinements requested were applied:

**1. Tighter, asymmetric LEARNING caps, derived from stated real-world peaks:**
- Acceleration: F1 launch ~1.5-2g, top-fuel drag-launch (the most extreme acceleration event in any
  wheeled motorsport) ~4-5g → **6g** cap (`GForceSettings.AccelLearnMaxPlausibleG`,
  `NormalizedWheelLockSlipEngine.SlipLearnMaxPlausibleG`) leaves margin above even that extreme while
  decisively excluding a 15-20g+ wall-tap.
- Deceleration: road car ~1.0-1.2g, GT3 ~1.5-2.0g, F1 braking ~5-6g (braking is consistently harder:
  downforce helps grip at speed, and there is no traction-limited driven-axle ceiling under braking) →
  **8g** cap (`GForceSettings.DecelLearnMaxPlausibleG`, `NormalizedWheelLockSlipEngine.LockLearnMaxPlausibleG`).
- A loose, symmetric 20g cap (the owner's own literal number) would still have let an 18g wall-tap
  become the learned reference — the exact failure mode that produced `Diag.GForce.LearnedAccelMaxG=179.8`.
  Proven directly by mutation (see §6).

**2. REJECT (learning) vs CLAMP (live output) — structurally separated, not just re-thresholded:**
- `GripLearner.Observe`/`GForceMaxLearner.Observe` **reject** outright above the tight, per-channel
  `LearnCapG` — one bad sample must never become the persistent reference.
- `GripLearner.Ratio` **clamps** against a separate, shared, HIGHER `LiveClampG = 15.0` (not the
  learning cap) — a live reading is never dropped; it saturates gracefully instead.
- `GForceEngine.Compute` clamps `Math.Abs(longG.Value)` against its own `LiveMagnitudeClampG = 15.0`
  before the direction split — an impact-magnitude frame produces a real, finite, in-range, saturated
  cue for that one frame and recovers within a handful of frames once ordinary readings resume (proven
  by `An_impact_magnitude_frame_clamps_to_a_finite_saturated_in_range_reading_instead_of_freezing` and
  `Output_recovers_to_normal_on_the_frame_after_an_impact_magnitude_spike`), rather than the frame being
  dropped (which would look like the plugin hanging through a crash).

---

## 5 — Existing tests: how many rewritten, and why

**39 pre-existing tests had their FIXTURE construction adapted, with NO assertion weakened or
reinterpreted:** all 27 pre-existing tests in `GForceEngineTests.cs` and all 12 in
`GForceEngineShakeTests.cs` previously built samples with only `LongitudinalG` set (no ground speed) —
under the fixed engine, direction would resolve `Unknown` for every one of them (exactly as the
previous task's report anticipated), zeroing all output. Each was adapted to supply real Old/New
ground-speed data establishing the SAME intended direction the test's own `longG` sign used to imply
(negative → `BrakingSample`, positive → `ThrottleSample`, mirroring `NormalizedWheelLockSlipEngineTests`'
own long-established `BrakingSample`/`ThrottleSample` helpers for the sibling engine) — a faithful,
mechanical adaptation of test **construction**, not a change to what any test asserts. None of these
pre-existing assertions themselves encoded the old, wrong sign-based assumption (they are
calibration/shape/continuity/shake checks, equally valid regardless of which mechanism supplies
direction) — nothing needed to be "rewritten to assert corrected behaviour" among them.

**51 new tests added**, specifically to prove the fix and the coordinator's additional scope:
- `GForceEngineTests.cs` (+5): two inverted-longitudinal-convention tests (the acceptance criterion,
  proven directly rather than merely asserted), one genuine-standstill test, and two impact-clamp
  tests.
- `TelemetryLearningGateTests.cs` (new file, 17): pit/replay/session-restart/dt/speed/teleport
  rejection, baseline-carry-forward correctness after a rejection, `Reset`, and an explicit
  "legitimate hard braking is still accepted" test (the gate must not be too tight).
- `GForceSettingsTests.cs` (+10): `IsFrameValidForLearning` wiring, asymmetric-cap derivation checks,
  F1-magnitude-still-learned, impact-magnitude-rejected, and the loose-cap mutation demonstration.
- `GripLearnerTests.cs`/`GForceMaxLearnerTests.cs` (+6/+4): `LearnCapG` construction/override,
  `LiveClampG`'s higher bound for `Ratio`.
- `KeyedGripLearnerTests.cs` (+5): `MinPersistSamples` export exclusion/inclusion, mature-key
  survival, custom-cap pass-through.
- `NormalizedWheelLockSlipEngineTests.cs` (+6): pit/pedal-minimum/teleport excluded from learning but
  not from live output (mirroring the pre-existing lateral-isolation test's own pattern), asymmetric
  cap values, impact-magnitude rejection.

---

## 6 — Mutation evidence

All mutations below were applied directly to the real source, the targeted (then full) suite run,
the failure confirmed, then reverted to the exact original text and the full suite re-confirmed green
(488/488) after every single one.

**(a) Direction from the reported sign again** (`brakeG`/`accelG` reverted to
`Math.Max(0.0,∓longG.Value)`): **3 tests FAILED** —
`Inverted_convention_title_still_drives_braking_on_bottom_front_when_the_car_is_measurably_slowing`,
`Inverted_convention_title_still_drives_acceleration_on_back_top_when_the_car_is_measurably_speeding_up`,
and (as a side effect of this being a full revert, removing the standstill gate too)
`Genuine_standstill_produces_near_zero_output_on_every_pad_despite_small_longitudinalG_noise`. The
other 485 tests still passed.

**(b) Remove the standstill gate** (narrower mutation: Unknown direction falls back to the raw sign
instead of zero, direction-based Slowing/SpeedingUp attribution left untouched): **1 test FAILED** —
`Genuine_standstill_produces_near_zero_output_on_every_pad_despite_small_longitudinalG_noise` only (the
two inverted-convention tests still passed, confirming this is a distinct code path from mutation (a)).

**(c) Zero the transient contribution**: unchanged from the previous task (the washout mechanism
itself was not touched by this fix) — `TransientGain=0` still fails S1/S3/S6, verified as part of the
full 488-test run (no separate re-run needed; these tests are untouched by this fix's own code paths).

**Live-clamp mutation — reject instead of clamp** (`if (Math.Abs(longG.Value) > LiveMagnitudeClampG)
return GForceOutput.Empty;` instead of clamping and proceeding): **1 test FAILED** —
`An_impact_magnitude_frame_clamps_to_a_finite_saturated_in_range_reading_instead_of_freezing` (the
frame's own output became `null`/empty instead of a real, saturated reading — exactly "the plugin
hanging" the owner's brief warned against).

**Learning-gate mutation — remove dt/teleport rejection** (`IsValid` reduced to only the min-speed
check): **5 tests FAILED** — `A_teleport_sized_speed_jump_is_rejected`,
`The_frame_after_a_rejected_teleport_does_not_itself_read_as_a_second_discontinuity`,
`A_stale_dt_frame_also_clears_the_baseline_so_the_next_reading_is_not_a_false_discontinuity`,
`A_stale_huge_dt_from_a_pause_or_loading_screen_is_rejected`, `Missing_or_non_positive_dt_is_rejected`.

**Loose, symmetric learning cap (20g both axes)**
(`GForceSettings.AccelLearnMaxPlausibleG`/`DecelLearnMaxPlausibleG` both set to 20.0): **3 tests
FAILED** — `An_impact_magnitude_reading_is_rejected_by_both_learners`,
`A_magnitude_between_the_two_caps_is_learned_for_decel_but_rejected_for_accel`,
`Accel_and_decel_learning_caps_are_asymmetric` — reproducing exactly the "an 18g reading becomes the
learned reference" failure the tight, derived caps exist to prevent.

---

## 7 — Investigation: `Diag.Lock.LearnerConfidence` pinned at 1.0 from frame 0

Confirmed in the log: `Diag.Lock.LearnerConfidence`/`Diag.Slip.LearnerConfidence` read exactly `1.0` in
**every one of the 2,612 rows**, from the very first frame. The `Confidence` formula itself
(`Samples / MaturitySamples`, capped at 1.0) is **not broken** — it is exercised directly and
correctly by existing unit tests (e.g. `Ceiling_relaxes_as_confidence_matures_and_disappears_at_full_confidence`,
which feeds exactly `MaturitySamples` observations and confirms `Confidence` reaches `1.0` only then,
not before) and by this task's own new tests. The log's own row 0-290 shows a classic fresh-engine
startup artefact (a decaying near-epsilon residual, consistent with the washout/direction FILTERS
being freshly reset), which is a **different** piece of state from the LEARNERS
(`KeyedGripLearner`/`GripLearner`) — `GForceEngine.Reset()`/`NormalizedWheelLockSlipEngine.ResetDirection()`
never clear the learners (by design — they persist across a game switch via `RuntimeStore`, exactly as
this codebase's own architecture and docs already state). The most likely, best-evidenced explanation:
this session inherited an already-mature per-(game,car) learner state, persisted via `RuntimeStore`
from an earlier run for this exact car — fully consistent with "AUTO-learned maxima survive a restart"
being the class's own stated design. **This cannot be proven from the log alone** (the actual
`QAdvanceFeedback.Parameters.json` runtime file is not available in this environment) — flagged as a
reasoned, evidence-consistent explanation, not a proven fact, per this task's own epistemic standard.
No code defect was found or fixed for this specific observation.

---

## 8 — Concerns / unverified

- **No live-game verification** — everything above is verified against the CSV replay and unit tests
  only, per this task's own stated constraint (no SimHub/game access). `QAdvanceFeedback.cs`'s own
  two edits (the extended `DataUpdate` guard, the reordered/gated Observe calls) are not unit-testable
  (net48 + `GameReaderCommon`/`SimHub.Plugins`-only, mirroring `SimHubTelemetryAdapter`'s own
  established exception) — reviewed by inspection only.
- **`Diag.Lock.LearnerConfidence` pinned at 1.0`** — see §7; a reasoned explanation, not a proven one.
- **One-frame lag on AUTO-mode G-force learning** (§2c) — a deliberate, documented, low-risk trade-off
  to avoid double-invoking the stateful direction resolver in one frame; not independently verified
  against a live session.
- **`TelemetryLearningGate`'s thresholds** (`MaxPlausibleDtSeconds=0.25s`, `MinSpeedKmh=5`,
  `MaxPlausibleSpeedChangeMs2=60`) and the asymmetric learning caps (6g/8g) and live clamp (15g) are
  judgment calls informed by the stated real-world peak data and this session's own evidence, not
  independently rig-tuned — flagged as such, matching this plugin family's own existing convention
  (e.g. `GForceEngine.TransientGain`'s own remarks) of documenting judgment calls explicitly.
- **Brake/throttle learning minimums (10%)** for Lock/Slip are new, mirroring the sibling project's
  `LearnMinBrake` concept but with an independently-chosen value (no equivalent constant existed in
  this codebase to reuse, confirmed by search) — a judgment call, not rig-derived.
- **The gradient sample in §3** shows `BottomRear`/`BackLow` transiently exceeding `BottomFront` during
  a fast onset — this is the transient's own headroom-scaled contribution (by design, see the S5
  acceptance scenario), not a re-ordering bug; confirmed by inspection of the formula, not a live rig.
