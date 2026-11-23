# Slip Angle fast learning, slide guard and unit dropdown - report (2026-11-21)

## Why

On F1 25 and FH6 the Slip Angle window never confirmed (0-4 crossings in a lap): a crossing only counted at 85% of
the curve's peak lateral G, and the window stayed at its starting value until three consistent corners. The owner's
requirement: learn as fast as Lock / Slip, from the first corners, and keep learning while the speed-aware lateral G
bands are still maturing.

## Design (owner-approved)

`Core/SlipAngle/SlipAngleEarlyEstimator.cs`, owned by every `SlipAngleWindowLearner`, fed by the frame processor's
per-frame `SlipAngleEarlyContext`.

- **Crossings:** the learner's crossing gate, armed at 40% of the speed band's reference lateral G.
- **Weight while the band is not matured** (the owner's tiers on the crossing's lateral G / band peak): >= 85% 1.0,
  75-85% 0.8, 65-75% 0.6, 55-65% 0.3, < 55% 0.1. A band with under 2 s of driving (no reference yet) 0.1. The key's
  first crossing at most 0.1 (owner: "fixed 10%"; a 3 g reference for it was simulated and rejected - see below).
- **Once the band is matured:** 1.0 at 85% of the band's learned mean lateral G, otherwise ignored.
- **Max before confirmation** = the weighted mean of the starting value (one corner's weight) and every crossing;
  Best / Min keep the starting window's shape. Total weight is renormalised at 200 corners (no overflow; later
  corners keep counting). Saved with the learner (`EarlyWeight`, `EarlyWeightedAngle`, `EarlyCrossings`,
  `EarlyGuarded` (long), `EarlyStartMax`), so a restart continues it.
- **Confirmation (green)** is unchanged (3 consistent crossings, a curve not still rising); on confirmation the key
  hands over from the estimate it was publishing, continuously.
- **Indicator (revised the same day, owner: "99% blue, but the value still changing"):** before confirmation all
  three LEDs show the estimate's CONFIDENCE = evidence x stability, at most 99%, tooltip "Learning - 63% confident
  (peak not confirmed yet)":
  - evidence = corners' worth of weight / 10 (3 corners read 99% while each new corner still moved Max by a quarter);
  - stability = how little Max moved over the last 5 taught corners: 1 when the spread (highest - lowest over their
    mean) is at most 3%, 0 at 15% or more, linear between;
  - full evidence with a steady Max latches the LED green ("Fully learned"). Learning never stops (owner: "it will
    keep learning even if it is fully matured... that is the general rule"); only the LED stays green.

### Slide guard

A crossing is ignored (neither the estimate nor the confirmed path learns from it) when:
1. the car's body angle (atan(|lateral| / |forward| velocity)) reached **10 degrees** within the last second - a slide,
   drift or spin, whatever the source; or
2. the source is a confirmed slip ratio (0-1 or 0-100) and the crossing, or either axle during it, reached **90% of full
   slip** (90 on 0-100 = 18 degrees). The owner's "either axle" was the decision left to us: it caught FH6's opening
   fishtail, which the learned axle alone (17 degrees) missed.

While a speed band is not matured, sliding frames do not teach its lateral G either.

Measured on the logs: grip corners 1.2-4.4 degrees body angle (F1), 1.2-7.3 (FH6); every slide 10.3-27.4.

## Results (the shipped code replayed on the logs; starting Max 10)

| Log | Max after 1st / 2nd / 3rd / 6th / 10th crossing | Guarded | End of lap |
|---|---|---|---|
| F1 All | 9.3 / 8.8 / 8.3 / 7.9 / 7.2 | 1 | 3.7 / 5.9 / 7.3 |
| F1 Front | 9.2 / 8.6 / 8.5 / 6.4 / 6.5 | 2 | 3.9 / 6.2 / 7.8 |
| F1 Rear | 9.4 / 8.8 / 8.3 / 7.5 / 5.1 | 2 | 2.3 / 3.7 / 4.6 |
| FH6 All | 9.4 / 9.3 / 7.2 / 8.2 / 8.7 | 6 | 4.4 / 7.1 / 8.9 |
| FH6 Front | 9.3 / 9.3 / 7.2 / 6.6 / 6.5 | 10 | 3.7 / 5.9 / 7.3 |
| FH6 Rear | 9.8 / 9.6 / 7.6 / 8.8 / 9.8 | 10 | 4.6 / 7.3 / 9.2 |

Before: no confirmation and the window at 10 / 16 / 20 (FH6) or the starting 5 / 8 / 10. F1 repeated three times
confirms at 159 s and hands over to 4.9 / 7.8 / 8.0.

**First corner: 3 g reference vs fixed 10%.** With the guard, the 3 g reference gave F1 nothing (its first corners are
light kinks, 10% either way) and gave FH6 a 60-100% first corner: Max 7.2-7.4 (All / Front) or 13.5 (Rear, when the
fishtail still slipped through) after one corner, ending within 0.5 degrees of the fixed 10% run. Fixed 10% chosen.

## Unit dropdown

**Revised 2026-11-22 (owner): the dropdown always shows** under the Slip Angle key data heading (tab and ShakeIt
panel) for Plugin Internal with a game, because the car angle is always available. Its entries depend on the game
(`SettingsHub.SlipAngleUnitOptions`):

| Game | Entries | Selected by default | Restore default |
|---|---|---|---|
| Unit hard-coded (the table states it: LFS, Drift21, the computed rows; or shipped in the lists: KartKraft, the five Forza games) | that unit ("- this game's known unit"), Car angle | that unit | back to that unit (a shipped game goes back into its list) |
| Any other game with a tyre field | **Auto-detect** (top), Degrees, Radians, Slip ratio 0-1, Slip ratio 0-100, Car angle | Auto-detect | back to Auto-detect: out of every list, checked again while driving |
| No tyre field | Car angle | Car angle | never shown |

- **Restore default** (was Reset) shows only when the game is not at its default and hides again after it; the
  dropdown stays. Picking the hard-coded unit, or Auto-detect, is the same as Restore default.
- **Car angle** puts the game in `UseCarAngle` (config `SlipAngle.SourceUnits`), hard-coded games included: the game's
  tyre field is not read at all and the car-angle estimate is learned and output, under the car-angle key and heading.
- **Detected vs picked.** A unit found by the check still shows as "Auto-detect (found: Radians)" with no Restore
  default; only a driver's pick is shown as picked. Picks are recorded in the new config list `ChosenByUser`; a game
  in `UseCarAngle` always counts as picked. (Before, a detected unit and a pick looked the same.)
- A pick writes the hub field `SettingField.SlipAngleSourceUnit` (saved at once, broadcast to both views); the field
  reads the selected entry and whether it is the default.
- **The lists are authoritative:** a listed game is read as its list says, even when the table states a unit. An
  unlisted game keeps the table's unit, else is read as radians while checked.

## New log columns

`Diag.SlipAngle.Sliding`, `Diag.SlipAngle.EarlyWeight`, `Diag.SlipAngle.EarlyGuarded` (with `Crossings`,
`PeakConfirmed`, `CurvePeakG`, `Identity` from earlier today).

## Persistence

Additive fields only - `SlipAngleLearnerState.Early*`, `SlipAngleSpeedBandState.ReferenceSeconds` (seconds driven in
the band, capped at 3600), the `UseCarAngle` / `AngleUsingRadians` config lists. An older file or config loads with
them empty: the estimate starts fresh, a band counts as young until 2 s of driving. The last five Max values are
saved as their count, lowest, highest and mean (`EarlyRecent*`), which is all the stability term reads - see
`parameters-file-format-report.md`.

## Tests

`SlipAngleFastLearningTests` (tiers, first crossing, young band, matured rule, the two guards, renormalisation,
save / restore, the learner publishing the estimate and keeping its shape, the old behaviour without context, band
age), `SlipAngleUnitChoiceTests` (each list, lists over the table, car angle reads no tyre value and keys / heads as
car angle; since 2026-11-22: the entries per game, always shown, hard-coded game to car angle and back, Auto-detect
vs a pick, Restore default, `ChosenByUser` in the config, hub write / save / broadcast / refusal, tab and panel). Updated: two
unit tests that pinned "the table wins", the hub field list, the indicator tooltip test.

## Revisions (2026-11-22, owner)

- **The LED only rises.** The confidence is still evidence x stability, but a corner that knocks the stability to 0
  uses the last stability above 0 (`SlipAngleEarlyEstimator.EffectiveStability`), and the learner keeps a high-water
  mark (`SlipAngleWindowLearner.MaxMaturity` never falls below it). Both are saved (`EarlyValidStability`,
  `MaxMaturityFloor`), so a restart does not drop it either.
- **Slip-ratio sources:** scale 10 points per degree (full slip 10 degrees), Max cap 22.5, ratio guard 13.5 degrees,
  and a crossing teaches only with the car angle at 5 degrees or more - see `slip-angle-source-units-report.md`.
