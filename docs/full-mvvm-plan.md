# Full MVVM: every settings tab and every ShakeIt effect — design and plan

Status: **implemented (2026-11-20).** Report: `docs\full-mvvm-report.md`; deviations from this plan:
§7 "As built". Execution: parallel sub-agents per wave (owner, 2026-11-19: "use sub-agents to update
all of the remaining items, in the shortest time").

Related: `docs\architecture.md` "UI: MVVM (standing rule)", `docs\shakeit-test-effect-mvvm-design.md`
(project 1: ShakeIt panels and Test Effects), `docs\slip-smax-crossing-gate-design.md` §10 and
`docs\architecture.md` "Key Data Points" (the restart display rule this plan extends to Slip Angle).

---

## 1. Why

1. **The page misses the first ShakeIt-side change** (owner-reported). The page is built once, eagerly,
   filled from settings in its constructor, and listens to `SettingsHub` only while it is Loaded. A
   catch-up exists only from an "unloaded" snapshot, and there is none before the first Loaded. A view
   model that owns the subscription for its whole life cannot miss a change.
2. **Apply writes the whole stale page back.** `SaveToSettings` copies every control into the live
   settings, so it can revert a field changed on the ShakeIt side while the page sat open.
3. **Two source-switch behaviours.** The page switches source modes without the per-mode archives; the
   hub (ShakeIt side) uses `WheelChannelSettings.SwitchSourceMode`.
4. **The standing rule.** Views bind; view models hold state, rules and commands; code-behind only wires.
   Today the page is 3,651 lines of code-behind (plus 500 in partials), 448 named controls, ~200 handlers
   wired in C#, ~380 control-property writes.

## 2. Goals / non-goals / anti-goals

Goals
- Every tab of the plugin page (Wheel Lock, Wheel Slip, Slip Angle, G-Force, Clutch, General) and every
  native ShakeIt effect panel (Wheel Lock, Wheel Slip, Slip Angle, G-Force ×4, Clutch, the two graph
  filters) is MVVM: XAML binds to view-model properties and commands; view code-behind is
  `InitializeComponent(); DataContext = vm;` (plus attached behaviours declared in XAML).
- **ShakeIt changes always update the page immediately, applied or not** (owner decision). Apply writes
  only fields edited on the page. One source-switch path.
- **Slip Angle follows the Lock/Slip rule for its key data** (owner, 2026-11-19): what the tab and the
  panel show, what the one-time seed writes and what a restart shows all come from ONE display rule —
  the exact persist key's value if the parameters file holds it, else the default.
- Persistence gap fixed: `RuntimeCache.CloneLearners` drops five `GripLearnerState` fields.

Non-goals
- No change to learning, output, published property names or the Custom Effect pattern.
- No visual redesign: every tab renders as today (screenshots compared before/after), apart from the
  values that were wrong.

Anti-goals (a failure even if it "works")
- A view model that reads or writes WPF controls, or a view that computes a rule.
- A page edit silently lost, or a ShakeIt edit silently reverted.
- Two code paths for the same action (page vs. panel).

## 3. Design

### 3.1 The page: draft, baseline, live (`Ui\ViewModels\SettingsDraft.cs`, pure, net8-tested)

- `Live` = the plugin's settings object. `Draft` = a deep copy the page edits. `Baseline` = the live
  values the draft was last synchronised with.
- Compared and merged at **JSON-leaf level** (Newtonsoft `JToken` paths), so no per-field table can fall
  out of date when a setting is added:
  - **IsDirty** = any leaf where Draft ≠ Baseline.
  - **External change** (hub `Changed` from any origin other than this page, a file reload, a runtime
    seed): for every leaf where Live ≠ Baseline, copy Live into Draft and Baseline. **ShakeIt wins**, by
    owner decision. The page reflects the latest live state, applied or not.
  - **Apply**: for every leaf where Draft ≠ Baseline, write Draft → Live; then `ApplySettings()` and
    `Hub.NotifyApplied(changed fields, PluginPage)`; Baseline = Draft.
  - **Restore defaults**: Draft = shipped defaults with key data carried across (today's rule); applies
    like any edit.
- Key data keeps its own immediate-save model (today's 700 ms debounce), routed through the same
  `KeyDataPanelModel.TryCommitManual` the ShakeIt panel uses, and adopted back into Draft/Baseline.

### 3.2 View-model tree (all in `Ui\ViewModels`, no WPF control types, net8-tested)

```
SettingsPageViewModel            owns SettingsDraft, hub subscription (constructed with the page,
 │                               disposed with the plugin), Apply / RestoreDefaults commands (confirm
 │                               via IDialogService), unapplied banner, tabs, ITicker timers
 ├─ WheelChannelPageViewModel ×2 (Lock, Slip — one class, isLock flag)
 │   ├─ TriggerThresholdViewModel   brake / throttle threshold, Lock sensibility, notes
 │   ├─ SourcesViewModel            mode (ShakeItSourceOptions + WheelChannelSettings.SwitchSourceMode),
 │   │   └─ SourceRowViewModel ×4   text, script type (P/J/N), action button state + Pick/Edit command
 │   │                              via IPropertyPicker / IScriptEditor services, shadow / Viper / ShakeIt
 │   │                              notes as bound properties, Viper game add/remove command
 │   ├─ CustomReferenceViewModel    Custom cold-start SMax/S90/S75 (≤ 95)
 │   ├─ KeyDataPageViewModel        extends the panel's KeyDataViewModel: Per-Game, learned hints,
 │   │                              status, reset command, manual gate (live push via host)
 │   ├─ AggregationViewModel        Max/Min/Front/Rear (+ Slip floor)
 │   ├─ CurveEditorViewModel        normalize pattern, preset, 5 anchors, 3 flatten ranges
 │   │                              (ProjectorAnchorEditor), live labels; graphs as bound controls
 │   │                              (NormalizedCurveGraph / SourceToProjectedGraph data)
 │   └─ PulseViewModel
 ├─ SlipAngleTabViewModel        exists — moved under the page VM, reads the draft
 ├─ GForcePageViewModel          axes (GForceAxisViewModel), scales, splits, sustain, motion, lateral
 │                               direction, GForceShakeViewModel (exists), GForceTestViewModel (exists)
 ├─ ClutchViewModels             exist — unchanged, hosted by the page VM
 └─ GeneralViewModel             diagnostics / CSV switches, ShakeIt import command + result,
                                 health list + copy command, version
```

- **Host interface** `ISettingsPageHost` (extends `IPanelHost`): settings, hub, `ApplySettings()`,
  current game, learned key data (`LearnedKeyData`), manual-gate state, source availability (ShakeIt /
  Viper / Motors export), health registry, ShakeIt import. The plugin implements it; tests use a fake.
  The plugin's push calls (`UpdateCurrentGame`, `UpdateLearnedKeyDataPoints`) become host events the
  view models subscribe to, so the page and the panels read the same values through the same rules.
- **Services** (interfaces in `Ui\ViewModels`, WPF/SimHub implementations in `Integration\` / `Ui\Views`):
  `IPropertyPicker`, `IScriptEditor`, `IDialogService` (confirm), `IClipboard`, `IUiDispatcher`,
  `ITicker` (exist).

### 3.3 Views (`Ui\Views`)

- `SettingsPageView.xaml` (TabControl, Apply bar, banner) and one view per tab:
  `WheelChannelPageView.xaml` (used twice), `GForcePageView.xaml`, `GeneralPageView.xaml`; the existing
  `SlipAngleTabView` and `ClutchBiteView` are reused. Tab `x:Name`s are kept for the screenshot harness.
- `Settings\SettingsControl` shrinks to a host: `InitializeComponent(); DataContext = vm;` (it stays the
  type SimHub's `GetWPFSettingsControl` returns).
- **Attached behaviours replace the remaining code-behind events**: `v:ViewModelLifetime.Activate="True"`
  (Loaded → `Activate`, Unloaded → `Deactivate` on an `IActivatable` DataContext),
  `v:EventCommand.DropDownOpened="{Binding RefreshSourcesCommand}"`. After this, every view's
  code-behind is exactly `InitializeComponent(); DataContext = viewModel;` and `MvvmRuleTests` enforces
  that (it is tightened, and extended to `Settings\SettingsControl.xaml.cs`).
- `NumericEditor` stays a custom control (a control may have code); it is bound via its `Value`
  dependency property, two-way.

### 3.4 ShakeIt effects (project 1 completion)

Already MVVM apart from lifetime/dropdown events in code-behind. Changes: the attached behaviours above;
`PanelViewModelBase` catch-up on `Activate` proven by tests (a change made while the panel is not shown
is visible the moment it is); panels read learned key data through the same `LearnedKeyData` rule
(done for Lock/Slip; Slip Angle in 3.5).

### 3.5 Slip Angle key data: the same rule as Lock/Slip

- **One display rule** `SlipAngleKeyDataModel.LearnedWindow(learner, game, car, identity)` (pure):
  `KeyedSlipAngleLearner.Window` for the exact key (game, car, source identity for the CURRENT tier
  — Native / Fallback / Custom —, the classified surface blend) → capped by `MaxCapDegFor`. After a
  restart with no driving, that is the persisted published window when the parameters file holds that
  exact key (the learner already makes it the key's cold reference), else
  `SlipAngleWindow.ShippedDefault`. Never another key's window.
- Used by the tab (Auto read-out and learned hints), the ShakeIt panel, and the one-time Manual seed.
- **Menu after a restart**: persist the last Slip Angle context (game, car, identity tier) in
  `RuntimeDocument` (next to `LastKeyDataGame/Car`), so the tab and panel form the exact key with no game
  running; no saved context → the shipped default.
- Manual: typed values clamp Max to the source cap (done); Manual with nothing stored pre-fills the
  default for that source, exactly like Lock/Slip.

## 4. Plan — waves, file ownership, parallel sub-agents

Rules for every agent: TDD (failing test first); never `git commit` (owner commits); never touch
`QAdvanceFeedback_1.1.0` / `_1.2.0`; preserve file encodings/line endings (use the Edit tool); new UI
strings only in the agent's OWN partial string files (`StringTableEn.<Area>.cs` /
`StringTableZhHans.<Area>.cs`, created in Wave 0); touch only the files the task owns; run its own tests
plus `dotnet build QAdvanceFeedback -c Release` before reporting; report every ruling it made.

### Wave 0 — foundation (1 agent, blocking, ~short)
Owns: `Ui\ViewModels\SettingsDraft.cs`, `ISettingsPageHost.cs`, `SettingsPageViewModel.cs` (shell:
draft, hub subscription, Apply/Restore commands, dirty, banner, tab VM slots as empty shells with
the agreed class names from 3.2), service interfaces, `Ui\Views\ViewModelLifetime.cs`,
`Ui\Views\EventCommand.cs`, string tables made extensible (`partial`, per-area files), test fake
`FakeSettingsPageHost`, `MvvmRuleTests` tightened (behaviours allowed; extended to the page).
Tests: SettingsDraft merge rules (edit, external change wins, Apply writes only edited leaves, dirty,
restore defaults keeps key data), page VM subscribes at construction (change before first show is
visible), the original defect reproduced as a test (first ShakeIt-side source change reaches the page).

### Wave 1 — parallel (6 agents, independent files)
| Agent | Scope | Owns (new unless noted) |
|---|---|---|
| A1 | Lock/Slip: triggers, sources (rows, script type, picker/editor services, mode switch via `SwitchSourceMode`, Viper add/remove, all notes and shadow states), custom reference, aggregation | `WheelChannelPageViewModel.cs`, `TriggerThresholdViewModel.cs`, `SourcesViewModel.cs`, `SourceRowViewModel.cs`, `CustomReferenceViewModel.cs`, `AggregationViewModel.cs`, `Ui\Views\WheelChannelPageView.xaml` (top half) + tests |
| A2 | Lock/Slip: key data (page), curve editor + both graphs, pulse | `KeyDataPageViewModel.cs`, `CurveEditorViewModel.cs`, `PulseViewModel.cs`, graph data adapters, `Ui\Views\WheelChannelPageView.Curve.xaml` (a UserControl the page view includes) + tests |
| B | G-Force tab | `GForcePageViewModel.cs`, `Ui\Views\GForcePageView.xaml` + tests (reuses existing G-Force VMs) |
| C | General tab + page view shell | `GeneralViewModel.cs`, `Ui\Views\GeneralPageView.xaml`, `Ui\Views\SettingsPageView.xaml` + tests |
| D | ShakeIt effects full MVVM | every `Ui\Views\*EffectView.xaml(.cs)`, `GraphView`, `SlipAngleGraphView`, `ClutchBiteView`, `SlipAngleTabView` code-behind → behaviours; `PanelViewModelBase` catch-up tests |
| E | Slip Angle key-data rule (3.5) + `CloneLearners` fix | `Core\SlipAngle\SlipAngleKeyDataModel.cs`, `QAdvanceFeedback.SlipAngle.cs`, `Core\Runtime\RuntimeDocument.cs` / `RuntimeCache.cs` / `RuntimeStore.cs` (additive), `SlipAngleTabViewModel.cs` / `SlipAngleEffectViewModel.cs` (display source only) + tests |

### Wave 2 — integration (1 agent = the coordinator)
Wire `SettingsPageViewModel` to the real plugin (`ISettingsPageHost` on `QAdvanceFeedback`, push calls →
host events); `SettingsControl` becomes the thin host; delete the old code-behind and its partials;
rewrite the source-text tests pinned to the old code-behind (`SettingsControlDirtyTrackingTests`,
`CustomSourceRestoreTests`, `ShadowRestoreTests`, `LearnedValueSourceMatchTests`,
`PerSourceKeyDataFlagsTests`, `ShippedGameListTests`, `ViperSourceTests`, `GForceSharedOptionsTests`,
`PanelReadoutSharingTests`, `PageTestEffectTests`, `ClutchLearningParametersTests`, …) as behaviour
tests on the view models, keeping every rule they protected; screenshot harness adjusted; full suite +
Release build.

### Wave 3 — parallel (3 agents)
- **Screenshots**, both languages, every tab and panel; before/after visual comparison report.
- **Docs**: `architecture.md` + zh (UI: MVVM — the page now follows it; page VM tree; draft/apply
  model; Slip Angle key-data rule), `shakeit-test-effect-mvvm-design.md` (project 2 done →
  this doc), READMEs (Slip Angle restart display), `docs\full-mvvm-report.md`.
- **Final review**: a fresh reviewer over the whole change set against this plan.

## 5. Acceptance

- The original defect, as a test and by hand: change Wheel Slip's source on the ShakeIt panel before the
  page was ever shown → the page shows it; same with the page shown, hidden, and showing an unapplied
  edit of another field (kept) or the same field (replaced by the ShakeIt value).
- Apply after a ShakeIt-side change never reverts it.
- Source switch from the page and from the panel produce identical settings.
- Slip Angle: restart with a saved exact key → tab and panel show its window; another car → default;
  first launch → default; seed writes exactly what was shown.
- `MvvmRuleTests` green with the tightened rule over every view including the page.
- Full suite green, Release build clean, screenshots unchanged apart from intended values.

## 6. Risks

- **Concurrent work (found 2026-11-19, 16:06).** Another session is executing
  `docs\shakeit-source-plan.md` (Slip Angle Start value; ShakeIt source through the native effect, which
  rewrites the Wheel Lock / Wheel Slip source lists on the page AND the ShakeIt panels, the source
  identity selection in `QAdvanceFeedback.cs`, and adds ShakeIt algorithm settings). That overlaps Wave 1
  A1 (sources), D (panels), E (Slip Angle tab/panel) and Wave 2 (`QAdvanceFeedback.cs`). **Start this
  plan only after that session is finished**, then re-snapshot and re-read every owned file; its new
  source-list rules and settings become requirements of A1/D (listed in their briefs at dispatch).

- **Behaviour buried in comments of the old code-behind** (dozens of owner-reported fixes). Mitigation:
  each Wave 1 agent lists every rule it found in its region and the test that now pins it; the final
  review checks the list against the old file.
- **Parallel edits**: file ownership above; anything outside an agent's list goes in its report for the
  coordinator.
- **WPF-only behaviour** (focus, NumericEditor commit timing) can't be unit-tested: covered by the
  screenshot pass and a scripted UI smoke check in the harness.

## 7. As built (2026-11-20)

What differs from §3-§4, and the rulings made on the way (full list with the cost if wrong:
`docs\full-mvvm-report.md` §3).

Execution
- **Private copies.** Parallel builds in one tree break each other, so every Wave 1 agent worked in its
  own copy of the repo; the coordinator merged the owned files back and reconciled the seams in Wave 2.
  Wave 2 and Wave 3 worked in the live repo.
- **`CloneLearners` fix** was already done by the concurrent `shakeit-source-plan` session; Wave 1 E
  dropped it.
- **Views.** A2's half of the wheel tab became three UserControls (`WheelChannelKeyDataView`,
  `WheelChannelCurveView`, `WheelChannelPulseView`) inside `WheelChannelPageView`, not a
  `WheelChannelPageView.Curve.xaml`. One SharedSizeGroup pair and one ScrollViewer per tab, scoped at
  tab level. The attached behaviours live in one file, `Ui\Views\ViewBehaviors.cs`
  (`ViewModelLifetime`, `EventCommand.Loaded` / `DropDownOpened`). String tables are per-area partial
  files (`StringTable{En,ZhHans}.<Area>.cs`).
- **Hosts.** `ISettingsPageHost` stays small (`ApplySettings`, `CreateDefaultSettings`,
  `PluginStateChanged`, `LogError`); the area interfaces (`IWheelChannelPageHost`, `IKeyDataPageHost`,
  `IGeneralPageHost`, `ISlipAngleTabHost`, `IClutchHost`, `IShakeItSourceStateHost`) stay optional for the
  view models (they cast and tolerate absence, so a fake implements only its area). The composite
  `IPluginSettingsPageHost` combines them all and is what `Integration\ShakeIt\PluginPageHost`
  implements, so the plugin cannot forget one. The picker / script editor go through the host's
  `EditSourceAsync` (the existing async editor path); the `IPropertyPicker` / `IScriptEditor` service
  interfaces of §3.2 exist but are unused.
- **Plugin side.** `QAdvanceFeedback.SettingsPage.cs`: ONE `SettingsPageViewModel`, created on the first
  `GetWPFSettingsControl` and disposed in `End()`; each SimHub view is a thin `SettingsControl` over it.
  Every push call into the page became `SettingsPageStateChanged` (the host's `PluginStateChanged`):
  game change, the 1 Hz learned readout, a key data seed, a ShakeIt / nativeness change.

Rulings (behaviour)
- **Page / draft.** ShakeIt-side changes win and show at once, applied or not; Apply writes only the
  leaves edited on the page. **Apply broadcasts once, from the plugin** (`ApplySettings` knows the game
  and the Slip Angle tier); the page view model does not call `NotifyApplied`. A failed page action is
  logged to SimHub (`LogError`), recorded in the health registry, and the General tab's health list
  re-reads at once.
- **Lock / Slip.** A page source switch is `SettingsHub.SwitchSource` - the same public method the hub
  uses for a ShakeIt-panel switch (picking Custom keeps the current text, archives per mode). The Raw
  shadow is display-only (the draft rows are never overwritten). Custom's Raw stand-in reference is shown
  without marking the page dirty, and the next Apply saves it (the 1.1.0 rule, owner 2026-11-20). Lock's Auto S90/S75 come from the shared display rule (physical ratios), as
  on the panel. Key data is never written on Apply (immediate save only). Subscriptions and timers of a
  wheel tab run only while that tab is shown; showing it catches up. The Viper game button flushes both
  tabs' pending key data first.
- **G-Force.** Hiding the tab stops the readout timer and the Test Effect; showing it restarts the timer.
  The readout's fixed value is the applied value.
- **General.** The ShakeIt-import "override" switch is the import's argument, not a setting: it does not
  arm Apply. The health list re-reads when the tab is shown and after a failed page action.
- **Slip Angle key data** (§3.5, Wave 1 E): `SlipAngleKeyDataModel` is the Lock/Slip display rule;
  Manual with nothing stored shows the source default; the last Slip Angle context is persisted in
  `RuntimeDocument` (`LastSlipAngleGame/Car/Tier`), so the tab and panel show the exact key after a
  restart; the seed writes what is shown; Max capped at 20° (native, Custom) / 30° (car angle).
- **Screenshots.** Unchanged pixel for pixel once the harness waits for MahApps' ToggleSwitch animation
  (the Wheel Lock "Auto Generate" switch is now set by binding after load and was caught mid-slide).
