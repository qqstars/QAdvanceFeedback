# Slip Angle channel (+ Lock/Slip test sliders, fallback warning, G-Force integration) — design

Date: 2026-11-18. Status: design, approved approach A, not yet planned.

Marked **[call]** = a decision made during design by the assistant (not explicitly by the owner);
the owner may revisit it. Everything unmarked was decided by the owner in conversation.

---

## 1. Intent

Real tarmac racing technique keeps the tyres' slip angle just under the angle of peak lateral grip.
SimHub's built-in **Traction loss** effect does not express that. Decompiled (kept outside the repo,
clean-room rule — see `docs\reference\README.md`), it is:

```
0 if airborne; ratio = Vlat / max(0.1, Vfwd); Left/Right split by sign;
output = max(0, ratio) × fade(GroundSpeed, 0→5 m/s)
```

i.e. tan(body sideslip), car-level, no threshold, no learning, no pedals. It reads oversteer/drift
only and says nothing about approaching the limit.

The owner wants a new **Slip Angle** channel that tells the driver, while braking and/or on throttle,
when the tyres enter the ideal slip-angle window, warns when they go beyond it, and — crucially for
WRC and drift scenarios — does not keep buzzing hard while the driver is intentionally holding a slide.

**What it should feel like (owner's words, condensed):** press the pedal → vibration rises as the
ideal window is hit → maximum vibration as the boundary is passed (owner: "the boundary" = normalized
100, i.e. α ≥ End = 1.5 × Max; past Max the output climbs from 60% toward 100%) → once the car is held stable in a
drift (detected quickly), vibration eases gently down to ~40% and stays there → back to normal once
the car is back below the ideal window.

## 2. Goals, non-goals, anti-goals

**Goals**
- A Slip Angle channel built on exactly the same design pattern as WheelLock/WheelSlip: Raw →
  Normalized (auto-learned, per game/car/surface, borrowing tiers, confidence, kick-in gate) →
  Projected (editable curve), a settings tab, a native ShakeIt effect, a Test Effect.
- Real tyre slip angle when the game provides it; a fallback estimate when it does not, with a
  visible warning.
- Drift-aware attenuation and airborne/landing handling.
- Three changes to existing features: Lock/Slip Test Effect group sliders (with the dot always on
  the curve), a fallback warning for Lock/Slip, and "Integrate Slip Angle" in G-Force.

**Non-goals**
- Per-tyre-compound keying (no common signal exists).
- Per-wheel / front-rear / left-right output routing for Slip Angle — it is ONE output channel.
- Changing any Lock/Slip learning, normalization or projection behaviour.
- A front-axle slip angle in fallback mode (needs steering angle, which is game-specific).

**Anti-goals (would be a failure even if it "works")**
- Constant buzzing during a held drift or on bumpy gravel/tarmac.
- A landing after a jump punching a strong pulse or teaching the learner wrong angles.
- Any regression in Lock/Slip/G-Force feel or published values.
- The settings page / ShakeIt panel violating the MVVM rule (`docs\architecture.md` "UI: MVVM").

## 3. Constraints

- **Layer rules** (`docs\architecture.md`): only Layer 2 knows SimHub types; `Core\` stays SimHub-
  and serializer-free and is link-compiled into the net8 test project.
- **Clean-room:** no decompiled SimHub code in the repo; the fallback formula below is standard
  vehicle dynamics, written in this project's own terms.
- **MVVM standing rule** for all new/touched UI (view models in `Ui\ViewModels`, WPF-free; code-
  behind wiring only; time via `ITicker`, UI thread via `IUiDispatcher`).
- **Frozen names:** a ShakeIt container's type name is persisted in profiles and channel maps —
  choose once (`QAdvanceFeedbackSlipAngleContainer`).
- **Backward compatibility:** every existing published property keeps name, value, timing. New
  properties are additive; new diagnostics/CSV columns are appended at the end. The pinned product
  list in `AllPublishedPropertiesTests` is extended deliberately, not rewritten.
- **Docs:** `docs\architecture.md` AND `docs\architecture.zh-Hans.md` must be updated in the same
  change (maintenance rule), both READMEs, new screenshots via `tools\screenshot-harness` per the
  capture rule. All UI strings in `StringTableEn` and `StringTableZhHans`.
- Specs/plans/reports live directly in `docs\` (never `docs\superpowers`).

## 4. Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | A dedicated Slip Angle stack mirroring WheelLock layer by layer, reusing the generic pieces (`ColdWarmBlend`, `ManualOverrideGate`, `SurfaceLooseFraction`, `ColdStartTier`/floors, `ColdStartScale`, `Aggregator`, `OutputProjector`/`ProjectorSettings`/`PulseGenerator`, the key-data slot model, `SettingsHub`), with FOUR behaviour-preserving extractions shared by Lock/Slip and Slip Angle: the borrowing-tier resolver (from `KeyedScaleLearner`), `CrossingGate` (from the two crossing gates, §9.5a), `CorroboratedLevelEstimator` (from `GripLearner`, §9.5b), plus the new `ImpactGuard` (off for Lock/Slip). Each extraction is proven bit-identical against a frozen copy of the shipped code. | Lock/Slip behaviour unchanged (proven, not assumed); degrees and a plateau learner fit naturally; protection logic exists once. Some structural duplication (settings class, tab). |
| B | Third channel inside `NormalizedWheelLockSlipEngine`/`WheelChannelSettings`. | Same code paths, but that engine assumes 0–100 sources, S75/S90/SMax and the G-based limit detector throughout; highest regression risk. |
| C | Refactor Lock/Slip into a generic channel framework first. | Cleanest end state; a large rewrite of tuned, heavily-tested code before any feature exists. |

Owner chose **A**.

## 5. Delivery order

1. Lock/Slip Test Effect group sliders + dot fix (§6) — independent, and establishes the slider/
   inverse-mapping pattern §9.11 reuses.
2. Lock/Slip fallback warning (§7).
3. Shared cores, extracted behaviour-preservingly with their frozen-copy equivalence tests, before
   any Slip Angle code uses them: tier resolver, `CrossingGate`, `CorroboratedLevelEstimator`,
   `ImpactGuard` (§4, §9.5a, §9.5b). Lock/Slip must be bit-identical at the end of this step.
4. Slip Angle channel (§9).
5. G-Force "Integrate Slip Angle" (§10) — depends on 4.

---

## 6. Lock/Slip Test Effect: group sliders, dot on the curve

**Problem found:** `TestBalls.For` uses x = `Aggregator.Compute(sources)` and y = projection of
`Aggregator.Compute(normalize(each wheel))`. Normalization is non-linear, so aggregate-then-normalize
≠ normalize-then-aggregate and the dot leaves the curve whenever the wheels differ.

**Behaviour:**

| Routing | Sliders shown | A group slider writes |
|---|---|---|
| Mono | **Single Channel** + 4 wheels | FL, FR, RL, RR |
| FrontRear | **Front Channel**, **Rear Channel** + 4 wheels | FL+FR / RL+RR |
| LeftRight | **Left Channel**, **Right Channel** + 4 wheels | FL+RL / FR+RR |
| Corners | 4 wheels only | — |

- A group slider's value is computed, never stored independently:
  `groupSource = N⁻¹( Aggregator.Compute(N(FL), N(FR), N(RL), N(RR), weights).<Group> )`
  where `N` is exactly `KeyDataNormalization.Normalize` with the key data the graph shows (the same
  call `LockSlipTestEngine.Step` makes) and `weights` = the channel's Wheel Combination weights.
  `N` is monotone non-decreasing on [0,100], so `N⁻¹` is a bisection returning the **smallest**
  source reaching the target (handles flat/saturated regions); a target ≥ N(100) returns 100.
- **Dot:** x = the group slider value (or the wheel value under Corners); y = the projected
  (pulse-free) value computed purely from the four wheels (unchanged). The dot is therefore always
  on the curve by construction — **a unit test asserts this across random wheel spreads, weights and
  key data**, which is the owner's cross-check that the slider maths is right.
- Dragging a group slider sets its member wheels to the dragged value through the view-model
  property setters (two-way bindings; no event handlers). Re-entrancy: while a group setter is
  writing wheels, the group values are recomputed once at the end, not per wheel.
- Routing change → slider set switches (bound visibility from the VM).
- Caveat (documented in UI help text): if the combination weights do not sum to 1, four equal wheels
  do not re-aggregate to the same value, so a group slider can settle slightly off where it was
  released; the dot still sits on the curve.

Files: `Ui\ViewModels\LockSlipTestViewModel.cs`, `Settings\TestBalls.cs`, the panel XAML, a new pure
helper for `N⁻¹` (e.g. `Settings\KeyDataNormalizationInverse.cs`), tests.

## 7. Lock/Slip fallback warning

Shown **only when the channel's source is Plugin Internal** (Raw). Never for ShakeIt Built-in, Viper,
Custom.

| Channel | Native (no warning) | Fallback (warning) |
|---|---|---|
| Wheel Lock | `WheelSpeedLock`, `RPS`, `RPSandDummyRadius` | `Braking vs speed`, `RPM vs Speed (legacy)`, `RPM vs Speed (assumed legacy…)`, both "no capabilities supplied" defaults |
| Wheel Slip | `Precalibrated slip`, `Slip data`, `Wheels speed` | same fallback set as Lock |

`Direct slip feedback` and `No branch matched` → no warning (not an estimate). Classification is a
pure function over `WheelSlipBranchNames` in `Core` (`BranchNativeness`), driven by the
`SelectedLockBranch`/`SelectedSlipBranch` the Raw engine already reports.

UI: an orange label ("Not natively supported by this game — using the fallback estimate (speed/
RPM).") on the Wheel Lock / Wheel Slip settings tab and the matching ShakeIt panel, bound to a VM
property; hidden with no game running. Propagated to ShakeIt panels through the existing
`SettingField.SourceAvailability` broadcast (extend its payload with nativeness) **[call]**.

---

## 8. Shared vocabulary for §9

- **α** — slip angle magnitude in degrees, ≥ 0 (sign discarded).
- **Min / Best / Max** — the three key data angles (the Slip Angle analogue of S75 / S90 / SMax).
- **End** = 1.5 × Max.
- **Start** (owner, 2026-11-19) — Start = ⌊0.5 × Min⌋ whole degrees (Min 5 → 2°, Min 9 → 4°; the
  shipped 5/8/10 gives 2°). Always derived from Min (learned or typed), never set on its own. When
  ⌊0.5 × Min⌋ is 0 (Min < 2°) the Start knot is omitted.
- **N(α)** — normalized value: piecewise monotone-cubic through (0,0), **(Start,15)**, (Min,30),
  (Best,60), (Max,80), (End,100); clamped to 100 for published values.
- **Nᵤ(α)** — *unclamped* normalized value used only by the drift state machine: equal to N below
  End, and beyond End extends the (Max→End) segment linearly (+20 points per 0.5 × Max). Needed so
  trends above End (e.g. 250% → 180% of Max) are still visible.

## 9. Slip Angle channel

### 9.1 Source

Two source modes **[call]**: **Plugin Internal** (default) and **Custom** (four per-wheel source
fields in degrees, plain/NCalc/JavaScript like the other channels). ShakeIt Built-in and Viper are not
offered — neither provides a slip angle. Source identity follows `SourceIdentity` as today.

**Plugin Internal, native tier** — real tyre slip angle from game raw data, per wheel. A pure Layer-2
table `SlipAngleRawSources` maps game id → four property paths + unit + transform. Verified in the
spike (`slip-angle-spike-report.md`, SimHub 9.13.2); only games with a verified field are listed:

| Game | Raw path (under `DataCorePlugin.GameRawData.`) | Wheel order | Unit | Kind |
|---|---|---|---|---|
| ACC | `Physics.slipAngle[0..3]` | FeedbackData index | unverified | direct |
| Assetto Corsa Rally | `Physics.slipAngle[0..3]` | not named | unverified | direct |
| Assetto Corsa EVO | `Physics.slipAngle[0..3]` | not named | unverified | direct |
| F1 23 / 24 / 25 / 26 | `PacketMotionExData.m_wheelSlipAngle[0..3]` | raw RL, RR, FL, FR (remap `(j+2)%4`) | unverified | direct |
| Forza (FM7, FH4/5/6, FM 2023) | `TireSlipAngle{Front,Rear}{Left,Right}` | named FL, FR, RL, RR | unverified | direct |
| Live For Speed | `OutSim2.OSWheels[i].TanSlipAngle` | raw RL, RR, FL, FR | tangent | direct |
| Rennsport | `TelemetryPoint.{LF,RF,LR,RR}.SlipAngle` | named LF, RF, LR, RR | unverified | direct |
| Wreckfest 2 | `Main.carPlayer.tires[i].slipAngle` | FL, FR, RL, RR | unverified | direct |
| Project Motor Racing | `PlayerTelemetry.m_wheels[i].m_slipAngle` | FeedbackData index | unverified | direct |
| Drift21 | `RaceTelemetry.TireSlipAngleRadians_{Front,Back}{Left,Right}` | named FL, FR, BL, BR | radians | direct |
| KartKraft | `Motion.WheelsArray[i].SlipAngle` | not named | unverified | direct |
| Bus Bound (non-car) | `Telemetry.Bus.Wheels[i].SlipAngle` | not named | unverified | direct |
| rFactor 2 | `CurrentPlayerTelemetry.mWheels[i]`: `atan2(mLateralPatchVel, abs(mLongitudinalGroundVel))` | FeedbackData index | rad (computed) | Layer 2 |
| Le Mans Ultimate | `PlayerNativeTelemetry.mWheel[i]`: same transform | FeedbackData index | rad (computed) | Layer 2 |
| NASCAR Heat 4/5, Tony Stewart games, SRX (one reader; PROVISIONAL) | `LCarPacket.wheel_{lf,rf,lr,rr}`: `atan2(lat_terrain_speed, abs(lon_terrain_speed))` | named LF, RF, LR, RR | rad (computed; field semantics unverified) | Layer 2 |

Assetto Corsa (original), Automobilista 2 and every other game have no slip angle or patch
velocity in raw data and are fallback only. "Unverified" units are not stated in the assemblies and
need a live capture.

A game counts as **native** only after sustained evidence (finite, non-constant values while moving,
same promotion pattern as `KeyedTelemetrySupport`, per game, never demoted within a session).

**Plugin Internal, fallback tier** — rear-axle estimate from body motion:
`α_rear = |atan2(Vlat, max(Vfwd, 1 m/s))|` (body slip angle at the CoG) with `Vlat/Vfwd` =
`FeedbackData.LocalVelocity` (m/s). **Revised after the spike:** the `− b·r` yaw term was dropped —
`YawChangeVelocity` (deg/s) has a per-game, unverified sign convention relative to `LocalVelocity`,
so the term could add instead of subtract on some titles; the learned normalization absorbs the
scale difference between body slip and rear-axle slip. Front axle is unavailable in fallback. Fallback → orange warning on the tab and ShakeIt panel (Plugin Internal only,
as §7).

Native and fallback are **different source identities** for learning (their scales differ).

### 9.2 Gating (before anything is output)

- **Trigger By** (owner): `Brake Pressed` | `Throttle Pressed` | `Any Pressed`; thresholds
  `Brake ≥ a`, `Throttle ≥ b`; Any = `brake ≥ a OR throttle ≥ b`. Defaults **[call]**: Any, a = 5%,
  b = 10%. Gate closed → published output 0 (same as Lock/Slip). The drift state machine keeps
  tracking α while the gate is closed so re-opening mid-slide is consistent **[call]**.
- **Speed:** output fades in linearly 10→20 km/h ground speed **[call]**; learning needs ≥ 30 km/h.
- **Airborne:** `IsFlying > 0` → output 0, no learning, drift state reset to Normal. (Spike:
  `FlightIsOnGround` is never written for car games, so it is NOT used.)
- **Landing wobble** (owner): on touchdown,
  `wobble = min(1, flightDuration / MaxFlightDuration) × MaxWobbleDuration`
  (defaults **1000 ms / 500 ms** — swapped by the owner 2026-11-19 to match real suspension settling
  of ~0.4–0.6 s: 1 s airborne → 500 ms wobble, 200 ms airborne → 100 ms). During the wobble: learning frozen,
  output multiplied by an S-curve fade 0→1 across the wobble, drift timers held at reset.
- **Small hops** not flagged airborne: frames with |vertical G − 1 g| > 0.5 g, plus 200 ms after,
  are excluded from learning only **[call]**. (Spike: `VerticalAcceleration` is populated only by
  iRacing and Rennsport; elsewhere hops are covered by `IsFlying` plus `ImpactGuard`'s vertical-jump
  check.)
- **Unverified units (spike):** for native games whose unit is not pinned in the table, Layer 2
  auto-detects per game during native promotion — radians when the sustained |value| maximum stays
  below 1.0 while |lateral G| ≥ 0.8 g, else degrees — and reports the detected unit in diagnostics.
- **Kerbs:** a wheel with `WheelsOnKerbs[i]` is excluded from its axle (§9.3) **[call]**.

### 9.3 Four wheels → ONE combined Raw angle (`Raw.All`)

Owner: one final combined Slip Angle Raw value, like WheelLock/WheelSlip's All; one Min/Best/Max set
on it; learning still per axle; how Raw.All is computed is internal. The combination below is
**[call, owner delegated]**.

- Axle α = mean of its two wheels' α (slip angles on one axle are physically close; the unloaded
  inside wheel is noisy). An excluded wheel (kerb) → the other wheel alone. A **lifted wheel** - one reading
  ≤ 0.05° while its partner reads ≥ 2° (a game reports 0 for a wheel off the ground) - is excluded the same
  way (owner, 2026-11-22: on the FH6 log FL 0 with FR 12.7 made a 6.3 front axle, about 2% of frames).
  Measured on the FH6 / F1 logs, the two wheels of an axle otherwise move together (one-wheel jumps: 21 / 17
  frames on FH6, none on F1), so the larger, the smaller or a jump-holding variant changed the noise by under
  5% - the mean is kept.
- **Axle equalisation (internal):** front and rear tyres peak at different angles, so a plain blend
  of the two would judge the front against the rear's window (or vice versa). Each axle has an
  internal per-axle learner (§9.5) whose only published use is its learned `Best`. The front angle is
  rescaled onto the rear's scale before combining:
  `α_F′ = α_F × k`, k = the median rear / front axle angle over the frames where the car is near its limit (owner, 2026-11-22;
  `SlipAngleBalanceEstimator`): speed-band-scaled lateral G at or above 85% of the cornering 98th percentile (trusted
  after 120 cornering frames; spikes above 6 g ignored), pulled toward 1 until 120 such frames
  (`k = exp(w ln median)`, `w = n / 120`). Never taught by a frame the learners refuse (pit, replay, restart,
  airborne, landing wobble, bad frame time), by contact (the impact verdict, light or heavy), by a slide (the body
  angle), by an axle beyond the source's angle cap (a spin) or a front under 0.3 deg. Per game, car, source and
  surface. A surface with no k evidence of its own STARTS from the same game, car and source's k on another surface,
  else from exactly 1 - at once, not eased over (owner, 2026-11-22; `SlipAngleBorrowRules.KScope`); its own evidence
  then takes over (`k = exp(w ln median + (1 - w) ln start)`); never another car or game. Saved with the bucket's All
  key (single values), so a restart carries on. Clamped to **0.3**-2.0 (was 0.5; F1 measures 0.29-0.46) and slewed at
  0.2 per second once the surface has evidence of its own. A source
  without both axles (fallback, a one-axle Custom source) never feeds or uses it. Replayed on the owner's logs it
  reached F1's 0.43 within the first hard corner (26.3 -> 27.1 s) and held 0.39-0.49, and held 1.00-1.10 on FH6. It
  replaces `Best_rear / Best_front` of the confirmed windows, which needed both axles' peaks confirmed: on F1 the rear
  crossed once in eight minutes, so k stayed 1 all session.
- `Raw.All = Max(α_F′, α_R) × W_Max + Min(α_F′, α_R) × W_Min` (degrees, "rear-equivalent"),
  defaults 0.85 / 0.15, editable under "Wheel combination" (same shape as `Aggregator`'s axle blend,
  not forced to sum to 1).
- Fallback tier: `Raw.All = α_R` (front unavailable, k unused).
- **Accuracy check (why one value is enough):** without equalisation, a car whose front peaks at 6°
  and rear at 9° would read an 8° rear (still under its own peak) as "beyond Max". With it, each axle
  is effectively judged against its own peak, and the single window learned on Raw.All behaves as the
  rear window. The only residual approximation is that both axles' windows have the same SHAPE
  relative to their Best (the Min/Max-to-Best ratios); real differences there are fractions of a
  degree, so accuracy is equivalent to two per-axle windows.
- Normalization (§9.4), projection (§9.6) and drift detection (§9.7) all run on `Raw.All` alone.

### 9.4 Normalization & key data points

- Mapping per §8: Start→15, Min→30, Best→60, Max→80, End(1.5×Max)→100 (owner). The settings tab and
  ShakeIt panel show Start as a read-only line ("Start: 2° (50% of Min)", or "—" when Min < 2°). Any α above End maps to exactly
  100 (`SlipAngleNormalizer.Normalize` clamps), and that clamped value is what drives the projection.
  Only the drift attenuator (§9.7) reads the unclamped overshoot, and it can only lower the output.
- **Learned-Max cap (owner, 2026-11-19), applied to the learned AND the typed Max:**
  **20°** when the game reports tyre slip angle natively (a car's best slip angle stays ≤ ~15°), so
  End ≤ 30°; **30°** for the car-angle fallback, so End ≤ 45°. Custom sources (the driver's own
  per-wheel tyre angles) use the native 20°. `SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity)`;
  the window learner caps every taught α, crossing snapshot, prior scale and published Max at it, and
  `KeyedSlipAngleLearner` caps every published / persisted / borrowed window
  (`SlipAngleWindow.CappedAt` scales Min and Best with Max, keeping the shape). A typed Max above the
  cap (up to 45°) is clamped to it on commit. A stored slot above the cap reads back scaled under it.
- **The 45° plausibility bound is unchanged** (`SlipAngleNativeSupport.PlausibleMaxDeg`, the gate's
  source ceiling, `MaxCeilingDeg` as the import/plausibility bound). Only α within 45° is considered
  for learning, but every frame is still mapped to 0–100 and drives the output.
- **Auto / Manual** (`AutoGenerate`), **Global / Per-Game** slots keyed (mode, game, source) —
  exactly `KeyDataPointSettings`' slot model, as a sibling `SlipAngleKeyDataSettings` holding
  Min/Best/Max (degrees, step 0.1, range 0.5–45).
- Validation: `0 < Min < Best < Max`, with a minimum gap of 0.2° **[call]**.
- **One triple** (owner), on `Raw.All` — exactly like WheelLock/WheelSlip's key data apply to their
  All channel. The one-time learned seed fills it from the Raw.All window learner (§9.5).
- Learning never stops under Manual (same rule as Lock): learned hints keep updating.
- **Kick-in gate:** a manual triple is applied only after `ManualOverrideGate` is satisfied for the
  context (30 s of moving in-game time AND handover confidence ≥ 0.95) — reuse the class as-is.
- One-time seed of manual slot from learned values on first eligibility, as Lock does - with exactly
  the window the display rule below shows (never a borrowed window).
- **Shipped cold-start defaults (Tier 1, nothing to borrow)** — owner: GT3 or slightly above:
  **Min 5.0° / Best 8.0° / Max 10.0°** (End 15°) — the midpoint between a GT3 slick (~4 / 6.5 / 8)
  and a road tyre (~6 / 10 / 12.5) under the §9.5 measurement rules — for both native and fallback
  sources.
- **Auto ⇒ read-only (owner):** under Auto, the Min/Best/Max editors (text box and +/− buttons) are
  read-only on BOTH the settings tab and the ShakeIt panel, bound to the same VM state
  (`ValuesEditable`), showing the learned value of the exact key, else the default (next bullet).
- **One display rule, the Lock/Slip one (owner, 2026-11-19; built 2026-11-20,
  `docs\full-mvvm-plan.md` §3.5).** `Core\SlipAngle\SlipAngleKeyDataModel` is the only source of the
  window the tab, the ShakeIt panel and the one-time seed use:
  - **Learned** = `LearnedWindow(learner, game, car, identity, blend)`: the EXACT persist key's published
    window - (game, car, source identity for the current identity tier: Native / Fallback for Plugin
    Internal, or the Custom composite; target All; the classified surface blend, Sealed on the menu) -
    capped by `MaxCapDegFor` (20° native and Custom, 30° car-angle). Null when the learner holds no such
    key. Never another key's window: the learner's borrowed window (another car, game or surface,
    `BucketWindow` for a missing key) is what the ENGINE uses cold, but it is not shown or seeded.
  - **Shown** (`Shown(auto, manualSlot, learned, identity)`, Lock/Slip's `KeyDataPanelModel.Resolve`
    rule): Auto = the learned window, else the source's default (`DefaultWindow` = the shipped
    5 / 8 / 10° capped for the source); Manual = the stored slot, else the source's default - **Manual
    with nothing stored shows the default**, never the learned window.
  - **Seed** (`TrySeed`, through `SlipAngleDisplayRuleKeyData` in the frame processor): the one-time
    Manual seed writes the learned window of the exact key - what is shown - and nothing when the key
    is unknown.
  - **After a restart** with SimHub on the menu there is no running context, so the plugin persists the
    last Slip Angle context in `RuntimeDocument` (`LastSlipAngleGame`, `LastSlipAngleCar`,
    `LastSlipAngleTier`, saved whenever the running game, car or identity tier changes; restored at Init
    after the learners are imported, `SlipAngleKeyDataContext`). The tab and the panel then form the
    exact key with no game running and show its persisted window; another car, or a first launch with
    nothing saved, shows the default. The restored tier also picks the Native / Fallback identity until a
    game runs. Older runtime files have no such fields and show the default until the first drive.

### 9.5 Learning the window (`SlipAngleWindowLearner`) **[call, owner delegated]**

One algorithm, three instances per context:
- **Front axle** and **rear axle** learners on α_F / α_R — internal, used only for the equalisation
  ratio k (§9.3);
- the **Raw.All** learner on Raw.All — its Min/Best/Max IS the published key data set.

Per key `(game, car, sourceIdentity, target, surfaceBucket)`, `target ∈ {front, rear, all}`. The
Raw.All learner is fed after equalisation, so it learns in the same rear-equivalent degrees it is
applied in; as k settles, its evidence decays toward the new scale (same decaying percentiles).

1. **Valid frames:** on ground, not in wobble/hop exclusion, ground speed ≥ 30 km/h, not in pit/
   replay/restart (`TelemetryLearningGate`). Pedal state is NOT required (learning is unconditional).
2. **Binning:** α in 0.25° bins, 0–30°. Each bin keeps a decaying high percentile (P90) of |lateral
   G| so the volume of drift frames cannot outvote at-the-limit cornering frames.
3. **Peak:** smooth bins (3-bin moving max-of-median **[call]**), `peakG` = max over bins with ≥ 20
   samples.
4. **All three values are MEASURED individually (owner):**
   - **Max — taught ONLY by traction crossings** (owner: "the same as SMax for lock/slip"), through
     a new `SlipAngleCrossingGate` (§9.5a). Max = the robust high estimate (P90, as SMax) of the
     crossing **onset snapshots**.
   - **Best / Min — from the grip curve, restricted to α ≤ Max** (frames past the limit cannot
     distort them):
     - **Plateau** = the contiguous bins below Max with P90 ≥ 97% of `peakG`; **Best** = its midpoint.
     - **Min** = the lowest angle on the RISING side where P90 first reaches **90%** of `peakG` —
       the S75 analogue ("getting into the window").
   - Ordering is enforced (`Min < Best < Max`, gaps ≥ 0.2°); if the rising side is not yet populated,
     Min falls back to the shipped ratio `Min = Best × 5/8` until it is.
   - Typical results: GT3 slick ~4 / 6.5 / 8; midway ~5 / 8 / 10; road tyre ~6 / 10 / 12.5.
   - The 97% and 90% thresholds are hidden constants (like Lock's own fixed percentages).
5. **Peak confirmation — guard against a driver who never reaches the limit (owner):**
   - The peak is **confirmed** only after **3 crossing events** (§9.5a) for that key (car + surface
     + source). A shape check also applies: if the curve is still clearly rising at the highest
     populated bin, the peak is NOT confirmed, regardless of sample count.
   - **Unconfirmed ⇒ the learned window is not used.** The published window stays the prior (borrowed
     tier, else shipped 5/8/10). Evidence may only RAISE it, never lower it: if grip is still rising at
     α_obs > prior Best, the prior window is scaled up proportionally so Best ≥ α_obs. A driver who
     stops at 5° therefore never gets Max = 5° and never gets a false strong "past Max" cue.
   - **Bins update only when visited** (per-bin decaying percentiles advance on that bin's own
     observations, not on time), so later cautious driving never erodes a confirmed peak.
   - Status text: "Peak not reached yet — using the borrowed/default window"; window quality capped
     (≤ 0.6) while unconfirmed.
6. **Two confidences [call]** — deliberately separate, following the lesson recorded in
   `ColdStartScale`'s remarks (a settledness-based confidence was tried in 1.0.6.8 and stalled near
   0.27 on real logs):
   - **Maturity / handover confidence** (drives the output scale AND the `ManualOverrideGate` 0.95
     kick-in test): Lock/Slip's model — a plain, saturating count, seeded from the persisted counts so
     a warm car starts warm — with a target sized so a car matures **within the first 2–3 near-limit
     corners** (owner):
     `maturity = clamp01(min(observations / 50, corners / 3))`
     - observation = a valid frame with |lateral G| ≥ 70% of the current `peakG`, counted at most
       10 per second (50 ≈ 5 s of near-limit cornering);
     - corner = one excursion: |lateral G| rises above 70% of `peakG` and later falls below 40%
       (stops one long sweeper maturing a car alone).
     It does NOT depend on past-peak evidence, so a driver who stays under the limit still matures and
     their manual triple still kicks in. Feeding `ColdStartScale`'s curve, corner 1 is felt at ≤ ~60%,
     corner 3 at ~100%.
   - **Learning never stops (owner, same as Lock/Slip):** maturity only gates the felt output scale
     and the manual kick-in. All three learners keep observing every valid frame forever, in Auto and
     Manual alike, after maturity, across sessions (decaying percentiles + persisted state); counters
     saturate at the overflow cap while the values keep updating — same rules as `GripLearner`/
     `LockAnchorLearner`.
   - **Output scale by maturity (owner: "exactly the same way Lock/Slip use"):** the Projected value
     goes through `ColdStartScale.ApplyAmplitudeScale(p, ColdStartScale.Compute(maturity,
     ColdStartTierFloors.FloorFor(tier)))` — the same seven-anchor curve, the same 0.5 hard floor, the
     same tier floors (0.5/0.6/0.7/0.8) and the same amplitude-dependent form. So a cold car with no
     borrowable reference caps a full-scale (100) reading at 50, never 100, until it has matured.
   - **Window quality** (display/status text and the live-vs-persisted `ColdWarmBlend` weighting
     only): coverage (populated bins across [Min, End]) × stability (variance of `Best` over the last
     N updates) × past-peak factor. Saturating, no steps. The past-peak cap (≤ 0.6) lives here only,
     and is shown to the driver as "Max not yet confirmed".
7. **Borrowing (owner, 2026-11-22; every rule in `SlipAngleBorrowRules`):** a slip-ratio source borrows its
   starting window within the same game and source (Tier 4 same car other surface, Tier 3 another car); an angle
   source (degrees, radians, car angle, Custom) only from the same game, car and source on another surface
   (Tier 4); never across games; else Tier 1 shipped defaults (§9.4). k: the same car's other surface only. Lateral
   G per speed group: never. The candidates are filtered by the rule, then ranked by the resolver extracted from
   `KeyedScaleLearner` (§4). Projected cold-start damping uses
   `ColdStartScale` with `ColdStartTierFloors` exactly as Lock/Slip.
8. **Surface — three buckets, from the game's own surface only (owner: WRC runs one car/tyre over several
   surfaces in one stage; 2026-11-22: "If you cannot get the surface information, then assume the surface as
   unknown. NO NEED TO GUESS"):**
   - Buckets: **sealed** (tarmac), **loose** (gravel/dirt), **low-grip** (snow/ice). Each has its own
     window (and its own front/rear/All learners).
   - **Source of the surface:** if the game reports a per-wheel surface type in raw data (spike checks
     EA WRC, Dirt Rally 2 and the rest), that is mapped to the buckets and used. Otherwise the surface
     is **Unknown** and the bucket is never switched: all learning stays in the starting (sealed) bucket.
     (The grip inference first built here flipped FH6 between buckets every few seconds on tarmac and
     scattered its learning; removed 2026-11-22, and the buckets it made are dropped on load.)
   - **Switching:** a continuous blend over ~1.5 s with hysteresis (a short dirt cut on a tarmac stage
     does not flip it); the blend weights the three windows like `SurfaceLooseFraction` does two.
   - An unseen bucket borrows the same car's window from another bucket (Tier 4), then learns from
     the corners driven on it; its maturity is counted separately.
   - Slip Angle only — Lock/Slip keep their existing `SurfaceLooseFraction` handling.
   - Diagnostics: active bucket, blend weights, source of surface (Game / Unknown).
9. Persisted in `QAdvanceFeedback.Parameters.json` (new section, `RuntimeDocument` version bump),
   counters saturate (overflow audit rule).

### 9.5a The crossing gate (`SlipAngleCrossingGate`)

**Shared, parameterised core (owner, revised):** one `CrossingGate` class configured by
`CrossingGateParameters`, used by Lock, Slip and Slip Angle. Verified feasible before deciding: the
two shipped gates' `Observe` logic is identical apart from variable names, their constants are
identical, and the ONE substantive difference — Slip's teaching weight is fixed (1.0 / non-crossing
weight) while Lock multiplies its corner detector's confidence — becomes a `TeachingWeightMode`
parameter.

| Parameter | Lock | Slip | Slip Angle |
|---|---|---|---|
| Source ceiling | 100 | 100 | 45° |
| Rise threshold | 1.0 | 1.0 | 0.5° |
| G fall | −0.05 | −0.05 | −0.05 g (lateral) |
| Hold / trend frames / `HoldMaxFrames` / nominal frame | 1.0 s / 5 / 500 / 1/60 | same | same |
| Walk-back G band / min saturated wheels | 0.10 / 2 | 0.10 / 2 | 0.10 / n/a |
| `TeachingWeightMode` | MultiplyConfidence | Fixed | Fixed |
| Enabled switch / non-crossing weight | own | own | own (`SlipAngleCrossingGateEnabled`, default on) |

- Each channel keeps its OWN instance, switch and weight, so the owner's standing rule ("disable one
  side only") still holds.
- `LockCrossingGate` / `SlipCrossingGate` remain as thin named wrappers holding the shipped
  parameters, so settings, call sites and their existing test files are unchanged.
- **Behaviour-preservation proof (mandatory):** verbatim frozen copies of both shipped gates live in
  the test project; identical randomised streams (spikes, NaN/zero dt, saturated wheels, back-to-back
  crossings, hold-window edges) through old and new must be bit-identical; every existing gate test
  passes unedited.

Each of the original design's hard-won rules maps one-to-one onto the Slip Angle instance:

| Lock/Slip rule | Slip Angle form |
|---|---|
| Candidate filter | \|lateral G\| ≥ 85% of the current surface bucket's `peakG` |
| Causal trend (current vs 5 frames back, never centred) | dα ≥ +0.5° **and** d\|lateral G\| ≤ −0.05 g → crossing |
| Arm only at the limit (§8.4 rule 3) | A crossing may only fire on a candidate frame; never in airborne, landing wobble or hop exclusion (a landing is the exact signature of a false crossing) |
| Teach the onset snapshot (§8.4 rule 1) | Every frame in the hold window teaches the α captured AT the crossing, never its own — so a steady or runaway α under still-falling G teaches the earlier crossing value, not a higher one (owner's "look back to the previous window value") |
| One crossing per event (§8.4 rule 2) | No re-arm while a window is live |
| Hold window | `CrossingHoldSeconds` 1.0 s |
| Weight 0, not skip (§2) | Non-crossing candidates fold in at weight 0, keeping maturity/decay economics |
| Robustness (§8.7) | dt ≤ 0/NaN ages by a nominal frame + `HoldMaxFrames` backstop; non-finite/negative α skipped before the trend history; the threshold check tests the value actually taught; a crossing whose snapshot is unusable is rejected (no window opens) — **never** a live fallback (2026-11-19, see `docs\slip-smax-crossing-gate-design.md` §10) |
| Walk-back (2026-11-19) | Steps back only onto frames that rose from their own predecessor, so the snapshot is never the flat frame a rise started from |

Crossing events also drive peak confirmation (§9.5.5). Applies identically to the front, rear and
Raw.All learners (each has its own gate instance per key) **[call]**.

### 9.5b Crash / contact protection (owner: "the same or similar pattern as Lock/Slip")

**Shared, parameterised cores (owner):**
- **`CorroboratedLevelEstimator` + `CorroborationParameters`**, extracted from `GripLearner`
  (`AdaptivePeakState` + `ApplyEvidenceWeightedObservation`): plausible cap, max plausible jump,
  raise/lower tolerance + floor, raise/lower cooldown, confidence base — unit-neutral names.
  `GripLearner` uses `CorroborationParameters.GripLearnerDefaults` = today's exact constants
  (cap 8 g, jump 3 g, 0.30 / 0.15, floor 0.05, cooldown 3 / 40, base 0.10); its persisted raise/lower
  hit fields keep their JSON names. Slip Angle uses its own sets (Max in degrees, `peakG` in g).
- **`ImpactGuard` + `ImpactGuardParameters`** (new): multi-axis jump detection, retro/forward
  exclusion window, yaw-consistency check. `Enabled = true` for Slip Angle; **`Enabled = false` for
  Lock/Slip** so their behaviour is unchanged (turning it on there later is a one-parameter decision
  for the owner after seat time).
- **Behaviour-preservation proof (mandatory):** a verbatim frozen copy of today's
  `ApplyEvidenceWeightedObservation` in the test project; identical randomised streams through old
  and new must be bit-identical; every existing `GripLearner` test passes unedited.

Mirrors `GripLearner`'s three layers (`MaxPlausibleG` reject, `MaxPlausibleJerkG` jump screen,
event corroboration with an asymmetric raise/lower band) and adds two physical checks. Owner's
scenario it must defeat: true Max 8°; at α = 4° the car is hit, lateral G spikes; α then keeps rising
while lateral G ramps back down → a fake "crossing".

1. **Reject:** |G| > 8 g on any axis, or non-finite → frame teaches nothing.
2. **Impact exclusion window:** a frame-to-frame jump > 3 g in lateral, longitudinal or vertical G
   marks an impact. For the preceding 5 frames (retroactively, from the trend buffer) and the next
   **1.5 s**: no learning, no crossing candidates, no maturity counting, no surface-inference input;
   the crossing gate's trend history is cleared and any live hold window is cancelled (its snapshot is
   discarded, not taught). This alone defeats the owner's scenario.
3. **Yaw-consistency check (soft contacts under 3 g):** for a tyre-driven car,
   `a_lat ≈ V·r` near steady state. Frames where |measured lateral G − k·V·r/g| > 0.3 g cannot be
   candidates or crossings and teach nothing. **Revised after the spike** (yaw sign not uniform):
   `r` is converted deg/s → rad/s and multiplied by a per-game relative sign `k ∈ {+1, −1}` that Layer 2
   learns by correlating lateral G with V·r over ≥ 3 s of cornering frames (|lat G| ≥ 0.3 g); until `k`
   is known, and on frames with |dVlat/dt| > 0.15 g (transients the steady-state relation does not
   cover), the check is skipped (the caller passes a null yaw / dVlat to `ImpactGuard`).
4. **Curve-plausibility check:** a crossing may arm only if the lateral G just before it is ≤ 115% of
   the learned curve's expected value at that α AND ≤ 115% of the learned `peakG` (a tyre cannot make
   more grip at 4° than its own peak at 8°). Skipped while the curve is still empty (cold), where the
   other layers carry it.
5. **Corroboration (GripLearner pattern):** Max moves only when a new level is matched by several
   SEPARATE crossing events inside a tolerance band (raise: ±30%, short cooldown; lower: ±15%, long
   cooldown — lowering needs more evidence than raising). Peak confirmation needs **3 crossings within
   ±15% of each other**. A single fake crossing can never pull Max down.

All five gate every learner input (crossings/Max, curve/Best/Min, `peakG`, maturity, surface
inference). They never touch output — a crash that really throws the car into a slide is still felt.
Diagnostics: impact flag, exclusion time remaining, yaw-consistency residual. Constants are hidden
(like GripLearner's) and pinned by tests, including the owner's scenario as a named regression test.

### 9.6 Projection (Layer 5)

`OutputProjector`/`ProjectorSettings` reused with a new `ProjectionChannel.SlipAngle` shipped preset
**[call]**: anchors input **30 / 60 / 80**, smooth ranges **3 / 6 / 3**, outputs **10 / 20 / 60** (owner
2026-11-22: Best 60 → 20 with a smooth range of 6, Max 80 → 60 with a smooth range of 3; was 30 / 62 / 78,
3 / 2 / 2, 10 / 30 / 60), End 100→100, **Start input 15** → Start output 0 (owner 2026-11-19: everything below
the Start angle outputs the Start output). The anchors sit on the learned Best / Max (§9.4 maps them to 60 /
80), so each reads exactly its output in the middle of its plateau. The shipped default, "Restore all
defaults" and the Curve preset give it; a saved config still on the previous shipped curve, unedited, moves to
it on load; a curve the driver changed keeps every value. Pulse-at-max available,
off by default **[call]**. Order: project → cold-start maturity scale (§9.5.6) → drift attenuation
(§9.7) → landing fade → pulse.

### 9.7 Drift attenuation (`DriftAttenuator`, pure, in `Core\Projection`)

Input per frame: combined Nᵤ (smoothed), the projected value `calc`, dt, the drift start (Nᵤ) and
the throttle. Output: final value.

```
Normal ──(Nᵤ > DriftStart for HoldTime, rise during hold ≤ StabilityRange)──▶ Drifting
Drifting: factor ramps 1.0 → ToPercent along S(t)=t^γ/(t^γ+(1−t)^γ) over Duration;
          out = Min(factor × calcPeak, calc)   (calcPeak = highest calc since entering Drifting)
Drifting ──(confirmed decrease)──▶ Recovering (locked = factor × calcPeak at that frame)
Recovering: out = Min(locked, calc)
Recovering ──(confirmed increase)──▶ Normal (out = calc directly; drift counting may restart)
Recovering ──(Nᵤ < 30, i.e. α < Min)──▶ Normal
Drifting / Recovering ──(throttle clear, below)──▶ Normal
```

- **Drift start (owner, 2026-11-20).** Drift counting starts at the angle
  `Max(1.2 × Max, Min(30°, 1.5 × Max))` (`SlipAngleDriftStart`), passed as its Nᵤ (never below 80):
  Max 10° → 15°, Max 15° → 22.5° (End, Nᵤ 100), Max 22° → 30°, Max 35° → 42°. So for a Max up to
  20° a drift is counted only past End. Previously it started at Max (Nᵤ 80), which was too sensitive.
  If the rise during the hold window exceeds `StabilityRange`, it is escalating (spin), not a drift:
  stay Normal at full output; the window slides, so a slide that later stabilises is still detected.
- **Locked output (owner, 2026-11-20).** The scale factor is applied to the highest calc since the
  drift began, never to a falling one. While the angle comes down the output holds the scaled-down
  value until the unscaled calc drops below it, then follows calc: 100 eased to 40 stays 40 while calc
  falls 100 → 40, then follows calc down. Previously Recovering used `Min(frozen × calc, calc)`, so the
  output fell from 40 at once.
- **Throttle clear (owner, 2026-11-20).** While the flag is on (Drifting or Recovering) and α ≤ Max
  (smoothed Nᵤ ≤ 80), each frame sets `ref = Min(ref, throttle)` (the first such frame starts it at the
  current throttle); the flag clears when `throttle > ref + ThrottleRiseClear` or
  `throttle ≥ ThrottleLevelClear`, so the next throttle-on slide is felt in full. The reference is kept
  if the angle goes back above Max and dropped when the flag clears. No throttle reading never clears.
  The Test Effect has no throttle, so it never uses this rule.
- Example (owner): drift at 80 → attenuated to 32; release 100 ms into the ramp with factor 0.8 and
  calc 80 → output 64 (now held at 64 while calc falls).
- **Noise handling:** Nᵤ is smoothed by an EMA (`SmoothingTime`). "Confirmed decrease" = smoothed Nᵤ
  ≤ (peak since entering Drifting) − `SwingMargin` held for `TrendConfirmTime`. "Confirmed increase"
  = smoothed Nᵤ ≥ (trough since entering Recovering) + `SwingMargin` held for `TrendConfirmTime`.
  Small wobbles never flip state.
- Airborne resets to Normal; landing wobble holds timers reset.

**Configurable parameters (settings page, "Drift handling" / "Landing" / "Noise" groups):**

| Parameter | Default | Range | Why the default |
|---|---|---|---|
| HoldTime | 250 ms | 50–2000 | A rally flick is past-peak ~0.4–0.8 s; detection must finish inside it |
| StabilityRange | 20 pts | 2–60 | Rise allowed during hold before it counts as escalating |
| ToPercent | 40 % | 0–100 | Owner |
| Duration | 120 ms | 0–2000 | Hold + ramp ≈ 370 ms, inside even a short flick (owner: must be short) |
| Gamma (S-curve) | 2.0 | 0.5–6 | Symmetric ease in/out |
| ThrottleRiseClear | 20 pts | 5–100 | Owner 2026-11-20: a held-slide feather is ~5–15 pts, a deliberate exit press 20–40 |
| ThrottleLevelClear | 90 % | 50–100 | Owner 2026-11-20: near flat out always clears |
| SmoothingTime | 50 ms | 0–500 | Kills surface chatter, keeps response |
| SwingMargin | 8 pts | 1–40 | **[call]** 8 not 5: a held gravel drift wobbles more than 5 pts |
| TrendConfirmTime | 80 ms | 0–500 | **[call]** prevents one-frame spikes confirming a trend |
| MaxFlightDuration | 1000 ms | 50–5000 | Owner (swapped 2026-11-19) |
| MaxWobbleDuration | 500 ms | 0–5000 | Owner (swapped 2026-11-19; real settling ~0.4–0.6 s) |

### 9.8 Published properties (additive)

- `QAdvanceFeedback.SlipAngle.Raw.All` (degrees, the combined value — the product property the
  key data and projection apply to; null when unavailable, never a fake 0).
- `QAdvanceFeedback.SlipAngle.Normalized.All` (0–100).
- Per-wheel/per-axle angles and the equalisation ratio k are **diagnostics only** (below), since the
  combination is internal.
- `QAdvanceFeedback.SlipAngle.Projected.All` (0–100, final: attenuated, faded, pulsed) — what the
  ShakeIt effect and G-Force read.
- Diagnostics (appended at the end of diag/CSV columns): per-wheel α, per-axle α, k, per-axle
  learned Best, source tier (native/fallback/none), drift state, drift factor, learned Min/Best/Max
  of Raw.All, handover confidence, window quality, borrowing tier, wobble remaining, trigger gate open.

### 9.9 Settings page: new "Slip Angle" tab

Same structure, order and visual language as the Wheel Lock tab, MVVM from the start:

1. Orange fallback warning (bound, Plugin Internal only).
2. **Sources:** mode (Plugin Internal / Custom), Custom fields.
3. **Trigger:** Trigger By + the threshold(s) visible for the selection.
4. **Wheel combination:** W_Max / W_Min.
5. **Output data and shaping:** Auto Generate, Global/Per-Game, Min/Best/Max editors (one set, on
   Raw.All) with `[Learned Value: x.x]` hints, reset to defaults, status (handover confidence; "Max
   not yet confirmed" from window quality).
6. **Two graphs**, as Wheel Lock: left = Normalized → Projected curve editor with dashed markers at
   30/60/80; right = Source (Raw.All degrees, 0 → End) → Projected, sampled via a Slip Angle
   equivalent of `SourceToProjectedGraph`.
7. Projection anchors editor + pulse (same controls as Lock).
8. **Drift handling**, **Landing**, **Noise** groups (§9.7 table).

### 9.10 Native ShakeIt effect

- `QAdvanceFeedbackSlipAngleContainer`, display "QAdvanceFeedback Slip Angle", category
  `QAdvanceFeedback`, `OutputMode.Any`, ONE placement (no routing dropdown), returns 0 when the
  plugin frame is older than 500 ms. Tone defaults **[call]**: low 40 Hz, high on 25 Hz, noise off.
- Filter: projection pass-through with the read-only source→projected graph in the response-curve
  slot (degrees on x), redrawn on projector/key-data change and once a second under Auto.
- Panel (minimal, owner): Trigger By; Brake/Throttle threshold(s) per selection; Auto/Manual;
  Min/Best/Max editors (text box + +/−, read-only under Auto, §9.4); orange fallback warning;
  status text; Test Effect.
- Two-way sync through `SettingsHub` with new fields: `SlipAngleTriggerBy`,
  `SlipAngleBrakeThreshold`, `SlipAngleThrottleThreshold`, `SlipAngleKeyDataAuto`,
  `SlipAngleKeyDataValues`, `SlipAngleProjector`, plus nativeness in
  `SourceAvailability`.

### 9.11 Slip Angle Test Effect (ShakeIt panel)

- One slider for **Raw.All** (degrees), **264 px** wide (owner, 2026-11-20: 60% of the earlier 440,
  which was cut off in the ShakeIt panel), range **0 → 300% of Max**.
- A vertical tick crossing the slider at its middle (= End, 150% of Max, normalized 100), labelled
  **"100%"** above the tick.
- The test engine feeds the slider value as Raw.All directly and runs the real normalization,
  projector AND `DriftAttenuator` with the configured parameters (no learner, no pedal gate, no
  landing logic, full confidence).
- **Dot:** x = `Min(slider, End)` (i.e. normalized capped at 100); y = the **actual output** after
  drift attenuation, pulse-free. Left of the tick the dot rides the curve; past it the dot sits at the
  top-right; once drifting is detected it keeps x and drops along the ramp to ToPercent.
- Same session/auto-off/publish-to-real-properties behaviour as the existing tests
  (`TestEffectSession`, 10 s auto-off, release on off/unload).

---

## 10. G-Force: "Integrate Slip Angle"

- New `GForceSettings.IntegrateSlipAngle` (default **ON — for fresh installs AND upgrades**; owner
  accepted that existing users' G-Force feel changes on update; a settings file without the field
  loads ON) and `WheelSlipAngleShakeScale`
  (default `DefaultShakeScaleFor(mode)`, like the other two scales), on the G-Force tab and the
  G-Force ShakeIt panels (new hub field `IntegrateSlipAngle`).
- `GForceEngine.Compute` gains `slipAngleAll0100`; per frame:
  - `gate = (IntegrateWheelLockAndSlip && Max(lock, slip) ≥ T) || (IntegrateSlipAngle && angle ≥ T)`
    on unscaled values;
  - `drive = Max(enabled contributions)` among `lock × sL`, `slip × sS`, `angle × sA`.
  - Everything downstream (modes, wave, feeling, tail, re-arm) unchanged. Angle input is the final
    Projected Slip Angle (already drift-attenuated).
- Wording: "Higher of G-Force or wheel lock/slip/angle" and the other mode/description strings
  updated (EN + zh-Hans). The `ShakeApplyMode` enum value names stay (persisted).
- The G-Force Test Effect: Lock/Slip bar gains an Angle bar **[call]**.

## 11. Persistence, docs, tests

- Settings: new `SlipAngle` object in `QAdvanceFeedbackSettings`; `RestoreDefaults` carries its key
  data across (same rule as Lock/Slip).
- Runtime: new learner section; `RuntimeDocument` version bump; unknown older files load cleanly.
  Since 2026-11-20 also the last Slip Angle context (`LastSlipAngleGame/Car/Tier`, additive, §9.4) for
  the restart display.
- Tests (net8, link-compiled): window learner (plateau, past-peak, borrowing, confidence), drift
  attenuator (every transition, owner's numeric examples, noise immunity with synthetic jitter,
  escalation), landing wobble mapping, gating, native detection, axle combination, branch
  nativeness, `N⁻¹` + dot-on-curve property test, G-Force max-of-three, published-name pins.
- Docs: `architecture.md` + zh-Hans (layer map, algorithms, file map), both READMEs, screenshots for
  the new tab and panel, a `docs\slip-angle-report.md` at the end.

## 12. Left to the builder

- **Spike first:** extract SimHub (`tools\fetch-simhub-refs.sh` style, full DLL set) and verify per
  game the raw slip-angle property paths, wheel order and units; units of `YawChangeVelocity` and
  `VerticalAcceleration`; that `FlightIsOnGround` behaves as expected; and which games expose a
  per-wheel surface type in raw data (EA WRC and Dirt Rally 2 first) for §9.5.8. Adjust §9.1's table to what is
  real; drop a game rather than guess.
- Internal class/file names beyond the frozen container name; exact UI copy (EN/zh).
- Bin smoothing details and decay rates in the learner, within §9.5's rules.
- How the extracted tier resolver is shaped, provided `KeyedScaleLearner` behaviour is unchanged
  (existing tests green, unmodified).
