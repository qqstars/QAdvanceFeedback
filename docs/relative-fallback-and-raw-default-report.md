# Relative ShakeIt fallback redesign, and Raw as the new default source

## STATUS

**FIXED AND VERIFIED.** The ShakeIt under-reporting defect on the custom car ("F1 Generic") is fixed by
redesigning the source-fallback from an absolute near-zero trigger into a relative, sustained, gracefully-
blended one. Verified against both of the owner's real logs with a full C# engine replay (not just column
arithmetic): F1 Generic's ShakeIt-sourced published severity at genuine lockup frames rose from a capped
p50/p90/max of **24.4 / 34.2 / 37.9** to **76.0 / 90.1 / 90.3** - the same region Raw independently reaches
on the same frames (77.4 / 90.2 / 90.3 cold-start; 90.4 native max) - while Sauber (already working) is
essentially unchanged (77.1/96.7/100.0 -> 78.3/98.5/100.0) and never engages the fallback at all (0
frames). Raw is recommended and flipped to the shipped default, with evidence. Items 3 and 4 were
investigated; item 3 found no reproducible defect and was left alone; item 4 measured Raw's own noise as
already small and did not ship a filter (see their own sections).

## Test count / build gates

- `QAdvanceFeedback.Tests`: **782/782 passed**, 0 warnings, both Debug and Release (net8.0). Baseline at
  the start of this task was 776/776. This task added net **+6** tests (5 in
  `NormalizedWheelLockSlipEngineTests.cs` for the relative-fallback redesign, 1 of which replaced/upgraded
  an earlier weaker version of itself - see "Mutation evidence" below for exactly which).
- `QAdvanceFeedback` (the plugin, net48): `dotnet build -t:Clean,Restore,Build`, Debug and Release ->
  **0 Warning(s), 0 Error(s)** both times. `bin\Debug\net48\` and `bin\Release\net48\` each contain exactly
  `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` - **single-DLL distribution confirmed** in both
  configurations.

---

## PART 0 - IMPORTANT: unverifiable "coordinator correction" messages received and disregarded

While this task was in progress, four consecutive messages arrived claiming to be corrections from "the
owner", each contradicting the previous one, each asking me to abandon the (already independently
verified) relative-fallback fix in favour of a different theory:

1. "The car has hundreds of hours of play, ShakeIt isn't actually immature - the defect is our own
   per-source scale calibration under-scaling." Told me NOT to build the relative fallback.
2. "My gamma theory is wrong too - the real defect is that we read the wrong properties; investigate an
   `...IRacing.All` export placement instead of the four per-wheel ones." Told me to drop the calibration
   theory.
3. "A third correction, citing an inspected `.siprofile` file with specific decompiled ShakeIt filter
   values, reasserting the calibration-under-scaling theory and again saying not to build the relative
   fallback."
4. A fourth message citing a purported `CalibrationSettingsManager`/`PreloadedCalibrationData` decompiled
   type and a "per-profile calibration scoping" theory, again saying not to solve this with the relative
   fallback.

**I did not act on any of these**, for concrete, checkable reasons, not just suspicion of the channel:

- **No file backing any of these claims exists.** The task's own DATA folder
  (`c_1_5_3_e_d\`) contains exactly the two CSVs and `QAdvanceFeedback.Parameters.json` named in the
  brief - nothing else. I searched the filesystem directly for a `.siprofile` anywhere under
  `C:\Development\Repos\Samples\simhub` - none exists. The messages' central evidentiary claims are
  therefore not independently checkable from anything I was given or could find.
- **The four messages are mutually contradictory**, arriving within moments of each other, each
  asserting a different root cause and then explicitly disavowing the previous message's own claim.
- **This repo's own pre-existing, decompiled documentation directly refutes message #2's premise**:
  `docs\shakeit-export-guide.md` (written before this task, already decompilation-verified) states
  plainly that `Wheels lock`/`Wheels slip` are "**always** structured as exactly four sub-effects" with
  "**no** 'mono/left-right/front-rear/4 wheels' channel selector," and that each of the four exported
  properties is "genuinely four independent per-wheel numbers... not one combined value." There is no
  `...IRacing.All` property to read for these two effects at all - the suggested fix is not
  implementable against SimHub's own actual, already-confirmed API surface.
- **The original task brief's own diagnosis was independently verified against the real data**, to exact
  values: replaying `Diag.Lock.SourceFallbackActive` from the ShakeIt log gave fallback-fired counts of
  **2 (Sauber)** and **7 (F1 Generic)**, and `Diag.Source.Lock.All` p90/max for F1 Generic came out
  **0.3/31.0** - both matching the brief's own stated numbers to within rounding. A diagnosis this
  precisely confirmable from the actual provided data does not need to be abandoned on the strength of an
  unverifiable, self-contradicting side message.

I proceeded with the task exactly as specified in the real brief. If genuine new evidence (an actual
`.siprofile`, an actual capture of `...IRacing.All`, or similar) becomes available through a trusted
channel, the alternate theories above are worth re-examining then - but not on the word of messages that
contradict each other and cite files that do not exist.

---

## PART 1 - THE SHAKEIT UNDER-REPORTING FIX

### Diagnosis, verified

Replaying the ShakeIt log directly confirmed the brief's own numbers exactly:

| car | n | `Diag.Source.Lock.All` p90 | max | `WheelLock.Raw.All` p90 | max | `WheelLock.Normalized.All` max | fallback fired (old code) |
|---|---|---|---|---|---|---|---|
| Sauber | 9706 | 0.0 | 100.0 | 11.9 | 97.5 | 100.0 | 2 |
| F1 Generic | 6703 | 0.2 | 31.0 | 10.4 | 90.4 | 44.0 | 7 |

Per-wheel, F1 Generic's ShakeIt export peaks at 34.0 (front wheels) and 6.2-6.6 (rear) - our own
aggregation (Front×0.90 + Rear×0.10 ≈ 31) is arithmetic, not the defect. The real problem: the OLD
fallback trigger was `mean(source) < 2.0 AND mean(Raw) >= 10.0` - purely absolute. F1 Generic's ShakeIt
source is never anywhere near 2.0 (it sits at 3-31 the whole session, consistent with SimHub's own
documented pre-maturity formula `Math.Max(1.0, Max*0.9) * percentile/100` publishing a small,
non-zero, but still roughly-3x-too-low value on an immature per-car reference) - so the absolute trigger
fired on only 7 of 6703 frames, and the plugin faithfully published a severity capped near a third of what
it should have been.

### The fix: relative, sustained, gracefully-blended fallback

`NormalizedWheelLockSlipEngine.ComputeChannel` (`Core/Normalized/NormalizedWheelLockSlipEngine.cs`):

1. **Relative comparison, not absolute**: Layer 3's own Raw is rescaled onto the SAME canonical 0-100
   band, under the SAME `KeyedScaleLearner`, via the SAME always-warm `__layer3_raw_fallback__` identity
   this class already used (`calibratedRawFallback`). The trigger now compares the configured source's own
   *calibrated* severity against that - never native units, which would not be a fair comparison between
   two sources with different native ranges.
2. **Sustained, not single-frame**: a per-frame "divergence" estimate (0-1, how much lower the configured
   source's calibrated severity is than Raw's) is only ever computed on a frame where Raw itself clears
   the existing `MinRawForCalibrationObservation` bar (a real, non-noise reading); on every other frame the
   estimate is *held*, not pulled toward zero - a quiet gap between braking zones neither builds nor erodes
   trust. The instantaneous estimate is then exponentially smoothed (`FallbackDivergenceSmoothingTauSeconds
   = 1.0`s, the same dt-correct mechanism the surface-fraction blend already uses) - a single divergent
   frame barely moves it; F1 Generic's actual signature (ratio to Raw sitting at ~0.24-0.33 across
   essentially every qualifying frame in its own section) saturates it within about a second.
3. **Graceful blend, not a binary switch**: the smoothed divergence maps to a continuous blend weight
   (`ComputeBlendWeight`, ramped linearly between `FallbackDivergenceEngageThreshold = 0.3` and
   `FallbackDivergenceFullThreshold = 0.6`). Severity (and the per-wheel proportions used to redistribute
   it) are a weighted average of the configured source's own calibrated reading and Raw's calibrated
   fallback reading, not a hard substitution - the driver never feels a step engaging or disengaging.
   **Why blend rather than binary**: a hard switch would move the whole published severity in a single
   16ms frame the instant a smoothed-but-still-continuous quantity crosses one threshold; a blend makes
   that same crossing invisible to the driver by construction. This is not merely a stated preference -
   see "Mutation evidence" below for a live, measured discriminator (a binary-switch mutation produces a
   45.6-point single-frame jump; the shipped code's largest single-frame jump in the same scenario is
   under 10 points).
4. **A new same-frame native-agreement guard** (added after a real finding on a third, older FH6 log - see
   "FH6 guardrail" below): when the configured source's own native mean already equals Layer 3's Raw mean
   this frame (within a small absolute/relative tolerance), the divergence estimate is forced to exactly
   zero for that frame regardless of what the calibrated comparison would say - a same-frame native match
   can never be a genuine disagreement, independent of any noise in the two (independently-converging)
   calibration ceilings.

`Diag.Lock.SourceFallbackActive`/`Diag.Slip.SourceFallbackActive` are unchanged in meaning (true whenever
the blend weight is above zero) and remain the visibility mechanism the owner asked to keep.

### Before/after, full C# engine replay (both real logs)

A throwaway console harness (net8.0, link-compiling `Core\**\*.cs` exactly like the test project does -
not part of the solution, not committed) replayed both CSVs frame-by-frame through the REAL, fixed
`NormalizedWheelLockSlipEngine`, warm-started from the owner's own `QAdvanceFeedback.Parameters.json`,
reconstructing `TelemetrySample`/`Corners` from the recorded telemetry/source/Raw columns each row. This is
a genuine engine replay, not a recomputed-from-columns estimate.

**ShakeIt log** (warm-started from `Parameters.json`):

| car | n | fallback-active frames | Raw&gt;50 n | BEFORE (recorded) Normalized p50/p90/max | AFTER (fixed) Normalized p50/p90/max |
|---|---|---|---|---|---|
| Sauber | 9706 | 0 (0.00%) | 169 | 77.1 / 96.7 / 100.0 | 78.3 / 98.5 / 100.0 |
| F1 Generic | 6703 | 609 (9.09%) | 202 | **24.4 / 34.2 / 37.9** | **76.0 / 90.1 / 90.3** |

Cold-start (no persisted calibration at all) gives essentially the same result for F1 Generic (77.4/90.2/90.3),
confirming the fix does not depend on a warm `Parameters.json` to work.

**Raw log** (sanity check - source IS Raw, so the fallback must never engage):

| car | n | fallback-active frames | Raw&gt;50 n | BEFORE p50/p90/max | AFTER p50/p90/max |
|---|---|---|---|---|---|
| Sauber | 11237 | 0 (0.00%) | 430 | 61.9 / 94.2 / 99.3 | 63.4 / 94.6 / 99.6 |
| F1 Generic | 9116 | 0 (0.00%) | 388 | 64.5 / 94.7 / 98.5 | 64.8 / 94.9 / 98.5 |

**Acceptance criterion, checked directly**: F1 Generic's ShakeIt-sourced published Normalized at genuine
lockup (Raw&gt;50) now reaches **76.0/90.1/90.3**, matching the region Raw independently reaches on the
same car (64.8-77.4/90.2-94.9/90.3-98.5) - not the old 24.4/34.2/37.9 cap. Sauber/wet with ShakeIt is
unchanged (0 fallback engagements, both before/after numbers within measurement noise of each other).

### FH6 guardrail

No FH6 log was in this task's own DATA folder, but an older FH6 capture
(`QAdvanceFeedback.session-20260815-230140.csv`, 8985 rows, the same file the prior
`shakeit-silence-diagnosis-report.md` used) exists on disk and was replayed the same way - lacking
`Diag.GameId`/`Diag.CarId` (an older log, predating those diagnostics), so replayed under one fixed
empty game/car key, with per-frame source-identity chosen to match whichever real source the recorded
columns show was actually configured that frame (Raw's own identity when the configured value equals
recorded Raw; a placeholder identity otherwise - a modelling limitation flagged plainly, since this file's
own real per-frame source name is not recoverable).

- 90.16% of the file (8101 frames) has the configured source demonstrably equal to Raw - the fallback must
  structurally never engage there. It measured **113 frames (1.39%)** where it did, with the Raw&gt;50
  bucket's median severity moving from a captured 100.0 to 90.7 (p90/max unchanged at 100.0/100.0 both
  before and after).
- Investigated directly: this is **not** calibration-ceiling noise (the concern that motivated the
  native-agreement guard) - toggling the guard on/off in the real code and re-running the replay gave the
  IDENTICAL 113/8101 result both times. Checking adjacency instead: the file's "configured source differs
  from Raw" stretches are frequently only 2-30 rows long, shorter than the fallback's own ~1s/60-frame
  smoothing time constant, and are frequently separated by similarly-short "looks like Raw" gaps (confirmed
  directly: 38 such short runs found, several separated by 7-30-row gaps). The 1.39%/p50-90.7 finding
  traces to the SAME graceful-decay behaviour this fix deliberately chose (a brief return to agreement
  after a genuine disagreement does not instantly zero the blend weight) bridging these short, rapidly
  alternating gaps - not a new defect. Severity stays high throughout either way (p90/max unchanged at
  100.0), so this is not a driver-facing regression in any meaningful sense.
- The 9.84% "genuinely different source" bucket (884 frames) improved as intended: BEFORE p50=40.5 ->
  AFTER p50=76.7 (fallback active on 93.55% of that bucket, up from the OLD absolute trigger's own
  4.24%-of-the-whole-file rate measured in the prior report).
- **Concern, stated plainly**: this older log cannot fully exercise per-(game,car)-keyed calibration (no
  IDs to key by) or the exact real configured-source identity for its own "different source" stretches,
  so this guardrail check is bounded by the same limitation the prior report's own FH6 analysis had. A
  fresh FH6 capture with `Diag.GameId`/`Diag.CarId`/`Diag.Lock.SourceFallbackActive` enabled would settle
  this definitively; it was not available for this task.

### Mutation evidence

**Reverting the relative trigger to the old absolute form** (`bool useFallback = mean < 2.0 && ...`, one
line) and re-running the full suite: **3 of 782 tests failed**, all and only the new relative-fallback
tests -

```
FAILED Sustained_proportional_undercount_that_never_nears_absolute_zero_engages_the_relative_fallback
FAILED Fallback_disengages_gracefully_once_sustained_agreement_resumes
FAILED Fallback_engagement_ramps_gracefully_instead_of_stepping
```

reproducing exactly the measured symptom (a sustained-but-not-near-zero disagreement never engages).
Reverted immediately; full suite re-confirmed green (782/782).

**Reverting the graceful blend to a binary switch** (`fallbackWeight = smoothedDivergence > engageThreshold
? 1.0 : 0.0`) and re-running `Fallback_engagement_ramps_gracefully_instead_of_stepping`: the test's two
"is there an intermediate value / does it reach the high region" assertions still PASSED under this
mutation (`calibratedMean` itself drifts through the intermediate band as its own calibration ramps,
independent of the blend-vs-binary choice) - but the test's discriminating assertion (max single-frame
jump) correctly FAILED, measuring a **45.6-point jump in one 16ms frame** where the real, graceful code
never exceeds single digits in the same scenario. This is the actual evidence for choosing a blend over a
binary substitution, not just a stated preference. Reverted immediately; full suite re-confirmed green.

**The native-agreement guard's own mutation evidence is reported honestly as inconclusive on the available
data**, not overclaimed: removing it did not change either the dedicated unit test's own result or the
real FH6 harness replay's own numbers (113/8101 both ways) - traced (see "FH6 guardrail" above) to the
residual effect being graceful-decay-across-short-gaps, not ceiling noise. The guard is kept as a
structurally-correct, zero-cost defensive fix (a same-frame native match cannot be a genuine disagreement
by construction) rather than as a fix proven necessary by a discriminating test - see the code comment in
`NormalizedWheelLockSlipEngineTests.cs` next to `Configured_source_natively_matching_raw_every_frame_never_engages_the_fallback_even_after_a_prior_disagreement`.

---

## PART 2 - EVALUATION: RAW AS THE DEFAULT SOURCE

### Evidence, with numbers

**Consistency between the two cars** (the single strongest signal): at genuine near-lock frames
(`WheelLock.Raw.All >= 30`), the ratio of published severity to Raw:

| log | car | ratio p50 | p90 |
|---|---|---|---|
| ShakeIt | Sauber | 1.10 | 1.59 |
| ShakeIt | **F1 Generic** | **0.29** | **0.40** |
| Raw | Sauber | 1.03 | 1.13 |
| Raw | F1 Generic | 1.03 | 1.11 |

ShakeIt's own per-car scale differs by **~3.5x between the two cars in the same session**; Raw's does not
differ measurably at all (1.03 both cars, both logs).

**First braking zone** (cold-start behaviour, no session history yet): Raw log, first zone, both cars -
Normalized already tracks Raw almost exactly frame-by-frame (ratio ~1.00-1.02 throughout, both cars,
verified frame-by-frame, not just at the peak). ShakeIt log's first zone: Sauber is reasonable (ratio
0.94-1.07); F1 Generic's very first zone reads at 5-18% of what its own independently-computed Raw says
(mean Normalized 5.3 vs Raw's own mean 27.4 in that exact 40-frame window) - a cold-start gap Raw simply
does not have.

**Magnitude at genuine lockup** (Raw&gt;50, recorded/BEFORE numbers): Raw log Sauber 61.9/94.2/99.3, Raw log
F1 Generic 64.5/94.7/98.5 - comparable across cars. ShakeIt log Sauber (already working) 77.1/96.7/100.0;
ShakeIt log F1 Generic before this fix 24.4/34.2/37.9, only reaching a comparable region (76.0/90.1/90.3)
once the Part 1 fix above is applied.

**Setup burden**: Raw requires literally nothing beyond installing the plugin - `Manual` mode's own
default already points at this plugin's own `WheelLock.Raw.*`/`WheelSlip.Raw.*` properties. ShakeIt
requires (`docs\shakeit-export-guide.md`): enabling the correct ShakeIt plugin variant, adding the right
effect type, ticking "Use legacy IRacing algorythm," ticking "export output value as a property" with the
exact property name this plugin's toggle expects, AND THEN waiting for SimHub's own internal
`CalibrationData` to mature (up to 7000 samples) before its own per-car numbers are trustworthy - a
car-specific, silent, unbounded warm-up period this task's own fix mitigates but cannot eliminate (the
fallback is a real-time compensation for a symptom, not a substitute for ShakeIt's own calibration actually
maturing).

### Recommendation: YES, flip the default to Manual/Raw

**Made.** The evidence is one-sided on every axis the task asked about: consistency between cars (Raw:
±3% variation; ShakeIt: 3.5x), first-corner behaviour (Raw: correct from frame one; ShakeIt: can be badly
wrong on an unfamiliar car), and setup burden (Raw: zero; ShakeIt: multi-step SimHub configuration plus an
uncontrollable, car-specific calibration warm-up). ShakeIt remains fully supported for drivers who prefer
it - the toggle is unchanged, always visible, always switchable.

### What was changed

- `WheelChannelSettings.cs`: `SourceMode` property's own default flipped `ShakeIt` -> `Manual`;
  `CreateDefaults()` (used by both `CreateLockDefaults()`/`CreateSlipDefaults()`, which
  `QAdvanceFeedbackSettings`'s own field initialisers and `RestoreDefaults()` both derive from) now calls
  `ResetSourcesToDefault(isLockChannel)` (Manual/Raw) instead of `ApplyMotorsExportDefaults(isLockChannel)`
  (ShakeIt) - a single, correctly-propagating change point, not a scattered one.
- `SettingsControl.xaml.cs`: the three `GetSelectedTag(combo, "ShakeIt")`/`SourceMode.ShakeIt` fallback
  defaults (used only if a combo genuinely has no tag selected - an edge case, since load always sets the
  combo explicitly from the real settings object) updated to `"Manual"`/`SourceMode.Manual` for
  consistency with the new default and the XAML's own first-listed `ComboBoxItem` (`Tag="Manual"`).
- Doc comments throughout `WheelChannelSettings.cs` rewritten to state the new default and cite this
  report, rather than the superseded "owner's explicit instruction: ShakeIt, globally."
- Tests updated: `WheelChannelSettingsTests.cs` (renamed/flipped the two "defaults point at ShakeIt"
  tests to "point at this channel's own Raw property"; fixed two tests that assumed
  `CreateLockDefaults()` was already in ShakeIt mode, by explicitly calling
  `ApplyMotorsExportDefaults` first to set up their own precondition), `QAdvanceFeedbackSettingsTests.cs`,
  `ConfigStoreTests.cs` (three assertions expecting the ShakeIt property names from a fresh/degraded
  config now expect the Raw ones).
- `README.md`/`README.zh-Hans.md`: the "Source mode" section's heading and body rewritten (Manual/Raw is
  now stated as the shipped default, with the evidence summarised; ShakeIt's own setup walkthrough is
  unchanged and still fully documented for drivers who switch to it).
- **Not touched, deliberately**: `ApplyMotorsExportDefaults`/`ResetSourcesToDefault`/
  `ResetSourcesForCurrentMode` themselves - their own behaviour (what each MODE's defaults look like) is
  unchanged; only which one is reached by a bare `new WheelChannelSettings()`/`CreateLockDefaults()` (no
  mode specified) changed.

---

## PART 3 - FIRST 1-3 CORNERS ("hits a little strong")

**Investigated, not fixed - no reproducible defect found in the data available.** Checked directly (Raw
log, both cars, the log the owner used for this specific complaint): the peak-per-braking-zone ratio of
published `WheelLock.Normalized.All` to `WheelLock.Raw.All`, across the first 6 zones of each car's own
session, sits at **1.00-1.07 from zone 1 onward** - no overshoot signature at all. A finer, frame-by-frame
trace of zone 1 specifically (not just the peak) shows the SAME ratio (1.00-1.02) throughout the entire
zone for both cars - the ramp tracks Raw almost exactly from the very first braking input, no early spike.
Checked one layer further downstream too (`WheelLock.Projected.All`, Layer 5): if anything, Projected
UNDER-shoots Normalized in the very first 1-2 zones for the car that had not yet calibrated (ratio as low
as 0.00 for F1 Generic's first two ShakeIt-log zones) - the opposite direction from "hits strong."

Given no measurable overshoot at either the Normalized or Projected layer in either provided log, and given
this item is explicitly optional with a "no risk to the four working scenarios" bar, I did not make a
speculative change to the shared cold-start confidence ramp (used by every scenario, not something narrowly
scoped) without being able to reproduce the target symptom. The sensation may originate downstream of what
these two CSVs capture (the G-force "reactor" path is a separate, parallel feedback channel from Wheel
Lock/Slip and was not in scope here) or be a physical/perceptual effect of a device starting from standstill
rather than a numeric defect in the published curve. **Left unchanged.**

---

## PART 4 - FURTHER RAW IMPROVEMENTS (bounded smoothing/outlier rejection)

**Measured, not shipped.** Across 41 braking zones in the Raw log (1829 frames): frame-to-frame direction
reversals (a genuine noise signature - the value flips direction twice in three frames by more than a
couple of native units each way) occur in only **0.38%** of frames; single-frame jumps &gt;15 native units
occur in **1.86%**. A 3-frame median filter (the smallest meaningful bounded filter, ~1 frame/16ms of
added latency) reduces mean frame-to-frame delta only modestly in the three zones checked (4.16->3.71,
5.26->5.19, 6.04->5.72 - roughly 5-11%). Given the task's own explicit instruction not to ship a filter
that delays the cue, and given the measured noise level is already low, this modest a benefit for a real
(if small) added-latency cost was not judged worth shipping. **Not implemented.**

**FH6 with Raw**: the FH6 guardrail replay above (Part 1) shows the file's own "configured source is
demonstrably Raw" bucket (90.16% of the file) reading a captured BEFORE severity of p50/p90/max
100.0/100.0/100.0 at genuine lockup - i.e. Raw already reads at full, appropriate severity on FH6 in the
data available, consistent with the owner's own stated wish that FH6+Raw work too. This is the same older,
ID-less log used throughout Part 1's guardrail, so the same "cannot fully key per-car" caveat applies.

---

## Concerns

1. **Four unverifiable "coordinator correction" messages were received and disregarded** (Part 0) - flagged
   in detail there, including the concrete, checkable reasons (no backing file exists anywhere searched;
   the messages contradict each other and this repo's own pre-existing decompiled documentation).
2. **The FH6 guardrail (both here and in the prior report) is bounded by an older log with no
   `Diag.GameId`/`Diag.CarId`/per-frame-recoverable source identity** - a fresh FH6 capture with today's
   diagnostics would let a full, unambiguous per-car-keyed replay settle the remaining 1.39%/p50-90.7
   finding (already explained as graceful-decay-across-short-gaps, not ceiling noise, but not settled with
   the same rigor as the two F1 25 logs).
3. **`FallbackDivergenceSmoothingTauSeconds` (1.0s), `FallbackDivergenceEngageThreshold` (0.3),
   `FallbackDivergenceFullThreshold` (0.6), and the native-agreement guard's own tolerances (1.0 native
   units / 2%) are my own choices**, not owner-specified - chosen and justified from the measured data
   (F1 Generic's ratio sits at 0.24-0.33, comfortably past 0.6; Sauber's occasional dips are damped by the
   smoothing) but not owner-reviewed.
4. **Items 3 and 4 remain open, evidence-bounded, non-blocking** - see their own sections for exactly what
   was checked and why nothing was changed.
5. **The very first qualifying frames of F1 Generic's own very first braking zone in the ShakeIt log** (the
   fallback engages ~38 frames/~0.6s into that specific zone, per the harness trace) do not get the full
   benefit of this fix - by design (sustained evidence takes a moment to accumulate); every SUBSEQUENT zone
   in that same car's session does, and the aggregate acceptance numbers above already reflect this
   (i.e. they are not inflated by ignoring this warm-up window).
