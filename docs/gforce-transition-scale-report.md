# G-Force: revised fixed maxima + Transition Scale

## Status

Done. Both changes implemented, tested, and localised (English + Simplified Chinese, key-parity
green). Solution builds clean at 0 warnings, single DLL (`QAdvanceFeedback.dll`), both Debug and
Release. Full suite: **813 tests passing, 0 failing** (up from the starting 799 - 14 new tests added
for the transition scale feature, 1 pre-existing test updated for the revised maxima default).

## 1. New default fixed maxima

`GForceSettings.FixedAccelMaxG` 0.9 -> **0.75**, `FixedDecelMaxG` 2.0 -> **1.5**.

- `GForceEngine` does **not** carry its own copy of these two values - `Compute(sample, accelMaxG,
  decelMaxG, ...)` receives them as plain parameters every frame, sourced from
  `GForceSettings.EffectiveAccelMaxG`/`EffectiveDecelMaxG`. So, unlike `ShakeFrequencyHz`/
  `TransientGain` (which genuinely exist on both classes and must be kept in parity via `ApplyTo`),
  there is nothing to keep in parity here - `GForceSettings` is the only place either default lives.
- `QAdvanceFeedbackSettings.RestoreDefaults()` replaces `GForce` with a fresh `new GForceSettings()`,
  so the "Restore all default settings" button picks up the revised field initialisers automatically -
  no separate restore-path code to touch.
- Updated: `GForceSettings.cs` doc comments (rationale text), `ConfigStoreTests.Missing_file_yields_
  GForce_and_General_defaults_too` (pinned 0.9 -> 0.75, and added a decel-default assertion that
  didn't previously exist), `GForceSettingsTests.Fixed_maxima_default_to_the_revised_075_and_15_values`
  (new), both READMEs' G-Force tab section.
- Every other `0.9`/`2.0` hit in the test suite (`GForceEngineTests`, `GForceEngineShakeTests`,
  `GForceShakeStabilityTests`, most of `GForceSettingsTests`) is a **local test constant passed
  directly into `Compute()`'s `accelMaxG`/`decelMaxG` parameters**, or an explicit `FixedAccelMaxG =
  0.9` assignment used as arbitrary test data - none of those pin the *settings default* and none
  needed to change.

## 2. Transition Scale - where it's applied, and why

**New setting**: `GForceEngine.TransitionAnimationScale` / `GForceSettings.TransitionAnimationScale`
("Transition scale" in the UI, G-Force tab's "Motion feel" group, fourth row after Sweep speed).
Clamped to `[0, GForceEngine.MaxTransitionAnimationScale]` (= **5.0**) in both setters. Wired into
`GForceSettings.ApplyTo` alongside the other engine-mirrored properties, so Init/Apply keep the two in
sync exactly like `TransientGain`.

**Exact locus**: `GForceEngine.StagedShape`, the private static method that turns `stageProgress`
(0->1) into the three keyframe values (far/mid/terminal) for a chain. Before this change it had a
single hardcoded `high = 1.0` constant used in three places:

1. The far pad's own `p=0` keyframe (its "fully lit" transit peak).
2. The mid pad's own `p=0.5` keyframe (its own transit peak).
3. The terminal pad's own `p=1` keyframe (its **true, settled** sustain value).

Only instances 1 and 2 are ever a pad's own *transit peak* - a value it passes through on the way to a
*different* final resting fraction (far ends at LOW, mid ends at MID). Instance 3 is not a transit
peak at all; it is the pad's own permanent resting value. `TransitionAnimationScale` (passed into
`StagedShape` as `peak`) replaces `high` in instances 1 and 2 **only**; instance 3 still uses the
literal, unscaled `1.0`, always.

This is why the design constraint holds by construction, not by convention: every branch of
`StagedShape` still resolves to exactly `(LOW, MID, HIGH=1.0)` at `p=1` for *any* value of `peak` -
each keyframe's two defining piecewise-linear branches meet at that keyframe's own TRUE value, never
at `peak`. The scale can only ever affect the two transit-only instants; the settled/sustain shape
(and therefore `AdvanceSustainLevel`, which is never touched at all) is mathematically incapable of
seeing it. `AdvanceStageProgress`'s own delta-driven rate/speed logic is completely untouched - the new
setting only ever multiplies into the *shape* values consumed downstream of it, never into the
progress-advancement math itself.

This is the "distinct from `AdvanceSustainLevel`" locus the brief asked me to find: the staged-sweep
shape function is architecturally separate from the sustain low-pass, and only its two transit
keyframes needed touching.

**Mutation evidence (a)** - leaking the scale into the sustain path: temporarily multiplied
`AdvanceSustainLevel`'s return value by `TransitionAnimationScale` for both chains. Result: both
dedicated "sustain unchanged across every scale value" tests failed immediately (expected 0 delta
between scale values, got a 25-/50-point gap). Reverted; suite is back to green.

## Default value: 1.5, reasoned from the available logs

Candidates considered: **1.0** (no change beyond the maxima revision) vs **1.5** (the owner's own
suggestion).

Analysed `c_1_5_3_e_d\*.csv` (F12025, cars "Sauber" and "F1 Generic" - the high-G case) and
`Common_1_5_e_d\*.csv` (Forza Horizon 6, GameId/CarId blank in these particular capture rows, wet/dry
runs - available as a real-log cross-check, though it turned out not to be a genuinely low-G dataset -
see Concerns).

**F1 Generic** (`c_1_5_3_e_d`, 9116-16409 qualifying rows across the two sessions): accel median
0.907-0.911g, p95 4.30-4.47g; decel median -0.42 to -0.45g, p05 -1.12 to -1.13g. Against the **new**
0.75g/1.5g maxima, F1's own *median* accel (0.907g) already exceeds the new accel max outright, and its
harder braking already sits close to/at the new decel max. Concretely: the sustained ratio
(`AdvanceSustainLevel`'s output, which the scale never touches) for F1 is at or near 1.0 (saturated) for
most of a real session under the new maxima, **with or without** `TransitionAnimationScale`. Since the
scale only multiplies the *transit* keyframes (`sustained * peak`), and `sustained` itself is already
~1.0, `sustained * 1.0` and `sustained * 1.5` both clamp to the same 100% ceiling - **scale 1.0 and
scale 1.5 produce an identical result for F1**, because F1 was already saturating the transit peak on
its own. Lowering the maxima (item 1) is what made F1 saturate sooner; layering the scale on top adds
nothing further for a car that's already there.

**Hypothetical low-G car** (the owner's own 0.3g accel / 0.9g decel example, matching what a road car
that never gets close to the maxima looks like): ratio 0.3/0.75=0.40 accel, 0.9/1.5=0.60 decel. At
scale 1.0 the transit peak tops out at exactly the sustain ratio (40%/60% - the numb, pre-feature
result the owner was complaining about). At scale 1.5: 0.40x1.5=0.60 (60%) accel, 0.60x1.5=0.90 (90%)
decel - reproducing the owner's own worked example exactly (verified as a dedicated test, see below),
and clearly perceptible without being pinned.

Conclusion: the two changes (lower maxima + scale 1.5) do **not** compound into a bad result for a
high-G car - they are simply redundant for one, because item 1 alone already pushed that car to
saturation. There was no basis in the log analysis to hold back the owner's own requested 1.5, so it
ships as the default.

**Bounds/continuity confirmed**: every pad stays clamped to `[0, 100]` regardless of scale (existing
`ClampMath.To01`/`To0100` aggregate clamps, unchanged); a fresh, dedicated test sweeps every channel
across a wide G range at the maximum configured scale (5.0) and asserts bounds throughout. A high scale
that saturates the transit peak early does not produce a discontinuity under the SAME gentle-ramp
methodology this codebase's own pre-existing continuity test already uses (only the scale was raised,
everything else held identical to the passing baseline test) - and does not permanently pin a channel:
continuing to brake at a much gentler, unsaturated level afterwards settles exactly at that level's own
true, unscaled value (20.0/10.0/5.0 for a 0.2 ratio).

## Sustain path: confirmed untouched

- `AdvanceSustainLevel` is never passed `TransitionAnimationScale` and never was touched.
- `StagedShape`'s terminal (`p=1`) keyframe always resolves to the literal `1.0`, never `peak`, so the
  settled distribution for ALL THREE pads (not just the terminal one) is bit-for-bit identical across
  scale values - proved directly by two dedicated tests (braking and acceleration chains, six scale
  values from 0 to the 5.0 ceiling, mid-level and fully-saturated ratios).
- Mutation (a) above is the falsifiability evidence: deliberately routing the scale into the sustain
  path makes those exact tests fail.

## Mutation evidence (b) - maxima revert

Temporarily reverted `_fixedAccelMaxG`/`_fixedDecelMaxG` back to 0.9/2.0. Result: `ConfigStoreTests.
Missing_file_yields_GForce_and_General_defaults_too` and `GForceSettingsTests.Fixed_maxima_default_to_
the_revised_075_and_15_values` both failed (expected 0.75/1.5, got 0.9/2.0), as expected. Reverted;
suite is back to green.

## Screenshot

Regenerated via the existing out-of-repo WPF harness (`...\scratchpad\qpreview\Program.cs`), rebuilt
against the Release build of the updated `QAdvanceFeedback.dll`, following the standing capture rule in
`docs\architecture.md` (per-tab content only, tab strip and Apply/Restore row excluded for the G-Force
tab). Output renamed from the harness's own `settings-g-force.png` to the required `settings-gforce.png`
and copied into `docs\images\settings-gforce.png`, replacing the stale one. Dimensions: **1024 x 1206**.
The other three screenshots (`settings-wheel-lock.png`, `settings-wheel-slip.png`,
`settings-general.png`) were regenerated by the same harness run into the scratch output directory only
- left untouched in the repo.

## Localisation

Added `GForce.TransitionScale.Label` / `GForce.TransitionScale.Note` to both `StringTableEn.cs` and
`StringTableZhHans.cs`. The Chinese table was edited via a Python script
(`io.open(..., encoding='utf-8')`, values built from explicit `\uXXXX` escapes computed from the
Chinese source text, never raw CJK bytes written through a shell heredoc), matching the existing table's
own escaping convention. `LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set`
passes.

## Concerns

- The `Common_1_5_e_d` logs (Forza Horizon 6) turned out not to be a genuinely *low-G* dataset once
  inspected - median accel there (1.25-1.36g) is actually higher than F1's own braking median, and the
  session-wide accel max reaches 4-5g (almost certainly curb/impact spikes rather than sustained
  driving G, similar in character to the ~19.8g impact spike this codebase's own live-clamp tests
  already guard against). The "hypothetical low-G car" analysis is therefore genuinely hypothetical -
  built directly from the owner's own 0.3g/0.9g worked example, not cross-validated against a real
  low-G log, because neither available log set actually contains one. Worth keeping in mind if a real
  low-G capture becomes available later.
- A cold, one-frame step directly into a heavily-saturating input at a high scale value produces a
  large single-frame change (verified via a throwaway debug harness during development, then removed).
  This is not a new defect - it is the same "a fresh engine's very first transient is a real, arguably
  correct one-off event, not a continuity violation" case this codebase's own
  `Output_never_jumps_as_longitudinal_G_sweeps_continuously` test already documents and exempts; a high
  `TransitionAnimationScale` simply makes that one-off transient larger in absolute terms, which is the
  intended effect ("a full-feeling sweep"), not a bug. The dedicated continuity test for this feature
  therefore mirrors the existing gentle-ramp methodology (only the scale changed, ramp shape/timing
  identical to the already-passing baseline test) rather than an instantaneous step.
- `TransitionAnimationScale` is a persistent configuration value, not per-frame transient state, so
  (correctly, matching `TransientGain`/`ShakeFrequencyHz`) it is not touched by `GForceEngine.Reset()`.
