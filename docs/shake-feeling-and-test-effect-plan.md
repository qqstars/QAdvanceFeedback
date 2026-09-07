# Shake feeling, the L/R preview graph, and the Test Effect panel

**Status: ALL PARTS DONE.** Agreed 2026-09-06, completed 2026-09-06. 1338 tests passing, plugin builds with 0 warnings, all four screenshots regenerated.

---

## Part 1 - the wave and its plumbing (DONE, 1336 tests passing)

`ShakeFeeling.cs` (new) and `GForceShake.SineHoldWave` / `FeelingPair` / `EffectiveHold` replace the
trapezoid-plus-blend model.

**One cycle**, for a channel starting at its MAXIMUM, as a fraction of the period:

```
[0,        h/4)             held at MAX
[h/4,      h/4 + (1-h)/2)   half a cosine, MAX -> MIN
[...,      ... + h/2)       held at MIN
[...,      ... + (1-h)/2)   half a cosine, MIN -> MAX
[1 - h/4,  1)               held at MAX
```

**The period is always 1/f.** The holds are a share OF the cycle, never an addition to it. This was the
one point where the owner's worked numbers and their stated period disagreed - their breakdown
(15/35/30/35/15 ms) summed to 130 ms for a "100 ms" cycle - and they chose to keep the period, so the
holds became 7.5/35/15/35/7.5. The sine portion is still exactly `(1-h)` of the cycle as specified, and
"10 Hz means ten Max-Min-Max travels per second" keeps holding at every hold value.

Verified against the owner's own example: at 10 Hz / hold 30% the boundaries land at 7.5, 42.5, 57.5,
92.5 ms; at hold 0 the wave equals `(1 + cos 2*pi*t)/2` to nine decimals.

**The three feelings**, and what each does to the pair:

| feeling | relationship | pair total `L+R` | shipped frequency |
| --- | --- | --- | --- |
| **Opposite Phase** (default) | half a cycle apart | very nearly CONSTANT | 10 Hz |
| **Same Phase** | identical | swings the full 0..2 | 10 Hz |
| **Blending** | follower offset by the leader's opening hold (`h/4`, so `T/8` at its fixed 50% hold) | swings 0..2, and it pans | 5 Hz |

Blending pins its own hold and ignores the setting - which is why the UI hides that control for it.

### What was retired

`EffectiveSustain`, `PeakExcursionFactor`, `Wave`, `QuadratureWave`, `MaxPanPhaseSeconds`,
`NormalizedPair`, and the whole pan/common decomposition. `PadRange` no longer shrinks the half-band
(the wave spans a full 0..1 for every feeling, so a pad's travel IS the band - the old factor is why a
nominal band of 70 only ever swung 35 wide). `PhaseForCorner` now SEARCHES one cycle for the phase
closest to the requested corner, because the sine+hold model has no closed form: where "both pads at
minimum" occurs depends on the feeling AND the hold.

`GForceShakeBlendTests` was deleted and replaced by `GForceShakeFeelingTests`;
`GForceShakeWaveTests` was rewritten for the new shape. `ShakeBlendPercent` survives only as a
persistence shim and no longer reaches the wave.

---

## Part 2 - the shake settings UI (DONE)

Replace the "Both-sides blend (%)" spinner with a **"Shake feeling"** dropdown (Opposite Phase / Same
Phase / Blending), placed AFTER "Shake applies to" and its description, BEFORE the spinners. Each
selection shows its own one-line description, and switching it OVERWRITES the frequency via
`GForceSettings.DefaultShakeFrequencyFor` (10 / 10 / 5 Hz).

Reorder so **Shake frequency** is the first spinner. **Hold at min/max** appears after the feeling's
description and before the frequency, and is HIDDEN entirely for Blending.

Lay the section out as a two-column grid: settings stacked on the left, the preview graph on the right.

## Part 3 - the L/R preview graph (DONE)

Right-hand column of that grid. Same WIDTH as the Lock/Slip curve canvas (**420**), height matching the
settings column. Shows **one second** of simulated output: **green = Left, red = Right**.

Confirmed against the real Lock/Slip plot rather than assumed:

| property | value |
| --- | --- |
| tick height | **5 px** (`HorizontalTickHeight`) - the owner's recalled "8px" was wrong |
| curve stroke | **2 px** |
| axis/gridline stroke | 1 px |
| canvas | 420 x 160, white |

Margins: 10 px top and right; bottom deep enough for the labels plus 3 px under them. Vertical
gridlines every 200 ms with the ms written under the axis - label **0, 200, 400, 600, 800 and NOT
1000**. The 0 ms vertical runs 5 px beyond the 100% line, with **100%** written to its left at the
exact 100% height.

The drawn curve must sit FULLY between the 0 line and the (invisible) 100% line - offset by half the
stroke so it never overlaps the axis.

## Part 4 - the Test Effect panel (DONE)

A new section at the BOTTOM of the G-Force tab.

- A toggle in the same style as "Integrate Wheel Lock and Slip", **off by default**.
- **While off, the panel shows nothing at all and generates no simulation data.** Every control and
  label below appears only when it is on.
- A frequency box under the toggle, **default 60 Hz**, setting how often simulated frames are pushed.

**Two drag targets**, per the owner's mock-up:

- **G-Force**: a styled rectangle with a horizontal and vertical axis crossing at its centre, and a
  filled round ball that drags anywhere inside. `L` at the left of the horizontal axis, `R` at the
  right; small `Acceleration` and `Deceleration` beside the vertical.
- **Lock/Slip**: a styled rounded-rectangle bar with the same ball, dragging along it. Output 0..100.

The ball must never leave its boundary. It **re-centres automatically** when dropped inside a deadzone
the size of its own footprint: for a 400 px box and a 20 px ball, an axis reading under
`(20/2)/(400/2)*100 = 5` on BOTH axes snaps back to 0,0. The handle may be larger than the drawn ball.

The G-Force ball's position is a PERCENTAGE of the configured maxima - pushed fully up it emits 100% of
the acceleration maximum. The **Lock/Slip scales apply to the simulated values too**.

**Readout under the controls**, label column right-aligned and value column left-aligned:

```
Acceleration G-Force:  xxxG (xx.x%)      <- "Deceleration" when the ball is below centre
         Lat G-Force:  xxxG (xx.x%)
     Wheel Slip/Lock:  xxx
      Channel outputs:
               Front:  xxx / xxx
                Rear:  xxx / xxx
                 Low:  xxx / xxx
                 Top:  xxx / xxx
```

## Part 5 - docs and screenshots (DONE)

README (both languages) and `architecture.md` (both languages) rewritten for the feeling dropdown, the
retired blend, and the Test Effect panel; all four screenshots regenerated (harness rebuilt first, since
it keeps its own copy of the plugin DLL).

### Default and reset decisions settled after the screenshot pass

The rendered G-Force tab prompted a review of the shake defaults. Outcome, all owner-decided:

1. **Picking a feeling DOES set the frequency** - 10 Hz for the two phase-locked feelings, 5 Hz for
   Blending, overriding a hand-tuned value. This is the owner's explicit 2026-09-06 instruction ("set
   the frequency as 10HZ (Even the user override to their own frequency value) ... if enabled
   'Blending' ... set the Shake Frequency as 5HZ instead"). It was briefly removed on a mistaken
   reading that it was unrequested, and is restored. It does NOT conflict with the shipped 5 Hz: the
   reset fires from SelectionChanged only, so a fresh install that never touches the dropdown keeps 5.
2. **The hold does NOT vary per feeling.** The two phase-locked feelings share the one configured value;
   Blending pins `BlendingHoldFraction` (50%) and the UI hides the spinner, so there is no per-feeling
   hold default to hand the driver.
3. **The shipped hold default is now 30**, down from 40 - the owner's own number, and the one their
   worked example of the sine+hold shape used.
4. **Switching mode NO LONGER rewrites the two scales**, and both ship at 1.5 in every mode (owner:
   "changing the mode will not impact the scale, and the scale will be applied for slip and lock
   individually"). `DefaultShakeScaleFor` is removed. The reasoning it encoded was wrong:
   `contribution = scale x wheel/100` is computed identically in all four modes, and the Lock and Slip
   scales already drive their own channels individually. Only what the contribution MULTIPLIES differs -
   the pad's own level in the two G-force modes, the full 0-100 range in the two wheel-driven ones - so
   at 1.5 a wheel-driven mode saturates from wheel 67 up. A saturation point, not a change of meaning,
   and not grounds for overwriting a hand-tuned value.
5. **The retired blend's leftovers are removed.** `GForceSettings.ShakeBlendPercent`,
   `GForceEngine.ShakeBlend` and `GForceShake.DefaultBlend` had survived as write-only state - ApplyTo
   kept pushing the value into an engine property nothing read.

Also fixed in passing: a stale doc comment from the deleted `EffectiveSustain` had been left attached
to the `ShakeStartCorner` enum, documenting a blend formula that no longer exists.
