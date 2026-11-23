# Slip Angle: final report

Branch `feature/slip-angle`, baseline `300461b`, 2026-11-18 → 2026-11-19. Design:
`docs\slip-angle-design.md` (it carries the post-spike revisions). Plan: `docs\slip-angle-plan.md`.
Telemetry survey: `docs\slip-angle-spike-report.md`. How the code works now: `docs\architecture.md`,
in the sections "Slip Angle: how it works and why", "Shared cores" and "Lock/Slip additions".

This report lists what shipped, the evidence behind it, every deviation from the design and the ruling
behind each one, and what is still open for the owner. Rulings are numbered R1–R40 in
the order the build recorded them.

## 1. What shipped, by phase

| Phase (design §5) | Tasks / commits | What it delivers |
|---|---|---|
| 0. Spike | T0: `ffd6947`, `fb66694`; design rulings `ef7c771`, `7877726` | `docs\slip-angle-spike-report.md`. Every SimHub 9.13.2 `GameRegistry` id was given a verdict (native direct / native computed / provisional / unverified / fallback / N/A), with raw paths, wheel order, units, per-wheel surface, and the `FeedbackData` semantics (yaw, local velocity, vertical G, `IsFlying`, `FlightIsOnGround`). |
| 1. Lock/Slip Test Effect group sliders, dot on the curve (§6) | T1–T3: `1c02644`, `de48a3d`, `95a2951`, `53d80bc` | `KeyDataNormalizationInverse` (bisection N⁻¹, smallest source), `TestGroupSliders`, Single / Front-Rear / Left-Right channel sliders on the Lock/Slip ShakeIt Test Effect. A property test asserts the dot is on the curve. |
| 2. Lock/Slip fallback warning (§7) | T4–T5: `3f6eb41`, `75c4e25`, `dd00897` | `BranchNativeness`, `NativenessBroadcaster` (0.5 s hold), `FallbackWarningViewModel`. An orange label on the Wheel Lock/Slip tabs and ShakeIt panels, for the Plugin Internal source only. |
| 3. Shared cores (§4, §9.5a, §9.5b) | T6–T9: `e660731`, `7b19037`, `531ae57`, `f83e27f` | `CorroboratedLevelEstimator`, `CrossingGate`, `ColdStartTierResolver` (each extracted with a frozen-copy equivalence proof) and the new `ImpactGuard`. Checkpoint: Lock/Slip bit-identical, suite 1972 green. |
| 4. Slip Angle channel (§9) | T10–T24: `aef1c4c` … `74bacd9` | Telemetry inputs and the native table with promotion and unit detection (T10). Raw calculator (T11). Gate (T12). Grip curve bins (T13). Window learner (T14). Surface classifier, keyed learner, borrowing and v12 persistence (T15). Normalizer and key data (T16). Drift attenuator (T17). Projected engine and preset (T18). Channel settings (T19). DataUpdate wiring and published properties (T20). Settings hub fields (T21). Settings tab (T22). Native ShakeIt effect and panel (T23). Test Effect (T24). |
| 5. G-Force "Integrate Slip Angle" (§10) | T25: `7a729d5` | `IntegrateSlipAngle` (default on, including on upgrade), `WheelSlipAngleShakeScale`, max-of-three drive, a G-Force Test Effect angle bar, and the tab and panel switches. |
| Docs | T26 (this commit) | Both architecture docs and both READMEs, the screenshot harness, the images, and this report. |

Published (additive, appended after every earlier name and CSV column):

- product properties `SlipAngle.Raw.All` (degrees, null when unavailable), `SlipAngle.Normalized.All`
  and `SlipAngle.Projected.All`;
- 33 `Diag.SlipAngle.*` diagnostics.

Persistence:

- `RuntimeDocument` version 12 adds `SlipAngleLearners`;
- the settings file gains a `SlipAngle` object and `GForce.IntegrateSlipAngle` /
  `WheelSlipAngleShakeScale`.

The native ShakeIt effect is `QAdvanceFeedbackSlipAngleContainer`. Its type name is frozen and its
display name is "Slip Angle".

## 2. Spike results (summary)

The method was clean-room. The SimHub 9.13.2 assemblies were unpacked and decompiled **outside the
repo**. Only structure was recorded (member names, types and paths), never code. SimHub was never run,
so every path is "the field exists in the raw-data type", not "observed live".

- **Native, direct** (17 ids plus Rennsport): ACC, AssettoCorsaRally, AssettoCorsaEVO, F12023,
  F12024, F12025, FM7, FM8, FH4, FH5, FH6, LFS (a tangent), Wreckfest2, ProjectMotorRacing, Drift21
  (radians), KartKraft, BusBound. Rennsport has a reader but no `GameRegistry` literal; the table
  keys it as `Rennsport`.
- **Native, computed**: RFactor2 and LMU, as `atan2(lateral patch velocity, |longitudinal ground
  velocity|)`.
- **Provisional**: the NASCAR Heat 4/5, Tony Stewart and SRX reader family (terrain speeds whose
  semantics are not proven).
- **Unverified**: iRacing. Its raw data is a runtime dictionary, so it runs on the fallback.
- **Fallback only**: every other game with tyres (AC, AMS2, R3E, EA WRC, Dirt Rally 1/2, F1 ≤ 22, GT7,
  BeamNG, ...).
- **Units:** only Drift21, LFS and the computed rows are known. Every other native unit is
  undocumented, which led to unit auto-detection (R6, then R16).
- **`YawChangeVelocity`** is deg/s, but its sign is not uniform across readers (R12). **`LocalVelocity`**
  is m/s with per-game remapped axes. **`VerticalAcceleration`** is populated only by iRacing and
  Rennsport (R7). **`IsFlying`** is derived by SimHub from free fall. **`FlightIsOnGround` is never
  written for car games** (R5).
- **Per-wheel surface:** values exist for many titles, but their *meaning* is documented only for
  rFactor 2 (an enum order) and GT7 (enum names). Those are the only two that are mapped (R13).

## 3. Equivalence evidence for the shared cores

Each extraction was proven bit-identical against a verbatim frozen copy of the pre-extraction code in
`QAdvanceFeedback.Tests\Frozen\` (kept as test oracles, R1). In every case the pre-existing tests pass
unedited.

| Core | Test | Streams |
|---|---|---|
| `CrossingGate` (Lock/Slip wrappers) | `CrossingGateEquivalenceTests` | 200 seeds × 3000 frames per wrapper: spikes, NaN/zero dt, saturated wheels, back-to-back crossings, hold-window edges. More than 5 crossings per seed are asserted, so the streams exercise the gate. Exact-threshold boundary streams too. Constants pinned. |
| `CorroboratedLevelEstimator` (`GripLearner`) | `CorroboratedLevelEstimatorEquivalenceTests` | 200 seeds × 5000 values at the shipped defaults; non-default plausible caps 4.5 / 6 / 12; confidence schedules; `GripLearner` public state compared at three caps. |
| `ColdStartTierResolver` (`KeyedScaleLearner`) | `ColdStartTierResolverEquivalenceTests` | 300 seeds × 600 operations, both through the shipped learner and through the bare resolver over the same store. |
| `ImpactGuard` | `ImpactGuardTests` | New code, so no frozen copy. `Enabled = false` for Lock/Slip (it is not wired there). The owner's crash scenario is a named regression test in the learner tests. |

## 4. Deviations from the design, and the ruling behind each

| Design says | Built | Ruling and reason |
|---|---|---|
| §9.2 airborne via `FlightIsOnGround == false` | `IsFlying > 0` only | **R5.** `FlightIsOnGround` is never written for car games, so it would gate every car title. |
| §9.2 unit: radians when sustained max < 1.0 at ≥ 0.8 g during the 5 s promotion window | Episode voting: an episode is ≥ 0.3 s at ≥ 0.8 g, ending below 0.5 g. It votes degrees if ≥ 70% of its frames are ≥ 1.0. The unit is decided after ≥ 3 episodes with ≥ 2/3 agreement. An undetermined unit means no promotion. | **R6 → R16.** Detecting in the first second locked in the wrong unit on a spin. **R17** accepts the 2-spins-1-corner residual (it recovers next session). **R15** keeps the 45° cut-off: the proposed π/2 cut would have discarded every degrees game's cornering frames. |
| §9.1 "implausible > 45° rejects" | Pinned-unit rows treat > 45° as neutral. Unverified rows count it toward a block only while the unit is undetermined and \|lat G\| < 0.3 g. | **R14.** Drift titles legitimately exceed 45°. |
| §9.2 hop exclusion everywhere | Only where `VerticalG` exists (iRacing, Rennsport). Elsewhere `IsFlying` and `ImpactGuard`'s vertical jump cover hops. | **R7.** |
| §9.1 fallback `atan2(Vlat − b·r, Vfwd)` | `\|atan2(Vlat, max(Vfwd, 1))\|`. The yaw residual uses a learned per-game sign k. The check is skipped until k is known, and when \|dVlat/dt\| > 0.15 g. | **R12.** The yaw sign is not uniform across readers. |
| §9.5.8 map game surfaces (EA WRC, DR2 first) | Only rF2 and GT7 are mapped (GT7 by name only). Everything else is inferred from grip. | **R13.** Values are used only where the spike documents their meaning. EA WRC and DR2 have no per-wheel surface at all. |
| §9.5a crossing gate saturated-wheel guard "n/a" | `Observe` is passed 0 saturated wheels | **R2.** |
| §9.3 silent on one-axle native | One available axle: Raw.All = that axle (front × k) | **R18.** Consistent with fallback using the rear. |
| §9.9 / §7 warning wording | The warning keys on tier Fallback with the Plugin Internal source. Tier Unknown with no Raw.All shows the status "No slip-angle data from this game" and no warning. | **R19.** |
| §9.5 step 3 `peakG` over all bins | `peakG` for Best/Min = the highest bin below Max | **R20.** Consistent with step 4 (α ≤ Max). |
| §9.5b.5 corroboration by "several separate events" | The first crossing alone never teaches Max (R21). The corroborator must lie on the same side of the level and must have completed after the level last moved by more than the floor: the persisted "level epoch" (R22). An original-cluster crossing completing after Max moved counts (R23). | Without these, a cluster's own scatter could walk Max, or one fake crossing could move it. |
| §9.4 one Auto/PerGame pair | Per-source Auto / PerGame flags, exactly like `KeyDataPointSettings` | **R24.** The owner's same-pattern rule overrides §9.4. |
| §9.7 escalation on the smoothed rise | rise = max(raw rise, smoothed rise). Smoothing still governs entry and trend confirmation. dt is capped at 0.1 s. | **R25.** A freshly seeded EMA hides a spin's onset. |
| §9.7 (implied) escalation can end a drift | No escalation exit from Drifting | **R26.** Owner tuning; `StabilityRange` is configurable. |
| §9.4 manual triple kick-in | The manual window stays gated by `ManualOverrideGate` | **R28.** The owner asked for the kick-in. |
| §9.5a enabled switch only | `CrossingGateEnabled` is wired with Lock/Slip parity (R29). With it off, runs still use snapshot/rollback and the 5-frame delay line. The ring/epoch commit only on a natural end, and a shape-only confirmation needs ≥ 2 runs (R30). | Gate-off must not become a back door around the crash protection. |
| §10 `IntegrateSlipAngle` owner | Added in Settings (T21); engine and UI in T25 | **R31.** Default true; a file without the field loads true. |
| §9.10 hub key data | The hub reads and writes the key data of the current Slip Angle tier identity, one identity per read/write; a refused commit is surfaced | **R32.** Writing both native and fallback slots corrupted one of them. |
| §9.6 "Best → 30%, Max → 60%" | The curve reads 29.75 / 60.73 at Best / Max | **R33.** The shipped 62 / 78 inputs plus flatten put the plateau edges at 60 / 80, exactly like Lock's. |
| §9.9 section order 1–8 | Section order follows the Wheel Lock tab | **R34.** Same-pattern rule. |
| §9.9 hub/draft | Key data edits are against the draft source (R35). Restore keeps Slip Angle custom sources (R36). | Tab review. |
| §9.10 display name "QAdvanceFeedback Slip Angle" | "Slip Angle" | **R37.** Owner rule 2026-11-18: no prefix on effect names. The type name is frozen. |
| §9.10 gain (unstated) | 75%, like Lock/Slip | **R38.** |
| §9.11 dot after drifting | The dot eases between Max and End while drifting | **R39.** Matches the owner's "slight drift" rule (80 → 32). |
| §9.5 / §9.10 threading (implicit) | The learner is touched only on the data thread. UI/CSV/ShakeIt read immutable per-frame snapshots. Persistence exports on the data thread. | Ledger carry-notes for T14–T15: the learner's getters mutate. |
| §7 broadcast payload extension | `SourceAvailability` stays notification-only. Nativeness is read through `IPanelHost`. | **R9.** |
| Plan T22 curve editor reuse | Rebuilt as bindable `NormalizedCurveGraph` / `GraphFurniture` controls inside the MVVM view | **R4** allowed an adapter. A bindable control was cleaner and is MVVM-clean. |
| Design header "approved approach A" | Unchanged. Frozen copies in tests kept (R1); `ITestEffectHost` member added to every implementation, including test fakes (R3). | n/a |

Also from this task: `docs\architecture.md` stated "Switching ShakeApplyMode does NOT rewrite the two
scales" (2026-09-06). The code follows the owner's **2026-09-07** reversal:
`GForceShakeViewModel.Mode` resets the Lock, Slip and now Angle scales, and the trigger, when a mode
is picked on the G-Force tab (previously `SettingsControl.OnShakeApplyModeChanged`). The settings property itself does not. Both architecture
docs and both READMEs now describe the code as it is. The READMEs had the same contradiction, and also
the wrong per-mode numbers.

## 5. Known limitations and owner follow-ups

### Fixed in the final review wave

- **Washboard could defeat drift detection (was the high WRC flag).** The escalation check now counts
  the raw rise only from samples taken while the EMA is young (< 3 τ since seed / Reset / landing) and
  the smoothed rise otherwise, so raw ±10-point surface noise no longer blocks a held drift. dt is
  capped at `min(0.1 s, HoldSeconds / 2)`, so even the 0.05 s minimum hold needs two frames.
- **Pausing moved the key-data editor to the wrong slot on fallback games.** The hub, the tab and the
  ShakeIt panel now pick the key-data identity from `SlipAngleIdentityTier` (the last in-session
  confirmed tier for the current game, kept across pause / menu, reset on a game switch); the
  immediate clear still drives the orange warning.
- **UI copy now matches the code** (EN and zh-Hans): the fallback help names body slip from lateral
  and forward velocity (no yaw rate); the wheel combination help says each axle's two wheels are
  averaged, the front is equalised to the rear, and the max / min weights apply between the axles
  ("Max / Min axle weight"). `settings-slip-angle.png` regenerated.
- **The G-Force shake block is on a view model** (`GForceShakeViewModel`, ruling R40) instead of
  code-behind poking `IsEnabled` / `Value`; behaviour unchanged (`settings-gforce.png` renders
  byte-identical).
- **Minors fixed:** the drift attenuator resets on a frame gap (`NoteGap`); the Slip Angle health
  entry clears after 300 consecutive good frames (`HealthRecoveryTracker`); `CrossingGateParameters`
  `LockShipped` / `SlipShipped` are built from the wrapper constants and the dangling "below" doc
  references point at `CrossingGate.Observe` and the frozen copies; `SliderToMaxRatio` is
  `2 × SlipAngleWindow.EndToMaxRatio`.

### Open

1. **Spins slower than about 80 points per second are classed as drifts with the defaults** (by design,
   R26: 20-point stability range over a 250 ms hold). A slow, steadily growing slide is eased down.
2. **Mode-switch scale reset — resolved, kept** (owner, 2026-11-19, reconfirming 2026-09-07). Picking
   a G-Force mode overwrites the Lock, Slip and Slip Angle scales and the trigger with that mode's
   defaults; loads never do. The docs match the code.
3. **Owner question: "Integrate Slip Angle" defaults ON also for upgraders** - including drivers who
   had "Integrate Wheel Lock and Slip" switched OFF. Confirm, or default it to the Lock/Slip switch's
   value on upgrade.
4. **During any Test Effect, Raw and Normalized read 0 or null** for that channel (Lock/Slip and Slip
   Angle alike); only Projected is driven.
5. **Unverified live `GameName` ids:** LFS, Forza, Drift21, KartKraft, BusBound, Wreckfest2 and the
   NASCAR family are keyed by registry codes read from the assemblies. A live mismatch would silently
   leave that game on the fallback. Check in SimHub's game picker.
6. **Units are unverified for most native games.** They are auto-detected (R16), so an unverified
   native game needs three hard corners before it goes native. Pin each unit in `SlipAngleRawSources`
   once observed live.
7. **Live WPF checks pending:** the Slip Angle Test Effect "100%" tick centring, the orange warnings
   (hidden offscreen, so they are in no screenshot), and the Slip Angle tab's "Max wobble duration"
   editor, whose 4-digit value renders cramped in the harness.
8. **GT7 surface maps by name only.** If SimHub exposes `surfaceType` as a number, GT7 falls back to
   grip inference.
9. **LMU raw-data exposure is unconfirmed** (the member is `JsonIgnore`). If it is not published, LMU
   gets a "path missing" verdict and runs on the fallback.
10. **Final-review minors left as follow-ups:**
    - M2 frame-level identity flapping (`SlipAngleFrameProcessor.Process`): the identity is chosen per
      frame from `raw.Tier`, so one non-finite native wheel switches that frame to the Fallback
      window / learner / gate with a different scale - a one-frame output jump and stray fallback
      samples. Fix later: debounce the identity or drop the frame.
    - M3 persistence size / churn: `GripCurveBins` state is about 2.9k doubles per entry (including the
      2400-value seed) × 3 targets × surfaces per car, and `SaveIfDue` exports every entry every 5 s on
      the data thread - several MB of Gen0 for many cars. Fix later: export only dirty entries; persist
      the seed only for bins with n < 20.
    - M5 per-frame allocations on the 60 Hz path: about 15-20 `MakeKey` strings, the `ExpectedLatG`
      capture closure, the request object, and in Custom mode `new double?[4]` plus
      `SourceIdentity.Compute`. Fix later: a key cache per (game, car, identity, bucket).
    - M8 the settings page's fallback warnings are refreshed by a code-behind `DispatcherTimer` instead
      of an `ITicker` in a view model (borderline against the MVVM threading rule).
11. **Parked minors from the per-task reviews** (none blocking; copied here from the build ledger,
    which is deleted with the workspace):
    - Task 0: design Kind column wording; ACC wheel-order inference weakly evidenced; a few bundled-id
      rows (F1 2012-16, PCars2/3 UDP, sims N/A); the Rennsport row sits in the Fallback table although
      its verdict is Native.
    - Task 1: the saturated test does not exercise "smallest"; a +Inf branch sits inside the zero test;
      the top target returns 100 per spec (the dot stays on the curve since it is flat there).
    - Task 2: loose skip-count floor; N duplicated in `SourceFor` vs `Step`; default-group inconsistency.
    - Task 3: XAML slider-row repetition / indentation; NaN key data would re-raise a group per tick
      (harmless); the idle-timer test is a guard, not a proof.
    - Task 4: a reflection test name overstates its per-channel check; ForLock / ForSlip duplication;
      a redundant null guard.
    - Task 5: the panel VM duplicates `FallbackWarningViewModel.Refresh`; the page label uses the
      applied SourceMode; `global::` qualifications; no marshalling test; pause / menu clears the
      warning immediately (spec-allowed).
    - Task 6: duplicated input filter (deliberate); public defaults read internal consts; a stale line
      range in a frozen header.
    - Task 7: the condensed `CrossingGate.Observe` rationale now points to the frozen copies (fixed);
      boundary scenario 1 does not pin MinG; `SaturatedWheelThreshold` is not parameterised (Slip
      Angle passes 0 saturated wheels per R2).
    - Task 8: one boxed enumerator per uncached scan; the resolver hard-codes a 4-segment key; a
      frozen header omits a trailing comment; frozen crefs may not resolve.
    - Task 9: verdict constructor undocumented; `ExclusionSeconds <= 0` untested.
    - Task 10: surface paths read every frame after a slip-missing verdict; F1 2019-22 surface order
      assumed FL, FR, RL, RR; a candidate unit can flip back before promotion; no minimum frame count
      per episode; stale impact baseline after a game switch.
    - Task 11: one non-finite native wheel drops the whole frame to the fallback (undocumented); the
      R18 front-only and both-unavailable native paths, NaN velocity and a short kerb array are untested.
    - Task 12: a 1-frame airborne flicker gives a 2-3 frame fade (untested); settings changed
      mid-wobble are ignored (undocumented); a NaN GroundSpeed does not fall back to SpeedKmh.
    - Task 13: a two-hump plateau may jump between humps under noise; max-of-median reading of the
      smoothing; a stale "360 per learner" state-size docstring; consider persisting the seed only for
      bins with n < 20.
    - Task 14: any blocked frame resets the gate, including yaw-residual-only frames (measure on WRC
      logs first); PriorScale stored relative to the prior; confirmation is checked only at event
      completion; epoch drift by sub-floor moves; the Import clamp may shift the epoch after
      sanitisation.
    - Task 15: per-frame key string building; seed buffer file size; Clone keeps the v11 stamp; a tier
      upgrade before confirmation steps the published window (no slew); a non-active bucket without a
      learner follows the borrowed live window unlatched; the borrowed key is not persisted (diag).
    - Task 16: the single-entry ThreadStatic curve cache rebuilds on alternating windows.
    - Task 18: ApplyPreset doc; Linear for SlipAngle untested.
    - Task 19: the watcher test's shared variable is not volatile; null GForce / Clutch / General via
      the watcher are not normalised (pre-existing).
    - Task 20: the diagnostics window shows the learned rather than the applied window; `MakeKey`
      string churn per frame; `_gateOffRuns` is not reset on Import or toggle (fresh learner in
      production).
    - Task 22: cross-thread dictionary access - `IsDefault` / `ResetSource` (UI thread) vs `TrySeed`
      (data thread), the pre-existing Lock pattern; typing can be overwritten by a `KeyDataRevision`
      bump (edge case); `CopyTo` sits between the `CreateShippedDefault` doc and its method; a stale
      "through the hub" class doc in `SettingsControl.SlipAngle.cs`.
    - Task 23: thresholds save per change (precedent: G-Force Max); hub events can clear an invalid
      draft (matches WheelLock); the graph refreshes only while the panel is open.
    - Task 24: a null channel ticks silently.
    - Task 25: the bit-identical test compares the post-change engine to itself (fixtures pin the
      legacy path); tail-finish behaviour untested.
12. **Owner housekeeping instruction:** once everything is done, delete the `.superpowers\` workspace
    and its `.gitignore` line, keeping only the design, plan, spike report and this report under
    `docs\`.

## 6. Verification at the end of the work

- Full test suite: 2475 passed, 0 failed (`dotnet test QAdvanceFeedback.Tests -c Release`); after the
  final review fixes: 2505 passed, 0 failed (`dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj`).
- `dotnet build QAdvanceFeedback\QAdvanceFeedback.csproj -c Release`: 0 warnings, 0 errors.
- `LocalizationTests` (EN / zh-Hans parity): green.
- Screenshots regenerated with `tools\screenshot-harness` and each one inspected:
  - new: `settings-slip-angle.png`, `shakeit-slipangle.png`, `shakeit-slipangle.zh-Hans.png`;
  - changed: `settings-gforce.png` (Integrate Slip Angle and the Slip Angle scale),
    `shakeit-gforce(.zh-Hans).png` (Integrate Slip Angle), `settings-general.png` (the new tab in the
    strip);
  - re-shot: `shakeit-wheellock-test(.zh-Hans).png`, now on Front/Rear routing so the group sliders
    show.
  - final review: `settings-slip-angle.png` re-shot for the corrected help texts; `settings-gforce.png`
    renders byte-identical after the shake block moved onto its view model.

## 7. Follow-up 2026-11-19: Start value and landing swap

Task A of `docs\shakeit-source-plan.md`. Design: `docs\slip-angle-design.md` §8, §9.2, §9.4, §9.6, §9.7.

- **Start value.** `SlipAngleWindow.Start` = ⌊0.5 × Min⌋ whole degrees. It is derived from Min and is
  never persisted or edited. It is null when the result is 0, i.e. Min < 2°.
- **Normalizer.** `SlipAngleNormalizer` adds a (Start, 15) knot, giving (0,0), (Start,15), (Min,30),
  (Best,60), (Max,80), (End,100). Without a Start the knot is omitted and the curve stays monotone.
  `Normalize`, the unclamped variant and the inverse `SourceFor` all follow it; the inverse bisects on
  the curve.
- **Projection preset.** The Slip Angle Curve preset ships `StartInput` 15 (`StartOutput` 0), so an
  angle below Start projects to 0. The Lock and Slip presets are unchanged. Saved curves are not
  migrated.
- **Display.**
  - A read-only "Start: 2° (50% of Min)" line on the tab and the ShakeIt panel (`SlipAngleStartText`),
    or "Start: — (Min below 2°)".
  - The tab's left graph gets a dashed Start marker at 15, shown only while the window has a Start.
  - The right graph and the Test Effect dot sample the normalizer, so they include the knot.
- **Landing swap.** `SlipAngleGateSettings` defaults are now `MaxFlightSeconds` 1.0 and
  `MaxWobbleSeconds` 0.5 (previously 0.5 and 1.0). So a 200 ms flight gives a 100 ms wobble, and the
  wobble is capped at 500 ms.
- **Tests.** 2536 → 2551. Eight existing tests were re-pinned to the new values; they are listed in the
  Task A section of `docs\shakeit-source-report.md`.
- **Owner notes.**
  - The "Linear" preset keeps the current Start input, so it now draws a 15 → 100 line.
  - The Start line is hidden while Min is empty or invalid.
- **Screenshots regenerated:** `settings-slip-angle.png`, `shakeit-slipangle(.zh-Hans).png`. They show
  the Start line, the Start marker and the 15 → 0 Start row.
  - The Landing group now shows 1000 in "Max flight duration". The "Max flight duration" and "Max wobble
    duration" editors are now Width 120 with a local NumericUpDown style (the page's implicit style had
    pinned the inner control at Width 80); the regenerated screenshot shows "1000" fitting.

## 8. Follow-up 2026-11-20: drift rules, Test Effect slider, 滑移角

Owner rules, built as specified (`docs\slip-angle-design.md` §9.7):

- **Locked output.** While the drift flag is on, the scale factor is applied to the highest calc since
  the drift began, never to a falling one. Drifting outputs `Min(factor × calcPeak, calc)`. Recovering
  locks `factor × calcPeak` at the release frame and outputs `Min(locked, calc)`. So 100 eased to 40
  holds 40 while the unscaled calc falls 100 → 40, then follows calc. Previously Recovering used
  `Min(frozen × calc, calc)`, which fell from 40 at once.
- **Drift start.** Counting starts at the angle `Max(1.2 × Max, Min(30°, 1.5 × Max))`
  (`Core\SlipAngle\SlipAngleDriftStart.cs`), passed to the attenuator as its Nᵤ and never below 80.
  Max 15° → 22.5° (End), Max 22° → 30°, Max 35° → 42°. Previously it started at Max.
- **Throttle clear.** While the flag is on and α ≤ Max, `ref = Min(ref, throttle)`. The flag clears on
  `throttle > ref + ThrottleRiseClear` or `throttle ≥ ThrottleLevelClear`. New `DriftSettings` fields:
  `ThrottleRiseClear` 20 (5–100) and `ThrottleLevelClear` 90 (50–100), on the Slip Angle tab under
  "Drift handling". Old settings files load with the defaults. The two existing clears (confirmed
  increase, α < Min) are kept. The Test Effect passes no throttle.
- **Test Effect slider.** 264 px (60% of the earlier 440, which was cut off in the ShakeIt panel).
- **Chinese name.** 侧偏角 → 滑移角 in the string table, the zh README and the zh architecture doc.
- **Tests.** 3082 pass (`DriftOwnerRulesTests` is new; five `DriftAttenuatorTests` expectations were
  re-pinned to the locked-output rule). Release build: 0 warnings, 0 errors.
- **Not changed, with the reason.**
  - Effect gain: Wheel Lock, Wheel Slip and Slip Angle already start at 75 (`NativeEffects.DefaultGainPercent`).
    A Slip Angle effect showing 100 kept the gain ShakeIt saved when it was added.
  - The "Add effect" list stays English: SimHub reads the compiled metadata attribute.
  - Default pedal ticks: left as SimHub does it (owner).
