# Anchor rescale (75 -> 80), "Max Grip" rename, and a real convergence bug found along the way

## STATUS: DONE

## Summary

`KeyedScaleLearner.CanonicalAtLimitAnchor` moved from **75.0 to 80.0**. The top curve anchor was
renamed from "Critical" to **"Max Grip"** in both languages, with a corrected description. Docs and
screenshots were refreshed. While verifying the rescale end-to-end (per a mid-task correction - see
below), a real, safety-relevant bug was found in `KeyedScaleLearner`'s own confidence ramp and fixed,
narrowly, in that one class only.

## The core premise in the original brief did not match the code - verified, not assumed

The brief asserted the top curve anchor's **input** was currently 75, coinciding with the old
`CanonicalAtLimitAnchor`, and asked to multiply the three anchor inputs (stated as 10/35/75) by
80/75. Checked directly against `ProjectorSettings.ApplyPreset`/`WheelChannelSettings.CreateLockDefaults`/
`CreateSlipDefaults`: the shipped Curve-preset anchor **inputs** are actually **30/60/80** for both
channels (fixed band boundaries) - the top anchor input was already **80**, not 75. The numbers
10/35/75 in the brief are the **Slip channel's own OUTPUT column** (`SlightlyOutput`/`ModerateOutput`/
`CriticalOutput`), which the brief separately (and correctly) protects from change. Given this, the
correct action was: raise `CanonicalAtLimitAnchor` to 80 and leave the curve anchor **inputs**
untouched at 30/60/80 - the coincidence (top anchor input == `CanonicalAtLimitAnchor`) is achieved for
free, because the top input was already 80. Multiplying the actual inputs by 80/75 (as literally
instructed) would have pushed the top input to 85.3, **breaking** the coincidence instead of
preserving it. This is reported per the brief's own instruction to surface such findings rather than
paper over them.

## The mid-task correction, and what it actually required

A correction arrived stating the requirement is not "a constant named 80 exists" but "the Normalized
output **measured when the car is physically at max grip** is actually ~80," and rejected documenting
a permanent under-report as acceptable. This was verified empirically (not assumed) using the real
`NormalizedWheelLockSlipEngine`/`KeyedScaleLearner` code path, driving realistic "physically at the
limit" telemetry sequences with synthetic jitter to mimic real driving noise.

### What was found

`KeyedScaleLearner`'s own primary-tier weight (`ColdWarmBlend.ConcaveHotWeight`) is a **product** of:
- a count term (`ConcaveCountConfidence`) that reaches **exactly 1.0** once
  `primary.Count >= CalibrationConfidenceScaleSamples` (200), and
- a dispersion term (`DispersionQuality`) that is **strictly less than 1.0** for any nonzero
  coefficient of variation - i.e. every real driving session, which never repeats the identical
  reading at every physically-at-the-limit moment.

The product therefore **never** reached full trust for any realistically-noisy source, no matter how
much more evidence accumulated - a permanent plateau, not a slow approach. This directly contradicts
`CalibrationConfidenceScaleSamples`'s own XML doc, which quotes the owner's own worked example ending
"...>=200 samples -> weight 1.0". Measured concretely (`KeyedScaleLearner.Rescale` at a fixed native
ceiling of 90, learned under an unbiased 15% jitter, old code, anchor 75 baseline math extended to 80):
at count=2000-20000 the weight permanently sits at `DispersionQuality(cv)` (~0.63-0.84 for CV in the
2-15% range measured), the learned ceiling permanently undershoots the true 90 (measured 88.55 at
CV~0.09), and a probe at the true native ceiling (90) permanently read **~81.1-81.5** instead of 80 -
not converging further between 2,000 and 20,000 samples.

### The fix (narrowly scoped)

In `KeyedScaleLearner.LearnedCeilingForKey`'s primary-tier branch only: once
`primary.Count >= CalibrationConfidenceScaleSamples`, the weight is floored to **1.0**, matching that
constant's own documented contract literally. `ColdWarmBlend` itself, and every other caller of it
(`GripLearner`'s own live confidence ramp, and every one of `GripLearner`'s already-tuned thresholds),
is **untouched** - the fix lives entirely inside `KeyedScaleLearner`, which is the file this task
already owns.

### Before / after (measured, this codebase, not estimated)

Realistic 10-15% jitter around a native "at the limit" ceiling of 90, teach-then-probe methodology
(many noisy teaching observations, then one fixed probe at the true native ceiling):

| | Before fix | After fix |
|---|---|---|
| Learned ceiling (true 90, jitter CV~0.09) | 88.55 (permanent, not improving with more samples) | 89.79 |
| `Rescale(90)` at count=2,000-20,000 | ~81.1-81.5 (flat, not converging) | ~79.95-80.4 |
| Low-native-ceiling source (true 60, 15% jitter, count=3,000) | not separately re-measured pre-fix at this exact config, but the same permanent-plateau math applies **below** the anchor (measured ~65-75 range depending on CV at moderate counts pre-fix) | 80.48 |

**The safety-relevant number - Projected output (Layer 5, through the curve) at the physical
max-grip state**, measured directly with `OutputProjector`/`ProjectorSettings.CreateShippedDefault`:

| Channel | Projected output at Normalized=75 (the OLD anchor value) | Projected output at Normalized=80 (the NEW anchor value, now what a real at-the-limit reading actually produces) |
|---|---|---|
| Wheel Lock | 68.75 | **80.00** (exactly the Max Grip anchor's own output) |
| Wheel Slip | 65.51 | **75.00** (exactly the Max Grip anchor's own output) |

This is the concrete "cue inversion" the correction warned about, and its fix: before, the genuine
at-the-limit moment produced only a 65-69%-strength cue - noticeably softer than the "release now"
signal the Max Grip anchor's own output value (80 Lock / 75 Slip) was designed to deliver. After the
rescale + the estimator fix, the genuine at-the-limit moment now lands exactly on that intended output.

### Confirmed nothing else is coupled to the old 75 constant

Searched the whole solution (excluding `obj`/`bin`) for `75` as a literal. Every other hit is an
unrelated constant already tied to its own, independent design decision, confirmed by reading each
one directly: `AggregationWeights.LockDefaults` (0.75/0.25 axle blend, `docs/aggregation-report.md`),
`GripLearner.ColdStartCeilingRatio` (0.75, an unrelated cold-start ratio), `GForceSettings`'s default
0.75g acceleration ceiling (`docs/gforce-transition-scale-report.md`), `RobustBandEstimator.MaxMeanBlendWeight`
(0.75, the G-Force auto-max-G pool blend - explicitly evaluated and declined for
`KeyedScaleLearner`/`GripLearner` already, per that class's own remarks), and a `ColdWarmBlend` XML
comment's own worked numeric example (unrelated to the anchor). None of these reference
`CanonicalAtLimitAnchor` or assume the top curve anchor sits at 75. Also confirmed the curve anchor
**inputs** (30/60/80, both channels) never referenced 75 anywhere - the only real "75" that meant
"the at-limit anchor" was `KeyedScaleLearner.CanonicalAtLimitAnchor` itself, plus its own doc-comment
cross-references (all updated) and test literals pinned to it (all updated - see Tests below).

## Naming

- `Curve.Anchor.Critical`: "Critical" -> **"Max Grip"** (English); Simplified Chinese changed from
  "严重" (Critical/Severe) to "最大抓地力" (Max Grip).
- `Curve.Lock.Critical.Desc` / `Curve.Slip.Critical.Desc`: rewritten in both languages to convey the
  new meaning - AT the measured grip limit (maximum braking/traction the tyre can deliver), not "on
  the verge, not locked yet." Both languages checked for grammar/spelling.
- Chinese text was written exclusively via a Python script constructing strings from `\uXXXX`
  codepoint integers (`chr(0x....)`), never raw CJK in any shell command - each string was decoded and
  read back before being applied to the real file, and (for the code-comment-adjacent
  `architecture.zh-Hans.md` insertions) cross-checked with an exact substring match against the real
  file content as an independent verification that every codepoint was correct.
- **Internal identifier rename (`Critical` -> `MaxGrip` in C#/XAML): SKIPPED, deliberately.**
  `ProjectorSettings.CriticalInput`/`CriticalOutput` are plain public properties serialized by
  Newtonsoft directly into a driver's persisted `config.json` - renaming them would silently break
  every existing driver's saved settings file (exactly the same risk this codebase already avoids for
  `ModerateInput`/`ModerateOutput`, kept as-is despite the UI label being "Ideal", specifically to
  protect persisted JSON keys - see `WheelChannelSettings`'s own remarks). A "complete" rename would
  need to touch these properties too (for internal consistency) or accept an inconsistent partial
  state (XAML/enum renamed, backing properties not) - the brief's own explicit gate ("ONLY if you can
  do it completely... a partial rename is worse than none") is not met, so **every** internal
  identifier (`AnchorSlot.Critical`, `ProjectorSettings.CriticalInput/CriticalOutput`, and every
  `Critical*`-named XAML element/field in `SettingsControl.xaml`/`.xaml.cs`) was left unchanged. Only
  the user-facing string table values changed.

## Tests

- `KeyedScaleLearner.CanonicalAtLimitAnchor` doc comment rewritten; two `MutationGuard` tests
  (`ColdStartAndCrossCarSeedTests.cs`, `RegressionFixTests.cs`) that pinned a local
  `fullyCalibratedAnchor = 75.0` constant were changed to reference
  `KeyedScaleLearner.CanonicalAtLimitAnchor` live (so they can never go stale again) and their expected
  captured-step values updated from 15.0 to 10.0 points (90 - 80).
- Stale explanatory comments in `NormalizedWheelLockSlipEngineTests.cs` recomputed (75 -> 80,
  ~83 -> ~88.9).
- **New**: `WheelChannelSettingsTests.CanonicalAtLimitAnchor_is_80` - asserts the constant is 80.0.
- **New**: `WheelChannelSettingsTests.Lock_and_slip_top_curve_anchor_input_coincides_with_the_canonical_at_limit_anchor` -
  asserts `CriticalInput == KeyedScaleLearner.CanonicalAtLimitAnchor` for both channels' shipped
  defaults - the coincidence that carries the anchor's meaning, pinned so it can never silently drift.
- **New**: `ColdWarmScaleLearnerTests.Primary_tier_reaches_full_trust_at_the_documented_sample_scale_even_with_realistic_dispersion` -
  regression test for the confidence-ramp fix (realistic 10% jitter, asserts convergence within 2
  points of the anchor once evidence is abundant).
- Two pre-existing tests needed adjustment as a **direct, understood, and documented** consequence of
  the estimator fix (not because the fix broke anything, but because it made the anchor point converge
  exactly, which mechanically widens the residual spread at off-anchor checkpoints and changes an
  inflated intermediate blend value back down to its accurate one):
  - `PerSourceCalibrationTests.Three_differently_scaled_sources_converge_toward_the_same_canonical_output`:
    the "slightly"/"ideal" off-anchor tolerance raised from 40.0 to 42.0 points (the at-anchor 5-point
    bar is unaffected and, if anything, now more tightly satisfied).
  - `NormalizedWheelLockSlipEngineTests.Fallback_engagement_ramps_gracefully_instead_of_stepping`: the
    "climbing toward Raw's high reading" bound lowered from >65.0 to >60.0, since Raw's own calibrated
    fallback now correctly converges to exactly 80 instead of an inflated ~85-90 the old, permanently-capped
    ramp used to leave in place.

### Mutation evidence (performed live, reverted immediately)

`CanonicalAtLimitAnchor` was changed to **85.0**, rebuilt, and the coincidence test
(`Lock_and_slip_top_curve_anchor_input_coincides_with_the_canonical_at_limit_anchor`) and the
`CanonicalAtLimitAnchor_is_80` test **both failed** exactly as expected (85 != 80 in one direction,
85 != the shipped 80 curve input in the other). Reverted to 80.0 immediately after confirming; full
suite re-confirmed green (856/856) before continuing.

## Documentation

- `README.md` / `README.zh-Hans.md`: band table rewritten to the four-band semantics (0-30 light,
  30-60 power braking/accelerating toward ideal, 60-80 ideal up to the grip limit, 80-100 past the
  limit/release immediately), plus an honest note that 80 is the measured limit itself (not a value
  seen at every corner) and that early-session peaks may read slightly below 80 while the learner's
  confidence is still building - framed as a temporary cold-start effect that converges, per the
  correction, not as a permanent under-report to be tolerated.
- `docs/architecture.md` / `docs/architecture.zh-Hans.md`: updated per the binding maintenance rule -
  the summary table's "Makes '60'..." fixed to "80" (was already stale before this task, unrelated to
  75/80, fixed while in the area), the five-anchor list renamed `Critical` -> `Max Grip`, the
  "near 75-80" verification note updated to state exactly 80 with a pointer to this report, and the
  `KeyedScaleLearner` bullet extended with the anchor value and the confidence-ramp bug/fix, explicitly
  noting `ColdWarmBlend`/`GripLearner` are untouched.
- `docs/setup/Setup Guide 设置指南.txt`: checked - no reference to the old anchor name or the old
  scale; no change needed.

## Screenshots

Recaptured `docs/images/settings-wheel-lock.png` and `docs/images/settings-wheel-slip.png` using a
fresh offscreen WPF rendering harness (`...\scratchpad\qpreview2\Program.cs` - outside the repo, per
the standing harness rule), following the established capture rule from
`docs/screenshot-capture-rule.md`: content-only (the selected tab's `ScrollViewer` content), measured
at its own full natural extent (`Measure` with `PositiveInfinity` height, then `Arrange` at the
resulting `DesiredSize`) so the WHOLE tab renders in one image with no tab strip/Apply row and no
clipping. A fresh `QAdvanceFeedback` plugin instance's private `_settings` field was set (via
reflection, since `Init()` needs a live SimHub `PluginManager` this harness doesn't have) to a bare
`new QAdvanceFeedbackSettings()`, which is itself field-initialised to `CreateLockDefaults()`/
`CreateSlipDefaults()` - so the capture shows the real shipped defaults, not a hand-built stub.

**Verified by hash, before and after, not assumed:**

| File | Hash before this task | Hash after recapture |
|---|---|---|
| `docs/images/settings-wheel-lock.png` | `259c4b90...` | `48a12118...` |
| `docs/images/settings-wheel-slip.png` | `d85b3c36...` | `c6bd20a2...` |

Both changed. Both images were also visually inspected (not just hash-diffed): each spans the FULL
panel top to bottom - Wheel Lock from "Trigger threshold" through "Pulse at maximum" including the
curve plot (1000x1609px); Wheel Slip the same span (1000x1669px) - nothing cropped, and both correctly
show the renamed "Max Grip:" row with its new description text and unchanged output values (80/80
Lock, 80/75 Slip). Note: a stale, already-regenerated pair of these same two files was found sitting in
the shared scratchpad from an earlier, different session (matching the brief's warning that this has
been gotten wrong twice before) - that pair was NOT trusted or reused; a completely fresh harness,
build, and render was done in an isolated directory, and the hash/visual verification above is against
that fresh output, copied into the actual repo path.

G-Force and General screenshots were left untouched - the rename/rescale does not touch those tabs.

## Do-not-touch confirmed undisturbed

`AggregationWeights.LockDefaults`/`SlipDefaults` (0.75/0.25/0.90/0.10 and 0.85/0.15/0.45/0.55 + 0.70
floor), and every Lock/Slip curve OUTPUT value (`SlightlyOutput`/`ModerateOutput`/`CriticalOutput` for
both channels) - unchanged, confirmed both by direct inspection and by the pre-existing tests that pin
them (`WheelChannelSettingsTests`, `OutputProjectorTests`) still passing unmodified.

## Final status

- **853 -> 856 tests** (3 added: anchor value, anchor/curve-input coincidence, confidence-ramp
  regression), **0 failures, 0 warnings**, single shipped DLL (`QAdvanceFeedback.dll`, net48 - the
  Tests project is a separate net8.0 assembly, not a second copy).
- No git operations of any kind performed (repo is not under Git anyway). No recursive deletes. All
  Chinese text written via Python `\uXXXX` codepoint construction, decoded and read back before being
  applied to any real file.

## Concerns / residual risk

1. **Confidence-ramp fix scope.** The same permanent-plateau-under-dispersion shape exists in
   `GripLearner`'s own use of `ColdWarmBlend` too (it is the same shared class). This task's fix is
   deliberately scoped to `KeyedScaleLearner` only, per the brief's own limits (no calibration rework
   beyond the rescale, don't touch already-tuned G-Force/GripLearner mechanisms). Whether `GripLearner`
   has an analogous, real-world-relevant convergence gap was NOT investigated here and is worth a
   dedicated look in a future task, since it feeds the physical-limit DETECTOR this same rescale
   depends on.
2. **Internal rename left undone.** `Critical`/`MaxGrip` naming is now inconsistent between the
   user-facing label ("Max Grip") and the internal C#/XAML identifiers (`Critical*`) - intentional, per
   the JSON-compatibility reasoning above, but a genuine, lasting inconsistency a future contributor
   should be aware of before assuming the names match.
3. **Off-anchor spread widened slightly.** `PerSourceCalibrationTests`' off-anchor tolerance moved from
   40.0 to 42.0 points - an expected, honestly-documented trade-off of making the anchor point itself
   accurate, not a regression, but worth knowing if that number is ever tightened again without
   remembering why it sits where it does.
4. **Screenshot harness is a scratch, out-of-repo asset**, as established by this project's own
   convention - it is not committed anywhere and would need to be rebuilt from scratch (as this task
   did) for any future regeneration.
