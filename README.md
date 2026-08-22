# QAdvanceFeedback

**English** · [简体中文](README.zh-Hans.md)

A SimHub plugin that turns wheel lock, wheel slip and G-force into feel — a **0–100 wheel signal that
means the same thing in every car**, plus eight independent G-force channels for full seat-pad haptic
rigs.

---

## 1. What this plugin gives you

### Reliable Wheel Lock and Wheel Slip

Every car grips differently. A GT car locks a brake at a different pedal feel than an open-wheeler, and
a raw "how much is this wheel slipping" number means something different from one car to the next — 0.4
in one car can be a non-event, and in another it's the moment before the wheel runs away completely.
That inconsistency is exactly what makes a raw slip/lock number unusable for a shaker: you'd have to
re-tune it every time you changed cars.

This plugin fixes that by **learning**, per game and per car, the maximum grip that car actually
achieves under acceleration and under braking. It watches your speed, throttle, brake and G-force over
time, works out where *this* car's real limit sits, and uses that learned peak to re-scale the raw
per-wheel signal (published by default as `QAdvanceFeedback.WheelLock.Raw.*` and
`QAdvanceFeedback.WheelSlip.Raw.*`) into `QAdvanceFeedback.WheelLock.Normalized.*` and
`QAdvanceFeedback.WheelSlip.Normalized.*` — a 0–100 scale whose bands mean the same thing everywhere:

| Range | What it means |
|---|---|
| 0–30 | Below the power threshold — light use of the tyre |
| 30–60 | Power braking / accelerating — working up toward the ideal zone |
| 60–80 | The ideal zone, up to the measured grip limit — higher is faster but progressively riskier |
| 80–100 | Past the limit — the wheel is locking / spinning; release the pedal immediately |

**80 is the measured grip limit** — the maximum braking/traction the tyre can deliver — not a value
you'll see at every apex; it only shows up when a wheel genuinely reaches that car's own learned
ceiling. Early in a session, before the learner has seen enough genuine at-the-limit moments, a peak may
read a little below 80 while its confidence is still building — that's an honest, temporary cold-start
effect, not a target you should expect to hit on lap one. Given a few dozen real braking/traction events
near the limit, peaks converge to 80.

That normalized value is then shaped one more time — through a curve you can edit, or a plain linear
projection if you prefer — into `QAdvanceFeedback.WheelLock.Projected.*` and
`QAdvanceFeedback.WheelSlip.Projected.*`. **This is the property tier you bind to hardware.** The curve
step exists so you can decide exactly how gently or aggressively each part of that 0–100 range should
translate into vibration, independently of how the learning itself works.

The curve has three named set points, each with its own editable raw-input position, output strength,
*and* a **flatten range**: how far either side of that point (in raw input units) the curve stays close
to that point's own output instead of ramping straight through it, forming a brief, near-flat plateau.
Shipped defaults (3 / 2 / 2) put the middle and top plateaus exactly on the shared 60/80 band boundaries,
so the curve reads smoothly through the 60–80 "ideal" zone and ramps sharply everywhere outside it. A
flatten range of 0 turns a set point back into a sharp, un-flattened corner. The curve's **Start** and
**End** rows also have their own editable output values (default 0/100) rather than being fixed —
raising the Start output gives a motor with a dead zone a permanent minimum hum once the channel
engages; lowering the End output caps the strongest cue below full strength.

**Wheel Lock's own section is titled "Output data and shaping"** and starts with a **Normalize Pattern**
selector — Wheel Slip has no equivalent, since it always uses the mapping-style formula:

- **Max-Grip/S90/S75 Mapping** (the default) applies scaling based on the 100%/90%/75% points of
  Max-Grip, each range scaled individually — the 100%/90%/75% normalized outputs are all guaranteed
  reliable. Under this pattern the three set points are labelled **Powerful**, **Perfect** and
  **Max Grip**.
- **Max-Grip Only** applies one general scaling based only on the Max-Grip point — only the Max-Grip
  normalized output is guaranteed reliable under this pattern. The set points are labelled **Slightly**,
  **Ideal** and **Max Grip** instead, matching this plugin's original, simpler curve.

Wheel Slip's own three set points are always labelled **Slightly**, **Ideal** and **Max Grip**.

### Rich G-force for 8-channel haptic pads

Separately from the wheel channels, this plugin computes a full eight-channel G-force feed under
`QAdvanceFeedback.GForce.*`, intended for seat/backrest pads that give a driver a genuine sense of where
braking, acceleration and cornering load is happening on their body:

| Pad position | Where it sits |
|---|---|
| TopBack | Upper back |
| LowBack | Waist / lower back |
| BottomRear | Upper thigh |
| BottomFront | Far thigh |

It's built for full 8-channel haptic seats and pads — the **NextLevelRacing HF-8**, the **Razer Freyja**
(which has 6 channels, so this plugin's 8 need to be mapped down to fit), and the **Sensit Haptics
MTC-P Extreme 2** are all supported.

The feel is an animation, not a static level. Under braking, the sensation travels **LowBack →
BottomRear → BottomFront** — starting at your waist and moving down and forward into your thighs as the
weight shifts forward under the stop. Under acceleration it runs the other way, **BottomRear → LowBack
→ TopBack** — starting under your thighs and pushing you back and up into the seat as the car pulls.
And whenever a wheel actually locks under braking, or spins under power, this plugin superimposes a
left/right alternating shake on top of that G-force feel — the car itself feels like it's wiggling
under you, exactly like it does through the seat of your pants in a real (or very good virtual) car on
the edge of grip.

---

## 2. Quick setup guide

Everything you need to get this running ships in **[`docs/setup/`](docs/setup/)**. There are two paths,
and you only need the advanced one if you want it.

### The simple path

1. Copy `QAdvanceFeedback.dll` into your SimHub folder and restart SimHub.
2. Enable the **QAdvanceFeedback** plugin from SimHub's plugin list.
3. Import **[`QAdvanceFeedback - WheelLockSlip.siprofile`](docs/setup/QAdvanceFeedback%20-%20WheelLockSlip.siprofile)** if you're driving Simagic HPR motors (or any ShakeIt
   motor rig), and/or import whichever profile under **[`docs/setup/G-Force/`](docs/setup/G-Force/)**
   matches your 8-channel pad if you're driving HF-8/SensitHaptics pad (or any ShakeIt BaseShaker rig).

That's it — both wheel lock/slip and G-force work straight out of the box on this plugin's own Manual
source, with no further SimHub configuration required.

### The advanced path

If you'd rather drive Wheel Lock/Wheel Slip from SimHub's own ShakeIt legacy-iRacing calculation instead
of this plugin's built-in one, additionally:

1. Enable the **ShakeIt Motors** plugin and import the same **[`QAdvanceFeedback - WheelLockSlip.siprofile`](docs/setup/QAdvanceFeedback%20-%20WheelLockSlip.siprofile)**
   there — this generates the ShakeIt legacy-iRacing wheel lock/slip properties this plugin can read from.
2. In this plugin's Wheel Lock / Wheel Slip settings, switch the source mode from **"Manual"** to
   **"Use ShakeIt output properties"**.

Everything else — the curve, the pulse, the G-force feel — works exactly the same regardless of which
source mode you pick.

Full step-by-step instructions, including the exact SimHub menu text and screenshots, are in the
shipped setup guide: **[`docs/setup/Setup Guide 设置指南.txt`](docs/setup/Setup%20Guide%20%E8%AE%BE%E7%BD%AE%E6%8C%87%E5%8D%97.txt)**.

---

## 3. Configuring the feel

SimHub's settings panel for this plugin has four tabs: Wheel Lock, Wheel Slip, G-Force and General.
Screenshots of all four are below, followed by short, practical guidance for the two tabs most worth
tuning.

**Wheel Lock**

![The Wheel Lock tab](docs/images/settings-wheel-lock.png)

**Wheel Slip**

![The Wheel Slip tab](docs/images/settings-wheel-slip.png)

**G-Force**

![The G-Force tab](docs/images/settings-gforce.png)

**General**

![The General tab](docs/images/settings-general.png)

### Wheel Lock and Wheel Slip

- Want the vibration to kick in later? **Raise the trigger threshold.**
- Want one axle to matter more than the other? **Adjust the wheel combination weights** — the
  front/rear axle weighting in particular is worth tuning to your car and your own preference.
- Feels too aggressive right around the ideal point? **Lower the Ideal output value**, or **raise its
  raw input value slightly**, so that part of the curve reads a gentler cue.
- Want a longer or shorter near-flat plateau around any of the three set points? **Adjust that row's
  flatten range** — wider feels more forgiving of small pedal-pressure variance right at that point; 0
  makes it a sharp corner again.
- Only care about the Max-Grip point and don't need the 90%/75% ranges scaled individually? **Switch
  Wheel Lock's Normalize Pattern to "Max-Grip Only"** — simpler, but only the Max-Grip normalized output
  is then guaranteed reliable.
- Want a lock-up to feel more urgent than a flat buzz? **Enable "pulse instead of holding flat at
  maximum"** for an ABS-like pulsing feel once you hit full lock/slip.

### G-Force

- If you'd rather not let the plugin learn your car's maximum G automatically, **set the maximum G to a
  fixed value** instead of Auto.
- In Auto mode, the "Auto detected" readout refreshes about once a second while the settings panel is
  open, so it will show real numbers partway through your first lap rather than only the very next time
  you reopen the panel.
- To change how a *held* G-force feels once the animation settles, **adjust the sustain percentages**.
- Want the traveling sensation to feel slower and more deliberate, or snappier? **Lower the sweep
  speed** for a slower animation, or **raise it** for a faster one.
- By default, a right-hand turn vibrates more strongly on the **left** side (the side you're being
  pushed toward). If that reads backwards on your rig, the **lateral feedback direction** setting
  reverses it.
- The wheel lock/slip shake laid on top of the G-force feel can be **disabled entirely**, or tuned
  independently via its **shake frequency** and the **lock/slip scales**.

### General

- The **General** tab shows exactly which version of this plugin's DLL is currently loaded — handy when
  reporting an issue or comparing against these release notes — alongside the diagnostics/CSV export
  toggles and the plugin health panel.

---

## 4. Technical details

The full layer-by-layer design — every subsystem's responsibilities, every source file's role, the
resilience model and the health-registry mechanism that surfaces a degraded subsystem to the driver
instead of hiding it — lives in **[`docs/architecture.md`](docs/architecture.md)** (also available in
[简体中文](docs/architecture.zh-Hans.md)). Read that document before changing any algorithm or file
structure; it carries a standing rule that it must be kept in sync with the code.

At a glance, here is what each subsystem is built on and why:

| Subsystem | Core algorithms / mechanisms | What it's for |
|---|---|---|
| **Wheel Lock Raw** / **Wheel Slip Raw** | Reproduces SimHub's own legacy-iRacing RPM/speed-derived lock and slip formula exactly, dispatched per title across several branch-specific models (wheel rotation rate, wheel-speed delta, pre-calibrated slip, a learned percentile distribution, or a brake+speed+RPM fallback when no per-wheel telemetry exists) selected by capability flags, then combined per wheel-group by a physically-motivated Max/Min axle blend plus a front/rear weighted blend. | The faithful, unnormalized reproduction of SimHub's own well-known algorithm — the common reference point everything else in this plugin builds on. |
| **Wheel Lock/Slip Normalizer** | Re-scales Raw's per-wheel shape against a per-(game, car, source) learned physical-grip reference (a deliberately slow-converging EMA, so a single spike is never mistaken for the car's real limit), cross-calibrated per source via a scale learner anchored to rare, independently-detected "at the physical limit" moments, blended between this session's live evidence and a persisted prior session's value by how *consistent* (not just how numerous) the live evidence has been. | Makes "60" mean the same thing — "right at the limit" — in every car, instead of a number whose meaning drifts with how grippy that particular car happens to be. |
| **Wheel Lock/Slip Projector** | Pushes the normalized 0–100 value through a driver-editable five-anchor curve, smoothed with monotone cubic interpolation so the output can never dip as the input rises, plus an optional pulse-at-maximum stage. | Turns "how severe is this, numerically" into "exactly how this should feel" — the one property tier meant to be bound to a shaker. |
| **G-Force** | A washout-style model that splits a *sustained* G level from a *rate-driven* transient, so a rising G pushes the felt sensation further along a pad chain than the same steady G would; per-game/per-car maxima are learned via a trimmed-pool robust estimator (excludes the top outliers, blends the remaining pool's max and mean) over a real-time rolling window; wheel lock/slip optionally superimposes an alternating left/right shake on top. | Gives a seat pad a continuous, directional sense of braking/accelerating/cornering load, independent of and complementary to the wheel channels. |

### Building from source

```bash
bash tools/fetch-simhub-refs.sh 9.11.22          # extracts reference assemblies into lib/
MSBuild QAdvanceFeedback.sln -t:Restore,Build -p:Configuration=Release
dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj
```

`lib/` is deliberately not committed — those third-party DLLs are extracted fresh from the official
SimHub installer by `tools/fetch-simhub-refs.sh`, targeting SimHub 9.11.22 by default. The algorithm
itself lives in `QAdvanceFeedback/Core/` with no SimHub dependency at all, so it's fully testable on a
machine with no SimHub installed.

**994 tests, 0 build warnings, single-DLL distribution** (`bin\Release\net48\` contains only
`QAdvanceFeedback.dll` and its `.pdb`).

---

## 5. Copyright, licence and references

- **This plugin is an independent, third-party project and is not affiliated with, endorsed by, or
  supported by SimHub or its author, Wotever** (github.com/SHWotever/SimHub).
- **SimHub's reference assemblies are required to build this project but are not redistributed** in this
  repository — `lib/` is git-ignored, and `tools/fetch-simhub-refs.sh` extracts them fresh from the
  official SimHub installer for anyone building from source.
- The Wheel Lock/Wheel Slip Raw algorithm deliberately reproduces the behavior of SimHub's own ShakeIt
  legacy-iRacing wheel-slip effect, for compatibility of feel with an algorithm SimHub users already
  know — studied via the shipped `SimHub.Plugins.dll` to confirm the arithmetic exactly,
  not reverse-engineered from guesswork.
- The G-force model's "washout" design — splitting a sustained level from a rate-driven transient, each
  with its own time constant — is adapted from the classical motion-cuing literature used in
  full-motion simulator rigs. This plugin borrows the *idea* (two filtered paths combined, not a literal
  rig's washout filter implementation) to shape a seat pad's feel rather than to drive an actual motion
  platform.

Licensed under the [MIT License](LICENSE).
