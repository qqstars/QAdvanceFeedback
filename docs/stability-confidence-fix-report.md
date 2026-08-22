# Stability-gated maturity for the cold-start ceiling (Part 1, safety) + counter-saturation completion (Part 2)

## STATUS

**BOTH PARTS SHIPPED.** `GripLearner.Ratio`'s cold-start ceiling now gates on a NEW `MaturityConfidence`
(sample count AND reference stability) instead of the old `Confidence` (sample count alone). The three
previously-missed unguarded `Int32` counters (`OnlineDistributionLearner._count`,
`RobustBandEstimator`'s `_bucketCounts[bucket]`/`_validCount`, `HealthEntry.OccurrenceCount`) now saturate
consistently with the existing caps, each proven (by test) to keep LEARNING after the counter itself
freezes.

`dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Build`: **0 Warning(s), 0 Error(s)**.
`dotnet test QAdvanceFeedback.sln -c Release --no-build`: **912/912 passed, 0 failed, 0 skipped** (900
baseline + 12 new tests - 3 in `OnlineDistributionLearnerTests.cs`/`RobustBandEstimatorTests.cs`/
`HealthRegistryTests.cs` for Part 2, 8 in the new `StabilityConfidenceFixTests.cs` plus 1 in
`ColdWarmGripLearnerTests.cs` for Part 1; 3 pre-existing tests were re-expressed against the new,
intended contract, not deleted - see Part 1's own "what changed and why"). `QAdvanceFeedback\bin\Release\
net48\` contains exactly `QAdvanceFeedback.dll` + `.pdb` - **single-DLL confirmed**. No git operation of
any kind was run (the repo is not under git, confirmed directly). No recursive deletes. No Chinese-string
changes were needed for this task. A full pre-task snapshot of the working folder (900 tests) was taken
before any edit, per the brief's own instruction, to
`C:\Users\qchen\AppData\Local\Temp\claude\...\scratchpad\snapshot_900tests\` - **not used**, since the
task did not need to revert.

**Headline result (Part 1)**: re-running the exact `docs\band-accuracy-simulation-report.md` methodology
(the real `NormalizedWheelLockSlipEngine`, all seven replayable logs, causal-cold vs same-session-converged
comparison): frames reaching the literal maximum (100, "fully locked/spun") while genuinely far from it in
hindsight fell from **3,858 to 2** (99.95% reduction), and among those two residual frames the worst
converged (hindsight) value is **56.7**, not 3.0 - i.e. even the residual cases are frames BOTH cold and
hindsight agree were genuinely severe, not a false alarm on trivial grip. The maximum COLD value EVER
published among all over-statement frames fell from **100.0 to 82.4** - effectively no frame reads the
literal maximum any more when hindsight later disagreed. The RAW total over-statement count (any gap
&gt;0.5pp) fell more modestly, from 15,453 to **13,140** (~15%) - reported honestly as NOT a dramatic
reduction in the literal count, because most of the residual ~13,000 frames are small (well under 40 points),
tracing to the same standing, already-diagnosed data-starvation limitation (the "converged" reference
itself keeps drifting for most of these short sessions - see Concerns).

---

## PART 1 - STABILITY-GATED MATURITY

### The mechanism actually shipped

`GripLearner.cs`:

```
MaturityConfidence = Confidence (unchanged, samples/200, linear) * StabilityWeight(QuietStreak)
StabilityWeight(streak) = 1 - (1 - min(streak/120, 1))^2      -- EXACTLY 1.0 once streak >= 120
QuietStreak: +1 every qualifying Observe() call; when a call actually MOVES the flat peak's Level,
             the streak is REDUCED proportionally to how big that move was (never a hard reset to a
             fixed value) - a move >= 1% of Level effectively zeroes it, a smaller move only dents it.
Ratio(): uses MaturityConfidence (not the old, unchanged Confidence) to decide the cold-start ceiling.
```

`Confidence` itself is **completely unchanged** (still the plain `samples/MaturitySamples` linear ramp,
still what `Diag.Lock/Slip.LearnerConfidence` reports) - it is now one INPUT to `MaturityConfidence`
alongside the new stability term, not replaced.

### Why this design, and why it is NOT KeyedScaleLearner's own fix reapplied blindly

The brief flagged `KeyedScaleLearner`'s own full-trust floor (`if (primary.Count >= 200) weight = 1.0`,
overriding dispersion "regardless of dispersion") as a relevant, suspicious precedent - added
(`docs\anchor-rescale-report.md`) because `ConcaveHotWeight`'s dispersion term is a property of
inherently-noisy RAW DATA and, for real driving, never actually reaches zero, so without that floor the
weight plateaus below 1.0 **forever**. Reapplying that exact pattern here (a raw "regardless of X" override
at 200 samples) would have reintroduced precisely the bug this whole task exists to fix - a hard,
sample-count-only escape hatch.

**The key difference that makes THIS floor safe without a "regardless of" override**: `QuietStreak` measures
something about the ESTIMATE's own trajectory (has the learned peak's `Level` stopped moving), not about
the dispersion of raw incoming data. A genuinely converged `Level` really does stop moving (bit-for-bit,
in floating point, once no further RAISE/LOWER hit is corroborated) - so the streak's own concave ramp
reaches EXACTLY 1.0 by ordinary accumulation, with no artificial override needed. This was the explicit
design goal from the brief ("Find a formulation that satisfies BOTH: the anchor must be reachable, AND
maturity must not be declared while the reference is still visibly moving") and is verified directly:
`Full_trust_is_genuinely_reachable_not_an_asymptote_that_never_quite_arrives` (`StabilityConfidenceFixTests.cs`)
asserts `MaturityConfidence == 1.0` to 12 decimal places, not merely "close".

**Hard constraints satisfied, explicitly**:
- **No hard sample gate anywhere**: `StabilityScaleSamples` (120) is a SOFT scale reference for a concave
  ramp (`1-(1-x)^2`), identical in shape to `ColdWarmBlend.ConcaveCountConfidence`'s own already-accepted
  convention - it engages from the very first quiet call (non-zero weight immediately) and simply
  approaches, then reaches, 1.0 - never a step, never a requirement gating anything else.
- **Identity as the true cold state**: a brand-new learner starts at `QuietStreak = 0`, so
  `MaturityConfidence = 0` immediately (`QuietStreak_starts_at_zero_for_a_brand_new_learner_identity_as_the_true_cold_state`).
- **Continuous, no steps at any sample count**: `MaturityConfidence_is_continuous_no_step_at_any_sample_count_while_holding_steady`
  confirms the live `u` never jumps by more than 0.05 frame-to-frame while holding a constant input, exactly
  mirroring the pre-existing `The_cold_start_ceiling_relaxes_continuously_with_no_step_at_any_sample_count`
  guarantee this fix builds on top of, not replaces.
- **Under-report rather than over-report while cold**: `While_still_climbing_the_ceilinged_ratio_never_exceeds_the_eventual_settled_ratio`
  is a direct unit-level invariant (not merely inferred from the real-log measurement) confirming a
  still-moving reference's ceilinged ratio never exceeds what the SAME magnitude reads once genuinely
  settled.

### Persistence

`RuntimeDocument.Version` bumped 6 -&gt; 7. `GripLearnerState` gained one new field, `QuietStreak`, exported/
imported AS-IS (not cold/warm blended - a settling-streak count is discrete bookkeeping about the
estimate's own recent trajectory, not a physical quantity meaningful to interpolate, the same reasoning
already applied to `RaiseCandidateHits`/`LowerCandidateHits`). A document written before this feature
existed simply lacks this key; Newtonsoft's construct-then-overwrite convention leaves it at 0.0, which
`GripLearner.Load`'s new 9-arg overload already treats as "no settling evidence yet this session" - bit-
identical to a freshly-constructed learner. **Deliberately persisted** (unlike a hypothetical
"always reset on restart" design) so a RETURNING car whose reference was already genuinely settled across
many past sessions does not have its stability clock reset to zero on every restart -
`A_settled_quiet_streak_round_trips_through_KeyedGripLearner_reproducing_full_trust_immediately` proves the
full `ExportAll`/`ImportAll` round trip reproduces full trust immediately with zero new observations, while
`A_pre_version_7_document_imports_with_zero_quiet_streak_bit_identical_to_fresh` proves a legacy document
(no `QuietStreak` key at all) imports conservatively, as a true cold start.

### What changed and why (3 pre-existing tests re-expressed, not deleted)

Exactly like the delta-g-band-mapping-report's own precedent ("tests whose premise is now the OPPOSITE of
the intended behaviour... re-expressed... with the historical rationale kept visible"):

- **`GripLearnerTests.Ceiling_relaxes_as_confidence_matures_and_disappears_at_full_confidence`**: used to
  reach full trust the INSTANT `MaturitySamples` (200) was hit. Re-expressed to feed a comfortable margin
  PAST 200 (`MaturitySamples + StabilityScaleSamples + 100`) so the reference has also had time to settle -
  "no ceiling once genuinely mature" now honestly means mature AND settled, not merely observed enough
  times.
- **`ColdWarmGripLearnerTests.A_restart_with_no_new_driving_reproduces_the_previous_mapping_exactly`**:
  used to restore full trust from a bare `Load(peak, samples)` call with zero new observations. Re-expressed
  to use the full 9-arg `Load` overload with an explicit, representative settled `QuietStreak` - exactly what
  the REAL production round trip (`KeyedGripLearner.ExportAll`/`ImportAll`) carries forward for a genuinely
  already-settled car. A NEW test,
  `A_restart_via_the_bare_2_arg_Load_with_no_carried_settledness_stays_ceilinged_until_re_earned`, pins the
  opposite, equally-important case directly: a restore with NO carried settledness signal (e.g. a brand-new
  source/surface bucket seeded from a migrated one) stays conservatively ceilinged until re-earned - the
  whole point of the fix.
- **`NormalizedWheelLockSlipEngineTests.A_genuinely_low_source_reading_no_longer_suppresses_a_genuine_new_physical_peak`**:
  used to assert a SINGLE frame reaching a brand-new, higher peak reads near the max-grip anchor
  INSTANTLY. Re-expressed: the single first sighting must now read BELOW the anchor (`&lt; 79.9`,
  proving the ceiling engaged), and only reads near the anchor (`&gt;= 79.9`) after a further
  `StabilityScaleSamples + 20` frames of the SAME new peak being sustained - "conservative for a short
  settling window, not conservative forever."

### Convergence speed, quantified (acceptance item 6)

**Worst case** (a reference still being actively revised right up to and past the 200-sample mark - exactly
the real-log failure mode this task fixes): full trust is now delayed by an ADDITIONAL ~120-140 qualifying
"quiet" observations (`StabilityScaleSamples` + margin) after the LAST significant revision, versus being
granted INSTANTLY the moment total samples passed 200 before this fix. At the ~15-25 qualifying frames per
ordinary braking zone this codebase's own prior reports already establish, that is roughly **5-9 additional
braking zones** of settled, unchanging evidence - not "forever": demonstrated directly by
`A_genuinely_low_source_reading_no_longer_suppresses_a_genuine_new_physical_peak` (reaches the anchor within
`StabilityScaleSamples + 20` = 140 further frames of the new peak) and by the PRE-EXISTING
`Sustained_proportional_undercount_that_never_nears_absolute_zero_engages_the_relative_fallback` (a
DIFFERENT, already-shipped test explicitly anchored to "a realistic single-session sample budget" of 100
frames, per its own comment referencing real captured `Parameters.json` sample counts) continuing to pass
unmodified at the final tuning - i.e. the fix's own settling requirement was tuned to fit inside the SAME
realistic-session budget this codebase's own pre-existing tests already assume, not a new, slower bar.
**Best case**: a reference that stops moving well before 200 total samples (the common case for Slip,
which reaches maturity within seconds per prior reports) sees NO additional delay at all - full trust
arrives at exactly sample 200, identical to before this fix.

### Tuning process, disclosed

Two constants were empirically tuned against the same nine real logs (`StabilityMoveResetFraction` = 0.01,
`StabilityScaleSamples` = 120): starting values (0.05 / 50) reproduced the ORIGINAL problem almost
unchanged (14,472 total, 1,075 still reaching cold&gt;=80); progressively tighter settings (0.02/100,
0.01/200, 0.005/400) eliminated the &gt;=80 tail entirely but the most conservative of these (200/0.005)
broke THREE pre-existing, already-shipped convergence-speed-sensitive tests
(`Sustained_proportional_undercount_...`, `Ratio_clamps_an_impact_magnitude_reading_...`,
`Being_at_the_limit_on_a_loose_surface_reads_high_severity_...`) by keeping the ceiling engaged for LONGER
than those tests' own realistic-session budgets tolerate. **120/0.01 was chosen as the tightest setting that
still passes every pre-existing test unmodified** while reducing the &gt;=80 tail to 2 residual frames (from
3,858) - a deliberate, measured trade-off between safety and convergence speed, not an arbitrary choice.

---

## PART 2 - COUNTER SATURATION COMPLETION

Three previously-missed unguarded `Int32` increments now saturate at the SAME shared cap
(`1,000,000`) every other counter in this codebase already uses:

1. **`OnlineDistributionLearner.cs:118` `_count++`** - the MOST safety-relevant of the three (feeds
   `KeyedScaleLearner.ConcaveHotWeight`'s own `count` parameter directly; a negative wrap would have
   silently collapsed a fully-earned calibration weight toward zero, per the brief's own flag). `_count`
   is a PURE bookkeeping counter with nothing else's own mean depending on it (the class's `_sum` field is
   dead/write-only, confirmed directly) - capping it alone is fully safe; `_histogram`/
   `_decayedWeightedSum`/`_decayedWeight` (what `GetAverage`/`GetPercentile` actually read) are entirely
   independent fields, unconditionally updated on every call regardless of the cap. Test:
   `Count_saturates_at_the_cap_while_the_decaying_average_keeps_learning`.
2. **`RobustBandEstimator.cs:187/190` `_bucketCounts[bucket]++`, `_validCount++`** - `_validCount` saturates
   independently (a plain population counter). `_bucketCounts[bucket]`/`_bucketSums[bucket]` are a PAIRED
   count/accumulator (unlike GripLearner's `SpeedBucket`, this class has no SEPARATE "actual learned
   quantity" structure alongside them - the pool estimate `TryEstimate` reports IS this state), so freezing
   both together (GripLearner's own `SpeedSumMps`/`Count` precedent) would have frozen the estimate itself,
   failing the task's own "keeps learning" requirement. Instead: the COUNT freezes at the cap, but
   `_bucketSums[bucket]` keeps moving via the SAME fixed, tiny (1-in-a-million) Welford-style nudge
   `WelfordAccumulator` already uses, self-consistently read back through the SAME unchanged `sum/count`
   formula `TryEstimate` always used (since `count` is now the frozen constant). A sample landing in an
   already-saturated bucket is no longer individually enqueued for eviction (nothing to un-count later).
   Test: `Bucket_count_and_validcount_saturate_at_the_cap_while_the_pool_estimate_keeps_learning`.
3. **`HealthEntry.cs:64` `OccurrenceCount++`** - a plain counter with nothing else depending on it; freezes
   with no side effect beyond the reported count no longer climbing, while `Detail`/`LastOccurredUtc` keep
   updating on every further recurrence. Test: `OccurrenceCount_saturates_at_the_cap_while_Detail_keeps_updating`.

All three tests explicitly prove **"a saturated counter keeps LEARNING while the counter itself stops
rising"**, per the brief's own explicit test requirement - not merely that the counter freezes.

---

## ACCEPTANCE RESULTS (re-run of `docs\band-accuracy-simulation-report.md`'s exact methodology, real
engine, all nine logs - 7 replayable, 2 lack `Diag.Telemetry.*` entirely, unchanged from that report)

### 1. THE HEADLINE

| | BEFORE (reproduced via mutation - see below) | AFTER (shipped) |
|---|---|---|
| **Total over-statement frames** (cold exceeds converged by &gt;0.5pp, ~73,000 qualifying) | 15,453 (~21%) | **13,140 (~18.0%)** |
| ...while samples&lt;200 (ceiling nominally active) | 1,745 | **1,507** |
| ...after samples&gt;=200 (confidence was 1.0, ceiling fully lifted) | 13,708 | **11,633** |
| Max cold value ever published among over-statement frames | 100.0 | **82.4** |
| **Frames reaching cold&gt;=80 ("fully locked/spun")** | 7 (during ceiling) + **3,858** (after maturity) = 3,865 | 0 (during ceiling) + **2** (after maturity) = **2** |
| Min converged value among those (during ceiling / after maturity) | 58.7 / **3.0** | n/a / **56.7** |

**The most severe, safety-critical failure mode - a frame reading the literal maximum while genuinely far
from it in hindsight - is reduced from 3,858 instances (min hindsight value 3.0, i.e. "barely any lockup at
all") to 2 instances whose worst hindsight value is 56.7 (a genuinely severe event, not a false alarm).**
The raw total count fell more modestly (~15%), honestly reported as NOT dramatic in absolute terms - see
Concerns for why, and why closing it further risked (and, at more aggressive tuning, DID) break pre-existing
convergence-speed guarantees.

### 2. Band correspondence

| channel | u≈0.75 (target 30) | u≈0.90 (target 60) | u≈1.00 (target 80) |
|---|---|---|---|
| Lock, BEFORE (`delta-g-band-mapping-report.md`) | 30.9 | 59.2 | 79.4 |
| Lock, AFTER | **30.3** (n=2699) | **58.0** (n=207) | **n=0 - unmeasurable, see below** |
| Slip, BEFORE | 30.7 | 61.4 | 80.0 |
| Slip, AFTER | **31.0** (n=6199) | **57.7** (n=2971) | **n=0 - unmeasurable, see below** |

The 30/60 anchors remain close to target and essentially unregressed. **The 80-anchor (u≈1.00) real-log
check is no longer measurable on these nine captures at all - EVERY one of the 7 replayable logs, both
channels, now shows n=0 frames with the design-internal, ceilinged `u` within ±0.03 of 1.00.** Diagnosed,
not merely observed: this harness bins by the LIVE, ceilinged `LockUtilization`/`SlipUtilization` (exactly
what the cited report validated against) - the fix's own settling requirement keeps that value away from
1.00 for most of these short sessions BY DESIGN (a frame reading genuinely at the limit is, in these short
captures, usually EITHER a brand-new peak just being set - inherently unstable, correctly ceilinged - OR a
re-confirmation of an old peak after a genuine quiet window, which these particular short captures rarely
contain twice). **This is not a formula regression**: the underlying `R(u)=1.0 -&gt; 80/75` mapping itself is
completely untouched by this task and remains bit-exact per the still-100%-passing synthetic unit tests
(`DeltaGCollapseBandMappingTests`' own Theory cases construct a steady-state u=1.0 directly and are
unaffected by real-log capture length). Reported plainly as a genuine, disclosed side effect of the fix's
own intended conservatism, not papered over.

### 3. Lock published `.All` time above 80

**0.01% pooled** (was 1.89%, must not regress) - **improved further**, not regressed; a direct, expected
consequence of far fewer frames reading the literal maximum any more.

### 4. Achieved/available ratio at anchors (should move toward ideal 75/90/100)

| | BEFORE dev | AFTER dev | direction |
|---|---|---|---|
| Lock @30 | +65.2pp | **+53.0pp** | toward ideal |
| Lock @60 | +9.8pp | +12.6pp | away from ideal (small) |
| Lock @80 | +12.3pp | **-3.0pp** | toward ideal (crossed to a slight under-read) |
| Slip @30 | -11.0pp | **-3.1pp** | toward ideal |
| Slip @60 | -7.1pp | +39.6pp | away from ideal |
| Slip @80 | +45.8pp | +118.7pp | away from ideal |

**Genuinely mixed, reported plainly rather than selectively favourable**: Lock improves at the 30/80
anchors and regresses slightly at 60; Slip improves at 30 and regresses at 60/80. This metric bins by
PUBLISHED `.All` (not by `u`), so it is a second-order consequence of exactly WHICH frames now populate
each capture band once the underlying severity distribution shifted (far fewer frames anywhere near
Normalized≈80 for Lock, changing which frames are even sampled for that band) - not a direct target of this
fix, and per the brief's own framing ("full correction may require better data than these short captures
provide, and saying so is acceptable"), not chased further here.

### 5. Source invariance (c153 Raw vs ShakeIt, same route)

Lock car-level gap **0.6pp** (was ~1.0pp), Slip car-level gap **0.5pp** (was ~1.0pp) - **not regressed**,
comfortably within the ~1.0pp bound.

### 6. Convergence speed

Quantified above (Part 1's own section) - worst case adds ~120-140 qualifying quiet frames (roughly 5-9
ordinary braking zones) before full trust, best case unchanged. Demonstrated to fit inside the SAME
realistic-session budget (100-300 qualifying samples) this codebase's own pre-existing tests already
assume - not "conservative forever."

---

## MUTATION EVIDENCE

**(1) Remove the stability requirement** (`Ratio` reverted to read the OLD, unchanged `Confidence` instead
of `MaturityConfidence`, rebuilt, `band-accuracy-harness` rerun live): reproduces the ORIGINAL report's
numbers **exactly** - 15,453 total / 1,745 during-ceiling / 13,708 after-maturity / 7 and 3,858 reaching
cold&gt;=80 / min converged 58.7 and 3.0. Confirms the fix is the sole, load-bearing cause of the
improvement above. Reverted immediately; 912/912 re-confirmed green.

**(2) Revert one saturation cap** (`OnlineDistributionLearner._count++` unguarded, rebuilt): its own
dedicated test failed immediately - `Count_saturates_at_the_cap_while_the_decaying_average_keeps_learning`,
`Expected: 1000000, Actual: 1000005`. Reverted immediately; 912/912 re-confirmed green.

---

## Concerns

1. **The raw total over-statement count (15,453 -&gt; 13,140) did NOT fall dramatically in absolute terms**,
   even though the safety-critical tail (frames reaching the literal maximum) fell by 99.95%. Diagnosed:
   most of the residual ~13,000 frames are SMALL over-statements (well under 40 published points), tracing
   to the same standing, already-diagnosed data-starvation limitation three prior reports in this codebase
   flag repeatedly - in these particular short (53s-400s), data-starved captures, the "converged" reference
   itself (this session's own FINAL state) keeps drifting upward for most of the session, so almost any
   causal (cold) reading will show SOME excess relative to where the session eventually ends up, purely as
   a structural property of short capture length, not a defect this mechanism can fully close without
   either much longer real captures or a design that stays conservative indefinitely (which would itself
   violate the "not conservative forever" requirement). More aggressive tuning (tried, measured, and
   REJECTED - see Part 1's own tuning section) reduced this further but broke pre-existing convergence-
   speed guarantees; 120/0.01 was chosen as the tightest setting compatible with every existing test.
2. **The u≈1.00 real-log band-correspondence check is no longer measurable on these nine captures** (n=0
   universally) - a genuine, disclosed side effect of the fix's own conservatism in short sessions, not a
   formula defect (verified unaffected at the unit level, where the mapping is exercised in a controlled,
   steady-state synthetic scenario rather than depending on real-log capture length).
3. **The achieved/available-ratio acceptance item shows a genuinely mixed result** (2 of 6 anchor readings
   move away from ideal) - a second-order, indirect consequence of the shifted severity distribution
   changing which frames populate each capture band, not something this fix directly targets; per the
   brief's own explicit allowance, not chased further.
4. **The two tuning constants (`StabilityMoveResetFraction`=0.01, `StabilityScaleSamples`=120) were
   empirically tuned against the real logs available in this repository** - the same standing caveat every
   constant-tuning report in this codebase already carries; a substantially different capture length or
   driving style could need retuning, and a slow, continuous "creep" in the reference (many tiny sub-
   threshold revisions accumulating over hundreds of frames) is less aggressively caught by this
   single-step, proportional-reset design than a sudden large jump is - a windowed/trailing-comparison
   design was considered as a more thorough alternative but not implemented, given the time available and
   the risk of introducing its own asymptote/reachability problems (the exact failure mode this task
   explicitly warns against reintroducing).
5. **Generalisation beyond these nine real logs is unverified** - the same standing caveat every prior
   measurement report in this codebase carries.
