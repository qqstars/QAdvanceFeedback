# QAdvanceFeedback

**English** · [简体中文](README.zh-Hans.md)

A SimHub plugin that turns wheel lock and wheel slip into a **0–100 signal that means the same thing in every car**, plus eight G-force channels for seat-pad cueing — built for ShakeIt Bass Shakers and ShakeIt Motors.

---

## Why this exists

Raw longitudinal wheel slip has a physical problem no amount of rescaling fixes: **past the peak of the tyre's grip curve there is no equilibrium.** Below the peak, more slip produces more braking (or driving) force and the wheel settles. Above it, more slip produces *less* force, which produces more slip — the wheel runs away and reaches full lock (or full spin) in roughly **100–300 ms**. So the raw signal sits around 0.1–0.2, telling you nothing is wrong, and then jumps to "fully locked" with nothing usable in between. By the time it moves, it is too late to react.

SimHub's own ShakeIt plugins already ship a legacy-iRacing wheel lock/slip effect (`WheelSlipEffect`) that reproduces exactly this cliff — it is a faithful, well-established algorithm, but it is **unnormalised**: its output scale depends on the car, the surface and the conditions, so a reading of "0.4" means something completely different in a GT car than in an open-wheeler. There is no way to bind it to a shaker and get a consistent feel across your whole garage.

**This plugin reproduces SimHub's own legacy-iRacing algorithm exactly** (Layer 3, below — the same RPM/speed-derived lock and slip computation, verified against SimHub's own decompiled effect code) **and then normalises and shapes it** into a 0–100 signal whose bands mean the same thing in every car: **0–30 is light, with margin to spare; 30–60 is the ideal working range — your best performance, hold it here; 60–80 means the tyre is starting to lock (or spin) — not there yet, but very close, ease off now; 100 is fully locked or fully spinning** — always, regardless of what car you're in. On top of that it adds eight G-force channels so a seat pad can cue braking, acceleration and cornering load directly, independently of the wheel channels.

### Did we consider reading ShakeIt's own internal values instead of recomputing them?

Only the fully-internal effect state — yes, and confirmed by decompilation that it is not possible: neither `ShakeITBSV3Plugin` nor `ShakeITMotorsV3Plugin` exposes that internal state as a SimHub property on its own. **SimHub's ShakeIt Motors effects DO have an explicit, opt-in "export output value as a property" option**, however (see "Using ShakeIt Motors as a source" below) — this was the earlier, incorrect finding in this document, corrected once the export path was actually decompiled and confirmed working. Layer 4 ("Normalized," below) still keeps its own `Raw.*` properties as the shipped default input, since the exported property's exact name depends on which ShakeIt plugin variant you enabled and what you typed into its "Property name" field — there is no fixed name this plugin could safely guess by default.

---

## The five-layer architecture

Telemetry flows through five layers, each published independently so you can bind whichever stage suits your rig:

| Layer | Published prefix | What it is |
|---|---|---|
| 1 | *(internal)* | Game-agnostic telemetry interface — the shape every game's own reader normalises into. |
| 2 | *(internal)* | The SimHub telemetry adapter — reads a real `GameData` frame into Layer 1's shape. |
| 3 — **Raw** | `WheelLock.Raw.*` / `WheelSlip.Raw.*` | SimHub's own legacy-iRacing RPM/speed algorithm, reproduced exactly, scaled to 0–100 and clamped, per wheel. `Front`/`Rear`/`Left`/`Right`/`All` are then combined from those four per-wheel values by a configurable, physically-motivated weight-transfer scheme (see "Wheel combination" below) — Normalized/Projected inherit the same scheme (each recomputes it on its own per-wheel output, rather than reusing Raw's own aggregate numbers). Unnormalised by design — this is the faithful reproduction, not the fix. Its brake/throttle pedal-pressed thresholds are configurable (see "Pedal thresholds" below) — a deliberate departure from SimHub's own hard-coded values. |
| 4 — **Normalized** | `WheelLock.Normalized.*` / `WheelSlip.Normalized.*` | Combines Raw's per-wheel *shape* with a car-relative severity learned from speed/throttle/brake/G alone (no per-wheel slip data, no third-party plugins) — this is what makes the bands mean the same thing in an arcade car pulling 4g as in a sim car pulling 1.2g. (Named for what this layer does — normalise against the learned per-car maximum — not a claim about reliability.) |
| 5 — **Projected** | `WheelLock.Projected.*` / `WheelSlip.Projected.*` | Normalized's output pushed through your own editable curve (Start/Slightly/Ideal/Critical/End anchors) and, optionally, a pulse at maximum instead of holding flat. **Bind this one** for the shaped, tunable feel. (Named after the curve-editor's own "output projector" mechanism.) |

> **Renamed from "Reliable"/"Final"** to "Normalized"/"Projected" (this plugin has not shipped or been bound in a live SimHub session yet, so this was a free rename, not a breaking change to any existing install — but if you had already typed the old property names into a ShakeIt custom effect, **you will need to rebind to the new names** shown above).

Each tier publishes the same nine per-wheel/per-group targets: `FrontLeft`, `FrontRight`, `RearLeft`, `RearRight`, `Front`, `Rear`, `Left`, `Right`, `All` — 9 targets × 3 tiers × 2 channels (Lock, Slip) = **54 properties**, always published.

### G-force channels (8 properties, always published)

Independent of the wheel channels — modelled on classical washout/motion-cueing rigs (see "References and copyright" below), separating the STEADY level you're holding from the MOTION of getting there:

```
QAdvanceFeedback.GForce.Bottom.FrontLeft   .FrontRight   .RearLeft   .RearRight
QAdvanceFeedback.GForce.Back.LowLeft       .LowRight     .TopLeft    .TopRight
```

### Complete published property list

**Product properties (62, always published):**

```
QAdvanceFeedback.WheelLock.Raw.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}
QAdvanceFeedback.WheelSlip.Raw.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}
QAdvanceFeedback.WheelLock.Normalized.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}
QAdvanceFeedback.WheelSlip.Normalized.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}
QAdvanceFeedback.WheelLock.Projected.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}
QAdvanceFeedback.WheelSlip.Projected.{FrontLeft,FrontRight,RearLeft,RearRight,Front,Rear,Left,Right,All}
QAdvanceFeedback.GForce.Bottom.{FrontLeft,FrontRight,RearLeft,RearRight}
QAdvanceFeedback.GForce.Back.{LowLeft,LowRight,TopLeft,TopRight}
```

**Diagnostic properties (9, only when "Enable diagnostics" is on — requires a SimHub restart):**

```
QAdvanceFeedback.Diag.Direction                    Unknown | Slowing | SpeedingUp
QAdvanceFeedback.Diag.MotionLevel                   which degradation tier is live (Direct | Derived | Unavailable)
QAdvanceFeedback.Diag.MotionMagnitudeG              the g-magnitude Layer 4 is working from
QAdvanceFeedback.Diag.Lock.LearnedPeakG             this game+car's learned braking peak (g)
QAdvanceFeedback.Diag.Lock.LearnerConfidence        0..1
QAdvanceFeedback.Diag.Slip.LearnedPeakG             this game+car's learned acceleration/traction peak (g)
QAdvanceFeedback.Diag.Slip.LearnerConfidence         0..1
QAdvanceFeedback.Diag.GForce.LearnedAccelMaxG       this game+car's AUTO-learned acceleration max (g)
QAdvanceFeedback.Diag.GForce.LearnedDecelMaxG       this game+car's AUTO-learned braking max (g)
```

All 71 names are on a **0–100 scale** (the diagnostics above are the exception where noted — g-values and confidence are not 0–100).

---

## How to use it in SimHub

### Install

1. Copy **`QAdvanceFeedback.dll`** into your SimHub folder (the one containing `SimHubWPF.exe`).
2. Start SimHub and approve the plugin when prompted.
3. Enable it in the plugins list.

One file. Nothing else to copy, no dependencies to install.

### Binding it in ShakeIt Bass Shakers / ShakeIt Motors

**SimHub's built-in ShakeIt effects cannot read a plugin's properties.** You must add a **Custom effect** — that is the one effect type in both ShakeIt Bass Shakers and ShakeIt Motors that lets you bind to an arbitrary property by name.

In either ShakeIt editor: add a **Custom effect**, then point it at the property you want, e.g. `QAdvanceFeedback.WheelLock.Projected.All` or `QAdvanceFeedback.WheelSlip.Projected.FrontLeft`.

**These properties are on a 0–100 scale.** Configure the custom effect for that range — if it expects 0–1 instead, everything above 1 saturates and the shaker sits at full strength the moment you touch a pedal.

### Suggested bindings by rig type

| Rig | Property |
|---|---|
| Single bass shaker / seat | `QAdvanceFeedback.WheelLock.Projected.All` and/or `QAdvanceFeedback.WheelSlip.Projected.All` |
| Four corner motors | `QAdvanceFeedback.WheelLock.Projected.FrontLeft` / `.FrontRight` / `.RearLeft` / `.RearRight` |
| Two channels, front/rear | `QAdvanceFeedback.WheelLock.Projected.Front` / `.Rear` |
| Two channels, left/right | `QAdvanceFeedback.WheelLock.Projected.Left` / `.Right` |

Bind the Wheel Slip channel the same way, under `QAdvanceFeedback.WheelSlip.Projected.*`, on its own custom effects so the two channels can be tuned to different motors, or the same ones, independently.

### Source mode: ShakeIt Plugin Output Properties (default) vs. Manual

Each of Wheel Lock/Wheel Slip's four wheel **sources** (Layer 4's input, not to be confused with the Layer 5 binding above) has a toggle at the very TOP of that tab's Sources section: **"ShakeIt Plugin Output Properties"** (the shipped default, both channels) vs. **"Manual"** (this plugin's own `Raw.*` properties, or anything else you type in). The toggle is **always visible and switchable**, regardless of whether SimHub is currently reporting the ShakeIt export — if it is not (yet), the tab shows an inline note saying so, plus the setup steps below, rather than hiding the option.

When ShakeIt mode is selected, this reads the four wheels straight from SimHub's own ShakeIt Motors "Wheels lock"/"Wheels slip" effect:

1. Enable the **ShakeIt Motors** plugin (SimHub Settings → Additional Plugins) and create a profile.
2. Add a **Wheels lock** effect (Wheel Lock tab) / **Wheels slip** effect (Wheel Slip tab) — group "Slip and lock".
3. Tick **"Use legacy IRacing algorythm"** (SimHub's own spelling) so it matches this plugin's own Layer 3.
4. Tick **"Export output value as a property"**, and set **"Property name"** to exactly `WheelLock.IRacing` (Wheel Lock) or `WheelSlip.IRacing` (Wheel Slip) — this plugin's own toggle expects those exact names, confirmed against SimHub's own decompiled naming (`<plugin type>.Export.<property name>.<placement>`):
   ```
   ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft   .FrontRight   .RearLeft   .RearRight
   ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft   .FrontRight   .RearLeft   .RearRight
   ```
5. Tick **"Disable output"** on that same effect's output row so it drives no hardware — it keeps computing and exporting the property either way.

Until SimHub reports all four wheels for a channel, that channel automatically falls back to its own `Raw.*` values every frame (never reads a missing property as 0), so nothing goes dead while you set this up.

When **Manual** is selected instead, the setup steps above are hidden and a short note reminds you: each wheel's Source field must supply its own 0–100 value (0 = no lock/slip, 100 = fully locked/spinning). The per-channel **"Reset to default"** button (under the four fields) always restores whichever mode is CURRENTLY selected — ShakeIt mode restores the four ShakeIt names above, Manual mode restores this plugin's own `Raw.*` names — it never silently switches you to the other mode.

Full, decompilation-verified walkthrough (including screenshots-worth of exact SimHub label text): `docs/shakeit-export-guide.md`.

### Pedal thresholds (owner-configurable, deliberately deviates from SimHub's own hard-coded values)

Both Wheel Lock and Wheel Slip gate on pedal position before the underlying algorithm engages — this applies in both Manual and ShakeIt source modes, since it gates Layer 3 itself:

| Setting | Default | What it does |
|---|---|---|
| Wheel Lock — brake threshold | **20%** | The wheel is only treated as locking once the brake pedal exceeds this. |
| Wheel Slip — brake threshold | **100%** | Checked *first* — if exceeded, Slip reads the same braking value Lock does (this mirrors SimHub's own undivided algorithm, which does not distinguish Lock from Slip internally). Shipped at 100% deliberately: the pedal cannot exceed 100%, so this branch never fires by default and Slip is throttle-only, as intended. |
| Wheel Slip — throttle threshold | **40%** | Only checked if the brake threshold above was not exceeded. Also requires the clutch pedal to be under 5% (SimHub's own fixed rule, not configurable). |

---

### 8-channel seat pad (G-force)

The eight `GForce.Bottom.*` / `GForce.Back.*` properties map directly onto an 8-pad seat/back rig laid out as bottom-cushion (front/rear × left/right) and backrest (low/top × left/right):

| Pad | Property | What lights it up |
|---|---|---|
| Bottom, front-left | `QAdvanceFeedback.GForce.Bottom.FrontLeft` | Hard braking (front of the seat pan, where weight shifts forward under braking) |
| Bottom, front-right | `QAdvanceFeedback.GForce.Bottom.FrontRight` | Hard braking |
| Bottom, rear-left | `QAdvanceFeedback.GForce.Bottom.RearLeft` | Shared zone: mid-braking and mid-acceleration both pass through here |
| Bottom, rear-right | `QAdvanceFeedback.GForce.Bottom.RearRight` | Shared zone |
| Back, low-left | `QAdvanceFeedback.GForce.Back.LowLeft` | Shared zone: braking's start and acceleration's mid-point |
| Back, low-right | `QAdvanceFeedback.GForce.Back.LowRight` | Shared zone |
| Back, top-left | `QAdvanceFeedback.GForce.Back.TopLeft` | Sustained hard acceleration (pushed into the top of the backrest) |
| Back, top-right | `QAdvanceFeedback.GForce.Back.TopRight` | Sustained hard acceleration |

Left/right pairs are biased by lateral G (see "General" below for the direction toggle) — the loaded side under cornering reads up to 1.5× its unbiased value, the unloaded side down to 0.5×.

**Recommended Hz range and how the frequency mapping works:** each pad's *value* (0–100) is meant to drive your shaker/pad controller's amplitude, while its *frequency* is set once, in your ShakeIt channel configuration, using the convention **value near 0 → high Hz (subtle), value 100 → low Hz (punchy)**. The G-Force settings tab shows a live recommendation (defaults to **100 Hz at value 0, down to 50 Hz at value 100** — revised twice based on real seat-time feel: an original 300 Hz default read too harsh/thin at the low end, and a subsequent 20 Hz low end didn't shake strongly enough), clamped to a declared device capability of **10–300 Hz**. Configure each ShakeIt channel's own "From Hz"/"To Hz" fields to match the recommendation shown in Settings (it updates if you change the device range) — this plugin only ever publishes the 0–100 amplitude value; the Hz mapping itself lives in ShakeIt's own channel configuration.

---

## Configuration

SimHub gives a plugin exactly one settings control, split into four tabs: **Wheel Lock**, **Wheel Slip**, **G-Force**, **General**. **Apply**, at the bottom, commits **all four tabs at once**, regardless of which one is currently showing — you do not need to click into each tab before Apply picks up its changes. Beside Apply is **Restore all default settings**, which resets every field on every tab — Wheel Lock, Wheel Slip, G-Force, General — back to one internally-defined default object, so the shipped defaults and this button can never quietly drift apart from each other. It asks for confirmation first, since it overwrites a driver's own tuning immediately and cannot be undone.

### Wheel Lock / Wheel Slip tabs

Each channel (braking lock, throttle slip) is fully self-contained — its own sources, its own curve, its own pulse settings — so tuning one never touches the other.

**Sources.** Each channel has four wheel fields (front-left/right, rear-left/right); `All`/`Front`/`Rear`/`Left`/`Right` are always *aggregated* from those four (the algorithm has no native concept of them) — never independently *sourced* — but exactly HOW they aggregate is now fully configurable, see "Wheel combination" immediately below. Each field defaults to a **plain reference back to that wheel's own Layer 3 `Raw` property** — the simplest, most robust default, needing no expression engine at all. A field can instead hold a SimHub expression (JavaScript or NCalc) if SimHub's script editor/expression evaluator is available on your install; the small "P"/"J"/"N" indicator next to each field shows which. Click **Reset to default** on either tab (a separate button from the global Restore-all-defaults) to put just that channel's four source fields back to their plain Layer 3 references, without touching the curve, pulse or any other tab.

**Impact:** leave the sources alone unless you have a specific reason to feed a wheel from something other than this plugin's own Raw computation (e.g. you want Wheel Slip to read from a different upstream signal than Wheel Lock does). Most drivers never need to touch this section.

**Wheel combination (aggregation).** How the four per-wheel values above combine into `Front`/`Rear`/`Left`/`Right`/`All` — a physically-motivated scheme (supplied and seat-tested by this plugin's own owner) that replaced a generic, weight-transfer-blind average, because weight transfer is the dominant real effect a wheel-lock/slip cue should reflect: **under braking, load shifts forward, so the front wheels carry the grip and matter most; under power, the driven wheels are the ones that spin.** Two blend stages, both simple weighted sums so the result is always continuous (no click as the worst wheel changes, unlike a bare "take the maximum"):

- **Axle blend (Front/Rear):** `Front = Max(FL,FR)×WMax + Min(FL,FR)×WMin`, same shape for `Rear` — the two numbers describe how much the STRONGER vs. the WEAKER wheel on that axle counts.
- **Side/car blend (Left/Right/All):** `Left = FL×WFront + RL×WRear`, `Right = FR×WFront + RR×WRear`, `All = Front×WFront + Rear×WRear` — the two numbers describe how much the FRONT axle counts against the REAR axle.
- **Wheel Slip only:** a floor, `result = Max(result, Max(participating wheels)×SlipFloorFactor)`, so a single strongly-spinning wheel is never averaged away to nothing. Wheel Lock ships this at 0 (disabled) — its own extreme front bias already carries a locked wheel through strongly, and lock events are far more often axle-symmetric than a lone power-slip spin is; the mechanism itself is generic and available on either channel, only the shipped default makes it Slip-only.

Every one of these five numbers (`WMax`, `WMin`, `WFront`, `WRear`, and Wheel Slip's own floor factor) is independently editable per channel — weights only need to stay ≥ 0 (Wheel Slip's floor factor is additionally capped at 1); they are **not** forced to sum to 1, so what you type is what you get, never silently rescaled. **Shipped defaults — Wheel Lock:** `WMax` 0.45, `WMin` 0.55, `WFront` 0.90, `WRear` 0.10, floor n/a. **Wheel Slip:** `WMax` 0.55, `WMin` 0.45, `WFront` 0.65, `WRear` 0.35, floor 0.4.

**Impact:** the shipped defaults already encode "front carries braking, whichever axle is driven carries slip" — most drivers never need to touch this. If your car is front-wheel-drive (or you simply prefer the opposite bias for Wheel Slip), raise `WRear` above `WFront` (or swap them outright) so the driven axle you actually have dominates the combined reading. See `docs/aggregation-report.md` for the full derivation, continuity proof and the finding on why Wheel Lock does not need its own floor.

**Output shaping (the curve).** This is the part worth spending time on — it decides what your shaker actually does with the 0–100 number. Each row reads *"when the value reaches this raw input, send this output"*; both columns are independently editable, so you can move *where* an anchor sits as well as *how strongly* it reads.

| Anchor | What it means | What you feel there |
|---|---|---|
| Start | Nothing below this produces any output | Silence during ordinary driving |
| **Slightly** (default raw 30, both channels) | Just entering the tyre's working range — below this you still have margin | A gentle, easy-to-ignore cue if you set its output low |
| **Ideal** (default raw 60, both channels — renamed from "Moderate") | The edge of the ideal working range — maximum effective braking/traction right before the tyre starts to lock/spin | Your "sweet spot" cue — strong enough to notice instantly, gentle enough to live with lap after lap |
| **Critical** (default raw 80, both channels) | On the verge of locking/spinning — not there yet, but only moments away | An unmistakable, hard cue — back off *now* |
| End | At and above this the output is always full strength (100) | Full-strength feedback once things are genuinely bad |

Two presets, plus **Custom** (selected automatically the moment you edit any cell by hand):

- **Linear** — a straight line from (Start, 0) to (End, 100), preserving whatever Start/End you already have rather than overwriting them. With the shipped Start=20/End=100 that means 20→0, 60→50, 100→100.
- **Curve** (default) — the shipped, opinionated shape. Both channels share the SAME anchor input positions (30/60/80/100, verified numerically to put "at the limit" near 75–80 and "fully locked/spinning" at exactly 100 — see `docs/refinements-report.md`); only the OUTPUT strength differs. **Wheel Lock:** Start 20, End 100, 30→10, 60→30, 80→80. **Wheel Slip:** Start 20, End 100, 30→8, 60→20, 80→75 — gentler throughout than Lock's curve, because a driven wheel's traction cue benefits from landing more softly than a braking cue does.

The curve is smoothed with monotone cubic interpolation, so the output can never *decrease* as the input rises — a plain spline can overshoot and dip, which you would feel as the shaker easing off at the exact moment things are getting worse.

**Pulse at maximum.** When enabled, a channel that reaches 100 pulses between 100 and a configurable **Minimum value** instead of holding flat — useful if you want a sustained lockup/spin to feel more urgent than a static buzz. **Gap (ms)** is one half-cycle (100→min or min→100); the floor of 200 ms (5 Hz) is enforced by the plugin itself, not just the spinner, so a hand-edited config file cannot sneak in a faster pulse. Disabled by default — most drivers are well served by the plain shaped value.

### G-Force tab

**Maximum acceleration/braking G.** Each axis (acceleration, braking) can be **Fixed** (you set the g-value the meter treats as "100%") or **Auto** (the plugin learns the largest g it actually observes, per game **and** per car, with outlier rejection so a single collision spike is never mistaken for your genuine peak — it must be confirmed by a second, similar reading before it's trusted). Switching games or cars under Auto switches to that combination's own learned maximum automatically; nothing bleeds between them. Defaults: **2.0g braking**, **0.9g acceleration** — braking gets the higher default because it benefits from both tyre grip and aerodynamic downforce at speed, while acceleration is purely traction-limited.

**Impact:** lower the Fixed value (or trust Auto) if your car never gets close to 100% and the pads feel numb; raise it if a merely firm stop already pegs the meter at max.

**Sustained feel (sustain floors).** Under sustained hard braking or acceleration, the trailing pads in each chain keep a *weaker* vibration instead of fading to nothing, so the feel stays continuous rather than cutting out the instant the sensation has "moved on" to the next pad. Defaults are derived from the model's own chain topology (each hop away from the fully-saturated leading pad halves the floor): the **middle** pad of each chain (Bottom Rear for braking, Back Low for acceleration) defaults to **50%**, and the **far** pad (Back Low for braking, Bottom Rear for acceleration) defaults to **25%** — a genuine spatial gradient, so you can feel *where* the sensation is, not just that it's present. **0%** reproduces the old fade-to-nothing behaviour if you'd rather have that.

**Motion feel (the staged sweep).** Braking/acceleration feedback travels through three stages — far pad, then middle pad, then the pad closest to the direction of force — before settling, separately from the STEADY level you're holding (how far each pad settles once it gets there). A hard stamp on the pedal sweeps through quickly and strongly; a gentle change sweeps slowly and gently. Three tunables control this:

- **Level response time** — how quickly the sustained/background level tracks the actual g. Higher = slower, smoother.
- **Sweep smoothing time** — how quickly a latched sweep rate eases off once the input stops changing, so one noisy frame can't snap the sweep forward. Higher = a softer, more gradual entry into the sweep.
- **Sweep speed** — how fast the sweep itself travels across the pads for a given change in g. Higher = a sudden change travels faster.

**Impact:** if you want the whole animation to feel slower, lower Sweep speed (it travels more gradually across the pads) and raise Sweep smoothing time (it eases in rather than snaps); raise Sweep speed if you want sharp trail-braking stabs and throttle blips to sweep through faster. Note that Sweep speed is capped internally at 5 sweeps/second, so past a point turning it up further won't make the sweep any quicker — and since raising Sweep speed lowers the input rate at which that cap kicks in, above that point gentle and violent inputs start to feel more alike. Default **1.2** (lowered from an original 1.5 per the owner's own real-hardware seat time: the animation was reported to read more clearly at 1.2).

**Left/right direction.** Lateral G biases the left/right pad pairs — cornering load makes the loaded side read up to 1.5× and the unloaded side down to 0.5×. **Normal** (default) sends the vibration to the pads on the side the car is *pushing you toward* under cornering (physically, the outside-of-the-corner side, where your body is loaded). **Reversed** flips that. Since the sign convention for which side is "loaded" is inferred rather than documented by every game, try both and keep whichever matches how your rig actually feels in a real corner.

**Integrate Wheel Lock and Slip.** On by default (a fresh install feels this without hunting for the toggle — a deliberate change from an earlier off-by-default; see `docs/integrate-default-report.md`). It is inert with no lock/slip signal wired up — the amplitude formula below is `0` whenever `wheelValue` is `0`, which is what a driver who has not configured the Wheel Lock/Wheel Slip channels sees. Uncheck it to fall back to the plain, pre-existing G-force feel. When enabled, wheel lock/slip modulates every left/right pad pair as an alternating shake superimposed on that pair's current G-force level — the pair's *width* grows with how hard the wheel is currently locking/slipping, while its *centre* stays anchored to the plain G-force value, so turning the feature on never causes a jump. Mechanically, per pad pair: `band = gForceValue × (wheelValue/100) × scale`, `half = band/2`, `output_L/R = centre ± half × sin(2πft)` — and if the resulting band would spill outside 0–100, the whole band SHIFTS (not squashes) so its full width is preserved; only in the extreme case where the band itself is wider than 100 does it fall back to a plain 0–100 clamp around a centre of 50. Three settings: **Shake frequency** (1–20 Hz, default **10 Hz** — RAISED from an original 3 Hz default per the owner's own real-hardware seat time: 3 Hz was tried on real hardware and 10 Hz reported as feeling much better; the 1–20 Hz bounds themselves are unchanged), and independent **Wheel Lock scale** / **Wheel Slip scale** (0–10, must be ≥ 0, default **1.5** each — RAISED from an original 1.0, i.e. **150%** of the original, unscaled amplitude, per the same driver feedback asking for a more pronounced shake by default; the spinner label shows "1.0 = 100%" so the multiplier reads intuitively as a percentage without adding a second, overlapping "intensity" control that would scale the same amplitude term twice). Concretely: at a pad level of 100 and a wheel value of 60, the *old* default produced a shake band of 60 (out of 100); the *new* default produces a band of 90. When both Lock and Slip are non-zero at once, the LARGER of the two scaled contributions drives the shake (not their sum), so a brief overlap between the two channels can never double the amplitude.
> **Not to be confused with the Layer 5 pulse's own, separate, unchanged floor:** "Pulse at maximum" above still enforces its own 200 ms (5 Hz) minimum gap — that is a different setting on the Wheel Lock/Wheel Slip tabs, untouched by the shake-frequency change here.

### General tab

**Enable diagnostics.** Off by default. When on, the 9 `Diag.*` properties above are also published, and extra informational logging is written — useful for troubleshooting or building a dashboard readout of *why* a channel is behaving the way it is. SimHub registers properties once at startup, so this needs a **SimHub restart** to take effect — Apply alone will not make the properties appear.

**Export session to CSV.** Off by default. When on, every published property (product and diagnostic) is written to a CSV file in the same SimHub `PluginsData\Common` folder as this plugin's own settings, for offline analysis or tuning against real recorded data. Takes effect immediately, no restart needed.

### Persisted files

Two files, both in SimHub's `PluginsData\Common` folder (resolved via SimHub's own `PluginManager.GetCommonStoragePath`, never a hard-coded path):

- **`QAdvanceFeedback.config.json`** — everything on the four settings tabs. Loaded once at plugin load, saved on Apply (and on shutdown). A file still present under the old name (`plugin.QAdvanceFeedback.config.json`, an earlier build of this plugin) is imported automatically, exactly once, the first time the new file doesn't yet exist.
- **`QAdvanceFeedback.Parameters.json`** — every **learned** value: the Wheel Lock/Wheel Slip channels' learned peak-g references and the G-force channels' AUTO-learned maxima, each keyed per **game and car**, so switching between cars (or games) automatically loads that combination's own previously-learned values instead of sharing one number across all of them. Updated in memory on every qualifying frame; a background timer flushes it to disk every 5 seconds only when something has actually changed, plus a final synchronous flush when SimHub shuts down. A corrupt file degrades to "start fresh" instead of crashing the plugin. Renamed from `plugin.QAdvanceFeedback.runtime.json`; a file still present under that old name is imported once, and (since it predates per-game/per-car keying) its single global value seeds the very first brand-new car each channel encounters after the upgrade, rather than being thrown away.

---

## Screenshots

*Rendered outside SimHub with a throwaway WPF harness, so they show the light MahApps theme; inside SimHub the same control picks up SimHub's own theme. Wheel Lock, Wheel Slip and G-Force are tall tabs, so their screenshots show only that tab's own settings (no tab strip, no Apply/Restore row) so the whole tab fits in one image; General is short enough that its screenshot instead shows the full control, tab strip and Apply/Restore row included.*

**Wheel Lock**

![The Wheel Lock tab](docs/images/settings-wheel-lock.png)

**Wheel Slip**

![The Wheel Slip tab](docs/images/settings-wheel-slip.png)

**G-Force**

![The G-Force tab](docs/images/settings-gforce.png)

**General**

![The General tab](docs/images/settings-general.png)

---

## Building from source

```bash
bash tools/fetch-simhub-refs.sh 9.11.22          # extracts reference assemblies into lib/
MSBuild QAdvanceFeedback.sln -t:Restore,Build -p:Configuration=Release
dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj
```

`lib/` is deliberately not committed (see `.gitignore`) — those seven DLLs (`SimHub.Plugins`, `GameReaderCommon`, `MahApps.Metro`, `Newtonsoft.Json`, `SimHub.Logging`, `log4net`, `System.Windows.Interactivity`) are third-party binaries extracted from the official SimHub installer, not this project's to redistribute. `tools/fetch-simhub-refs.sh` rebuilds `lib/` from scratch on a clean clone, targeting **SimHub 9.11.22** by default, and needs nothing already installed — it downloads the SimHub release zip and installer, unpacks the installer with `innounp` (Inno Setup 6.4's own unpacker; older `innounp`/`innoextract` versions do not support it), and copies the seven DLLs out.

The algorithm lives in `QAdvanceFeedback/Core/` with **no SimHub dependency at all** — the test project link-compiles those sources directly rather than referencing the built DLL, so the whole algorithm is testable on a machine with no SimHub installed, and a SimHub dependency creeping into `Core/` would break the test build immediately.

### Repository structure

Telemetry flows through five layers (Telemetry interface → SimHub adapter → **Raw** → **Normalized**
→ **Projected**), plus an independent G-Force subsystem and a settings/persistence layer — see "The
five-layer architecture" above for what each one publishes. There is no `Private/` folder and no
reflection-based factory any more: every file that implements these layers ships in this
repository, under `QAdvanceFeedback/Core/` (Layers 1/3/4/5, G-Force) and `QAdvanceFeedback/`
(Layer 2, the settings UI, persistence). Full layer-by-layer responsibilities and a file-by-file map
are in [`docs/architecture.md`](docs/architecture.md).

**669 tests, 0 build warnings, single-DLL distribution** (`bin\Release\net48\` contains only `QAdvanceFeedback.dll` and its `.pdb` — every SimHub/GameReaderCommon/MahApps/Newtonsoft reference is `<Private>false</Private>`). There is no separate "clean clone" variant to build or test any more — see "Repository structure" above.

---

## References and copyright

- **The Wheel Lock/Wheel Slip algorithm (Layer 3, "Raw") deliberately reproduces the behaviour of SimHub's own ShakeIt legacy-iRacing wheel-slip effect**, for compatibility of feel with an algorithm SimHub users already know — it was studied via decompilation of the shipped `SimHub.Plugins.dll` to confirm the arithmetic exactly, not reverse-engineered from guesswork.
- **SimHub is a product of Wotever** (github.com/SHWotever/SimHub). **This plugin is an independent, third-party plugin and is not affiliated with, endorsed by, or supported by SimHub or Wotever.**
- **SimHub's reference assemblies are required at build time and are NOT redistributed** in this repository — `lib/` is git-ignored and `tools/fetch-simhub-refs.sh` extracts them fresh from the official SimHub installer for anyone building from source.
- **The G-force model's "washout" design** (splitting a sustained level from a rate-driven transient, each with its own time constant) is adapted from the classical motion-cueing literature used in full-motion simulator rigs — separating low-frequency sustained acceleration cues from high-pass "onset" transients is the standard technique that literature describes for cueing acceleration on a physically limited platform; this plugin borrows the *idea* (two filtered paths combined, not a literal rig's washout filter implementation) to shape a seat pad's feel rather than to drive an actual motion platform.

See [`LICENSE`](LICENSE) (MIT) for the terms this project itself is distributed under.

---

## Known limitations / unverified

Stated plainly, per this project's own standing practice:

- **Nothing here has been run inside a live SimHub session, against a real game, or on real rumble hardware.** Every one of the 669 tests is a pure, offline, hand-computed-expectation unit test (or a real-temp-file test for the persistence layer) — the actual runtime behaviour once installed in SimHub, reading a real game, and driving real shakers is unverified and cannot be verified in this development environment.
- **`LongitudinalG`'s sign convention and `LateralG`'s sign-to-side convention are inferences**, not documented SimHub facts (checked directly — no doc comments, no adjacent XML). Direction (braking vs. accelerating) is decided from differentiated ground speed, never from `LongitudinalG`'s sign, specifically so this ambiguity cannot affect the Wheel Lock/Wheel Slip channels; the G-force channels' pad selection does read the sign, so if it is inverted for a given title the fix is a one-line swap (see `Core/GForce/GForceEngine.cs`'s own remarks).
- **The per-wheel redistribution inside "Normalized" is a heuristic** (Raw's own four-way proportions reshaping a car-level severity), not a physically derived per-wheel model — this plugin is explicitly barred from using wheel-speed-derived slip, so this is the best available per-wheel signal under that restriction, not a claim of more precision than it has.
- **The G-force washout model's time constants were calibrated against an offline simulation of the exact discrete filter recurrence**, not against real hardware — they produce the intended qualitative behaviour in that simulation and in the engine's own unit tests, but how they actually feel on rumble pads is unverified.
