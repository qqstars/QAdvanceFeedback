# v1.0.6.8 curve-UI port and G-Force readout report

## Status

Both gaps closed. `dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 warnings, 0
errors**. `dotnet test QAdvanceFeedback.sln -c Release --no-build`: **977/977 passing** (up from the
pre-task 957 — 20 new/ported tests). `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` +
`QAdvanceFeedback.pdb`. `AssemblyVersion`/`FileVersion` confirmed **1.0.6.8** both in the `.csproj` and via
direct reflection on the built DLL (`(Get-Item ...).VersionInfo.FileVersion` → `1.0.6.8`).

## Task 1 — curve UI port: feature checklist

Ported from `QAdvanceFeedback_1.0.6.2_prerelease`'s `docs\prerelease-1062-report.md`, reconciled with
1.0.6.8's own four-range Lock mapping / `LockAnchorLearner` / ratio-of-Smax refinement (all untouched —
see below).

| Feature | Present | Notes |
|---|---|---|
| (a) Per-setpoint flatten ranges, defaults 3/2/2, both channels | Yes | `ProjectorSettings.SlightlyFlattenRange`/`ModerateFlattenRange`/`CriticalFlattenRange`; labelled `mah:NumericUpDown` column on both Wheel Lock and Wheel Slip tabs; persisted (plain auto-properties); reset by `ApplyPreset`'s `ResetFlattenRangesToDefault()` |
| (b) Six hidden control points, range-0 omits them entirely | Yes | `OutputProjector.AcceptSetpointWithFlatten`; ported the bit-identical regression `All_flatten_ranges_at_zero_with_the_original_30_60_80_inputs_is_bit_identical_to_the_pre_flatten_curve` (both channels) plus the per-anchor-independence, negative-range, and fractional-range regressions |
| (c) Anchor input defaults 30/62/78 | Yes | `ProjectorSettings.ApplyPreset(Curve, ...)` for both channels; Linear preset intentionally stays 30/60/80 (unaffected, matches the 1.0.6.2 port) |
| (d) Labels `Powerful (30)`, `Ideal (60)`, `Max Grip (80)` | Yes | Static parenthesised band value, not generated from the threshold field; 1.0.6.8's existing "Slightly → Powerful" rename kept |
| (e) Configurable Start/End output values, defaults 0/100 | Yes | `ProjectorSettings.StartOutput`/`EndOutput`; `LockStartOutput`/`LockEndOutput`/`Slip*` `NumericUpDown`s replace the old static "(always 0)"/"(always 100)" text; `ColdStartScale.ApplyAmplitudeScale`'s divisor stays absolute 100 (untouched — confirmed in `ProjectedWheelLockSlipEngine.Compute`, which was not modified) |
| (f) Descriptions naming the raw value, both string tables | Yes | e.g. `"...at raw value 80..."` (English); Chinese translated and round-trip-verified via Python, file confirmed pure ASCII |
| (g) Monotonicity validation + overlap guard + max-safe-range | Yes | 4 edge-case tests ported (`Start_output_above_the_first_anchors_own_output_absorbs_it_into_a_longer_flat_start`, `End_output_below_the_last_anchors_own_output_cannot_pull_the_curve_back_down`, `Start_output_equal_to_end_output_degenerates_gracefully_rather_than_crashing`, `End_output_below_start_output_flattens_the_entire_curve_at_the_start_output_height`) plus `A_very_large_flatten_range_clamps_instead_of_breaking_the_curve`; max safe range at the shipped 62/78 anchors is **8** (half of the 16-wide Ideal–Max Grip gap), documented in `ProjectorSettings.cs` and the test's own remarks |

Tests were **ported**, not just the code (`OutputProjectorTests.cs` grew from ~34 to ~54 test
methods/theories; `WheelChannelSettingsTests.cs` updated for the new anchor/range fields).

### Measured curve outputs

These are exact node values (the flatten-range hidden points and the anchors themselves are literal
control points the monotone-cubic curve interpolates through exactly), pinned in
`OutputProjectorTests.Curve_default_plateau_numbers_lock_channel_match_the_measured_report_table` /
`..._slip_channel_match_the_measured_report_table`.

| Input | Lock output | Slip output |
|---|---|---|
| 27 | 9.4000 | 9.4000 |
| 30 (anchor) | 10.0000 | 10.0000 |
| 33 | 10.3750 | 10.4688 |
| 60 (Ideal plateau lower edge) | 29.7500 | 34.6875 |
| 62 (anchor) | 30.0000 | 35.0000 |
| 64 (Ideal plateau upper edge) | 31.2500 | 36.0000 |
| 76 (Max Grip plateau lower edge) | 78.7500 | 74.0000 |
| 78 (anchor) | 80.0000 | 75.0000 |
| 80 (Max Grip plateau upper edge) | 80.3636 | 75.4545 |

Slip's numbers are byte-for-byte what the sibling 1.0.6.2 branch measured (Slip's Critical output, 75,
was never touched). **Lock's 60/62/64/76/78/80 numbers differ from that branch's own report** because
1.0.6.8 keeps Lock's Critical anchor **output** at 80 (not the sibling branch's 60) — see next section.

### 1.0.6.8's WheelLock Max Grip anchor OUTPUT

**Confirmed unchanged at 80** (`ProjectorSettings.CriticalOutput` field default, and the `Curve` preset's
own `ApplyPreset(Curve, Lock)` branch). This was checked explicitly, per the brief's own warning that it
is a separate matter from the sibling 1.0.6.2 pre-release branch's own change (which set it to 60,
"otherwise it will shake too strong when reaches the best braking force" — a decision specific to that
branch, never made on this one). Only the curve-input **thresholds** (`ModerateInput`/`CriticalInput`,
62/78) moved as part of this port; `CriticalOutput` was deliberately left alone, and
`WheelChannelSettingsTests.Lock_defaults_ship_the_briefs_lock_curve_numbers` now pins both facts (78 for
`CriticalInput`, 80 for `CriticalOutput`) so a future change to either is caught immediately.

### Four-range mapping / endpoint guarantees — confirmed intact

`NormalizedWheelLockSlipEngine`'s four-range Lock mapping, `LockAnchorLearner` (branch filter + S75/S90
ratio-of-Smax refinement), `LockUsesAggregatedAllScale = true` / `SlipUsesAggregatedAllScale = false`, and
the endpoint guarantees (`source == Smax → 80`, `source == 100 → 100`) live entirely in Layer 4
(Normalized) and were **not touched** by this port, which is confined to Layer 5 (`Core\Projection\*`,
`Settings\SettingsControl.xaml(.cs)`, the string tables). `KeyedScaleLearner.CanonicalAtLimitAnchor` (80)
is also unchanged; the coupling test
(`WheelChannelSettingsTests.Lock_and_slip_top_curve_plateau_edge_coincides_with_the_canonical_at_limit_anchor`)
was updated to assert the coincidence against the **plateau edge** (`CriticalInput + CriticalFlattenRange
= 78 + 2 = 80`) rather than the raw threshold directly, matching the 1.0.6.2 port's own correction. All
957 pre-existing tests plus the 20 new ones pass.

## Task 2 — G-Force settings readout

**Root cause: a stale UI snapshot, not a key/channel/learner mismatch.** The entire data path was traced
end to end and found byte-for-byte identical across every 1.0.6.x build checked (1.0.6.5, 1.0.6.6,
1.0.6.7, 1.0.6.8_stability, and pre-task main) — `GForceSettings.cs`, `GForceMaxLearner.cs`,
`GForceEngine.cs`, `TelemetryLearningGate.cs`, `RobustBandEstimator.cs` (aside from an unrelated
saturation-cap addition), and `QAdvanceFeedback.cs`'s own `SetCurrentGameAndCar`/`ObserveAccelG`/
`ObserveDecelG` wiring were all unchanged. `SetCurrentGameAndCar(gameId, carId)` and the
`ObserveAccelG`/`ObserveDecelG` calls use the identical `(gameId, carId)` pair every frame;
`TryGetCurrentAccelAutoDetected`/`TryGetCurrentDecelAutoDetected` (the settings UI's own query) and
`CurrentLearnedAccelMaxG`/`CurrentLearnedDecelMaxG` (what feeds the published `Diag.GForce.LearnedAccelMaxG`/
`LearnedDecelMaxG` properties the owner's logs show reaching real values) call the exact same
`GForceMaxLearner.GetLearnedMax(gameId, carId)` on the exact same field — there is no daylight between
them. No analogue of the historical "wrong empty surface-bucket key" bug was found anywhere in this path.

The actual defect: `SettingsControl.RefreshGForceLearnedText()` — the method that builds the "Auto
detected: 2.3G" / "Auto: still using default (no data yet)" text — was invoked **only** from the
constructor's `LoadFromSettings()`, from the post-Apply/Restore-Defaults `LoadFromSettings()` calls, and
from the Accel/Decel mode combo's own `SelectionChanged` handler. There was no periodic or per-frame
refresh anywhere in `SettingsControl`. A driver who opens the settings panel before a session (correctly
seeing "no data yet") and leaves it open while driving never triggers any of those three call sites again,
so the label freezes at whatever it said at that moment — even though the underlying learners keep
accumulating correctly in the background the entire time.

**Fix:** a one-second `System.Windows.Threading.DispatcherTimer` added to `SettingsControl`, started in
the constructor (ticking `RefreshGForceLearnedText()` through the existing `SafeUiAction` exception guard)
and stopped on `Unloaded` so it cannot outlive the control or accumulate across repeated settings-panel
opens.

**Cosmetic or behavioural?** **Purely cosmetic.** `GForceSettings.EffectiveAccelMaxG`/
`EffectiveDecelMaxG` — the values that actually feed the live, G-derived severity path SimHub's
telemetry pipeline uses every frame (`QAdvanceFeedback.cs`'s own `Update`) — are queried fresh from the
same learners on every single telemetry frame, never cached or read from the stale UI text. The Lock
severity path's G-derivation (in place since 1.0.6.7) was never at risk: the logs' own
`Diag.GForce.LearnedDecelMaxG` reaching 3.85–3.94g is direct proof the live behavioural path already had
real evidence throughout; only this one settings-panel label failed to say so.

## Screenshots

Regenerated via `tools\screenshot-harness` (`dotnet build tools\screenshot-harness\ScreenshotHarness.csproj -c Release`,
then run the built `.exe` directly) and copied into `docs\images\` (`settings-g-force.png` renamed to
`settings-gforce.png` per the harness/README naming mismatch already documented in
`docs\architecture.zh-Hans.md`).

| File | Old SHA-256 | New SHA-256 | Changed? |
|---|---|---|---|
| `settings-wheel-lock.png` | `b6330a06...` | `33b096ed...` | **Yes** |
| `settings-wheel-slip.png` | `28b48eef...` | `cd52dcee...` | **Yes** |
| `settings-gforce.png` | `2954d65b...` | `2954d65b...` | No (legitimately) |
| `settings-general.png` | `161b1bdf...` | `161b1bdf...` | No (legitimately) |

Wheel Lock/Wheel Slip changed because their tab layouts genuinely changed (new flatten-range column, new
Start/End output spinners). G-Force and General are pixel-identical because neither tab's visible layout
or default-state text changed — Task 2's fix is purely a live-refresh *behaviour* change (only observable
while telemetry streams with the panel open), and Task 1 never touched either of those two tabs; the
harness has no live telemetry to feed, so G-Force correctly still renders its honest "no data yet" state
in the screenshot, identically to before.

All four PNGs were opened and visually confirmed: full-height and uncropped (Wheel Lock renders at
1042×1772, Wheel Slip at 1042×1832 — well past one screen, with every section down to "Pulse at Maximum"
visible), SimHub-styled (dark MahApps `BaseDark`/`Blue` theme, matching the live plugin), and — for Wheel
Lock/Wheel Slip — showing the new "flatten range" column header, the `3`/`2`/`2` range spinners next to
Powerful/Ideal/Max Grip, the editable Start/End output spinners (0/100), the `Powerful (30)`/`Ideal (60)`/
`Max Grip (80)` labels, the "...raw value NN..." description text, and a visibly plateaued curve plot
(small flat bumps at each anchor instead of sharp corners).

## Concerns

- The G-Force diagnosis rests on an exhaustive file-level comparison (MD5/diff) across five historical
  builds rather than a live repro, since no running SimHub/telemetry session was available in this
  environment. The fix (a `DispatcherTimer`) is a direct, low-risk remedy for the one gap that
  comparison actually found (no periodic refresh anywhere in the settings control), and is consistent
  with every symptom reported (detection genuinely works per the logs; the panel's own text does not).
  If the owner can reproduce the original complaint against this build and it persists, the next place to
  look is SimHub's own settings-host lifecycle (whether it ever reconstructs vs. reuses a plugin's
  `Control` instance) rather than this plugin's own code, which was otherwise found completely correct.
- The curve-UI port intentionally did NOT bring across the 1.0.6.2 branch's separate Cold-Start
  Device-Feel Scale mechanism (`ColdStartScale`) — 1.0.6.8 already has its own `ColdStartScale.cs`
  file (present before this task started, part of a later "v1.0.6.9 rework" already in progress on this
  branch) and it was left completely untouched, per the task's own scope (items (a)-(g) only).
- Chinese translations for the 19 new/changed string-table entries and the 3 new README passages were
  authored as literal UTF-8 text (README, matching that file's own existing convention) or converted to
  `\uXXXX` escapes via a small Python script with a round-trip decode assertion (the C# string tables,
  matching that file's own ASCII-only convention) — both `StringTableZhHans.cs` and `StringTableEn.cs`
  confirmed to still carry exactly the same key set (`LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set`
  passes) and `StringTableZhHans.cs` confirmed pure ASCII via `file` after every edit. One transcription
  typo (陡岭 instead of 陡峭) introduced while hand-applying an edit was caught and fixed during this same
  verification pass.
