# Native ShakeIt effects — implementation report (1.2.0.0)

Date: 2026-11-16. Design: `docs\shakeit-native-effects-design.md`. Plan:
`docs\shakeit-native-effects-plan.md`.

## What was built

| Area | Files |
|---|---|
| Six native ShakeIt effects | `Integration\ShakeIt\QAdvanceFeedbackWheelChannelContainers.cs` (WheelLock, WheelSlip), `Integration\ShakeIt\QAdvanceFeedbackGForceContainers.cs` (BottomFront, BottomRear, BackLow, BackTop), `QAdvanceFeedbackEffect.cs`, `ShakeItEffectSupport.cs` |
| Effect panels (no text boxes) | `Integration\ShakeIt\WheelChannelEffectPanel.cs` (Source + output channels), `GForceEffectPanel.cs` (Auto toggle + always-editable max G + readout label per axis, `Settings\GForceReadout.cs`) |
| Read-only "Source value -> Projected value" graph in ShakeIt's curve slot | `Integration\ShakeIt\QAdvanceProjectionFilter.cs`, `ProjectionCurvePlot.cs`, `Settings\SourceToProjectedGraph.cs` (shared with the page), `Core\ShakeItEffects\GraphPoint.cs` |
| ShakeIt built-in lock/slip | `Integration\ShakeIt\ShakeItBuiltInTap.cs`, `Core\ShakeItEffects\ShakeItTapValues.cs` (+ `TapDeduper`), `ShakeItTapPropertyNames.cs`; `QAdvanceFeedback.ResolveWheel` |
| Routing, values, tone defaults | `Core\ShakeItEffects\EffectRouting.cs`, `ToneDefaults.cs`, `FrameSnapshot.cs` |
| Two-way settings sync | `Settings\SettingsHub.cs`, `Settings\ConfigFileWatcher.cs`, `Settings\PageFieldRefreshPlan.cs`, `Settings\SettingsControl.Sync.cs`, `Settings\ShakeItSourceOptions.cs`; plugin `Hub`, `ApplySettings` broadcast, `SaveConfig` |
| Plugin frames for effects | `QAdvanceFeedback.Instance`, `LatestProjected`, `LatestGForce` (the G-Force Test Effect included) |
| Diagnostics | `Diag.ShakeIt.TapActive`, `Diag.ShakeIt.WheelLock/WheelSlip.<Wheel>` (appended at the end of the diagnostic and CSV columns) |
| Strings | `Core\Localization\StringTableEn.cs`, `StringTableZhHans.cs` (ShakeIt panel text; ShakeIt source label/setup/unavailable text; health subsystem) |
| Docs | `README.md`, `README.zh-Hans.md`, `docs\setup\Setup Guide 设置指南.txt` (new section D), this report, the design doc |

## Verification done here

- `dotnet test QAdvanceFeedback.Tests` — full suite green, 1644/1644 (baseline was 1565), including
  the new tests: `ShakeItEffectsMappingTests`, `ShakeItTapTests`, `PanelReadoutSharingTests`,
  `SettingsHubTests`, `ConfigFileWatcherTests` and `PageFieldRefreshPlanTests`. Every new test was
  first seen failing.
- `dotnet build QAdvanceFeedback -c Release` — 0 warnings, 0 errors. All SimHub-facing code compiles
  against `lib\SimHub.Plugins.dll` 9.13.2, which confirms the ShakeItV3 members used exist and are
  accessible. `bin\Release\net48` still holds only `QAdvanceFeedback.dll` + `.pdb`.
- Backward compatibility: the 62 product property names are unchanged (pinned by test); CSV
  header/row parity holds with the new diagnostic columns.

## Not verified here (needs SimHub — no SimHub on the build machine)

1. The six effects appear under category "QAdvanceFeedback" in ShakeIt Motors and Bass Shakers.
2. Adding WheelLock/WheelSlip to a Bass Shakers profile shows 30/20 Hz and 45/30 Hz; each G-Force
   effect shows 100 Hz low, 60/70/70/60 Hz high, white noise 10 off.
3. The curve slot shows the read-only QAdvanceFeedback graph with no Gamma sliders (design Risk 1).
4. Switching output channels (One / Front-Rear / Left-Right / Per wheel) updates the channel-mapping
   rows straight away and survives a profile reload (Risk 2).
5. With Source = "ShakeIt Built-in" and any QAdvanceFeedback effect in the active profile,
   `Diag.ShakeIt.TapActive` turns true in a session and the values track the old export (Risk 3).
6. Source / G-Force edits in a ShakeIt panel appear on the plugin page within a moment, and the
   plugin page's other unapplied edits are kept; Apply on the page updates open ShakeIt panels.
7. Bind the G-Force effects (Sensit! Haptic Core: BottomFront 0/1, BackLow 2/3, BackTop 4/5,
   BottomRear 6/7) and check each pair with the plugin's G-Force Test Effect.
8. Then export the native profile/channels from SimHub into `docs\setup` (plan Task 9 step 2). Not
   done here: these files must come from SimHub, not be hand-written.

## Key data points in the WheelLock/WheelSlip panel

Added on the owner's request. Both the panel and the plugin page now apply one set of rules,
`Settings\KeyDataPanelModel.cs` (design: "Key data points in the ShakeIt panel"):

- **Panel:** Normalize Pattern (SMax only / SMax-S90-S75), Auto Generate, and the SMax / S90 / S75
  editors (`Integration\ShakeIt\KeyDataSection.cs`). They are read-only under Auto, and only SMax
  shows under SMax only. The section uses the page's own labels in both languages.
- **Commit rule:** the page's `PersistChannelIfManual` now delegates to
  `KeyDataPanelModel.TryCommitManual`, so the two UIs cannot write key data differently. The two
  page source-scan guards in `PerSourceKeyDataFlagsTests` (stored-flag gate, seeded latch) were
  extended to check the shared model.
- **Sync:** six new hub fields, keyed by the current game (`QAdvanceFeedback.CurrentGameId`).
  `SettingsControl.Sync.cs` refreshes the page's key data and pattern controls when they change in
  ShakeIt; the pending debounce for the other channel is committed first.
- **Tests:** `KeyDataPanelModelTests` (14). Full suite 1657/1657.
- **Not verified here (needs SimHub):** typing in the panel updates the plugin page's boxes within
  about a second, and vice versa; Auto shows the learned values live while driving.

## ShakeIt graph and G-Force panel follow the page

Added on the owner's request:

- **WheelLock/WheelSlip graph:** now the page's right graph, "Source value -> Projected value",
  instead of the normalized graph. The page's sampling was moved into `Settings\SourceToProjectedGraph.cs`
  and both draw through it. The ShakeIt graph uses the key data values its panel shows (learned under
  Auto) and redraws on projector, pattern, key data and source changes, and once a second under Auto.
  The old normalized-graph geometry (`ProjectionCurveGeometry`, its 5 tests) was removed as unused.
- **G-Force panel:** the page's pattern. An Auto toggle replaces the Auto/Fixed dropdown, the max-G
  editor stays editable under Auto (it is the default), and a label shows the default and the Auto
  detected value. The label text moved into `Settings\GForceReadout.cs`, used by both.
- **Tests:** `PanelReadoutSharingTests` (9), including source scans that the page uses both shared
  helpers. Full suite 1661/1661; Release build 0 warnings.
- **Not verified here (needs SimHub):** the graph tracking the learned SMax while driving; the G-Force
  label showing "Auto detected" once evidence exists.
- **Follow-up:** the readout label now shows two decimals (`{0:0.00}`), matching the max-G box, on the
  page and in ShakeIt (it showed 0.8 for a 0.75 box). The G-Force panel also has Lateral feedback
  direction and Integrate Wheel Lock and Slip, as two new shared hub fields with page refresh
  (`GForceSharedOptionsTests`, 5; full suite 1666/1666). The screenshot harness now waits 600 ms so toggle animations finish.

## Screenshots

`tools\screenshot-harness` now also renders the native effects' editor contributions, in English and
Simplified Chinese: `docs\images\shakeit-wheellock.png`, `shakeit-wheelslip.png`,
`shakeit-gforce.png`, each also as `.zh-Hans.png`. `README.md` links the English set and
`README.zh-Hans.md` the Chinese set. The capture rule is in `docs\architecture.md` ("Settings
screenshot capture rule").

Rendering the effects inside ShakeIt's own editor was tried and is not possible offscreen: SimHub's
container constructor needs `WoteverLocalization.dll`, which only a SimHub install has. The same
harness run also re-rendered the settings-page screenshots:

- `settings-wheel-lock.png` and `settings-wheel-slip.png` show the new "ShakeIt Built-in" source text.
- `settings-general.png` shows version 1.2.0.0.
- `settings-gforce.png` is unchanged.

## Review fixes

An independent whole-change review found three issues, fixed test-first (`ReviewFixTests`; full suite
1644/1644 afterwards):

- **The plugin page missed ShakeIt-side changes made while it was off screen, and a later Apply
  reverted them.** SimHub shows the plugin page and the ShakeIt pages one at a time. The page now
  snapshots the live settings when it unloads, and on return reloads exactly the shared fields that
  changed since (`PageFieldRefreshPlan.FieldsChangedSince`, `SettingsControl.Sync.cs`).
- **Errors in ShakeIt panel handlers could reach SimHub's UI.** `SettingsHub` now catches a failing
  apply/save and reports it to the health panel; every panel handler and refresh runs through
  `ShakeItEffectSupport.Guard`.
- **G-Force native effects held the last value for up to 500 ms after pause/menu, and played while
  spectating.** They now follow ShakeIt's own in-game gate, except while the Test Effect runs
  (`GForceOutputGate`, since renamed `TestAwareOutputGate` and shared with WheelLock/WheelSlip).

Deferred (minor, not fixed):

- The once-per-frame tap dedupe keys on `GameData.FrameTime`. Two real frames sharing a timestamp
  (~15 ms clock resolution) would skip one tap sample.
- `AggregationMode` set directly by SimHub (rather than through the panel's routing control) does
  not drop the cached effect list. That is harmless today, because SimHub only does it on a freshly
  added effect, which is already Mono.
- Adding any native effect (even G-Force only) switches a ShakeIt-source channel from the export
  profile to the built-in engines with fixed settings. Disabling it, or ShakeIt switching to a
  profile without one, switches back to the export.
- While paused or in a menu, the page may say the ShakeIt source is unavailable when only a native
  effect feeds it.
- `Diag.ShakeIt.TapActive` keeps its last value after pause or game exit.
- A ShakeIt panel can subscribe to the hub twice if WPF raises `Loaded` twice.
- Each G-Force spinner tick in a ShakeIt panel does a full apply and save; there is no debounce.
- The page's Apply button can stay lit after a ShakeIt change cleared its only pending edit.

## Deviations from the plan (all recorded as rulings)

- The ShakeIt source keeps its existing identity (the export property names) instead of moving to new
  names, so learned calibration and the ShakeIt cold-start classification carry over. The tap is
  diagnostics-only.
- The hidden ShakeIt engines use the owner's export-profile settings as fixed values instead of the
  plugin's Slip thresholds (100/40), to reproduce exactly what the export produced.
- The plugin page's graph code was not extracted (it renders unapplied working values). The ShakeIt
  plot is a separate read-only control sharing the geometry and drawing the left graph only.
- `docs\shakeit-export-guide.md` was kept: the export path is still supported as the fallback.
- Single consolidated report instead of one per task (all tasks ran in one pass).

## Follow-up (2026-11-18): names, gains and tones

- Effect names are short and localized: Wheel Lock / Wheel Slip / G-Force BottomFront, BottomRear, BackLow, BackTop / Clutch Bite (zh-Hans 车轮抱死 / 车轮打滑 / G力 坐垫前部… / 离合器结合点), under the unchanged "QAdvanceFeedback" category. `Core/ShakeItEffects/NativeEffects.cs` holds the string keys; `ShakeItEffectSupport.ApplyIdentity` swaps ShakeIt's get-only `ContainerName`/`ContainerDescription` (built from the English attribute) for `TranslatableString.FromConstant(localized)` by reflection, best effort. ShakeIt's "Add effect" list reads the attribute itself, so it shows the English names. Class names (saved ContainerType, channel-map keys) are unchanged.
- Default gain of a newly added effect: Wheel Lock/Slip 75 %, all others 100 % (ShakeIt's own default is 50 %). ShakeIt always saves Gain, so effects already in a profile keep theirs.
- Tone defaults: Lock 40/20 Hz, Slip 45/25 Hz, Clutch 40 Hz with 15 Hz at the bite.
- Tests: `NativeEffectDefaultsTests`; tone tests updated. Full suite 1844/1844.

## Follow-up (2026-11-20): descriptions belong to "Add effect" only

- Description wording (attribute and both string tables): the "QAdvanceFeedback's" / "QAdvanceFeedback" prefix is gone (the effects already sit under the QAdvanceFeedback category), and the G-Force pads name the body part: seat-bottom front **leg** pads, seat-bottom rear **butt** pads, seat-back low **waist** pads, seat-back top **back** pads (zh 腿部 / 臀部 / 腰部 / 背部). No other strings changed.
- Why Slip Angle showed a description after it was added: ShakeIt's `AddEffect` copies the "Add effect" (English) description into the effect's saved `Description`, the field Rename writes. The header shows it as " - <text>" after the name unless it equals `ContainerDescription` (current-language or default string). With the localized (Chinese) identity it never matched, so any effect added in Chinese showed it; the Lock/Slip/Clutch effects in the owner's profile predate the localized identity.
- Fix: `NativeEffects.IsBuiltInDescription` recognises an effect's own description (current English/Chinese wording or an earlier one), and `ShakeItEffectSupport.ClearBuiltInDescription` clears such a saved copy from each container's `LoadDefaultSettings` (ShakeIt calls it straight after copying the text in) and `OnDeserialized` (profiles saved before the fix). A name typed in Rename is kept. The description is still the tooltip on the effect name.
- Tests: `NativeEffectDefaultsTests` (wording, matching, hooks). Full suite 3100/3100; Release 0 warnings.
