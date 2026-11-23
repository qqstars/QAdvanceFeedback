# Slip Angle speed-aware lateral G - report (2026-11-21)

## Why

F1 25 (log `slipangle_f1.session-20261120-222049`) never confirmed a Slip Angle window: the learned window
stayed at the starting 10 degrees grown to 12.03. In a downforce car lateral G follows speed (2.3 g at 60 km/h,
4.0 g at 220 km/h at the limit), so the learner's lateral-G-by-angle curve keeps rising, its "85% of peak G"
crossing bar is set by the fast corners only, and confirmation is blocked.

Braking G gives a speed curve too, but at high speed F1 braking is limited by the brakes, not grip; its fitted
c = k / gMech = 0.000106 per (m/s)^2 is half what cornering needs (about 0.00024) and left learning
unconfirmed, so the lateral curve is learned on its own.

## Design (owner-approved)

`Core/SlipAngle/SlipAngleSpeedBands.cs`, owned by `KeyedSlipAngleLearner.SpeedBands`, fed by
`SlipAngleFrameProcessor`.

- **Bands:** 40 km/h, from 30 km/h; band 8 is 320 km/h and above. Keyed per game, car, source and surface and
  learned on every surface (owner, 2026-11-22; was per game and car, sealed only), never borrowed
  (`SlipAngleBorrowRules.SpeedBandScope`). Bands saved per game and car move to the car's Sealed key for the
  Plugin Internal source on first use. Why never borrowed: another surface's G levels would keep this surface's
  bands from ever learning (gravel rarely reaches 85% of tarmac's band peak), and replaying the F1 / FH6 logs with
  fully learned bands from the start gave no faster learning (LED at the 20th crossing: F1 99% vs 98%, FH6 61% vs
  53%; the crossings arrive at the same moments).
- **Crossings:** the learner's own `CrossingGate` (`CrossingGateParameters.SlipAngle`) on Plugin Internal's
  All angle, "at the limit" = lateral G at least 85% of the band's peak. The crossing's lateral G is the
  gate's walked-back G peak. It is applied after 5 more learnable frames (an impact or excluded frame drops it).
- **Band peak:** the lowest lateral G over 10 consecutive frames in the band (1/6 s), decaying 1/600 per second,
  never below the band's mean. On the F1 log it matches each band's 98th percentile (160-200 km/h: 3.75 g vs
  a 3.67 g p98); 3 frames let a 4.38 g spike through and blocked that band's real corners.
- **Acceptance (owner's staged rule):** the first crossing sets the mean (not counted). Then accepted within
  45% of the mean while none is counted, 30% once 1-2 are, 15% once matured (3 counted); counted when within
  15%. Below the band: dropped (not at the limit). Above it: a challenger; a second within 15% of it replaces
  the band (mean and count from the challengers).
- **Mean:** plain average for the first 20 crossings, then each new one weighs 1/20 (follows tyre wear, track
  rubber, setup). Stored as `MeanG` (double) + `SampleCount` (long).
- **Scale:** lateral G x (highest matured band mean / mean at this speed), piecewise linear between matured
  band centres, flat outside them, in [1, 2.5]. Applied to what the slip-angle learners and the surface
  classifier see; the impact guard and the bands themselves keep the raw value. **Matured bands only** - in
  the first replay a provisional seed (one light corner, 1.62 g in the 160 km/h band) scaled that speed by
  2.48 and cut crossings from 4 to 2. It is 1 until a band matures: a cold car and a car without downforce
  behave as before.
- **Same pace:** the scale (and so the F1 confirmation) waits for matured bands - about three laps, the same as
  the slip-angle confirmation's 3 consistent crossings.

## Persistence

Runtime file version 13, new section `SlipAngleSpeedBands` (`game|#|car|#|` -> bands), saved with the window
learners (`KeyedSlipAngleLearner.SpeedBandSave` -> `RuntimeStore.SaveSlipAngleSpeedBands`), loaded with them.
Per band: `LowKmh` (int), `MeanG`, `SampleCount` (long), `CountedCount` (long), `ChallengerMeanG`,
`ChallengerCount` (long), `PeakG`. An older file has no section and starts empty. New log column
`Diag.SlipAngle.SpeedScale`.

## Replay (F1 log through the real bands and window learner)

The log is about one lap, so it was also repeated 3 and 5 times; identical laps give identical crossings,
which flatters both versions.

| Repeats | Today (All / Front) | With speed bands (All / Front) |
|---|---|---|
| x1 | unconfirmed / unconfirmed | the same (no band matured) |
| x3 | confirmed 158 s, 8 crossings / confirmed 158 s | confirmed 158 s, 12 crossings, quality 0.93 / confirmed 140 s, 15 crossings |
| x5 | window drifts to 4.6/6.3/7.2, quality 0.16 / 8.0/8.5/9.2 | 7.9/8.9/9.2, quality 0.88 / 7.8/9.6/9.9, 27 crossings |

Learned bands after x5: 80-120 km/h 2.36 g, 120-160 2.91 g, 160-200 3.16 g, 200-240 3.90 g, 240-280 3.33 g.
Rear confirmed at 389 s on 2.2/2.7/2.9 degrees from three identical replays of one corner - an artefact of the
repetition, not evidence; a real multi-lap log is still needed to check the rear axle.

## Tests

`SlipAngleSpeedBandsTests`: band edges; the staged acceptance (seed, 45/30/15%, counting, maturing, lower
dropped, challenger replace / restart); the 20-crossing mean; the scale (cold, unknown car, interpolation,
matured only, cap); crossings from frames (taught, dropped by an excluded frame or an impact, below 85% of the
peak, a short spike never raises the peak); persistence (export / import / Json with counts above
int.MaxValue, an older file, bad bands skipped, saved by the keyed learner); the processor scaling and
reporting `SpeedScale`. `SlipAnglePublishedPropertiesTests` gains the new column.
