# Slip Angle source units - report (2026-11-20)

## Why

FH6 (log `slip_session-20261120-213529`) gave hard Slip Angle output on small swings. Forza's
`TireSlipAngle*` is not an angle: Forza's Data Out docs define it as a *normalized* slip angle (0 = 100%
grip, |value| > 1.0 = loss of grip). The old unit vote only chose between degrees and radians, settled
on radians, and multiplied the ratio by 57.3. In frames with output >= 90 the plugin read a median 35
degrees while the car's real body angle was 2.6 degrees; the learned Max grew to the 20-degree cap.

## Per-game units (web research)

| Game | Field unit | Evidence |
|---|---|---|
| FM7, Forza Motorsport (2023), FH4, FH5, FH6 | 0..1 grip ratio (goes over 1) | official Forza Data Out docs (high) |
| F1 23 / 24 / 25 | radians | open-source F1 25 tool converts with x 57.3; every stated angle in the spec is radians (medium-high) |
| AC EVO | radians | community copy of the Kunos guide (medium) |
| KartKraft | degrees | official schema "Degrees -180 -> 180" (high) |
| ACC, AC Rally, original AC | not documented, probably radians | (low) |
| Project Motor Racing, Rennsport | not documented, slight radians lean | (low) |
| Wreckfest 2, Bus Bound | unknown | none found |

SimHub cannot tell: `FeedbackCapabilities` has no slip-angle field or unit, its config files hold only
user settings, and the earlier survey of its game readers found a stated unit only for Drift21 and LFS.

## Design (owner-approved)

- **Table first.** A row with a stated unit (Drift21, LFS, the computed rF2 / LMU / NASCAR rows) is used
  as it is.
- **Config lists** (`SlipAngle.SourceUnits` in the plugin config): `ByAngle` (degrees),
  `AngleUsingRatio1`, `AngleUsingRatio100`. Seeded when missing: `ByAngle` = KartKraft;
  `AngleUsingRatio1` = the five Forza games (`ExceedsFullScale: true`); `AngleUsingRatio100` = empty.
  Kept by Restore all defaults.
- **Anything else: radians**, from the first frame (no waiting for a vote).
- **Ratio -> angle (revised 2026-11-22, owner):** 0..1 is x 100; angle = the 0..100 value / 10 (full slip = 10
  degrees, the default Max; was 20% x value = 20 degrees). The learned Max is capped at 22.5 degrees (225 / ratio
  2.25) and the reading at 1.5 x that (33.75 degrees). FH6's lateral G peaks at ratio 1.0-1.1, i.e. 10-11 degrees.
  The fast estimate's ratio guard is 90% of 15 degrees = 13.5 (ratio 1.35; was 90% of full slip, 18 degrees on the
  old scale, which dropped exactly FH6's limit corners). A slip-ratio crossing teaches Max only when the car angle
  at the crossing is at least 5 degrees (`SlipAngleEarlyEstimator.RatioMinCarAngleDeg`), on both learning paths; a
  slip ratio has no car-angle slide guard. On the owner's FH6 log the crossings counted before it were flat-out
  sweepers at 280-420 km/h with the car rotating 1-5 degrees - the G peak came from the road, not the tyres - and
  pulled Max to about 4 degrees; with it 18 of 21 are dropped and Max stays near the 10-degree start. Runtime file
  v16 drops an older file's slip-ratio windows once (`KeyedSlipAngleLearner.DropSource`). A ratio source learns under its own key `SlipAngle.PluginInternal.NativeRatio`, so a
  window learned from the mis-read values is not used. The settings page and the ShakeIt panel key the
  key data the same way (`SettingsHub.SlipAngleIdentity` now takes the game).
- **Body-angle check** (`SlipAngleUnitClassifier`) for unlisted games: r = mean |wheel| x 57.3 / body angle
  over grip-cornering frames (|lateral G| >= 0.8, body angle 0.3-15 degrees, no impact); its median
  decides radians (<= 4), 0..1 ratio (<= 28), degrees (<= 229) or 0..100 ratio. Needs 5 s over 3 corners.
  Measured: F1 25 1.9, FH6 8.1, the F1 values as degrees 109, the FH6 values as 0..100 812 - about 2x
  margin at every edge. A single value >= 180 degrees is not used as a verdict: a real spin can reach it
  in a radians game, and a ratio game's crash spikes do too (FH6 reached 10.0 = 575 "degrees").
- **Over 100%?** (`SlipAngleRatioRangeTracker`): a value above 100.5% means "goes over"; three separate
  reaches of 99.5% with none above mean "stops at 100%". Saved on the list entry.
- **Warnings** (Slip Angle tab and ShakeIt panel): ratio - "Source is a slip ratio (0 -> 100 = grip ->
  full slip). Angle = value / 10 (full slip = 10 degrees)."; car angle - "This game has no tyre slip angle. Using the car's body
  angle." (Chinese texts to match.)

## Drift start for ratio sources

The drift counter only runs while the smoothed value is *above* the start, and the smoothed value never
exceeds a source's ceiling. So:

- **Goes over 100% (or unknown):** start = 1.5 x Max as before, but never above 98% of the 30-degree cap.
  Max 20: 29.4 degrees, reached by a value held at the cap.
- **Stops at 100%:** when 1.5 x Max is past full slip (Max above ~13 degrees), start = 98% of full slip
  (19.6 degrees) and the hold is Min(2 s, Hold time x 6) (1.5 s at the default 0.25 s); otherwise the
  normal start and hold. With Max 20 the start (78) sits under the Max mark (80), so the counter's floor
  is now 75 instead of 80 (`DriftAttenuator.MinDriftStart`); normal sources still start at 80 or above.

Pinned by tests: a value held at the cap drifts with Max 20; a value held at full slip drifts only after
the longer hold.

## Effect on the FH6 log

Raw.All: median 19.2 / p90 74.5 / p99 324 degrees before; 6.7 / 26.0 / 30.0 with the ratio scale. Small
swings now sit below the window's Min instead of past its Max.

## F1 25 learning (log `slipangle_f1.session-20261120-222049`) - not fixed yet

F1's unit was right (radians; per-wheel median 1.2 degrees, body angle 0.75). The window stays at 12.03
because the grip peak is never confirmed: lateral G in an F1 car follows downforce (speed), not slip
angle, so the curve of lateral G against slip angle keeps creeping up (3.0 g at 4 degrees, 3.95 g at 10+)
and the learner, which can only grow the starting window until a peak is confirmed, ends at 10 x 1.203.
The rear-axle curve does peak at about 4 degrees (4.1 g) but gets too few peak events (0-2) to confirm.

Prototypes, replayed offline through the real learner:

| Lateral G normalisation | All | Front | Rear |
|---|---|---|---|
| none (today) | 6.2/9.9/12.3, unconfirmed | unconfirmed | unconfirmed |
| per 20 km/h band, scaled to the overall peak | 4.8/7.6/8.0, confirmed | unconfirmed | unconfirmed |
| same, reference never dropping with speed | unconfirmed | unconfirmed | unconfirmed |
| lateral G / (1 + c v^2), c = 0.000132 / 0.0002 / 0.0003 | unconfirmed / 8.0/8.9/9.3 / 5.3/7.8/8.2 | mixed | unconfirmed |

None is stable across reasonable settings on this 1 min 45 s log (about ten corners), and a wrong
learner change would affect every game, so none is shipped. Next step: a longer F1 log (several clean
laps) to design and validate a speed-aware learner against.

Update 2026-11-21: addressed by the speed-aware lateral G bands - see `slip-angle-speed-bands-report.md`.

Update 2026-11-21 (owner): a fourth list, `AngleUsingRadians` (seeded empty). The body-angle check now records
every verdict, radians included, so it finishes for every unlisted game; a unit the table states is still
respected and never checked. The key data heading names the unit (see `learning-maturity-indicator-report.md`).

## Tests

`SlipAngleSourceUnitsTests` (scale, lists and seeding, Json round trip without duplicates, restore
defaults, the four-way classifier and its evidence rules, the over-100% tracker, the ratio drift start,
drift at the cap and at full slip, the reader's detection and save event, Forza read as a ratio from the
first frame, the ratio learning key in the processor and the settings hub, both warning texts). Updated:
`An_unlisted_unverified_row_reads_radians_from_the_first_frame`, the drift start floor tests.
