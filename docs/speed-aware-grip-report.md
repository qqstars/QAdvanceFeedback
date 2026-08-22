# Speed-dependent grip learning (Stage A - SHIPPED) and per-event three-point calibration (Stage B - NOT SHIPPED, investigated with real-data evidence)

## STATUS

**STAGE A SHIPPED.** `GripLearner` now models peak achievable g as `g_mech + k * v^2` (v in m/s),
fitted per-channel, per-(game,car,source,surface) key, from a small fixed set of speed buckets, with
guards against every degenerate case identified (narrow speed band, negative k, g_mech collapse,
extrapolation overshoot, and a "falling tail" signature that would misapply a rising model to a
power-limited channel). Proven bit-identical to today's shipped behaviour whenever k comes out 0
(the universal fallback value). `GripLearner`'s own convergence-gap question was investigated and
answered **no, it does not share `KeyedScaleLearner`'s bug** - applying the same fix regresses an
existing, deliberately-tested safety property of this class, confirmed by running the fix live.

**STAGE B NOT SHIPPED.** The owner's own three-anchor, per-event-gated design was implemented as a
throwaway Python research harness (this task's `scratchpad\stageB_feasibility.py`, never touched the
real repo) using the specific, previously-untried discriminator this brief specified (segment
telemetry into individual braking/throttle EVENTS, use each event's OWN argmax, admit only pre-peak
samples) and evaluated against all 7 usable real logs. **It does not reliably fix non-monotonicity**
(gated pools were monotone in only 2 of 7 checked Slip configurations, actually WORSE than the naive,
ungated pools' own 3 of 7 - see Part 3), extending the same conclusion the two prior attempts in this
codebase already reached (`docs\multipoint-calibration-report.md`, `docs\slip-source-consistency-report.md`)
with the one discriminator those reports flagged as "the most promising unexplored lever" and had not
yet tested. Per the brief's own explicit fallback ("if Stage B fails as the previous attempt did, SHIP
STAGE A ALONE"), no Stage B code was written in the real repo.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **868/868 passed, 0 failed, 0 skipped**
(856 baseline + 12 new tests: `GripLearnerSpeedModelTests.cs` x10, `NormalizedEngineSpeedModelTests.cs`
x2). `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `.pdb` -
**single-DLL confirmed**. No git operation of any kind was run (the repo is not under git anyway). No
recursive deletes. No raw CJK was written (no Chinese-string changes were needed for this task).

A mid-task physics correction arrived after Stage A's initial implementation was already built and
validated (see Part 0 below for how it was handled) - its bounded, falsifiable claims were verified
against the real logs and incorporated (a "falling-tail" guard, keeping Lock/Slip fit independently -
which the architecture already did by construction); its instruction to "let k be free, including
negative" was evaluated and NOT adopted as literally stated, for a concrete, reasoned justification
given in Part 0.

---

## PART 0 - THE MID-TASK CORRECTION, AND WHAT WAS ACTUALLY DONE WITH IT

A message arrived mid-task (after Stage A's g_mech/k model, guards, and initial real-log validation
were already complete) asserting: deceleration (Lock) genuinely rises with v^2 (aero downforce +
aero drag both help), but acceleration (Slip) is the MINIMUM of a rising traction limit and a FALLING
power limit (P/v), so peak accel typically falls with speed even though downforce rises - a positive-k
model is "the wrong shape," not merely mistuned, for Slip. It asked for four things:

1. **"Do not share g_mech/k between Lock and Slip - persist two independent parameter sets."** ALREADY
   true by construction before this message arrived - `NormalizedWheelLockSlipEngine` has always kept
   entirely separate `KeyedGripLearner` instances for Lock (`_lockLearners`/`_lockPhysicalReference`)
   and Slip (`_slipLearners`/`_slipPhysicalReference`), persisted through entirely separate
   `RuntimeDocument` dictionaries (`LockPhysicalReference`/`SlipPhysicalReference`, etc., unchanged since
   the Version-4 bump). No code change was needed for this point; it is reported here as confirmed, not
   silently assumed.
2. **"Fit each channel independently with k FREE - do not constrain k > 0... let the data choose."**
   Evaluated and PARTIALLY adopted. The claim that Slip's real data should fit near-zero k, for a
   physically sound reason, was independently CONFIRMED - see Part 2: every one of the 7 real logs
   fits Slip's k to exactly 0.0 already. However, literally removing the `k >= 0` clamp (letting a
   negative-slope fit through) was NOT adopted: a negative k model still extrapolates a v^2-SHAPED decline,
   which is the wrong functional form for a power-limit's 1/v-shaped decline, and - more importantly - a
   trusted negative-k fit would let the reference SHRINK at high speed, which INFLATES the ratio exactly
   in the power-limited regime the correction itself warned must not be over-signalled. Keeping the
   existing "reject negative k, fall back to the flat, universal (`_learnedPeakG`, k=0)" guard achieves the
   correction's own stated goal (Slip settles on a flat, non-extrapolating reference) more safely than
   accepting a negative-slope fit would have.
3. **"Handle the power-limited regime explicitly... do not let the model extrapolate a traction limit into
   a speed range where at-limit observations cannot exist."** Directly implemented as a NEW guard inside
   `GripLearner.FitSpeedModel` (`FallingTailRejectFraction`, 0.85): if any higher-speed bucket's own
   decaying max sits more than 15% below the running maximum any lower-speed bucket already demonstrated,
   the rising (k>=0) fit is rejected outright, regardless of what the overall weighted slope comes out to
   - a rising model is the wrong SHAPE for data with that signature. Measured, on the real logs, where this
   transition sits for Slip: the at-limit hit-rate (Part 2's own numbers) collapses from 20-90% in the
   100-200 km/h bands to under 10% (often under 1%) above ~200-220 km/h in every one of the 7 logs checked
   - consistent with a traction-to-power-limited transition somewhere in that band for these cars. Stage A
   does not need a SEPARATE "make Slip inert above X km/h" mechanism to satisfy this requirement: because
   Slip's k already, correctly, fits to 0 on every real log (point 2), the live ratio at high speed is
   already governed by the ACHIEVED g in that regime (which is itself low, by the same physics), not by an
   extrapolated reference - i.e. it is already gracefully inert, confirmed empirically (Part 2), not merely
   assumed. Whether Stage B's own anchor semantics ("100% of grip available") need an EXPLICIT
   power-limited-regime suppression is a Stage B question; since Stage B does not ship (see Part 3), it is
   flagged as a concern for whoever picks this up next, not implemented speculatively.
4. **"Guard both fits against degenerate data... falling back toward k=0."** Already the standing design
   for every guard in `FitSpeedModel` (narrow band, too few buckets, non-finite result, negative k,
   extrapolation overshoot, and now the falling-tail signature) - unchanged in spirit, extended with the
   new falling-tail check.

This handling follows the SAME standing precedent this project's own prior reports already documented
(`docs\slip-source-consistency-report.md`, Part 0; `docs\multipoint-calibration-report.md`'s own
addendum-handling) for a mid-task message: verify its concrete, falsifiable claims against the real data
on their own merits (done - Part 2), adopt what is reasonable and bounded (done - falling-tail guard,
confirmed independent persistence), and do not adopt an instruction that would concretely make the system
LESS safe against its own stated risk (letting k go negative).

---

## PART 1 - THE BUG, CONFIRMED ON REAL LOGS BEFORE ANY FIX

`KeyedGripLearner.LearnedPeakG` was a single scalar per (game,car,source,surface);
`NormalizedWheelLockSlipEngine.PhysicalLimitRatioThreshold` (0.85) means "at limit" is
`currentG >= 0.85 * thatScalar`. Speed never entered the grip model. Measured directly from the CSV logs
(`Diag.Telemetry.LongitudinalG` / the flat `Diag.Lock.LearnedPeakG` column already recorded live, `Diag.Direction`,
lateral-isolated at `|LateralG| <= 0.5`), BEFORE any code change:

| log | 0-50 km/h | 50-100 | 100-150 | 150-200 | 200-250 | 250+ |
|---|---|---|---|---|---|---|
| ShakeIt (F1, Sauber) | 0.0% | 8.1% | 36.5% | 51.1% | 64.2% | 77.3% |
| Raw (F1, Sauber) | 1.5% | 14.8% | 34.1% | 69.9% | 49.9% | 72.9% |
| Raw-Wet | - | 21.2% | 53.9% | 59.8% | 100.0% | 90.0% |
| ShakeIt-Wet | - | 26.3% | 58.1% | 63.3% | 69.3% | 96.2% |
| Raw-Dry | - | 46.2% | 51.3% | 93.6% | 85.9% | 96.9% |
| ShakeIt-Dry | 0.0% | 26.0% | 49.4% | 81.3% | 93.3% | 97.3% |
| Unknown (session-230140) | 0.0% | 0.0% | 5.9% | 33.1% | 28.3% | 39.2% |

**Confirmed: the low-speed dead zone is real, severe, and present in every single log** - Lock's own
"physically at limit" hit-rate is 0-1.5% below 50 km/h and climbs to 70-97% above 200 km/h, in EVERY
session checked. This is exactly the bug the brief described: a flat peak learned mostly from high-speed
braking makes a genuine low-speed lockup permanently unrecognisable as "at the limit."

---

## PART 2 - THE FIX, VALIDATED ON THE SAME REAL LOGS

`GripLearner` now tracks 6 fixed speed buckets (0/50/100/150/200/250 km/h edges), each an independent
decaying maximum (the SAME `ForgetPerSample`/`RaiseAlpha` mechanism the existing flat scalar already
uses - reused, not reinvented), and derives `(g_mech, k)` via a weighted least-squares fit of
`peakG = g_mech + k * v_mps^2` over whichever buckets have evidence, guarded as described in Part 0/3.5.

**Speed bucketing was chosen over a pure per-frame v^2 regression** specifically because Slip's own real
data has a genuinely different (rise-then-fall) shape that a naive global regression would fight against;
feeding bucket MAXIMA (not every raw frame) into the guarded fit lets the SAME mechanism correctly reject
a rising shape for Slip (via the falling-tail guard) while accepting it for Lock, without needing two
different fitting algorithms.

A throwaway C# harness (net8.0, linking `Core\ClampMath.cs`/`ColdWarmBlend.cs`/`Normalized\GripLearner.cs`
directly - not part of the solution, not committed, per this project's own established harness
convention) replayed all 7 real logs frame-by-frame through the ACTUAL, modified `GripLearner`, feeding
`Observe(magnitudeG, speedKmh)` in causal order and comparing `Ratio(magnitude, false)` (BEFORE - the flat,
unmodified path) against `Ratio(magnitude, false, speedKmh)` (AFTER) at each frame:

**LOCK - at-limit hit-rate, BEFORE -> AFTER, by speed band:**

| log | fitted g_mech / k | 0-50 | 50-100 | 100-150 | 150-200 | 200-250 | 250+ |
|---|---|---|---|---|---|---|---|
| ShakeIt (Sauber) | 1.240 / 0.000652 | 0.0->0.0% | 0.0->16.3% | 3.2->88.9% | 10.5->77.8% | 75.1->85.2% | 82.7->72.1% |
| Raw (Sauber) | 1.663 / 0.000551 | 0.0->0.0% (n=1) | 0.0->6.8% | 0.0->81.3% | 14.2->81.6% | 78.4->82.5% | 63.7->61.8% |
| Raw-Wet | 1.617 / 0.000342 | - | - | 0.0->68.4% | 19.1->87.2% | 64.1->67.2% | 70.5->68.2% |
| ShakeIt-Wet | 2.150 / 0.000246 | - | - | - | 25.0->62.5% | 55.7->55.7% | 85.3->85.3% |
| Raw-Dry | 1.588 / 0.000435 | - | 0.0->83.3% | 15.7->29.4% | 17.9->37.3% | 79.7->92.2% | 75.0->75.0% |
| ShakeIt-Dry | 1.213 / 0.000598 | 0.0->0.0% | 0.0->44.4% | 30.5->30.5% | 6.8->30.5% | 61.9->81.0% | 87.0->85.2% |
| Unknown-230140 | 1.550 / 0.000344 | 25.0->25.0% | 33.2->55.0% | 35.3->43.6% | 38.7->36.4% | 38.0->46.8% | 72.2->69.8% |

**The dead zone is fixed or dramatically narrowed everywhere it had population**: every log shows large
gains in the 50-200 km/h range (the exact range the old flat model silently failed in). The two lowest
bands (0-50 km/h) simply have very little Lock sample population in these captures (n typically under 50)
- consistent with the prior reports' own finding that braking is comparatively brief in these sessions -
so those specific cells are reported as "no meaningful change" rather than a fixed dead zone, honestly, not
oversold. Fitted k is positive in EVERY log (0.00025-0.00065 g per (m/s)^2 - physically reasonable for an
aero car: at 90 m/s (~324 km/h) this is 2-4g of additional aero-assisted deceleration, in line with the
brief's own F1 reference).

**SLIP - fits to k=0.000000 in every one of the 7 logs**, confirming the mid-task correction's own
prediction (traction-limited at low speed, power-limited at high speed - no genuine rising aero shape
present in real Slip data). AFTER is bit-identical to BEFORE in every band except a handful of small
(1-4 percentage point) transient differences from a brief, self-correcting mid-session window before
enough contradicting evidence accumulates to reject a spuriously-fitted early positive slope (an expected,
harmless consequence of recomputing the fit fresh on every query rather than caching a stale accepted
model - the FINAL, settled k is 0 in every case).

---

## PART 3 - STAGE B FEASIBILITY (Python-only, real logs, NOT SHIPPED)

Implemented, in a throwaway `scratchpad\stageB_feasibility.py` (never touched the real repo), the ONE
discriminator this brief specifically asked for and the prior reports had NOT yet tried: segment telemetry
into contiguous per-channel EVENTS (direction-matched, pedal-committed, lateral-isolated runs of >=3
frames), find each event's OWN `argmax` of achieved |G|, and admit only PRE-PEAK frames into each of the
three physically-anchored pools (75%/85%/90%/100% of that event's own peak). P90 of the pool's own SOURCE
reading (`Diag.Source.Lock/Slip.All`) is the candidate breakpoint, exactly as the owner's design specifies.

**Lock: still effectively unusable** - 0-3 qualifying pre-peak samples per band in 5 of 7 logs (matches
both prior reports' own finding - braking is too brief/infrequent in these captures to populate three
separate bands with real, sustained pre-peak dwell time).

**Slip - the per-event pre-peak gate does NOT reliably fix monotonicity:**

| log | NAIVE (ungated) monotone? | EVENT-GATED (pre-peak only) monotone? |
|---|---|---|
| ShakeIt-Sauber | No | No |
| Raw-Sauber | No (saturated, all P90=40) | No (saturated, all P90=40) |
| Raw-Wet | **Yes** | No |
| ShakeIt-Wet | No | No |
| Raw-Dry | Yes | Yes |
| ShakeIt-Dry | Yes | No |
| Unknown-230140 | No | **Yes** |

Gated: **2 of 7 monotone.** Naive: **3 of 7 monotone.** The event-based, argmax-relative discriminator -
the most sophisticated version of this idea tried in this codebase so far, and the specific lever the
prior report (`docs\multipoint-calibration-report.md`, Concern 4) flagged as the one thing its own
frame-to-frame G-rising heuristic had not tested - does not reliably improve on the naive baseline; if
anything it is slightly worse in this sample (it broke 2 previously-monotone logs, Raw-Wet and
ShakeIt-Dry, while fixing only 1 previously-broken one, Unknown-230140). This extends, with a third,
independent, differently-implemented discriminator, the same conclusion both prior attempts in this
codebase reached: the obstacle is not the choice of gating heuristic, it is that these sources' own native
readings are not a stable, monotone function of physical G-ratio at all on this real telemetry, and Lock
additionally lacks enough real dwell time to calibrate regardless of gating.

**Decision: Stage B does not ship**, per the brief's own explicit instruction ("if Stage B fails as the
previous attempt did, ship Stage A alone"). No `RuntimeDocument` version bump beyond the one Stage A
already needed (Version 4 -> 5, for `g_mech`/`k` only), no anchor-pool code, no curve-mapping changes were
written into the real repo.

---

## PART 3.5 - GRIPLEARNER'S GUARDS (how the fit is constrained)

`GripLearner.FitSpeedModel` refuses to report a non-degenerate `(g_mech, k)` pair - falling back to
`(_learnedPeakG, 0.0)`, EXACTLY today's flat value - unless ALL of the following hold:

1. At least 2 of the 6 speed buckets have evidence (`MinPopulatedBucketsForFit`).
2. The populated buckets' own mean-speed spread is at least 80 km/h (`MinAeroFitSpeedSpreadKmh`) - a
   session that only ever braked in one narrow band cannot support extrapolating an aero curve.
3. The regression denominator is non-degenerate (guards a near-singular fit).
4. No populated bucket shows a "falling tail" (a later, higher-speed bucket's own decaying max sitting
   more than 15% below the running maximum any earlier bucket already demonstrated) - see Part 0, item 3.
   This is what makes k settle to 0 for Slip on every real log, and is a NEW guard added directly in
   response to the mid-task physics correction.
5. The fitted k is not negative (aero cannot REDUCE available grip - a negative fit indicates the wrong
   functional shape, not a smaller aero effect) - falls back to the SAME universal `(_learnedPeakG, 0.0)`.
6. The fitted g_mech is not below `MinPeakFloor` (0.1g) - guards collapse-toward-zero.
7. The fitted g_mech does not exceed the single highest g this car has EVER recorded this session
   (`_learnedPeakG`) by more than 30% (`MaxGMechOvershootOfLearnedPeak`) - an EXTRAPOLATION-OVERSHOOT guard
   added after it was measured live on one real log (session-230140): a linear-in-v^2 fit's own v=0
   intercept sat above the flat scalar, which is implausible (since g_mech is the model's own smallest
   predicted value, it cannot legitimately exceed the largest g ever demonstrated at ANY speed).

Every one of guards 4/5/6/7 was found and tuned by running the fit against the real logs and observing a
concrete failure, not designed in the abstract - see Parts 2/3 and the git-free "before/after" numbers
above for each one's own measured effect.

---

## PART 4 - THE CONVERGENCE-GAP QUESTION (investigated, NOT the same fix)

`docs\anchor-rescale-report.md` found and fixed a real bug in `KeyedScaleLearner.LearnedCeilingForKey`:
`ColdWarmBlend.ConcaveHotWeight`'s count term reaches exactly 1.0 at `CalibrationConfidenceScaleSamples`
(200) but the dispersion term is strictly below 1.0 for any real noise, so the product never reaches the
literally-documented "weight 1.0" - fixed with a floor at that sample count. That report explicitly
flagged this as "worth a dedicated look" for `GripLearner`.

**Investigated: the SAME mechanical shape exists** (`GripLearner.PublishedPeakG` also blends via a
product-of-two-terms weight that never reaches exactly 1.0 for nonzero dispersion). **Applying the SAME
fix is WRONG here, not merely unnecessary** - confirmed by actually applying it and running the full test
suite: `ColdWarmGripLearnerTests.A_noisy_scattered_session_stays_near_cold_even_with_many_samples` (and its
own paired `MutationGuard_count_only_weighting_would_have_trusted_the_noisy_session_far_more`) went RED
immediately (published reference jumped from an expected <2.0 to ~5.47 for a deliberately erratic 0.2-7g
session). These are pre-existing, deliberately-designed tests requiring a genuinely noisy session to stay
distrusted FOREVER, at any sample count - the OPPOSITE requirement from `KeyedScaleLearner`'s own bug fix.

**Why the two classes are different, not just differently tuned**: `KeyedScaleLearner`'s primary tier only
ever records a raw reading at a moment ALREADY independently detected as "physically at the limit" - an
inherently narrow, low-noise signal by construction (its own realistic CV of 5-15% plateauing below full
trust WAS a genuine bug against its own literal "-&gt;200 samples -&gt; weight 1.0" doc comment).
`GripLearner`'s `_hotSession` instead spans EVERY qualifying frame of an entire braking/throttle zone
(light-to-heavy, not just the peak) - real, legitimate sessions can and do show meaningfully higher
dispersion than a physically-anchored instant, and nothing in this class's own contract ever promised
"reaches exactly 1.0 at any count" the way `CalibrationConfidenceScaleSamples`'s doc comment did for
`KeyedScaleLearner`. **Left unchanged** - reported as a "does not have the same actionable gap" finding,
with the mutation evidence that proves it, not an oversight.

---

## PERSISTENCE

`RuntimeDocument.Version` bumped 4 -> 5. `GripLearnerState` (shared by `LockLearners`/`SlipLearners`
AND `LockPhysicalReference`/`SlipPhysicalReference` - i.e. BOTH channels already persist completely
independently, per Part 0 item 1) gained two new fields, `GMech` and `K`, both defaulting to `0.0`. A
document written before this feature existed simply lacks these two keys in its JSON; Newtonsoft's
construct-then-overwrite convention leaves them at that default, and `GripLearner.Load` treats a
non-positive `GMech` as "fall back to the flat `PeakG`" and any invalid `K` as `0.0` - the exact
bit-identical behaviour this feature guarantees for every pre-existing persisted profile, with no explicit
one-time-import code needed (same reasoning as the 2-&gt;3 bump). `PersistedGMech`/`PersistedK` mirror
`PersistedPeakG`'s own "HOT must not corrupt COLD" gate exactly. The JSON remains human-readable: `g_mech`
and `k` appear as two plain numeric fields per key, directly next to the existing `PeakG`/`Samples`.

---

## MUTATION EVIDENCE

**(1) Force k=0 and confirm a speed-dependence test fails** - done live: temporarily forced
`FitSpeedModel` to always return `(gMech: _learnedPeakG, k: 0.0)` and re-ran the suite.
`GripLearnerSpeedModelTests.Wide_speed_spread_with_a_genuine_aero_shape_fits_a_positive_k_and_fixes_the_low_speed_dead_zone`
and `KeyedGripLearner_persists_and_restores_gmech_and_k` both failed immediately ("expected a positive
fitted aero term, got 0"). Reverted immediately; suite re-confirmed green (868/868).

**(2) Disable per-event gating and confirm anchor-ordering degrades** - N/A as a shipped-test mutation
(Stage B never shipped, so there is no production gating to disable). The closest honest equivalent is
already reported in Part 3's own table: naive (ungated) vs event-gated monotonicity was measured side by
side on all 7 logs, and gating did NOT reliably improve ordering (2/7 vs 3/7) - the comparison this
mutation asks for was performed as the core of the feasibility check itself, not as an afterthought.

**(3) Replace pooled robust estimation with a point estimate** - N/A for the same reason (Stage B, which
is where pooled robust estimation would have lived, never shipped). Not fabricated or claimed.

---

## ACCEPTANCE CRITERIA

- **Normalized 30/60/80 <=> 75%(decel)/85%(accel)/90%/100% of grip available "at that moment"**: N/A to
  measure end-to-end, since Stage B (which is what would compute these three calibrated breakpoints) did
  not ship. What DID change is what "at that moment" means for the PHYSICAL detector feeding
  `KeyedScaleLearner`'s existing single anchor (80/100%) - Part 2's numbers are the direct measurement of
  that: the physical-limit detector now fires correctly across the whole speed range for Lock, instead of
  only above ~150-200 km/h.
- **No regression in F1 wet/dry consistency or the Forza guardrail**: unaffected by construction for any
  scenario that does not teach a wide speed spread (every pre-existing test in the 868-strong suite uses a
  fixed or narrow speed, so `k` stays exactly 0 for all of them, which is proven bit-identical to before -
  see the dedicated tests) - confirmed empirically by the full suite passing unmodified.
- **Non-monotonicity frequency (Stage B)**: 5 of 7 real Slip configurations non-monotone even with
  per-event pre-peak gating (Part 3) - this is the disqualifying number for Stage B.
- **Anchor pool dispersion (Stage B)**: not separately re-measured (P90-of-pool was already the estimator
  used, per the owner's own design, in both the naive and gated variants above) - the problem is
  monotonicity of the breakpoints themselves, not pool dispersion, exactly as the prior reports also found.

---

## Concerns

1. **The Lock channel remains fundamentally data-starved for anything beyond a single physical anchor** in
   every one of these 7 captures (0-3 pre-peak samples per band even with a full event-based gate) - this
   is now the THIRD independent investigation in this codebase to hit the same wall. A genuine three-point
   Lock calibration likely needs a capture with substantially more sustained, varied-intensity braking than
   any log available today.
2. **Stage A's fit is recomputed fresh on every single query** (no caching/hysteresis across frames) -
   this is simple and correct at convergence, but means a BRAND NEW key can show a few frames of transient
   k-nonzero-then-rejected behaviour before settling (see Part 2's Slip note) - harmless (bit-identical to
   the flat model at every frame where it actually matters, since the model self-corrects the instant
   enough evidence exists to reject a spurious fit), but worth knowing if this is ever profiled.
3. **The power-limited-regime "graceful inertness" for Slip is currently an emergent property** (Slip's k
   settles to 0, so the live ratio is governed by the naturally-low achieved g at high speed), not an
   explicit, separately-tested mechanism - it is CONFIRMED true on all 7 real logs (Part 2), but a future
   Stage B attempt (or a differently-shaped car where Slip's data DOES show a genuine positive slope for
   some other reason) should re-verify this before assuming it always holds.
4. **The falling-tail guard's 15% tolerance and the overshoot guard's 30% tolerance were both tuned against
   only 7 real logs** (one measured failure each) - they are principled and directionally justified, but
   not exhaustively cross-validated against a larger corpus; a future session with a very different car
   could in principle need retuning.
5. **No new `Diag.*` properties were added** for `g_mech`/`k` - they are inspectable directly in
   `QAdvanceFeedback.Parameters.json` (the brief's own "human-readable, sanity-checkable" bar), but not on
   a live dashboard/rig readout. Adding `Diag.Lock/Slip.GMech`/`.K` would be a small, bounded follow-up if
   the owner wants live visibility.
