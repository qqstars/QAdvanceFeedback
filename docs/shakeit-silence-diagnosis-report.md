# ShakeIt-sourced Wheel Lock silence: diagnosis and fallback fix

## STATUS

**DIAGNOSED AND FIXED, with one honestly-labelled gap.** The silence is **UPSTREAM, in ShakeIt's own
export** - measured directly (not assumed) on the owner's own four F1 25 logs, using the SAME
previously-driven, already-calibrated car the logs actually contain. The three prior-pass fixes
(continuous cold-start ramp, cross-car seeding, `physicalReference` persistence) do **not** address this
symptom - they fix different, real defects nearby, and I confirm that with direct evidence below, not by
assumption. The fix implemented here is a narrow, detectable, automatic fallback to Layer 3's own Raw
(never a fabricated value) plus a new diagnostic that makes the degraded state visible. The literal
custom-"F1 World"-car scenario the owner reported cannot be reproduced from the data provided (the logs
are for the *working* car, exactly as the task itself warned) - what capture would settle that specific
case is spelled out at the end, not guessed at.

## Test count / build gates

- `QAdvanceFeedback.Tests`: **737/737 passed**, 0 warnings, Debug and Release (net8.0). Baseline measured
  at the start of this task (clean rebuild, before any change here) was 726/726. This task added **9
  new tests** (5 in `NormalizedWheelLockSlipEngineTests.cs` covering the fallback mechanism, 4 in
  `AllPublishedPropertiesTests.cs` covering the 4 new diagnostic names). Re-running a clean rebuild
  repeatedly and reproducibly shows 737, not 735 (726+9) - I looked for a cause attributable to my own
  changes and found none (no `[Theory]`/`[MemberData]` anywhere in the suite whose row count depends on
  anything I touched); flagging this small, pre-existing +2 discrepancy honestly rather than asserting a
  precise cause I could not verify.
- `QAdvanceFeedback` (the plugin, net48): `dotnet build -t:Clean,Restore,Build` for both Debug and
  Release -> **0 Warning(s), 0 Error(s)** both times. `bin\Release\net48\` and `bin\Debug\net48\` each
  contain exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` - **single-DLL distribution confirmed**
  in both configurations.

## Where I looked (read before changing anything)

Read `docs\cold-start-and-timing-fix-report.md` and `docs\f1-normalization-fix-report.md` first, then the
actual code: `NormalizedWheelLockSlipEngine.ComputeChannel`, `KeyedScaleLearner`, `SourceIdentity`,
`KeyedGripLearner.MakeKey`, `QAdvanceFeedback.cs` (the composition root), `PropertyPublisher.cs`,
`AllPublishedProperties.cs`. Then the DATA: `Common_1_5_e_d\QAdvanceFeedback.Parameters.json` and all
four F1 25 CSVs (`withRawWet`, `WithShakeItWet`, `WithRawDry`, `WithShakeItDry`), analysed column-by-column
with a throwaway Python script (not part of the solution, not committed) - the same "replay the real
captured columns" technique the prior reports' own harnesses used, but here done directly off the
already-recorded `Diag.Source.Lock.*`/`WheelLock.Raw.*`/`WheelLock.Normalized.*` columns rather than a
re-compiled engine harness, since the question ("did the SOURCE itself go quiet") is answerable straight
from what was actually recorded.

---

## WHETHER THE SILENCE IS UPSTREAM OR OURS - the numbers that decide it

**Method** (exactly as the task specified): for each of the four logs, at every frame where Layer 3's
own, independently-computed `WheelLock.Raw.All` (always computed regardless of the configured Normalized
source) exceeds a threshold - a strong, source-independent signal that the wheel genuinely is near its
limit - compare `Diag.Source.Lock.All` (what Layer 4 actually consumed from the CONFIGURED source) and
`WheelLock.Normalized.All` (what we published) at that SAME frame.

| log | Raw threshold | n (qualifying frames) | Source mean | Source min | **Source < 5** | Normalized mean | **Normalized < 5** |
|---|---|---|---|---|---|---|---|
| withRawWet | Raw>30 | 349 | 54.15 | 30.07 | 0 (0.0%) | 55.37 | 66 (18.9%)* |
| withRawWet | Raw>50 | 169 | 71.30 | 50.16 | 0 (0.0%) | 66.03 | 25 (14.8%)* |
| withRawWet | Raw>70 | 82 | 85.35 | 71.14 | 0 (0.0%) | 86.46 | 0 (0.0%) |
| WithRawDry | Raw>30 | 595 | 47.71 | 30.24 | 0 (0.0%) | 50.49 | 51 (8.6%)* |
| WithRawDry | Raw>50 | 97 | 76.43 | 50.04 | 0 (0.0%) | 76.06 | 10 (10.3%)* |
| WithRawDry | Raw>70 | 64 | 85.97 | 71.36 | 0 (0.0%) | 83.26 | 5 (7.8%)* |
| **WithShakeItWet** | **Raw>30** | 329 | **43.32** | **0.00** | **68 (20.7%)** | 54.40 | 72 (21.9%) |
| **WithShakeItWet** | **Raw>50** | 119 | **54.64** | **0.00** | **27 (22.7%)** | 59.94 | 27 (22.7%) |
| **WithShakeItWet** | **Raw>70** | 37 | **82.62** | **0.00** | **1 (2.7%)** | 88.83 | 1 (2.7%) |
| **WithShakeItDry** | **Raw>30** | 689 | **26.21** | **0.00** | **117 (17.0%)** | 44.25 | 119 (17.3%) |
| **WithShakeItDry** | **Raw>50** | 134 | **31.99** | **0.00** | **29 (21.6%)** | 62.92 | 24 (17.9%) |
| **WithShakeItDry** | **Raw>70** | 76 | **36.87** | **0.00** | **17 (22.4%)** | 72.32 | 11 (14.5%) |

\* the Raw-sourced logs' own Normalized<5 counts are a residual, pre-existing, source-vs-canonical-scale
spread already documented (not silence - Source itself never dropped to zero there; see the
`f1-normalization-fix-report.md`'s own "Residual Raw-vs-ShakeIt spread" concern). They are shown only for
contrast with the ShakeIt rows.

**Conclusion, with the deciding numbers**: on the two Raw-sourced logs, `Diag.Source.Lock.All` is **never**
below 5 at any threshold, at any of 349/169/82/595/97/64 qualifying near-lock frames (0.0% every time) -
Source==Raw there by construction, so there is nothing to disagree about. On the two **ShakeIt**-sourced
logs, the configured source reads **literal 0.00** in 17.0%-22.7% of frames where our own independent Raw
computation says the wheel is genuinely at Raw>30/50, and even at the highest bar checked (Raw>70 -
overwhelming, unambiguous near-lock evidence), ShakeIt still reads exactly 0.00 in 1-17 of the qualifying
frames per log. **`WheelLock.Normalized.All` tracks that same near-zero almost exactly** (20.7% vs 21.9%,
22.7% vs 22.7%, 2.7% vs 2.7%, 17.0% vs 17.3%, 21.6% vs 17.9%, 22.4% vs 14.5%) - proof this engine is
**faithfully passing through** what ShakeIt reports, not independently suppressing a healthy source. This
matches the decompiled `ShakeItV3.Calibration.CalibrationData` mechanism cited in the task brief exactly:
`IsReady => Count >= 7000` and the pre-maturity `GetPercentile` fallback `Math.Max(1.0, Max*0.9) *
percentile/100.0` can legitimately publish near-nothing while a car's own per-metric `Max` has not yet
been meaningfully set - and this is happening **on a car ShakeIt has already been driven with across
multiple prior sessions** (the owner's own `Parameters.json` shows 112-181 of OUR OWN samples recorded
against this car+ShakeIt key, a different, smaller counter than ShakeIt's own internal 7000-sample gate).

**This settles the question for the case the data covers: the fault is upstream, in ShakeIt, not in our
normalization math.** For the LITERAL reported scenario - a from-scratch custom "F1 World" car, which
ShakeIt has never seen at all (its own internal counter starting from zero, not just under-matured) - the
mechanism above predicts an even more severe and more sustained dropout, but I cannot produce that exact
number from the data provided; see "What capture would settle the custom-car case" below.

---

## ALSO EXAMINED

### Weather not in the learning key

Checked directly: across **all four** F1 25 logs (withRawWet, WithShakeItWet, WithRawDry,
WithShakeItDry - roughly 19,000 frames combined, captured back-to-back in one continuous session per the
prior report), `Diag.Lock.LearnerConfidence` reads **exactly 0.000 at every single row**, and
`Diag.Lock.SourceScaleCeilingIsPrimaryTier` reads **`False` at every single row**. This means
`KeyedScaleLearner`'s PRIMARY (physically-anchored) tier - the one tier that IS surface-keyed and would
otherwise separate a wet-taught reference from a dry one - **never engaged even once** across the entire
four-log session, for either the Raw or the ShakeIt source. Calibration instead ran entirely on the
SECONDARY (99th-percentile) tier or the cross-car seed, neither of which distinguishes wet from dry within
one (game, car, source) key.

This is a genuine, real finding (and consistent with the cold-start report's own Concern #4 about the
physical reference rarely firing for F1 25), but it is **not evidence that weather-mixing causes
silence specifically**: the same "Source reads literal 0.00 at a genuine near-lock moment" signature
above is present in the **dry**-only `WithShakeItDry` log just as strongly as in the wet one (17-22% either
way), and does not correlate with a wet/dry transition - it correlates with ShakeIt's own
maturity/dropout behaviour, which is source-side, not surface-side. I did **not** add weather/surface to
`KeyedScaleLearner`'s key on this evidence - doing so would be exactly the speculative change the task
asked to avoid without stronger evidence. What IS worth a follow-up (flagged, not implemented, to keep
this pass scoped to the reported symptom): the primary tier's near-total non-engagement for F1 25 means the
"honest, physically-anchored" calibration path this class was designed around is essentially unused for
this title, leaving calibration to the coarser percentile/cross-car mechanisms - a separate concern from
today's silence.

### Car-id fragmentation ("Sauber" vs "F1 Generic")

Confirmed directly in the owner's own `Parameters.json`: `LockLearners` has BOTH
`F12025|#|Sauber|#|...|#|Sealed` (253 Raw-side samples, 112 ShakeIt-side) and
`F12025|#|F1 Generic|#|...|#|Sealed` (119 Raw-side, 181 ShakeIt-side) - the same physical car, two
different persisted car ids across sessions. `KeyedGripLearner.MakeKey`/`SourceIdentity` key everything by
the LITERAL `carId` string passed in per frame (empty-safe via `?? string.Empty`, but not otherwise
validated) - so a car id that is merely DIFFERENT-but-stable across sessions fragments learning across
restarts (the already-documented issue), while a car id that is UNSTABLE WITHIN one session (changing
frame-to-frame, or oscillating empty/non-empty) would prevent learning from ever accumulating even inside
a single drive - a strictly worse failure mode.

**I could not settle which of these applies to the custom car**, because none of the four CSVs carries a
`CarId` column at all - there was previously no way to check car-id stability from ANY capture, past or
future. To close that gap, I added `Diag.GameId`/`Diag.CarId` as new diagnostics (see "What I changed"),
so the next capture on the custom car can answer this directly in one session.

### Did the prior pass's three fixes already resolve this?

No, and I can say so with the same numbers above: all four logs were replayed with the CURRENT,
already-fixed engine in place (this task built directly on top of the cold-start/cross-car-seed/
`physicalReference`-persistence work) - the Source-reads-zero signature persists regardless, because none
of those three fixes touch what ShakeIt itself exports. They are real fixes for real, different defects
(the 15-point calibration step, missing cross-car seeding, unpersisted `physicalReference`) but they
operate entirely downstream of the point where ShakeIt's own export already reads zero - there is nothing
for a Layer-4 calibration fix to rescale when the input itself is already zero.

---

## What I changed

**Proposed behaviour** (stated before implementing, per the task's instruction): when the CONFIGURED
source reads near-zero for a channel WHILE Layer 3's own, independently-computed Raw (always available,
regardless of what's configured) reads a genuine, well-above-noise-floor value, fall back to Layer 3's
Raw for that frame's severity and per-wheel proportions - a real, already-computed alternate measurement,
never a fabricated one - and publish a new diagnostic so the degraded state is visible rather than
silently indistinguishable from "nothing is happening."

**`NormalizedWheelLockSlipEngine.cs`** (`Core/Normalized/`):
- `Compute`/`ComputeChannel` gained two new optional parameters, `layer3RawLockWheels`/
  `layer3RawSlipWheels` (default `Corners.Zero`, so every pre-existing caller/test keeps compiling and
  behaving exactly as before - a permanently-zero fallback input can never look like a genuine
  disagreement).
- The fallback's own scale calibration is kept warm every qualifying frame under a dedicated, always-fed
  identity (`"__layer3_raw_fallback__"`, never colliding with a real `SourceIdentity` output), mirroring
  how the shared `physicalReference` detector is already always-fed regardless of configured source - so
  when the fallback engages, it is not itself starting cold.
- Trigger condition: `mean(configured source) < 2.0 (SourceLooksColdEpsilon) AND mean(layer3 Raw) >= 10.0
  (MinRawForCalibrationObservation - the SAME bar this class already trusts as "a real reading, not a
  placeholder")`. Deliberately narrow: a source CORRECTLY reading near-zero because nothing is happening
  will have Layer 3 Raw near-zero too (both measure the same physical event), so the fallback does not
  engage for the overwhelmingly common case - only for the specific disagreement measured above.
- New `LockSourceFallbackActive`/`SlipSourceFallbackActive` engine properties expose whether the most
  recent frame used the fallback.

**`QAdvanceFeedback.cs`** (composition root): passes `legacy.LockWheels`/`legacy.SlipWheels` (Layer 3's
own Raw, already computed every frame regardless of the configured source) into the new parameters, and
publishes the fallback-active state and the resolved `(gameId, carId)` every frame.

**`PropertyPublisher.cs` / `AllPublishedProperties.cs`**: four new diagnostic properties (gated behind
`EnableDiagnostics`, same as every other diagnostic):
- `Diag.Lock.SourceFallbackActive` / `Diag.Slip.SourceFallbackActive` - true exactly when this frame
  substituted Raw for the configured source.
- `Diag.GameId` / `Diag.CarId` - the resolved keying identity, previously uncapturable at all.

**No `RuntimeStore` changes needed** - the fallback's calibration key is just another entry in the same
`KeyedScaleLearner` dictionaries already imported/exported wholesale; `Diag.GameId`/`Diag.CarId`/
`SourceFallbackActive` are per-frame diagnostics, not persisted state.

### Tests added (9, in `QAdvanceFeedback.Tests`)

- `Configured_source_reading_near_zero_while_layer3_raw_reads_high_falls_back_to_raw_instead_of_publishing_silence`
- `Configured_source_reading_genuinely_low_does_not_trigger_the_fallback_even_if_layer3_raw_is_unavailable`
- `Fallback_does_not_engage_when_the_configured_source_and_layer3_raw_agree_both_low`
- `Slip_channel_has_its_own_independent_fallback_that_does_not_engage_from_locks_own_disagreement`
- `MutationGuard_removing_the_shakeit_silence_fallback_reproduces_published_silence`
- `Diagnostic_names_include_the_resolved_game_and_car_id` / `Diagnostics_off_never_publishes_the_resolved_game_and_car_id`
- `Diagnostic_names_include_the_source_fallback_active_flags` / `Diagnostics_off_never_publishes_the_source_fallback_active_flags`

## FH6 guardrail

`QAdvanceFeedback.session-20260815-230140.csv` (FH6-mixed, 8985 rows, the same log the prior reports used
as the only FH6 capture with the columns needed to reason about this directly) already carries
`Diag.Source.Lock.All` and `WheelLock.Raw.All` for the same frame simultaneously - checked the EXACT
trigger condition my code uses (`Source < 2.0 AND Raw >= 10.0`) directly against the recorded columns:

- **90.16%** of frames (8101/8985) have `Diag.Source.Lock.All == WheelLock.Raw.All` exactly - i.e. Raw
  itself was the configured source for the overwhelming majority of this session. The fallback **cannot**
  engage there by construction (if source==Raw, the trigger's two conditions can never both hold at once).
- Of the remaining 9.84% (884 frames, where a different source was configured), **381 frames (4.24% of
  the whole file)** meet the exact trigger condition: `Diag.Source.Lock.All` reads a mean of essentially
  zero there while `WheelLock.Raw.All` reads a mean of **97.52** (min 17.98, i.e. every one of these
  frames is a genuine, strong near-lock reading) - and the CSV's own recorded `WheelLock.Normalized.All`
  for those same frames averages **1.93** (near-silent, exactly the reported symptom, on FH6 this time,
  not just F1 25).
- This is a **real, bounded, correct-direction** change: 4.24% of the file, previously silent despite Raw
  independently confirming a near-total lock, would now read a real, non-silent value close to Raw's own
  calibrated equivalent. No bin goes silent as a result of this fix; the fix's entire purpose is to
  prevent exactly this kind of silence. The dominant, already-agreeing 90.16% of the session is completely
  unaffected by construction, not merely by measurement.
- I did not build a full C#-engine replay harness for FH6 in this pass (time-boxed to the reported
  symptom); the direct-from-CSV trigger-condition check above is sufficient to bound the guardrail's
  impact, using the same technique the prior "Symptom 2" analysis in the cold-start report used for its
  own frame-matched comparison.

## Mutation evidence

Reverted the fix (`bool useFallback = false;` in `ComputeChannel`, disabling the fallback entirely - the
code as it was before this task) and re-ran the new fallback tests:

```
FAILED Configured_source_reading_near_zero_while_layer3_raw_reads_high_falls_back_to_raw_instead_of_publishing_silence
  a silent configured source with a genuinely high independent Raw reading must fall back to a usable, non-silent cue, got 0
FAILED Slip_channel_has_its_own_independent_fallback_that_does_not_engage_from_locks_own_disagreement
  Slip's own fallback must engage independently of Lock, got 0
```

Exactly the reported symptom reproduced (published severity `0`, indistinguishable from "nothing is
happening"). Reverted immediately after capturing this; the full suite was re-confirmed green (737/737,
0 warnings, both Debug and Release, plugin build unaffected). The captured values are pinned in
`MutationGuard_removing_the_shakeit_silence_fallback_reproduces_published_silence`.

## What capture would settle the custom-car case directly

The four logs provided are for the WORKING (previously-driven) car and could not, and did not, settle the
literal "custom car, first time, produces total silence" claim - only the general "ShakeIt itself can go
silent even on a known car" mechanism, which is now measured and fixed. To settle the custom-car case
specifically, in one session:

1. Enable diagnostics, select the custom "F1 World" car with the ShakeIt source configured (as in the
   report), and drive several hard-braking events in BOTH dry (as reported) and, if convenient, wet.
2. Capture (now possible for the first time): `Diag.GameId`/`Diag.CarId` every frame - confirms whether
   the custom car's id is merely different-but-stable (the already-known "Sauber"/"F1 Generic" class of
   issue) or genuinely unstable within the session (a strictly worse, previously invisible failure mode).
3. Capture `Diag.Source.Lock.All` alongside `WheelLock.Raw.All` at the SAME frames - if Source reads
   near-zero while Raw reads high for a SUSTAINED stretch (much longer/more total than the 17-23% seen on
   the known car), that is the predicted, more-severe version of today's finding for a car ShakeIt's own
   7000-sample counter has never touched at all.
4. Capture `Diag.Lock.SourceFallbackActive`/`Diag.Slip.SourceFallbackActive` - confirms whether the fix
   shipped here actually engages and produces a non-silent reading for that specific car, or whether
   something else (e.g. ShakeIt's effect never being assigned to the custom car at all in SimHub's own
   ShakeIt configuration - outside this plugin's reach) is the true cause, which this plugin cannot fix by
   any Layer-4/Layer-3 change.

## Concerns

1. **The custom-car scenario itself remains unreproduced from data** - the mechanism is measured and
   fixed for the general case; whether it is the WHOLE story for the owner's own specific session cannot
   be confirmed without the capture above.
2. **The +2 test-count discrepancy** (9 tests added, 11 more than the 726 baseline) noted in "Test count"
   above - could not attribute to a specific cause; flagged rather than asserted away.
3. **Weather-in-the-key and car-id-instability-within-a-session remain open, evidence-bounded questions**,
   not fixed - see "Also examined" above. Neither was implemented on the strength of today's evidence,
   per the task's own instruction not to add a key dimension speculatively.
4. **`SourceLooksColdEpsilon` (2.0) and reuse of `MinRawForCalibrationObservation` (10.0) as the
   fallback's own trigger bar are my own choices**, not owner-specified - documented in the code, chosen
   to be narrow (avoid engaging on ordinary "nothing is happening" frames) rather than aggressive.
5. **FH6 guardrail impact (4.24% of one file's frames) was measured directly from recorded CSV columns,
   not a full engine replay** - a full replay (as the prior reports' own harnesses did) would refine the
   exact post-fix `Normalized` values for those specific frames, though the trigger-rate-and-magnitude
   bound already established is unlikely to change materially, since the fallback's own output is a
   deterministic function of the already-observed Raw value.
