# Raw-vs-ShakeIt gap and G-force pad-balance report — QAdvanceFeedback, Forza Horizon 6 sessions 20260815-153801/153941

## STATUS

**TASK 1 (Wheel Lock/Slip Raw-vs-ShakeIt gap):** DIAGNOSED, NOT CODE-FIXED — the decisive evidence (below)
shows the gap is dominated by a genuine, per-wheel-differentiated ShakeIt signal that **no car-level
formula this plugin has ever implemented can structurally reproduce**, because Layer 1
(`ITelemetryFrame`) deliberately excludes the per-wheel `WheelRPS`/`WheelSpeed` telemetry the real branch
needs. Per this task's own explicit instruction ("if something cannot work as specified, STOP and say so
with reasoning"), no third branch-guess was made. **`Diag.Source.*` was added** (both channels, per wheel
plus All) so this reconstruction never needs to be repeated by inversion again.

**TASK 2 (G-force pad balance):** DECIDED FROM THE LOGS — **not our maths**. TopBack genuinely reaches
high published values during acceleration and LowBack does not over-dwell; BottomFront genuinely reaches
high published values during deceleration. No staged-animation code was changed.

**MID-TASK FOLD-INS (both addressed, both plain data/UI changes with no computed-output effect):**
- Recommended pad frequency revised twice by the owner's own real seat-time feel, ending at **100 Hz -\>
  50 Hz** (not the intermediate 50 Hz -\> 20 Hz instruction, which was explicitly superseded before being
  acted on further than the settings default).
- **A real, owner-confirmed defect was found and fixed**: the G-force "Integrate Wheel Lock and Slip"
  shake was reading the PULSED `Projected.*` value as its own amplitude, so the pulse's presentation
  waveform modulated the shake — "one oscillation modulating another." Fixed by adding
  `ProjectedWithoutPulse` (curve-projected, pulse stage skipped) and rewiring the shake to consume it.

**Tests:** 533 total (522 pre-existing + 11 new), 0 failed, 0 skipped, full build (`Private/` present).
**Clean-clone tests:** 490 total (479 pre-existing + 11 new — all 11 new tests are public, none live in
`Private/QAdvanceFeedback.Tests/`), 0 failed, 0 skipped, `Private/` absent, verified in an isolated
scratch copy (not the working tree).
**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release -v:m` → `0 Warning(s), 0 Error(s)` for both the full tree
and the isolated clean-clone scratch copy (with `lib/` copied alongside it, since the reference DLLs live
outside the project folder). `bin/Release/net48/` contains exactly `QAdvanceFeedback.dll` and
`QAdvanceFeedback.pdb` in both cases.

**Localisation:** no new UI-facing strings were introduced this pass — `Diag.Source.*` and
`WheelLock/WheelSlip.ProjectedWithoutPulse.*` are diagnostic *property names*, not localized display
text (matching every pre-existing `Diag.*` name, none of which appear in the string tables either), and
the pad-frequency numbers are displayed through the EXISTING parameterised
`GForce.RecommendedHz.Note` string (`{0:0} Hz ... {1:0} Hz`) in both tables, unchanged. Key-parity test
(`LocalizationTests`) still passes.

**Screenshot regenerated:** `settings-gforce.png` (via the harness at the isolated scratch path, kept out
of the repo — only the resulting PNG was copied into `docs/images/`), since the G-Force tab's displayed
recommendation text changed from "300 Hz .. 20 Hz" to "100 Hz .. 50 Hz". Wheel Lock/Wheel Slip/General
tabs are visually unaffected (Diag.Source.*/ProjectedWithoutPulse are diagnostics-only, not shown in any
settings control) — not regenerated.

---

## TASK 1 — Wheel Lock/Slip Raw vs ShakeIt: reconstruction, the gap, and why it isn't a Layer-3 fix

### 1a. Method — inverting the Normalized transform without guessing

Log A (`QAdvanceFeedback.session-20260815-153801_With_Internal.csv`, 2234 frames, source =
ShakeIt output properties) contains, frame-aligned: our own `WheelLock/WheelSlip.Raw.*` (Layer 3,
computed in parallel regardless of source) and `WheelLock/WheelSlip.Normalized.*` (Layer 4, computed
from ShakeIt's real per-wheel value as `rawLockWheels`/`rawSlipWheels`).

`NormalizedWheelLockSlipEngine.ComputeChannel` (`QAdvanceFeedback/Core/Normalized/NormalizedWheelLockSlipEngine.cs`)
computes, per wheel `i`, `Normalized_i = severity * s_i` where `s_i = w_i / mean(w)` (`w` = ShakeIt's raw
four-wheel vector) and `severity = max(effectiveGripUtilization, mean(w))`, but ONLY once a shared
`triggered`/`engaged`/`motion-available` gate has passed (otherwise the output is forced to exactly 0, or
- in the "no G signal at all" fallback - passed straight through as `w` itself, clamped).

Two facts make this invertible without guessing:

1. **Relative per-wheel shape is ALWAYS exactly recoverable, regardless of severity.** Since `severity`
   is one shared scalar applied to all four wheels via `s_i = w_i/mean`, the *ratios* between the four
   published `Normalized_i` values are always identical to ShakeIt's own raw ratios — this needed no
   assumption about which term (`effectiveGripUtilization` vs `mean`) actually won the `max`.
2. **`severity` itself is directly observable**: `Normalized_FL + Normalized_FR + Normalized_RL +
   Normalized_RR = severity * 4` falls straight out of the `s_i` definition, so `severity = average of
   the four published wheel values` — no reconstruction needed at all.
3. **`effectiveGripUtilization`'s upper bound (`gripUtilization`) is independently reconstructable** from
   the logged `Diag.MotionMagnitudeG`, `Diag.Lock/Slip.LearnedPeakG`, and `Diag.Lock/Slip.LearnerConfidence`
   columns, by re-deriving `GripLearner.Ratio`'s exact formula (`Clamp(magG,0,15)/learnedPeakG`, then the
   cold-start ceiling `0.75 + confidence*0.25` while confidence \< 1). Whenever the observed `severity`
   (fact 2) exceeds this upper bound, `max` MUST have picked `mean` — so `mean = severity` **exactly**,
   and (from fact 1) **every per-wheel `Normalized_i` equals ShakeIt's own raw wheel value exactly**, that
   frame. Frames where `severity <= gripUtilization` are ambiguous (G-floor may be dominating) and were
   excluded from the exact-reconstruction bucket rather than guessed.

This is a genuinely stronger, log-only method than trying to recompute SimHub's candidate branch formulas
directly: **the CSV export has no raw Brake%/SpeedKmh/RPM columns**, so an alternate formula
(RPM/Speed-legacy, RPS, etc.) literally cannot be recomputed from this log even if desired — only shape
comparisons against our own already-computed Raw are possible, which is exactly what the task's own
"binned by our Raw" instruction asks for.

### 1b. The decisive finding: genuine per-wheel differentiation, not a magnitude/threshold gap

**Our own `WheelLock.Raw.*` is uniform across all four wheels on every single one of the 2234 frames**
(`FrontLeft == FrontRight == RearLeft == RearRight`, spread exactly 0.0 throughout) — expected, since
`SimpleBrakingLockAlgorithm`/`GetSimpleBraking` has no per-wheel term at all.

**ShakeIt's real Lock output does NOT share that property.** Example, frames 1030-1050 (a genuine hard
brake application, `Diag.Direction`=Slowing, `Diag.MotionMagnitudeG` 2.8-4.1g throughout):

```
frame  our WheelLock.Raw.All   ShakeIt-derived Normalized (FL / FR / RL / RR)
1030   100 (already saturated)  0 /   0 / 0 / 0
1037   100                      3 /   0 / 0 / 0
1038   100                    100 /   0 / 0 / 0   <- FrontLeft alone reaches full lock
1044   100                    100 /   0 / 0 / 0   <- held there for 7 more frames
1045   100                    100 /  84 / 0 / 0   <- FrontRight starts joining
1046   100                    100 / 100 / 0 / 0   <- both fronts locked, rears never do
1050   100                    100 / 100 / 0 / 0
```

This is a physically coherent, sustained (multiple-frame), progressive **front-wheel-only** lockup
sequence — while our own Raw sits flat at 100 for all four wheels the entire time, because it has no
mechanism to ever read differently per wheel. This is not noise: across the whole session, of 406 frames
where ShakeIt's Lock reads \>1 on at least one wheel, **160 (39%) show a per-wheel spread \>20 points**,
and the same pattern (one or two wheels active, the others silent, held for many consecutive frames)
recurs repeatedly. Slip shows the same thing, more strongly — e.g. frame 59: our own `WheelSlip.Raw`
reads a UNIFORM 9.9 across all four wheels (straight-line acceleration, no lateral velocity to
differentiate by), while ShakeIt's reconstructed Slip reads `FL=6.7, FR=0, RL=100, RR=20.8` at that exact
same frame — of 703 frames where ShakeIt's Slip reads \>1 on at least one wheel, mean per-wheel spread is
43.4, median 39.5.

**Why this rules out both branches this plugin has ever implemented for Lock:**
- `GetSimpleBraking` (the previous pass's conclusion, `SimpleBrakingLockAlgorithm.cs`) uses only
  `Brake`/`SpeedKmh` — structurally uniform across wheels, by construction, always. It cannot produce the
  above sequence under any tuning of `LockSensibility`.
- The RPM/Speed-legacy branch (`GetRpmSpeedSlipLegacy`, what `LegacySlipAlgorithm` reproduces, used for
  Lock before the previous pass and still used for Slip) is ALSO effectively car-level for its dominant
  brake term (`Rpm` is a whole-car field, not per-wheel) — only its small lateral-floor term varies by
  wheel (via cornering, not braking asymmetry), and even that was already empirically shown wrong for
  Lock by the owner's own live-rig test (switching source to ShakeIt fixed the complaint).
- The decompiled dispatch (`docs/reference/SimHub.WheelSlipEffect.decompiled.cs`) shows the only
  branches capable of this kind of genuine per-wheel result for a Lock container are
  `LockFromWheelsRPSAndDummyRadius`, `GetLockFromWheelSpeed`, or `GetRpsLock` — **all three require
  per-wheel `WheelRPS`/`WheelSpeed` telemetry that Layer 1 (`ITelemetryFrame`) deliberately does not
  expose**, an architectural restriction this project has flagged in every prior pass
  (`docs/lock-and-animation-report.md`'s own "Concerns" section already called this out).

**CONCLUSION — cannot be fixed as specified, said plainly:** with the current Layer 1 architecture, no
car-level Lock (or Slip) formula this plugin could write would ever reproduce ShakeIt's genuine per-wheel
lock/slip timing. This is not a threshold, scale, or branch-choice bug to fix — it is a missing-input
problem. Making a third branch guess (there have now been two: RPM/Speed-legacy, then GetSimpleBraking,
both empirically or structurally wrong) without new evidence of which of the three RPS/WheelSpeed
branches is live, or without Layer 1 growing wheel-level telemetry it deliberately never carried, would
just be a coin flip risking a third regression. Per this task's own explicit permission, this is reported
as a hard stop, not forced.

### 1c. The magnitude gap, where the shape IS car-level (secondary, smaller finding)

Restricting to the 238 (Lock) / 96 (Slip) frames where exact reconstruction was possible AND the
reconstructed ShakeIt value happens to be near-uniform across wheels (87.8% of Lock's exact-bucket
frames, 76% of Slip's), a magnitude gap is also visible, binned by our own Raw:

```
Lock — our_raw bin      n    our_raw mean   ShakeIt-reconstructed mean
[30,40)                 1        37.1              54.2
[40,50)                 2        41.9              63.5
[50,60)                 4        53.8              74.7
[60,70)                 4        62.8              87.3
[70,80)                 3        74.5              99.4
[80,90)                 3        84.9             100.0
[90,100)               221        99.9              98.0   (both saturated)
```

Direction is consistent (ShakeIt reads higher, converging only at saturation) but the mid-range sample
count is tiny (17 total frames from 30-90) and the underlying `LockSensibility` **used by ShakeIt's own
internal effect configuration is a SimHub-side setting this plugin cannot read** — it is a different,
independently-configured value from `_settings.Lock.LockSensibility` in this plugin, despite sharing a
name/range/default by convention. A gap fully consistent with "the two knobs are set to different
values" is not evidence of a math bug on our side, and chasing it (e.g. re-deriving a different
sensitivity mapping) risks fighting a driver's legitimate, separately-configured ShakeIt value. **Not
changed**, flagged as unverifiable rather than guessed.

### 1d. The fix that WAS made: `Diag.Source.*`

Added (`AllPublishedProperties.cs`, `PropertyPublisher.cs`, `QAdvanceFeedback.cs`):
`Diag.Source.Lock.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}` and the Slip
equivalent — publishing **exactly** the `Corners` Layer 4 (`NormalizedWheelLockSlipEngine.Compute`) is
called with this frame (our own Raw by default, or whatever the driver's Source fields resolved to
instead — a ShakeIt export, a Manual expression, or the Layer 3 fallback), aggregated with the same
p-norm convention every other tier uses. Gated behind the existing `EnableDiagnostics` toggle, included
in the CSV export. **Future comparisons need no inversion at all** — the value this report spent most of
its effort reconstructing is now one CSV column away.

---

## TASK 2 — G-force pad balance: decided from the logs, not our maths

Extracted `GForce.*` published values from both logs, split by `Diag.Direction` (ACCEL = SpeedingUp,
DECEL = Slowing), and characterised peak/median/time-above-threshold per channel:

```
LOG A (2234 frames, ~43s)                          LOG B (2661 frames, ~56s)
ACCEL (n=1395)                                     ACCEL (n=1631)
  TopBack (L/R)   peak=100 median=88-94 >60%=60-63%   peak=100 median=59-73 >60%=49-56%
  LowBack (L/R)   peak=96-100 median=46-48 >60%=5-12%  peak=69-75 median=30-38 >60%=1-6%
  BottomRear(L/R) peak=87-96  median=23-24 >60%=~1%    peak=99   median=16-19 >60%=~0%

DECEL (n=626)                                      DECEL (n=509)
  BottomFront(L/R) peak=100 median=99 >60%=83-84%     peak=100 median=99 >60%=71-74%
  BottomRear(L/R)  peak=100 median=50  >60%=10-12%     peak=99-100 median=50 >60%=7-13%
  LowBack(L/R)     peak=100 median=25  >60%=3-4%       peak=100 median=25  >60%=8%
  TopBack(L/R)     peak=29-30 median=0 >60%=0%          peak=3-4  median=0  >60%=0%
```

**The decisive test, answered directly from these numbers:**
- **"Does TopBack genuinely reach high published values during acceleration?"** Yes — median 59-94
  (both logs), peak 100, and above 60/100 for 49-63% of every accelerating frame. This is the
  acceleration chain's own TERMINAL pad, by design meant to carry the sustained, strongest cue once
  fully staged — and it does, in the published numbers.
- **"Does LowBack actually dwell as long as it feels?"** No — LowBack (acceleration's MIDDLE pad) spends
  only 1-12% of accelerating frames above 60/100; its median sits at 30-48, clearly the fading
  middle-of-the-sweep pad the staged model intends, not a pad camped at high strength.
- **"Does BottomFront genuinely reach high published values during deceleration?"** Yes, emphatically —
  median 99/100 in BOTH logs, above 60/100 for 71-84% of every braking frame. This is braking's own
  TERMINAL pad and it is essentially saturated most of the time a driver brakes.

**Per the task's own decision rule: TopBack reaches high and LowBack does not over-dwell, and BottomFront
reaches high — so the imbalance the driver feels is downstream of this plugin.** The published
distribution matches the staged model's own design intent (far pad fades quickly, terminal pad carries
the sustained cue) in both sessions, independent of which source fed Wheel Lock/Slip. **This is not our
maths.** Per-channel gain trimming (why LowBack "feels" strong or TopBack "feels" weak on the driver's
own hardware despite the published numbers already being the intended shape) belongs in the driver's own
ShakeIt effect configuration (per-channel output-scale calibration for their specific pad hardware), not
in this plugin's output. **No staged-animation code was changed** — the data says the distribution this
plugin already produces is correct.

---

## MID-TASK FOLD-IN 1 — Recommended pad frequency: 300 Hz -\> 20 Hz, revised twice, to 100 Hz -\> 50 Hz

Two successive owner corrections arrived mid-task, the second explicitly superseding the first:
1. First instruction: 300 Hz -\> 20 Hz down to 50 Hz -\> 20 Hz (300 Hz read too harsh/thin at the low
   end).
2. Superseding instruction (after further seat time): 20 Hz did not shake strongly enough; the actual
   current recommendation is **100 Hz -\> 50 Hz**.

Applied as the final, current state (the intermediate 50/20 was never left in place as "current
guidance" anywhere — `docs/gforce-report.md`'s correction note narrates both revisions for the historical
record):
- `GForceSettings.cs`: `_recommendedFromHz` default 300.0 -\> **100.0**, `_recommendedToHz` default 20.0
  -\> **50.0**. `DeviceMinHz`/`DeviceMaxHz` (the hardware capability, a SEPARATE concept from the
  recommendation) are UNCHANGED (10/300).
- `GForceSettingsTests.cs`: default-value assertions updated to 100.0/50.0.
- `README.md`/`README.zh-Hans.md` (Chinese edited via a Python script with `io.open(...,
  encoding='utf-8')`, verified to decode cleanly afterward — no raw CJK went through a shell/heredoc):
  updated to state the current 100/50 default and its two-revision history.
- `docs/gforce-report.md`: a correction note added above the original description (kept, not deleted,
  for the historical record per this task's own instruction), pointing at this report.
- `settings-gforce.png` regenerated (see STATUS) — confirmed visually showing "100 Hz at value 0, down
  to 50 Hz at value 100."

**Verified this is guidance-only, as instructed:** `RecommendedFromHz`/`RecommendedToHz` are read ONLY by
`SettingsControl.xaml.cs`'s `GForceRecommendedHzText` display and by `ConfigStore`'s persistence
round-trip; nothing in `Core/GForce` or any other computed pipeline reads either property (confirmed by
searching every reference to both names). No behavioural test moved — the full 533/490 suites already
reported above include this change with zero unrelated failures.

## MID-TASK FOLD-IN 2 — The pulse-into-shake defect (real, fixed)

**The defect, as diagnosed:** with "Integrate Wheel Lock and Slip" ON and a channel's pulse enabled,
`QAdvanceFeedback.cs` fed `GForceEngine.Compute` the PULSED `projected.LockAll`/`SlipAll` as the shake's
own amplitude driver (`wheelLockAll0100`/`wheelSlipAll0100` -\> `GForceShake.Apply`'s `wheelContribution`
-\> `band = centre * contribution`). Once a channel saturates and its pulse engages, that value itself
oscillates 100-\>min-\>100 over the pulse's own `GapMs`-driven period — a presentation effect meant for
the shaker motor, not a real change in lock/slip severity — so the shake's own amplitude wobbled at the
pulse's frequency, superimposed on the shake's own intentional 5-20 Hz oscillation. Confirmed structural
(not merely a report): `PulseGenerator.Advance`'s own documented behaviour (100 while pinned, ramping to
`MinValue` and back over `2*GapMs`) is exactly what feeds straight into `GForceShake.Apply`'s amplitude
term when the pulsed value is used.

**The fix:** `ProjectedWheelLockSlipEngine.Compute` now also computes the curve-projected value BEFORE
the pulse stage (`_lockProjector.Project(lockRaw[i])`/`_slipProjector.Project(slipRaw[i])` - the exact
same call already made just before `PulseGenerator.Advance` consumes it, so it is trivially, structurally
identical to the pulsed value whenever the pulse is not currently engaged - below saturation, or the
pulse disabled - since `PulseGenerator.Advance` itself returns that same plain value in that case).
Exposed as `LockWheelsWithoutPulse`/`LockFrontWithoutPulse`/.../`LockAllWithoutPulse` (and the Slip
equivalent) on `ProjectedWheelLockSlipResult`. `QAdvanceFeedback.cs`'s G-force integration now reads
`projected.LockAllWithoutPulse`/`SlipAllWithoutPulse` instead of the pulsed `LockAll`/`SlipAll`.

**Published internally, per the owner's own explicit naming/scoping request:** `WheelLock.ProjectedWithoutPulse.*`
/ `WheelSlip.ProjectedWithoutPulse.*` (all nine targets, both channels — NOT "Diag."-prefixed, since the
owner asked for these specific names, but gated behind `EnableDiagnostics` exactly like every other
diagnostic name), NOT part of the default published/CSV-visible-by-default property set.

**Tests added** (`ProjectedWheelLockSlipEngineTests.cs` + new `GForceShakeStabilityTests.cs`):
- `WithoutPulse` equals `Projected` exactly, frame for frame, with pulse disabled.
- `WithoutPulse` equals `Projected` below saturation even with pulse enabled (the pulse never engages).
- With pulse enabled and the channel saturated, `Projected` visibly oscillates (swings from \>95 to \<5
  within one pulse cycle) while `ProjectedWithoutPulse` holds steady at exactly 100 throughout.
- Each channel's `WithoutPulse` is independent of the other channel's pulse settings.
- **The actual driver symptom, asserted directly**: feeding a real `PulseGenerator`'s pulsed sequence into
  `GForceEngine.Compute` (isolating the shake's own amplitude envelope from its intentional fast
  oscillation by comparing peak-to-peak spans measured over successive one-shake-period windows) shows
  the span visibly rising and falling (min 4.8, max 19.4+ in the reproduced scenario) — while feeding the
  steady, WithoutPulse-equivalent value produces IDENTICAL spans window over window (the fix's own
  guarantee).
- The shake still responds to a genuinely different lock/slip severity (10 vs 100) with a visibly larger
  span — the fix removes the wobble without making the shake inert.

---

## MUTATION EVIDENCE

**(a) Task 1's diagnosis — N/A, by design.** No Layer 3 formula/branch code was changed (see §1b/1c's own
conclusion: the evidence rules out a productive third branch guess, and no magnitude "fix" was applied
either, given the confound in §1c). There is therefore nothing to revert-and-fail for this item; stated
plainly rather than fabricating a mutation for a change that was never made.

**(b) Task 2's stage weights — N/A, by design.** The G-force pad distribution was confirmed correct from
the logs (§ above); no stage-weight or sweep-rate code was touched, so there is nothing to revert-and-fail
here either.

**(c) `Diag.Source.*` removal.** Commented out the two `SourceLockPrefix`/`SourceSlipPrefix` loops in
`AllPublishedProperties.DiagnosticNames()` and re-ran
`AllPublishedPropertiesTests.Diagnostic_names_include_the_resolved_source_value_per_wheel_plus_All_both_channels`
alone: **FAILED** (`Assert.Contains() Failure: Item not found in collection` for `Diag.Source.Lock.FrontLeft`).
Reverted to the exact original text; that test and the full 533-test suite are green again (confirmed
above).

**(d) Pulse-into-shake fix, pointed back at the pulsed value.** Temporarily changed
`Fed_a_steady_WithoutPulse_style_value_the_shake_amplitude_is_stable_window_over_window` to feed a real
pulsed `PulseGenerator` sequence (mirroring what `QAdvanceFeedback.cs` reverted to the pulsed
`projected.LockAll` would produce) instead of a steady 100.0: **FAILED**
(`Assert.Equal() Failure: Expected: 19.378213, Actual: 4.808881` — the window spans were no longer
equal). Reverted to the exact original text; that test and the full 533-test suite are green again
(confirmed above). `QAdvanceFeedback.cs` itself is not unit-testable directly (it requires
`SimHub.Plugins` types the net8.0 test project cannot load — the same, previously-documented limitation
`PropertyPublisherStructureTests` already works around), so this test is the closest available testable
proxy for that exact wiring decision, using the identical real `GForceEngine`/`PulseGenerator` classes
QAdvanceFeedback.cs itself calls.

---

## Concerns / unverified

- **No live-game/SimHub/hardware verification anywhere** — everything above is verified against the two
  CSV replays, the decompiled SimHub source, and unit tests only, per this task's own stated constraint.
- **The exact live SimHub branch for Lock and Slip remains formally unidentified.** The evidence
  positively rules out `GetSimpleBraking` and the RPM/Speed-legacy branch as structural matches (genuine
  per-wheel divergence neither can produce), and points at one of the WheelRPS/WheelSpeed-based branches
  — but WHICH one, and its exact capability-flag value, is not confirmed (the reader plugin that sets
  `FeedbackCapabilities` is not present in this environment, unchanged from every prior pass's own
  caveat).
- **The Lock magnitude gap in the uniform-shape subset (§1c) is confounded by ShakeIt's own,
  unreadable, internally-configured `LockSensibility`** — reported as a pattern, not asserted as a bug,
  and deliberately not "fixed" against a possibly-legitimate driver setting we cannot observe.
- **Sample sizes in several bins are small** (e.g. 17 total Lock frames across the 30-90 "exact" range,
  2 frames for the strict low-speed-fix-engaged check) — flagged inline where used; no strong magnitude
  conclusion was drawn from these beyond directional consistency.
- **Task 2's dwell/frame-fraction numbers are frame-count-based, not wall-clock-integrated** — both
  sessions run close to a constant ~48-52 fps, so this is treated as a reasonable proxy for time, not
  exact seconds.
- **`ProjectedWithoutPulse`/the shake-stability fix are verified by unit test only** — the actual
  hardware "wiggle" the driver felt is inferred to be explained by this mechanism from the documented,
  provable behaviour of `PulseGenerator`/`GForceShake`, not from a live rig re-test (out of scope here).
