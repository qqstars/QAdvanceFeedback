# Slip SMax: the traction-crossing teaching gate

**Status: IMPLEMENTED 2026-09-03.** §2's crossing gate and §7's fallback are in the code; §8 records
what shipped and the three separate defects found while building it. Sections 1-7 are kept as written -
they are the reasoning and the pre-implementation measurements, and §8 corrects them where building it
proved them wrong.

Sections 1–4 were measured on one real session log,
`c_1_8_2_e_d\QAdvanceFeedback.session-20260902-202751.csv` — F1 2025, "F1 Generic", 19,727 frames,
354.2 s, ~56 Hz. **Section 6 re-measures the design across the whole 17-log corpus by replaying it
through the real pipeline, and corrects two of those single-log numbers — read it before
implementing.** The numbers are reproduced inline deliberately: this document must stay readable
without the logs, and without any other document. (Two design documents this project's code cites —
`cross-channel-smax-report.md` and a `GForceMaxLearner.DefaultMinSamples` constant — turned out not to
exist, which is why the evidence is embedded here rather than referenced.)

---

## 1. The problem

Slip's SMax is taught by a single boolean on total G magnitude:

```csharp
bool physicallyAtLimit = physicalRatioNow >= PhysicalLimitRatioThreshold;   // 0.85 of the car's peak G
double smaxTeachingWeight = isLockChannel ? atLimitWeight : (physicallyAtLimit ? 1.0 : 0.0);
```

`physicalRatioNow` is this frame's |G| as a fraction of the learned peak G. It has no direction, no
speed condition, and no plausibility ceiling. Measured consequences on the log:

| | frames | share of session |
| --- | --- | --- |
| Slip teaching frames | 6101 | **30.9%** |
| Lock teaching frames (corner-local detector) | ~200 | ~1% |

Of slip's 6101 teaching frames:

| regime | frames | share of slip teaching |
| --- | --- | --- |
| **Braking** (brake > 20%) | 3212 | **52.6%** |
| On power (throttle > 40%) | 2833 | 46.4% |
| Coasting | 56 | 0.9% |

So **over half of the slip ceiling is taught by braking frames.** Slip is a traction phenomenon;
deceleration G should not qualify a frame as "at the slip limit". Five frames also passed the gate at
**7.6 g to 19.5 g** — impacts. The G-force max learner hard-rejects above 8 g; this gate has no
equivalent.

The failure this produces in practice is a standing start: launch G is genuinely near the car's peak,
so the gate opens, and the source is reading full wheelspin. Simulated against the real learner with a
cold reference of 80:

```
TODAY - every teaching frame at weight 1.00
  t=0   cold start                     SMax= 80.0   live= null   confidence= 0.00
  t=10  after standing start @100      SMax= 99.8   live=100.0   confidence= 1.00
  t=30  after 20s driving, no crossing SMax= 83.5   live=100.0   confidence= 1.00
  t=34  three more crossing corners    SMax= 83.6   live=100.0   confidence= 1.00
```

Ten seconds of launch produces **SMax 99.8 at full confidence**, and the live anchor stays pinned at
100 for the rest of the session. This reproduces the owner's field observation of Slip SMax = 100.

---

## 2. The design

**Teach on positive evidence of a traction limit, and on nothing else.**

The physical signature of crossing the traction limit is **slip rising while acceleration-G falls** —
the tyre is past the peak of its slip curve, so more slip is buying less grip. That, and only that,
qualifies a frame to teach SMax.

### The rule

1. Keep the existing teaching gate (`physicalRatioNow >= 0.85`) as the **candidate** filter. It decides
   which frames are *considered*, not which frames *count*.
2. On each candidate frame compute a short **causal** trend — the current frame against the frame 5
   back, never centred, because a live detector cannot look ahead:
   `d(slipSource) > +1.0` **and** `d(accelG) < −0.05` marks a **crossing**.
   `accelG` here is unambiguous: `Diag.MotionMagnitudeG` and `|Diag.Telemetry.LongitudinalG|` are the
   same number in every logged frame of the corpus (19,727 of 19,727 exactly equal on the reference
   capture), so the two candidate definitions are one detector, not two.
3. A crossing opens a **qualified window**. Frames stay qualified while a crossing has occurred within
   the last `CrossingHoldSeconds` (proposed **1.0 s**).
4. Feed **every** candidate frame to the learner, with weight:
   - **1.0** if qualified
   - **0.0** if not

### Why weight 0 rather than skipping the call

This is the crux of the design and is easy to get wrong. The learner counts two different things:

```csharp
if (_count < SampleCountSaturationCap) _count++;   // once per call, weight-independent
_decayedWeightedSum = … + weight * abs;            // the histogram IS weighted
```

- `Count` → drives `CeilingHandoverConfidence` and advances the decay clock. **Counts calls.**
- `PositiveSampleCount` → gates `GetPercentile` and `PhysicalAnchorReadinessWeight`. **Weight-derived.**

So a zero-weight fold-in keeps the sample economics of today while contributing nothing to the value.
Measured on the log — note these counts are the **CSV-only** ones §6 corrects, so read the columns as
a ratio between the three rows, not as absolute throughput:

| | fold-ins | p90 of teaching basis | histogram half-life | 2000-sample full confidence |
| --- | --- | --- | --- | --- |
| Today, all weight 1 | 6101 | 17.2 | 805 s | **1.9 min** |
| **Drop** non-crossing | 2438 | 19.8 | 2014 s | 4.8 min |
| **Weight 0.00** | 6101 | **19.8** | **805 s** | **1.9 min** |

Weight 0 gives the value of dropping with the responsiveness of today. Dropping is strictly worse: the
decay is *per fold-in*, so feeding fewer samples stretches the forgetting half-life in wall time. The
argument is structural and survives §6's correction — indeed the real yield being *lower* than assumed
makes dropping worse still, since it would stretch the half-life further.

### Constants

| name | value | note |
| --- | --- | --- |
| `CrossingTrendFrames` | 5 | half-width of the trend window |
| `CrossingSlipRise` | +1.0 | minimum d(slipSource) over the trend |
| `CrossingGFall` | −0.05 | maximum d(accelG) over the trend |
| `CrossingHoldSeconds` | 1.0 | how long a crossing keeps frames qualified |
| non-crossing weight | **0.0** | see §3 — 0.05 was measured and rejected |

A first pass, counting crossings **from the CSV columns alone**, put the yield at 2438 qualified
frames per session (6.88/s) and concluded every learner threshold was cleared comfortably. **That
number is wrong and §6 supersedes it.** It counted frames the engine never reaches: `ComputeChannel`
teaches only when the channel is *engaged* (`LongitudinalDirectionResolver` says SpeedingUp for Slip),
and the replay additionally drops sub-3 km/h frames. Measured through the real pipeline the same
capture yields **76 / 0 / 362 qualified frames across its three sessions**, and the corpus-wide median
is 594 with a floor of 0. See §6 for the corrected throughput table and what it costs.

### Expected behaviour

```
PROPOSED - non-crossing weight 0.00
  t=0   cold start                     SMax= 80.0   live= null   confidence= 0.00
  t=10  after standing start @100      SMax= 80.0   live= null   confidence= 0.00
  t=30  after 20s driving, no crossing SMax= 80.0   live= null   confidence= 0.00
  t=31  first crossing corner @40      SMax= 70.8   live= 40.0   confidence= 0.25
  t=34  three more crossing corners    SMax= 40.8   live= 40.0   confidence= 1.00
```

Until a crossing occurs the channel publishes the **cold reference** and reports **confidence 0** —
honestly unknown, rather than confidently wrong. No launch detection, no speed threshold, no clutch
condition, no window timeout: a launch simply never qualifies.

Also record, per key, **`validFrames`** (qualified) and **`totalFrames`** (all candidates).
`totalFrames` is **not to be used yet** — it is reserved for a future Lock/Slip balancing decision
(§4).

---

## 3. Alternatives measured and rejected

**Non-crossing weight 0.05 (or any small non-zero).** Rejected. SMax is a **P90**, so any contaminant
occupying more than ~10% of the weighted pool owns the answer outright; a small weight only delays how
long the contaminant must be present. Against a cold reference of 80 with a launch at source 100:

| launch length | w=0.05, after 5 crossing corners | w=0.00, any length |
| --- | --- | --- |
| 5 s | 69.7 ✓ | 80 → 40.8 ✓ |
| 10 s | 68.2 ✓ | 80 → 40.8 ✓ |
| **20 s** | **87.1 — anchor still pinned at 100** | 80 → 40.8 ✓ |

A 5 s launch survives only because `300 × 0.05 = 15.0` weighted samples falls just under
`MinPhysicalAnchorSamples = 20`. That is luck, not design.

**A commit/discard window with a timeout (2–20 s).** Superseded. It worked (99.4% commit on normal
driving) but needed a window size, a timeout, and a default-on-expiry, and it committed *everything*
buffered — so a crossing late in a window could rescue a launch earlier in the same window (measured
max committed window duration: 4.78 s). Weight 0 removes the failure mode by construction instead.

**Excluding braking frames from slip teaching.** Correct in principle — slip should not learn from
deceleration — but low value: braking and on-power teaching frames carry nearly the same values
(p90 17.6 vs 16.8), so removing half the pool barely moves a percentile. Superseded by the crossing
gate, which excludes them anyway. Keep as a possible tidy-up, not a fix.

**Borrowing Lock's SMax for Slip.** Rejected on measurement — and re-confirmed corpus-wide in §6,
which shows the ratio stays at 1.52 *even with the crossing gate applied*, so the gate does not make
the two ceilings interchangeable.

```
Lock SMax : p50 68.3        Slip SMax : p50 35.4
Lock/Slip ratio: p10 1.06   p50 1.92   p90 2.04

source while BRAKING  (lock events): p90 17.4  p99 70.0  max 70.0
source while ON POWER (slip events): p90  8.8  p99 24.7  max 48.1
```

Even though branch 7 makes the two channels' sources bit-identical, the source *reaches* far higher
under braking (a locked wheel drives |slipRatio| to 1.0; a spinning rear reaches ~0.39). Lock's ceiling
is legitimately ~2× Slip's, so substituting it would inflate the slip reference and make slip output
read roughly half what it should. The ratio is also unstable (1.06–2.04), so no fixed correction factor
exists, and it is car- and title-specific.

---

## 4. Not resolved, deliberately deferred

- **The never-crossing case. PROMOTED TO REQUIRED by §6 — this is no longer a corner case.** 5 of the
  25 replayed sessions produced fewer than 100 qualified frames and one produced **zero**, so a
  fallback is part of shipping the gate, not a follow-up. The suggested direction is unchanged: a
  **time-based relaxation** — after N minutes of driving with confidence still 0, begin admitting
  non-crossing candidates at a small weight — so the channel converges on something measured from its
  *own* regime rather than importing Lock's. Still not designed.
- **`totalFrames` consumption.** Recorded now, unused. Intended for a future Lock/Slip balance that
  weighs "how much evidence exists" separately from "how much of it qualified".
- **The plausibility ceiling.** Independent of this design and worth doing regardless: reject teaching
  frames above the same 8 g the G-force learner already uses. Five frames in this log passed at up to
  19.5 g.

---

## 5. Unverified — read before implementing

**The log contains no standing start.** It opens at 253 km/h; the only sub-5 km/h frames are the last
152 (parking). So:

- Every commit-path number above is measured on real driving and is trustworthy.
- **The launch behaviour is inferred, not measured.** It rests on the physics argument that a launch
  has no slip-rising-while-G-falling moment, plus the synthetic simulation in §2.
- `ClutchPercent` is **0.0 in all 19,727 frames** in this title, so any design depending on clutch is
  untestable here. This design deliberately does not use it.

**Before implementing, capture a log containing a standing start** — a single out-lap from stationary
is enough — and confirm that no crossing is detected during the launch. That is the one assumption the
whole design rests on, and the one this log cannot check.

---

## 6. Cross-log verification — measured 2026-09-03

**Question asked:** §3 rejected borrowing Lock's SMax on a Lock/Slip ratio of ~1.9, measured while
Slip was being taught by *every* at-limit frame. If Slip is taught only on crossings, does its ceiling
move close enough to Lock's for the two to be interchangeable?

**Method.** Every capture in `C:\Development\Repos\Samples\simhub` with the 1.5+ schema — 17 files,
24 replayed sessions across two titles, two cars, both sources (Raw and ShakeIt), wet and dry —
replayed through the **real** `NormalizedWheelLockSlipEngine`, three times over: today's rule, and the
crossing gate under each of the two `accelG` definitions. Sessions split on 5 s timestamp gaps,
sub-3 km/h frames dropped, learner state carried across sessions within a file through the same
`ExportAll`/`ImportAll` round trip a SimHub restart uses. Reported value is the median of the
per-frame learned ceiling over each session's second half. The two `accelG` definitions produced
byte-identical results — see §2.

**Answer: no. The gate narrows the gap slightly and tightens its spread, but Slip stays ~1.5× below
Lock. §3's rejection of borrowing stands, and is now corpus-wide rather than single-log.**

| statistic over 24 sessions | today | crossing gate |
| --- | --- | --- |
| median Slip SMax | 47.7 | 52.2 |
| **median Lock/Slip ratio** | **1.59** | **1.52** |
| p10 → p90 Lock/Slip ratio | 0.79 → 3.33 | 0.81 → 2.71 |
| spread of the ratio (σ of ln) | 0.537 | 0.483 |
| mean \|ln(Lock/Slip)\| | 0.557 | 0.516 |
| sessions moved closer / further / unchanged | — | 15 / 8 / 1 |

The gate is not a systematic lift, either: Slip SMax rose in 15 sessions, **fell in 6**, and was flat
in 3, median change +3.1%. What it actually does is cut the tail — the p90 ratio drops from 3.33 to
2.71 — which is the correct shape for a fix that removes contamination rather than adding signal.

**It does fix the pathology it was designed for.** The 1.8.1 capture is the session whose Slip SMax
the owner observed pinned at 100:

```
1.8.1 s1    Lock 91.0    Slip 100.0 -> 70.9    ratio 0.91 -> 1.28
1.6.8 ShkIt Lock 100.0   Slip  28.7 -> 47.8    ratio 3.48 -> 2.09
1.7.1 s2    Lock  75.9   Slip  47.3 -> 58.2    ratio 1.60 -> 1.30
```

Slip 100.0 → 70.9 is the largest single movement in the corpus and it is in the right direction. No
other mechanism tried has moved that session.

**The cost: crossing yield is far lower than §2 assumed, and is worst on the newest captures.**

| session | frames | candidates | qualified | qual/cand | qualified/s |
| --- | --- | --- | --- | --- | --- |
| 1.7.1 s4 | 6758 | 1457 | 985 | 68% | 8.16 |
| 1.6.5 Raw | 6732 | 1263 | 810 | 64% | 6.74 |
| *(corpus median)* | | | **594** | **50%** | **5.0** |
| 1.5 RawDry | 5608 | 1136 | 89 | 8% | 0.89 |
| **1.8.2 s1** | 3454 | 843 | **76** | **9%** | 1.23 |
| **1.8.2 s3** | 14874 | 2858 | **362** | **13%** | 1.36 |
| **1.8.2 s2** | 1257 | 184 | **0** | **0%** | **0.00** |

Five of 25 sessions fall under 100 qualified frames and one gets none at all. Against
`CeilingConfidenceFullSamples = 2000` the median session needs ~6.7 min of driving to reach full
confidence; the 1.8.2 sessions would need ~25 min or never. This is why the never-crossing fallback
moved from §4's deferred list into the required set.

**Fidelity caveat.** The replay always starts each file cold, whereas the real plugin carried persisted
state into these captures, so replayed and logged `Diag.Slip.SourceScaleCeiling` agree on some sessions
(1.8.2 s3: replay 34.7 vs logged 35.4) and diverge on others (1.8.2 s1: 38.8 vs 61.3). That does not
weaken the comparison above, which is a **within-replay contrast** — both arms start from the same
cold state and differ only in the gate.

**Reproducing this.** The run is not in the test suite: it needs a one-line experimental seam in
`NormalizedWheelLockSlipEngine.ComputeChannel` (force `smaxTeachingWeight` to 0 on unqualified Slip
candidates, and let `ObserveAtPhysicalLimit` still be called at weight 0), which does not belong in
shipped source before the design is accepted. It was run against a scratch copy of the tree, driven by
`RealLogSMaxConvergenceReportTests`' own CSV/session-splitting helpers. Runtime ~8 minutes for the
three passes.

---

## 7. The low-confidence fallback — three-way ceiling blend

Agreed in principle 2026-09-03, as the answer to §6's finding that the crossing gate can leave Slip
with little or no evidence of its own. **Measured below; one mandatory correction.**

### 7.1 "Confidence never reaches 1.0" is the SAME problem, not another one

It is worth stating plainly because the design turns on it. Today's published ceiling is already a
two-way blend (`KeyedScaleLearner.LearnedCeilingForKey`):

```
published = anchor + c * (learned - anchor)          c = CeilingHandoverConfidence
```

`anchor` is the resolved tier reference — a borrowed ceiling from another key/session, or the shipped
constant at Tier 1. So "confidence below 1" is not a fault condition; it is the normal operating state
the blend exists to handle. **No separate "cold start finished but nothing recorded" trigger is
needed** — that state is simply `c = 0`, and a continuous formula covers it and every value in between.

What the crossing gate changes is only *how long* `c` sits near zero, and it genuinely will not reach
1.0 in some sessions. `CeilingHandoverConfidence` is `weight × PhysicalAnchorReadinessWeight`, and the
readiness term needs `CeilingReadinessSamples = 100` **positive** (weight-bearing) samples. Under the
gate, §6's yields translate directly:

| session | qualified frames | ceiling of `c` |
| --- | --- | --- |
| corpus median | 594 | 1.00 |
| 1.8.2 s1 | 76 | **0.70 — can never reach 1** |
| 1.8.2 s2 | 0 | **0.00 — permanently cold** |

(`Count` still advances on every candidate, including the weight-0 folds — that is what §2's weight-0
choice buys. It is `PositiveSampleCount` that starves.)

### 7.2 The rule

Split the existing `anchor` term into Lock's own evidence and the borrowed/fixed anchor:

```
c   = Slip's CeilingHandoverConfidence
cL  = Lock's CeilingHandoverConfidence

wSlip   = c
wLock   = (cL / 2) * (1 - c)
wAnchor = (1 - cL / 2) * (1 - c)

SMax_slip = wSlip * slipLearned + wLock * lockContribution + wAnchor * slipAnchor
```

The weights sum to 1 by construction. The owner's own worked example: Lock at 80% real-time / 20%
borrowed and Slip at 30% gives Slip 30%, Lock `0.80/2 × 0.70 = 28%`, borrowed Slip `0.60 × 0.70 = 42%`.

Two properties worth keeping:

- **It is a strict generalisation of today.** At `cL = 0` it collapses to `c·learned + (1−c)·anchor`,
  today's formula exactly. Wherever Lock has no evidence, behaviour is provably unchanged.
- **The `/2` is the discount for borrowing across channels.** Even a fully confident Lock never takes
  more than half the non-Slip share, because Lock's ceiling measures a different physical event.
- `lockContribution` must be drawn from **Lock's own at-limit anchor**, not Lock's *published* ceiling
  — the published one is itself `anchor + cL·(…)`, so feeding it in would count the borrowed part twice.

### 7.3 MANDATORY: Lock's SMax must be scale-corrected first

`lockContribution = lockAnchorLevel / LockToSlipRatio`. **This divisor is not optional.** §6 measured
the two channels at a median Lock/Slip ratio of 1.52; injecting Lock's raw number makes every estimate
worse. Measured on the 17 cold (Tier-1, nothing to borrow) sessions, truth being each session's own
crossing-taught Slip SMax, at the worst case `c = 0, cL = 1`:

| what is published | median abs error | mean abs error | beats today |
| --- | --- | --- | --- |
| **today** — shipped constant alone | 25% | 43% | — |
| blend, **k = 1.00** (raw Lock) | **50%** | **61%** | 4/17 |
| blend, k = 1.25 | 33% | 45% | 7/17 |
| blend, k = 1.50 | 23% | 36% | 11/17 |
| blend, **k = 1.75** | **22%** | **31%** | **13/17** |
| blend, k = 2.00 | 20% | 28% | 13/17 |

Uncorrected, the fallback roughly doubles the error it was meant to reduce. Corrected, it clearly
helps. **Proposed default `LockToSlipRatio = 1.6`**, mid of the flat 1.5–1.75 optimum and consistent
with §6's independently measured 1.52. A per-(game, car, source) *learned* ratio — recorded whenever
both channels hold a confident SMax at once, persisted and borrowable like every other tier reference
— is the natural v2 and is not needed to ship.

### 7.4 RECOMMENDED: taper Lock's share by the anchor's tier

The same test run where a **real** previous-session Slip ceiling exists to borrow (7 multi-session
cases) reverses the verdict:

| what is published | median abs error | mean abs error |
| --- | --- | --- |
| today — borrowed Slip SMax alone | **16%** | **20%** |
| blend with k = 1.52 | 17% | 23% |
| blend with k = 1.00 | 31% | 27% |

Against a genuine measurement of Slip's own ceiling, Lock contributes noise, not information. Against
a shipped constant (§7.3) it contributes real, same-session, same-car evidence. **So Lock's share
should follow how good the thing it is displacing is** — which the tier system already knows:

```
wLock   = tau * (cL / 2) * (1 - c)
wAnchor = (1 - tau * cL / 2) * (1 - c)
```

| slip anchor's tier | what the anchor is | proposed `tau` |
| --- | --- | --- |
| Tier 1 | shipped constant, nothing to borrow | 1.0 |
| Tier 2 | a different game | 0.7 |
| Tier 3 | same game, different car | 0.4 |
| Tier 4 | same game+car, different surface | 0.2 |

`tau = 0` still degenerates to today's formula, so the backward-compatibility property survives.

### 7.5 What this does and does not resolve

Both of the owner's stated goals hold:

- **A standing start that teaches nothing no longer leaves the channel stranded.** With `c = 0` the
  ceiling is `(cL/2)·(Lock/1.6) + (1 − cL/2)·anchor` — measured evidence from this very session and
  car, instead of a shipped constant.
- **The standing start no longer inflates the real-time Slip SMax.** That is §2's crossing gate; §7
  only supplies what to publish while the gate is correctly refusing to learn.

Not resolved, and worth stating before implementing:

- **`cL` is confidence, not correctness.** A confidently wrong Lock is imported at full weight. §6 has
  two such sessions (1.5.3 s2 Lock 28.5; 1.8.2 s3 Lock 23.1) where Lock is the broken channel. The
  `/2` and `tau` factors bound the damage; nothing detects it.
- **7 and 17 cases are thin.** Both tables are the whole corpus, but the corpus is small and the
  "truth" column is itself the crossing gate's own output, not an independent measurement.
- **§5's gap is still open.** No capture in the corpus contains a standing start, so the launch
  behaviour this whole design targets remains inferred rather than measured.

---

## 8. What shipped — 2026-09-03

Implemented as designed, plus **two corrections the design did not anticipate and one defect it would
have shipped silently.** Read 8.2 before trusting §2 on its own.

### 8.1 The code

| file | change |
| --- | --- |
| `Core\Normalized\SlipCrossingGate.cs` | **new** — the detector: causal 5-frame trend, 1.0 s hold, **onset snapshot + one-crossing-per-event + at-limit arming** (all three needed — see 8.4), `NonCrossingWeight` (default 0.0), per-key `validFrames`/`totalFrames` |
| `Core\OnlineDistributionLearner.cs` | **new** `AddNonQualifyingObservation()` — advances `Count` and every decay clock, contributes no value |
| `Core\Normalized\KeyedScaleLearner.cs` | `ObserveNonQualifyingAtPhysicalLimit`; the cross-channel fallback (§7); the Tier-1 secondary guard (8.2); `ExportAll` now labels `isPrimaryTier` off `PositiveSampleCount` |
| `Core\Normalized\NormalizedWheelLockSlipEngine.cs` | owns the gate, wires Slip to Lock, `SlipNonCrossingTeachingWeight`, `SlipCrossingValidFrames`/`TotalFrames` |

`totalFrames` is recorded and **not consumed by anything**, as agreed.

### 8.2 THE CROSSING GATE ALONE DOES NOT FIX THE STANDING START

The most important thing learned by building it. A test drove a synthetic launch — full wheelspin on
the source, G climbing to the car's peak and holding — and the published Slip ceiling still reached
**100.0 with the gate fully active**. Switching the gate off changed nothing: **the primary at-limit
path was not the mechanism at all.**

Slip's Tier-1 cold ceiling is drawn from the **general** distribution — every engaged frame,
unconditionally, with no gate anywhere near it. Ten seconds of full wheelspin makes 100 genuinely the
commonest reading, so that percentile *is* 100. The statistic is correct; adopting it as SMax is what
was wrong.

Fixed in `Tier1ColdCeiling`: **for Slip only**, the hand-over from the shipped reference to the
general percentile is now scaled by `PhysicalAnchorReadinessWeight` — the channel must have seen its
own traction limit before its own population percentile may be adopted. Lock is untouched (it scales
by 1.0). This is consistent with the position `LearnedCeilingForKey` already records under "THE FIRST
FIX (SUPERSEDED)", where the general percentile was rejected *as* a definition of SMax.

**Anyone re-deriving §2 from scratch would rebuild the gate, ship it, and still have the bug.**

### 8.3 A defect the design's own wording would have shipped

§7 said Lock is queried "with the SAME (game, car, source) key Slip is being resolved for… in practice
both channels run off the same configured source". **That is false.** Each channel's identity is
`SourceIdentity.Compute` over *that channel's own* four wheel properties, so the default configuration
alone yields `Plain:WheelLock.Raw.FrontLeft~…` for Lock and `Plain:WheelSlip.Raw.FrontLeft~…` for
Slip. Looking Lock up under Slip's identity finds nothing, and the entire fallback would have been a
permanent, silent no-op in the shipped plugin — while passing every test that lazily passed one
identity to both channels. Fixed with `SetCrossChannelLockSourceIdentity`, set per frame by the engine,
and pinned by `Lock_is_looked_up_under_locks_own_identity_not_slips`.

The same mistake invalidated the first replay run: a bare `"Raw"` identity classifies as **Unknown**,
so `KnownSourceColdStartReference` never applied and every cold session fell back to
`CanonicalAtLimitAnchor` (80) instead of the shipped 64/66. Every number in 8.4 uses realistic
identity strings.

### 8.4 The hold window defect, and the three rules that fixed it

The first implementation shipped §2 literally: a crossing opens a 1 s window, and every candidate frame
inside it teaches **its own live reading**. On `c_1_8_1` the ceiling still read 100. Instrumenting every
frame that actually teaches SMax showed why:

```
FIRST IMPLEMENTATION   taught 324 frames: 13 crossing instants, 311 hold frames
                       frames at basis >= 80: 58   crossings among them: 0   <-- NONE
                       crossings themselves: p50 27.8  max 64.5
                       taught pool P90: 100.0
```

Not one frame at >= 80 was a crossing. A crossing fired at basis ~28, then the tyre let go completely
and the source ran to 100 *inside the same second* - and every runaway frame taught 100. **The hold
window was doing 96% of the teaching and its tail set the ceiling.**

Three rules were needed, not one. Each was insufficient alone, and each failure was measured:

| rule | what it fixes | measured result on c_1_8_1 |
| --- | --- | --- |
| **1. Teach the onset snapshot** - a hold frame teaches the reading captured AT the crossing, not its own | the runaway's own values | still 100 - the runaway *re-triggered* the detector (basis 36 to 100 while G falls is another "crossing") and overwrote the snapshot |
| **2. One crossing per event** - the detector cannot re-arm while a window is live | the overwrite | still 100 - crossings were firing on frames that were **not at the limit** (a spin, a break-away tail), arming a window that reached forward into the next genuinely-at-limit frames. 289 of 291 taught frames had a snapshot armed by a non-candidate crossing |
| **3. Arm only at the limit** - a crossing may only fire on a frame that is itself `physicallyAtLimit` | the non-candidate arming | **fixed** |

With all three:

```
AFTER   taught 209 frames: 6 crossing instants, 203 hold frames
        frames at basis >= 80: 0
        crossing instants: p50 39.5  p90 64.5     hold frames: p50 39.5  p90 64.5   <-- identical
        taught pool P90: 64.5
```

The hold frames now carry exactly the crossing distribution, which is the proof that the snapshot is
what gets taught rather than the live source.

Rule 3 is the one worth remembering: the snapshot is *defined* as "the source's reading at the traction
limit", so it has to be sampled while the car is actually there. Slip rising while G falls is also the
signature of a spin, a kerb strike, and wheels leaving the ground.

### 8.5 Measured, before vs after, over the whole corpus

Both arms are the real pipeline over all 17 captures / 25 sessions; "before" is the pre-change tree
replayed identically, with realistic source-identity strings (see 8.3).

**The reported defect is fixed.**

```
c_1_8_1 s1   Slip SMax 100.0 -> 64.5          sessions with SMax >= 95:  before 1  ->  after 0
```

**Cold start got FASTER, not slower** - measured on the 18 sessions where both arms reach full
confidence (elsewhere a "0 s settle" only means the ceiling never moved, which flatters the before arm):

| | before | after |
| --- | --- | --- |
| median time to settle within 10% of the session-end value | 77.2 s | **33.5 s** |
| mean | 67.6 s | 37.9 s |
| per-session change | - | **median -21.6 s** (11 faster, 3 slower, 4 unchanged) |
| median Slip SMax on that subset | 52.5 | 54.8 |

Teaching one stable value per event converges far quicker than a pool scattered between the onset and
the break-away, which is why this went the good way rather than the feared one.

**LOCK IS UNAFFECTED - verified numerically, not by inspection.** Lock's settled ceiling is identical
in both arms in **all 25 sessions**. `Lock_learns_exactly_the_same_ceiling_whatever_the_slip_gate_is_doing`
additionally pins it to 10 decimal places against a busy Slip source and against
`SlipNonCrossingTeachingWeight = 1.0`. This matters because Lock shares `ComputeChannel` with Slip and
its teaching basis flows through the same locals the gate writes.

**THE COST, and it is larger than a first pass suggested.** Stricter arming means far fewer qualifying
frames, so many more sessions end with no confident evidence of their own and lean entirely on §7's
fallback:

| | before | after |
| --- | --- | --- |
| sessions ending below confidence 0.10 | **3** of 25 | **7** of 25 |

```
I_1_6_8 s2        0/791 qualified      1.5 RawDry      58/1136
1.5 ShakeItDry  141/1233               c_1_5_3 ShkIt s2  80/1488
c_1_8_2 s1      120/843                c_1_8_2 s2         0/184
c_1_8_2 s3      142/2858   <- was confidence 1.00, now 0.07
```

**Every session of the owner's current title (c_1_8_2) is now starved**, where before two were partial
(0.17, 0.13) and one was fully confident (1.00). On that car Slip's ceiling is therefore carried by the
shipped reference plus Lock rather than learned from the car itself. The ceilings land in a plausible
band (53.6 / 63.4 / 36.9 against the looser gate's 59.1 / 60.5 / 34.3), so the fallback is doing its
job - but "the gate is correct and the fallback is carrying it" is a materially different operating
mode from "the channel learned its own ceiling", and it is now the common case rather than the
exception. This is the §6 tension, and it got worse, not better.

**A consequence worth knowing:** in 5 of 25 sessions the published Slip SMax now exceeds the highest
value that session's own source ever read. Four are starved sessions where SMax legitimately comes from
the fallback rather than the source at all (so the channel reads attenuated). The fifth,
`c_1_5_3_e_d/20260816-212439_Raw s2`, is **fully confident** at SMax 94.1 against a source maximum of
84.2, which the fallback cannot explain - carried-over state from session 1 is the likely route.
**Unexplained; not investigated.**

**The `LockToSlipRatio = 1.6` constant was re-checked against the fixed gate and holds.** The corpus
Lock/Slip ratio is now median 1.30 over all sessions and 1.51 over confident ones (was 1.52 in §6).
Re-running §7.3's cold-session test on the new truth values: k=1.4 is best on median (27%, level with
the shipped constant alone), k=1.6-1.8 best on mean (42-39% against 45%). So 1.6 stays - but note the
fallback's advantage is now marginal (better in 8 of 14 cold sessions, median slightly worse, mean
slightly better) where §7.3 measured it clearly positive. §7.3's numbers were computed against the
pre-fix teaching pool and should be read as superseded.

### 8.6 Existing tests that had to change

Five tests in three files (`SlipRawFloorTests`, `NormalizedWheelLockSlipEngineTests`,
`DeltaGCollapseBandMappingTests`) warmed Slip's ceiling by holding a **constant source at constant G**
for 300-400 frames. That is now, correctly, the standing-start signature and teaches nothing. Every
assertion is unchanged; only the setup moved to a crossing pattern. Suite: **1250 passing, 0 failing.**

### 8.7 Robustness review (2026-09-03)

Audited on the assumption that every value reaching the gate is game telemetry and can be absent,
null-coalesced, NaN, infinite, negative, or out of range. Five real defects found and fixed; three
suspicions cleared by reading the code.

**Fixed:**

1. **A zero frame time froze the hold window open forever.** The engine passes `dtSeconds = 0.0`
   whenever a title reports no frame time (its own `Dt.HasValue && TotalSeconds > 0.0` ternary), and a
   window aged by nothing never expires - one crossing would have taught the same snapshot for the rest
   of the session, the exact failure this feature exists to prevent. An unusable dt now ages by
   `NominalFrameSeconds`, plus a `HoldMaxFrames = 500` backstop for a tiny-but-positive dt.
2. **An infinite basis could arm a crossing.** `Inf - finite > SlipRise` is true, so a window would
   open with an infinite snapshot, which `ObserveAtPhysicalLimit` then discards - teaching nothing for
   a full second while `RecordCandidate` counted the frames as qualified. Non-finite or negative
   readings are now skipped without entering the trend history.
3. **The teaching threshold tested the wrong value.** `MinRawForCalibrationObservation` was checked
   against the LIVE basis while SMax now teaches the crossing SNAPSHOT, so a sub-threshold snapshot
   could calibrate the ceiling. Both teaching calls now test the value actually being taught;
   `ObserveGeneral` still tests the live one, which is correct for it.
4. **An unusable snapshot failed silently.** `TeachingBasis` now falls back to the live reading when the
   snapshot is not something the learner would accept, rather than handing over a value that is
   discarded.
5. **`ApplyCrossChannelFallback` let a NaN through** - `NaN <= 0.0` is false, so the bare positivity
   test would have published a NaN ceiling into `Rescale`. Not reachable today; the guard is free.

**Cleared:**

- `_weightScale` growth from zero-weight fold-ins: `RenormaliseHistogram` resets it unconditionally, so
  the new path cannot run it away. Verified with 600,000 non-qualifying observations followed by real
  evidence - the ceiling still lands on the taught value.
- NaN weights and confidences: `ClampMath.Clamp` maps NaN to its minimum, so `To01(NaN) == 0.0`
  throughout. A NaN would otherwise have made both `weight > 0` and `weight <= 0` false and silently
  disabled the channel.
- Out-of-range source values: the upstream layers clamp to 0-100 before `ComputeChannel`, so a source
  of 1e9 leaves the at-limit distribution empty and the ceiling at the anchor. This is what makes a
  range guard inside the gate unnecessary, so it is pinned by a test rather than assumed.

One existing test had to be narrowed: `Non_finite_frame_times_do_not_expire_a_live_crossing` asserted
that 500 NaN frame times left the window open, which was defect 1 stated as a requirement. It now
checks the property that genuinely holds - one bad frame must not discard a live window.

Coverage: `SlipCrossingRobustnessTests` (11 cases through the real engine, including null game/car/
source identifiers) plus 10 boundary cases in `SlipCrossingGateTests`. Suite: **1278 passing**.

---

## 9. The WheelLock twin — implemented, shipped OFF (2026-09-05)

The owner observed the same SMax = 100 failure on **WheelLock in EA WRC**. Lock now has the same rule,
through `Core\Normalized\LockCrossingGate.cs`.

**A separate class, not a shared or parameterised one** — the owner's explicit requirement: "similar
mechanism logic but different implementation so we can disable one side only just in case". Separate
gate object, separate switch (`LockCrossingGateEnabled`), separate weight knob
(`LockNonCrossingTeachingWeightFactor`), separate branch in `ComputeChannel`. The duplication is the
feature: it makes "turn Lock off, keep Slip" a switch on an independent object rather than a
conditional threaded through shared code.

**One substantive difference.** Lock's teaching weight is already the corner-local detector's
*continuous* confidence, so its gate **multiplies** that confidence rather than replacing it, and arms
on "the detector has any confidence in this frame" rather than on a G threshold.

### 9.1 Why it ships OFF

Measured across the whole 17-log corpus, both halves, before vs after:

| | before | Tier-1 guard only | guard + crossing gate |
| --- | --- | --- | --- |
| median Lock SMax | 85.4 | **85.4 (identical)** | 66.1 |
| sessions at SMax ≥ 92 | 6 | 6 | 2 |
| sessions at SMax = 100 | 1 | 1 | **0** |
| 1.7.1's four sessions | 72.8–77.4 | 72.8–77.4 | **53.4–77.7** |
| 1.7.1 cross-session spread | 1.06× | 1.06× | **1.46×** |

Two findings, both decisive:

1. **The Tier-1 secondary guard does nothing for Lock.** All 25 sessions are bit-identical with it on.
   The fix that mattered most for Slip is irrelevant here — Lock's ceiling does not reach 100 through
   the general-percentile path at all.
2. **The crossing gate fixes the one Lock = 100 session** (1.0.6.8 ShakeIt, 100.0 → 51.3) **but undoes
   1.0.6.9's validated result.** That work established 72–78 at a 1.08× spread across two cars and two
   sources as *correct* for the 1.7.1 capture; the gate moves it to 53.4–77.7 at 1.46×. A lower ceiling
   makes `Rescale` multiply harder — the over-shake this project has chased since 1.0.6.

The asymmetry has a cause worth remembering: **Lock already has a purpose-built event detector.** The
corner-local at-limit confidence was built for exactly the question the crossing rule asks, and
layering a second, cruder detector on top replaces its frame selection with an earlier, lower reading.
Slip had no such detector, which is why the identical rule is a clear win there.

Yield is not the problem: Lock qualifies **442 frames per session at the median, minimum 145, with no
starved sessions** — far healthier than Slip's, precisely because of that arming condition.

### 9.2 What would justify turning it on

An **EA WRC capture**. The corpus contains no loose-surface title, and the one session that reads
Lock = 100 (1.0.6.8 ShakeIt) has only 645 non-zero lock frames with a p90 of 79 and a genuine p99 of
100 — thin evidence rather than obviously the reported mechanism. Turning the gate on today trades a
validated F1/AC result for a fix to a case this corpus cannot reproduce.

With a WRC log: enable `LockCrossingGateEnabled`, replay, and check both that the WRC ceiling comes off
100 **and** that 1.7.1 stays inside 72–78. If both hold, flip the default. If only the first holds, the
gate needs an arming condition that respects the corner-local detector rather than competing with it.

### 9.3 Coverage

`LockCrossingGateTests` — 12 cases: the detector, the onset snapshot, one-crossing-per-event, the
weight-multiplier semantics, the robustness guards, and four INDEPENDENCE tests proving each channel's
switch cannot reach the other and that each kill switch genuinely restores pre-gate behaviour.
Suite: **1302 passing**.

### 9.4 Enabled by the owner — 2026-09-05

`LockCrossingGateEnabled` now defaults **true**, on the owner's decision after §9.1's trade-off was put
to them, on the strength of the EA WRC field report this corpus cannot reproduce. §9.1's measurements
stand unchanged and are the first thing to re-read if Lock starts over-shaking on a sealed-surface
title: setting the flag false restores 1.0.6.9's validated behaviour exactly and reaches nothing on the
Slip channel.

**Eleven existing tests had to change**, in five files, all for the same reason and none of them
altering what the test asserts. Each warmed Lock's ceiling by holding a **constant** lock source under
a constant (or merely varying) G for 300 frames — which the crossing gate correctly refuses to learn
from, since an unchanging source is not a wheel going past its friction peak. They now share
`TestFrames.WarmLockWithCrossings`, which ramps the source into a crossing while G falls.

One detail in that helper is load-bearing and easy to get wrong: the ramp is arranged to **land exactly
on the target value at the detection frame**, and the recovery frames hold the source **at** that value
rather than at the bottom of the ramp. Both are needed for the learned ceiling to come out as exactly
the number the old constant warm-up produced — otherwise every test with an exact derived expectation
(Rescale outputs, borrowed Tier-3 references, the front-bias mutation gap) shifts by a few points. A
first attempt that held recovery at the ramp's bottom taught 88 instead of 90.
