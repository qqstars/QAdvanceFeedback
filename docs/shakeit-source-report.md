# ShakeIt source via the native effect, Slip Angle Start value, internal-source learning — report

Date: 2026-11-19. Plan: `docs\shakeit-source-plan.md`. Specs: `docs\shakeit-source-design.md` (ShakeIt
source), `docs\internal-source-learning-design.md` (Task G), `docs\slip-angle-design.md` §8, §9.2,
§9.4, §9.6, §9.7 (Start value, landing swap). Architecture: `docs\architecture.md` ("Wheel Lock/Slip
sources" section and the file map).

## 1. What shipped

| Task | What | Tests after |
|---|---|---|
| A | Slip Angle Start value: ⌊0.5·Min⌋, omitted when Min < 2°. A (Start,15) normalizer knot with its inverse. Preset `StartInput` 15. Read-only Start line on the tab and panel, and a graph marker at 15. Landing defaults swapped to MaxFlight 1000 ms / MaxWobble 500 ms. | 2551 |
| B | ShakeIt detection. Per-channel state machine NotSetUp / SetUpIdle / Supported / NotSupported. 1 Hz profile inspector over both ShakeIt plugins. A heartbeat from every QAdvanceFeedback effect. Per-channel game support from ShakeIt's own check. Debounced `ShakeItSourceState` broadcast. | 2588 |
| C | Per-channel resolution: Supported + fresh tap → ShakeIt values under the canonical ShakeIt identity; anything else → Raw under `RawSourceFallback.RawIdentity`. Export path removed. Shadowed save stores the chosen preset's rows (ShakeIt and Viper). | 2612 |
| D | ShakeIt algorithm settings: Lock legacy / sensibility / lock-only, Slip legacy / mute. Filters driven by our thresholds. Mute emulation. Algorithm fingerprint in the identity. Five `SettingsHub` fields. | 2693 |
| E | UI: ShakeIt option visibility and "(not set up)" label; VM-bound state notes; the ShakeIt algorithm block on tabs and panels; live refresh at the menu; fingerprint-aware identity everywhere. Health id `ShakeItExport` → `ViperAvailability`. | 2727 |
| G | Plugin Internal always learns (internal mirror). Every source isolated. `__layer3_raw_fallback__` retired with a load migration. Custom-matching-Raw = Plugin Internal. Slip Angle Plugin Internal always learns. Identity-change resets, same-type cross-channel borrow, grip-learner persistence fix. | 2788 |
| Final fixes | Custom reproducing a shipped preset (Raw, ShakeIt, Viper) gets no Custom cold start (`RawSourceFallback.MatchesShippedPreset`). Slip Angle landing editors widened to 120. ShakeIt tap stamped with its algorithm/threshold snapshot. Snapshot configured before the in-session gate. | 2794 |
| F | Docs, comments, screenshots (this report). | 2794 |

Baseline before the plan: **2536** tests. Final: **2794** passed, 0 failed. Release build: 0 warnings,
0 errors.

## 2. Rulings and decisions

- **R41, staleness vs reported unsupported.** A game that ShakeIt itself reports unsupported stays
  NotSupported until a game switch. A NotSupported reached only through 3 s of stale driving recovers
  on the first fresh values; a later dropout needs the full 3 s again.
- **"Driving" = ShakeIt's own gate.** Running, not paused, not in a menu, and the FeedbackData condition
  of `ShakeITV3PluginBase.DataUpdate`.
- **Health.** The profile inspection reports under its own `ShakeItProfile` subsystem. Each ShakeIt
  plugin is read and guarded separately.
- **Engine vs page.** The engine routes on the live state, so in game SetUpIdle reads Raw under the Raw
  key. The page shadows to Raw only on a verdict (NotSetUp / NotSupported), never while idle.
- **Canonical ShakeIt identity.** The ShakeIt route always learns under
  `ShakeItSourceResolution.ShakeItIdentity`, byte-identical to the legacy export names at the default
  settings. It never uses the stored rows.
- **Tap snapshot stamping.**
  - Tap values are stamped with the algorithm/threshold snapshot they were computed under.
  - A tap from an older snapshot counts as not fresh. The channel then reads Raw with the Raw key until
    ShakeIt produces values under the new settings. After a pedal-threshold change this lasts a frame
    or so.
  - The snapshot is handed to ShakeIt before the in-session gate, so changes made at the menu apply
    before the first in-game frame.
- **Custom reproducing a shipped preset.** `ApplyCustomColdStart` skips the Custom cold-start reference
  when `RawSourceFallback.MatchesShippedPreset` matches. That covers the Raw identity, the canonical
  ShakeIt identity (fingerprint stripped) and the shipped Viper NCalc preset. So a Custom cold-start SMax
  never reaches those keys.
- **Shadowed save, ShakeIt and Viper alike.** A preset applied while its channel is shadowed stores that
  preset's own rows.
- **Filter-rule correction.**
  - Slip UseBrakeFilter = brake threshold < 100 AND throttle threshold > 0. Slip UseThrottleFilter =
    throttle threshold > 0.
  - At throttle 0 our OR gate is always open, so both filters must be off. The spec's "throttle filter
    on when > 0" alone would have gated ShakeIt slip by brake.
  - Lock brake filter = round(Lock brake threshold), rounded away from zero.
  - Accepted edge differences: SimHub uses an exclusive `>` where we use an inclusive `≥`; with no
    pedal data SimHub's gate is open while ours fails closed; the RPS-and-dummy-radius lock branch
    ignores the filter.
- **Dropped settings.**
  - "Lock sensibility (extended)" (`LockSensibilityEx`) is a non-persisted alias of `LockSensibility`.
  - "Low-quality algorithm level" is a non-persisted, capability-derived warning level that no
    algorithm reads.
  - Neither is a setting or part of the fingerprint. Evidence: SimHub's `WheelsLockContainer` /
    `WheelsSlipContainer` code.
- **Sensibility** is snapped to SimHub's step of 5 everywhere: on load, in the snapshot, in the
  fingerprint and in the UI.
- **Mute.**
  - When on, all four Slip wheels are muted while any wheel of our hidden Lock value is > 0.
  - This also applies without a Lock effect in the profile, because our tap always computes the hidden
    Lock.
  - ShakeIt's 100 ms hold is not emulated.
- **Fingerprint.**
  - Lock: `#alg=legacy:X,sens:N,lockOnly:X`. Slip: `#alg=legacy:X,mute:X`.
  - Empty at the defaults. Thresholds are excluded.
  - Shipped key-data defaults are looked up by source TYPE, with the fingerprint stripped. Manual
    slots, Auto and learning are keyed WITH it.
  - Draft key-data edits on the page use the draft fingerprint.
- **Page list rule.** On the page, ShakeIt stays listed while it is the saved source, even when a
  different draft source is picked (until Apply). On the panel it is listed only while it is set up or
  current. A hidden option is also disabled, so the keyboard cannot reach it.
- **Same-type cross-channel borrow.** Slip internal ↔ Lock internal always; ShakeIt ↔ ShakeIt and
  Viper ↔ Viper by type; Custom and mixed setups do not borrow.
- **Physical reference and surface follow the internal source.** The physical g reference admits frames
  with the internal source's at-limit confidence. The Slip Angle surface classifier is stepped from
  Plugin Internal's angle. Both are observed once per frame, whatever source is selected.
- **Identity-change resets.**
  - On any change of the active identity, the shared crossing gate, the shared at-limit basis and the
    fallback divergence are cleared.
  - The previous source's Lock-anchor run is closed under its own game/car, except when the previous
    source is the internal key.
- **Pairing thread safety.** The pairing inputs are an immutable snapshot published with Volatile, and
  each snapshot has its own answer cache.
- **Owner-accepted output changes.**
  - Drivers who used only the export profile now get Raw until they add one of our effects.
  - ShakeIt-source Slip filters follow our thresholds: throttle 40 instead of 10, brake filter off
    instead of 99. So ShakeIt Slip engages later.
  - Mixed-type setups lose Slip's cross-channel cold-start borrow.
  - ShakeIt / Viper / Custom outputs can shift slightly, because the physical-reference speed buckets
    and the quiet-source fallback now follow the internal source.
  - Custom Slip Angle uses the internal surface classification. Games that report the surface are
    unaffected.
  - The first frames after a source switch no longer use the previous source's trend.

## 3. Task A re-pinned tests (Start value, landing swap)

`SlipAngleGateTests`: `Defaults_are_pinned`, `Wobble_scales_with_flight`,
`Landing_fade_is_s_curve_0_to_1`, `Takeoff_during_wobble_cancels_it_and_flight_restarts`;
`ProjectorSettingsSlipAngleTests`: `Curve_for_slip_angle_is_the_shipped_10_30_60_preset`,
`Linear_for_slip_angle_is_a_straight_line_from_0_to_100`;
`SlipAngleProjectedEngineTests.Preset_outputs_10_30_60_at_min_best_max`;
`SlipAngleTabViewModelTests.Preset_change_rewrites_the_anchors_and_the_curve`. Task C deleted the 8
`MotorsExportAvailabilityResolverTests` (removed export path), replaced by `ShakeItSourceResolutionTests`.
Task D appended the 5 new enum names to `SettingsHubTests.Existing_enum_values_keep_their_ordinals_and_new_ones_are_appended`.

## 4. Files

- **New:**
  - Core\ShakeItEffects: `ShakeItSourceAvailability.cs`, `ShakeItSourceResolution.cs`, `ShakeItAlgorithm.cs`
  - Core\Normalized: `PluginInternalSourceIdentity.cs`, `InternalSourceKeyMigration.cs`, `CrossChannelSourcePairing.cs`
  - Core\SlipAngle: `SlipAngleStartText.cs`
  - `Integration\ShakeIt\ShakeItProfileInspector.cs`
  - Settings: `ShakeItAlgorithmSettings.cs`, `SettingsControl.ShakeItSource.cs`
  - `Ui\ViewModels\ShakeItSourceViewModels.cs`
- **Removed:** `MotorsExportAvailabilityProvider.cs`, `Core\MotorsExport\MotorsExportAvailabilityResolver.cs`
  (and `MotorsExportAvailabilityResolverTests.cs`).
- The full per-file list is in `docs\architecture.md`'s file map.

## 5. Known issues and live checks

- **Pre-existing, not introduced here.** The UI thread's `PublishedCeiling` read walks
  `KeyedScaleLearner`'s non-thread-safe dictionaries (tier caches, learners) while the data thread
  writes them.
- **ShakeIt panels do not shadow to the Raw key while the channel has fallen back.** The page does.
  On the panel, a manual key-data edit made while the channel reads Raw lands on the ShakeIt slot.
  Viper's panel already behaves the same way. Follow-up.
- **`docs\setup\QAdvanceFeedback - WheelLockSlip.siprofile`** still contains the two ShakeIt export
  effects (`ExportProperty`), which are no longer read. Its Custom Effects still read our properties, so
  the README and the Setup Guide ("Alternate A") still link it, with a note. Owner to decide whether to
  strip those effects or drop the link.
- **Non-internal Slip borrow.** A non-internal Slip key borrows only from a same-type active Lock
  source. No source mapping exists beyond that.
- **On-screen checks that tests cannot do (in SimHub):**
  - The selected item's "(not set up)" label refreshes in the closed combo.
  - Menu note latency (expected about 1–2.5 s: 1 Hz inspection + 2 s heartbeat + 0.5 s debounce).
  - Spacing of the ShakeIt algorithm block on the tabs and panels.
  - The ShakeIt panels' shared label column is now wider even when the block is hidden (seen in the
    regenerated `shakeit-wheellock/wheelslip(-test)` screenshots). The hidden block's longer labels
    ("Use legacy iRacing algorithm", "Mute slip while lock is active") appear to reach the shared size
    group. This is cosmetic.
  - Fixed: the Slip Angle "Max flight duration" / "Max wobble duration" editors are Width 120 with a
    local NumericUpDown style (the page's implicit style pinned the inner control at Width 80, so the
    outer width alone had no effect). The regenerated `settings-slip-angle.png` shows "1000" fitting
    beside the +/- buttons; a glance in SimHub is still worthwhile.
- **Live checks** from `docs\slip-angle-report.md` §5 (Open), still pending:
  - live `GameName` ids;
  - native units;
  - the WPF checks (Test Effect "100%" tick centring, orange warnings).
- **Screenshots.** Regenerated in the documented default state (Plugin Internal source):
  - Changed: `settings-slip-angle.png`, `shakeit-slipangle(.zh-Hans).png` (the Start line, marker and
    row), and `shakeit-wheellock(.zh-Hans).png`, `shakeit-wheelslip(.zh-Hans).png`,
    `shakeit-wheellock-test(.zh-Hans).png` (wider label column).
  - `settings-wheel-lock.png` and `settings-wheel-slip.png` render byte-identical, because the ShakeIt
    notes and the algorithm block show only with the ShakeIt source.
  - No screenshot shows the ShakeIt algorithm block or the ShakeIt notes. The harness renders the
    default source; a ShakeIt-selected capture would need a harness change and new image names.
