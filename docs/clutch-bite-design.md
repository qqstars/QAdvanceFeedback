# Clutch Bite effect — design

Date: 2026-11-17. Status: implemented (report: `docs\clutch-bite-report.md`). Builds on `docs\shakeit-native-effects-design.md` and
`docs\shakeit-test-effect-mvvm-design.md` (MVVM foundation, Test Effects, publish path).

## Intent

The owner's current clutch-bite feedback is a pair of Custom Effects in
`docs\setup\QAdvanceFeedback - WheelLockSlip.siprofile`. A hidden one exports `MyDataFields.Clutch40`, a V
that is 0 at 40 % and 100 at 10 % and 70 %. A visible one plays it through a gamma 2.0, 150 % gain filter.
That shape has three problems:
- it is quietest at the bite, not strongest;
- it drops off a cliff at each edge;
- it cannot follow a car's real bite point.

The goal is a QAdvanceFeedback clutch-bite feel that simulates a real car:
- strongest exactly at the bite point, fading smoothly either side;
- low-frequency judder at the bite;
- the bite point learned per game and car;
- a published property, a native ShakeIt effect, a graph and a Test Effect that plays on the device with
  no game running.

## Goals

1. **Published property** `QAdvanceFeedback.Clutch.Bite.Output` (0–100), computed by the plugin every
   frame.
2. **Native ShakeIt effect** "QAdvanceFeedback Clutch Bite" (one channel). It plays only that published
   value, like WheelLock/WheelSlip.
3. **Settings** in a new **Clutch** tab on the plugin page *and* in the effect's ShakeIt panel, synced
   through the settings hub (either side applies at once and shows on the other).
4. **Graph** of clutch input (x, 0–100) → output (y, 0–100), read-only, in the panel's curve slot and on
   the tab.
5. **Test Effect:** reads the pedal axis with no game running, publishes the real
   `Clutch.Bite.Output`, plays on the device, and draws a ball on the graph. It auto-stops after 10 s
   without input.
6. **Auto bite point:** learned from standing starts, blended into the configured value with the
   Lock/Slip SMax ramp, and persisted in `QAdvanceFeedback.Parameters.json` beside the Lock/Slip learners.

## Non-goals

- Learning the bite from gear changes while moving.
- Keying the bite point by surface.
- Changing any existing published property or Custom Effect binding.
- Restructuring the rest of the plugin page (project 2).

## Anti-goals

- **No silent wrong learning:** a bad standing start (throttle swinging, clutch dumped, slope or loose
  surface with no RPM dip) must never move the bite point.
- **No surprises outside a session:** the device never vibrates from pedal movement outside a game
  session unless the Test Effect is on.
- **No persistence from tests:** nothing the Test Effect does is learned or saved.

## Settings (defaults)

| Setting | Values | Default | Meaning |
|---|---|---|---|
| When Apply | Press and Release / Press Only / Release Only | Press and Release | Option B: output follows position whenever the pedal's **last direction of movement** matches. Holding still after easing out to the bite keeps vibrating under Release Only. |
| Bite Point Mode | Auto / Manual | Auto | Manual: Bite Point is final. Auto: Bite Point is the starting point the learning moves from. |
| Bite Point | 10–90 | 50 | Clutch position (0 released … 100 fully pressed, the game's `[Clutch]` convention) where output is 100. Always editable. |
| Faded Range | 1–50 | 30 | Distance either side of the bite where output reaches 0. |
| Gamma | 1.0–6.0 | 2.5 | S-curve steepness (1 = straight line). |
| Fallback clutch axis | a SimHub controller axis, or none | none | Used when the game gives no clutch, and always by the Test Effect. |
| Invert axis | on/off | off | For pedals that read 100 % when released. |

**Shaker defaults** (`ToneOutput`, set when the output is first created, like the other effects):
- low-output frequency **45 Hz**;
- high-output frequency **25 Hz**, used, so the bite itself (output 100) plays 25 Hz;
- white noise off.

Rationale: a 4-cylinder idling at about 800 rpm fires at about 27 Hz, and lugging down at the bite drops
lower. A lighter buzz away from the bite reads as "not there yet".

## Output calculation (SimHub-free, unit-tested)

For clutch position `c` (0–100), final bite `b` (clamped to 10–90), range `r` and gamma `k`:

```
lo = max(5,  b − r)        hi = min(95, b + r)        (5-point deadzone at each end)
S(t) = tᵏ / (tᵏ + (1−t)ᵏ)  (t clamped to 0..1)
c ≤ lo or c ≥ hi  → 0
lo < c ≤ b        → 100 · S((c − lo) / (b − lo))
b < c < hi        → 100 · S((hi − c) / (hi − b))
```

Owner's examples:
- b 75, r 30 → 45 to 75, then 75 to 95;
- b 20, r 30 → 5 to 20, then 20 to 50.

**Direction (option B):**
- The tracker remembers the last direction of movement. It changes direction only after the pedal has
  moved at least **2 %** of travel the other way since the last turning point.
- Under Press Only / Release Only, output is the curve value while the last direction matches, else 0.
- Until the first real movement in a session, Press Only / Release Only output 0. Press and Release
  always outputs the curve value.

**When it plays (live):**
- Live output is published only while a game session runs (game running, not paused, not in a menu,
  not spectating). Otherwise it is 0.
- While the Clutch Test Effect runs, the test owns the output.
- The native effect uses `TestAwareOutputGate` like the others.

## Clutch input

- **Priority:** the game's `[Clutch]` first, then the fallback axis.
- **Game clutch counts as available** once the game has reported a value above 5 % this session. That
  tells a game that does not report clutch (always 0) apart from a released pedal.
- **Until then:** the fallback axis if one is chosen and connected, otherwise the game value (0).
- **Pedal axes:** SimHub's Controllers plugin publishes every connected controller axis with no game
  running:
  - as properties `JoystickPlugin.<Device>_<Axis>` (raw 0–65535);
  - as registered axes (0–1, `PluginManagerEvents.Instance.AxisListChanged` / `RegisteredAxis`).
  - The picker lists those axes; the value is mapped to 0–100, inverted if chosen.
- **Panel readout:** which source is in use now (Game clutch / Pedal axis / none).
- **The "game clutch available" latch resets on a game change** (reporting is per title), not on a car
  change.
- **A source switch mid-start drops that sample:** if the source identity changes while the detector is
  armed or watching a release, the detector resets, so a sample can never land under the other source's
  key.

## Auto bite point

**Detection: standing starts only.**
1. **Arm** when the car is in gear, below 0.5 km/h, with the clutch at least 90 % pressed.
2. **Watch the release.** During the release, keep a rolling 1 s history of (time, clutch, RPM, speed).
   - **Baseline:** the RPM just before release.
   - **RPM trigger:** the first frame RPM is **≥ 4 %** below the baseline.
   - **Speed trigger:** the first frame speed is **0.3 km/h** above standstill.
3. **Backdate to the onset** (owner's concern: a late read must never become the bite). The position
   where the dip is confirmed, or where the driver stops, is never the sample. Look back through the
   history for the **onset frame**:
   - for an RPM trigger, the first frame RPM fell below baseline − 1 % and kept falling to the trigger;
   - for a speed-only trigger, the first frame speed rose above standstill.
4. **Extrapolate and correct** (as built; see the report's rulings). For an RPM onset, a straight line of
   RPM against pedal position is fitted over the dip frames and solved for the baseline RPM. That removes
   the trigger threshold and the frame-quantisation error, so only the engine's response delay is left.
   Then one frame of pedal travel back toward pressed is added. A speed-only onset uses the onset frame's
   position, plus that one frame.
   **Correct the response delay.** Sims show the engine loading a few frames after the clutch bites
   (about 17–50 ms), and a moving pedal is then already past the bite by about *pedal speed × delay*.
   - The sample is the clutch position at the onset frame, **corrected by one frame of pedal travel**
     back toward pressed, the shortest delay.
   - The residual is held small by the release-speed rule below.
5. **Confirm** when speed reaches **+2 km/h**. An RPM onset is preferred over a speed onset.
6. **Reject** the sample if any of these hold:
   - **the pedal moved faster than 30 % of travel per second** in the 0.2 s before the onset frame.
     Crossing the bite quickly (for example 46 → 40 in 0.1 s, or 55 → 35) cannot be measured; at or under
     30 %/s the error is at most about 1 point;
   - throttle moved by more than 15 points during the release (that changes RPM by itself);
   - it is a speed-only sample on a loose surface, using the existing surface detection, because the car
     rolls later there;
   - the game is paused, in a menu, or spectating.
   - **Holding position never creates a sample by itself:** above the bite nothing happens; below it the
     sample still comes from the backdated onset during the release.
7. **Never key by surface:** the bite is a property of the clutch. The surface rule only rejects
   unreliable speed-only samples.

**Learning: the Lock/Slip SMax kick-in ramp, with evidence that carries across sessions** (owner's rule).
One session may give a single standing start, so every accepted sample is kept and learning continues the
next time the same car is driven, until the bite has matured.
- **Evidence per key:** the accepted samples, kept across sessions. The last 15 are kept; the median is
  taken over them.
- **Cold anchor:** the configured Bite Point, always. It is the starting point until the evidence takes
  over.
- **Before maturity:**
  `final = Blend(configured, median, ConcaveHotWeight(totalCount, dispersion, scaleSamples = 9))`. This is
  the same concave, dispersion-weighted ramp as SMax: the first sample moves it meaningfully, more
  consistent samples move it further.
- **Mature** once at least **9 samples** are kept with a standard deviation of at most **3 points**
  (owner's rule):
  - the median at that moment becomes the key's **matured bite** and `final = matured bite` from then on;
  - the samples are dropped and no longer saved;
  - learning stops for that key, so later standing starts for that car are ignored.
- **Manual mode:** final = configured. Learning still runs and is saved, but is not used.
- **Dispersion in pedal points, not CV** (designer's call). CV divides by the mean, which would make low
  bite points almost never mature and high ones mature on sloppy data. `ColdWarmBlend` is reused
  unchanged by feeding it `standardDeviation / 50` as its coefficient-of-variation input (a fixed 50-point
  reference).
- **Key:** `KeyedGripLearner.MakeKey(game, car, clutchSourceIdentity)`. The source identity is
  `GameClutch` or `Axis:<axis name>`, so a game or car switch loads that car's value through the same code
  path as Lock/Slip, and the two clutch sources never mix.
- **Persistence: everything learned is saved, matured or not** (owner's rule).
  - **Where:** a new `ClutchBiteLearners` dictionary in `QAdvanceFeedback.Parameters.json` (`RuntimeStore`),
    beside the Lock/Slip learners. Per key it holds either:
    - **while learning:** the kept samples (up to 15) and the total accepted count; or
    - **once matured:** only the matured bite value. The saved samples are dropped at maturity.
  - **When:** whenever a sample is accepted, and on game exit, car switch and plugin shutdown, through the
    existing save path.
  - **As built (review fix I1):** "saved" means the runtime cache. The store's timer writes it to disk
    within seconds, and shutdown flushes synchronously. A synchronous flush on SimHub's data thread would
    stall every effect on each switch.
  - **Kept:** the store keeps every loaded key, so a key is never deleted by being left out of an export.
  - **Restart:** after a restart the key continues exactly where it stopped; the final bite is recomputed
    from the saved samples.
- **"Forget learned bite" command** (panel and tab, designer's call): clears the current key's samples or
  matured bite and returns that car to the configured Bite Point. Learning then starts again, which is the
  way to relearn a car after a change such as a new pedal or game update.

**Readout** (panel and tab), the same pattern as the Lock/Slip key data under Auto:
- configured value;
- learned median;
- sample count, with "learning" or "matured";
- final bite point.

## Learning parameters (owner's follow-up)

The learning thresholds are editable on the plugin page's Clutch tab only. They are never shown in the
ShakeIt panel: the shared view shows the section only when its view model is created with
`showLearning: true`, which only the page does.

| Parameter | Default | Range |
|---|---|---|
| Clutch pressed to arm (%) | 90 | 70–99 |
| RPM dip that detects the bite (%) | 4 | 1–15 |
| Speed that confirms pull-away (km/h) | 2 | 0.5–10 |
| Fastest release at the bite (%/s) | 30 | 5–200 |
| Largest throttle change (points) | 15 | 0–100 |
| Steady idle band (%) | 2 | 0.5–10 |
| Samples to mature (also the blend ramp's full-trust count) | 9 | 3–30 |
| Largest spread to mature (points) | 3 | 0.5–10 |

- **Fixed values:** the onset threshold (1 %), standstill (0.5 km/h), speed trigger (0.3 km/h), trend
  check (1 %), frame-gap reset (0.2 s), the one-frame correction cap (3 points) and the 15 kept samples stay
  fixed.
- **Sync:** the set travels through the settings hub as one field (`SettingField.ClutchLearning`), so it
  applies at once and syncs with the config file.
- **Scope:** changes affect new samples. A matured car keeps its bite until Forget.
- **Restore all defaults** resets every Clutch setting, the learning parameters included. Learned bite
  points are kept.

## Graph

- **Axes:** x = clutch input, y = output.
- **Drawn:** the curve, the deadzones (shaded 0–5 and 95–100), and the bite marker at the final bite.
  Under Auto, also a faint marker at the configured value.
- **Test ball:** while the test runs, a ball at (pedal, output).
- **Shared:** one `ClutchGraphViewModel` / view for the panel's curve slot and the tab.

## Test Effect

- **Toggle:** Test on/off, plus a live readout (input, output, source).
- **Input:** reads the fallback axis at 60 Hz. With no axis chosen or connected, a manual slider
  (0–100) appears instead.
- **What it publishes:** each tick computes the **real** output (current settings, final bite, direction
  tracker) and publishes `Clutch.Bite.Output` through `PublishClutchTest`; the native effect plays it.
- **Which bite it uses** (designer's call), so the test feels like the game:
  - the final bite of the **most recent in-game key** (game + car + game clutch);
  - that key is remembered in `QAdvanceFeedback.Parameters.json`, so it survives a restart;
  - with none yet, the configured Bite Point.
  - The test readout names the car whose bite it is using. The graph and readout out of game use the same
    rule.
- **Session:** new kind `TestKind.Clutch` in `TestEffectSession` (owner-keyed, one test at a time).
- **Auto-off:** 10 s without input (pedal movement or slider) turns the test off, toggle included.
  Leaving the panel/tab stops it too.
- **No learning:** the test never feeds the learner.

## Approaches considered

1. **Chosen:** the plugin computes and publishes; the native effect plays the published value. One
   calculation, learning beside Lock/Slip, and the test publishes through the same path.
2. **Rejected:** the native effect computes it itself (AZOM). It would need a second copy of the
   calculation for the property and the learning, which breaks "test output = published value".
3. **Rejected:** a plain Custom Effect formula. No learning, no panel, no graph, no test.

## Architecture

**Core and Settings (SimHub-free, unit-tested):**
- `ClutchBiteCurve`: the range and S-curve math.
- `ClutchDirectionTracker`: option B, with 2 % hysteresis.
- `ClutchBiteDetector`: the standing-start state machine.
- `ClutchBiteLearner`: samples, median, blend, persist rule, keyed state.
- `ClutchBiteSettings` in `QAdvanceFeedbackSettings`.

**Plugin:**
- each frame: resolve the clutch source, compute and publish `Clutch.Bite.Output`, feed the detector;
- `LatestClutch` snapshot;
- `PublishClutchTest`;
- `RuntimeStore` load/save of `ClutchBiteLearners`;
- diagnostics `Diag.Clutch.Source/Input/BiteFinal/BiteLearned/BiteSamples` (appended to the diagnostic
  and CSV columns).

**Hub:** new `SettingField`s for each clutch setting.

**Views and view models (MVVM rule):**
- `ClutchBiteViewModel` (settings, readout, test) and `ClutchGraphViewModel`;
- views for the ShakeIt panel and the new **Clutch** tab, with the same view model shape;
- the tab applies at once, like the key data (no Apply needed), so both sides behave the same.

**Native container:** `QAdvanceFeedbackClutchBiteContainer`. The class name is frozen once shipped.

## Constraints

- net48 WPF, single DLL, no new runtime packages.
- Existing properties and Custom Effect bindings are unchanged. Product properties grow from 62 to 63;
  the pinned-name tests are updated deliberately.
- Strings in both languages.
- Screenshots and READMEs in both languages.
- Docs under `docs\`.
- Test-first for SimHub-free code.
- No git.

## Left to the builder

- Control layout and labels.
- Axis picker details: list `JoystickPlugin.*` axes, refresh on `AxisListChanged`.
- How the plugin page hosts the new tab.
- Exact readout wording.

## Needs SimHub to verify

- The pedal axis is readable with no game.
- The test plays on the device.
- Game clutch availability detection across titles.
- Standing-start samples in a few cars.
- 25/45 Hz feel.
