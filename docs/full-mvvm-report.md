# Full MVVM: the settings page and every ShakeIt effect — report

Date: 2026-11-20. Plan: `docs\full-mvvm-plan.md` (status: implemented; deviations in its §7 "As built").
Standing rule: `docs\architecture.md`, "UI: MVVM (standing rule)".

## 1. Summary

The plugin settings page is now MVVM end to end. `Settings\SettingsControl` is a thin host
(`InitializeComponent(); DataContext = viewModel;`) over `Ui\Views\SettingsPageView`; one
`SettingsPageViewModel` per plugin owns a draft / baseline / live settings model, the hub subscription
for its whole life, Apply / Restore all defaults and the six tab view models. The old 3,651-line
code-behind, its six partials, `ApplyDirtyState` and `PageFieldRefreshPlan` are deleted. The ShakeIt
panels' remaining code-behind events moved to attached behaviours. Slip Angle key data now follows the
Lock/Slip display rule, including after a restart.

What a driver notices:
- A change made in ShakeIt is on the settings page at once — also when the page was never opened or was
  hidden (the owner-reported defect) — and wins over an unapplied page edit of the same setting.
- Apply writes only the settings changed on the page, so it never undoes a ShakeIt-side change.
- A source switch on the page behaves exactly like one in ShakeIt (per-mode archives kept).
- The Slip Angle tab and panel show the saved window for the last game / car / source after a SimHub
  restart; Manual with nothing stored shows the default.
- Leaving the G-Force tab stops its Test Effect; the General tab's health list re-reads when shown.
- Nothing else changed: every screenshot is pixel-identical to before (§5).

Tests: **2794 → 3050** passing (full suite, 2026-11-20, 0 failures). `dotnet build QAdvanceFeedback.sln
-c Release`: 0 warnings, 0 errors.

## 2. What shipped, per wave

| Wave | Who | What |
|---|---|---|
| prep | coordinator | String tables split into per-area partial files (`StringTable{En,ZhHans}.{Areas,Page,LockSlip,Curve,GForce,General,Panels,SlipAngle}.cs`); `Ui\Mvvm\IActivatable`; `Ui\Views\ViewBehaviors.cs` (`ViewModelLifetime.Activate`, `EventCommand.Loaded` / `DropDownOpened`); `PanelViewModelBase` and `SlipAngleTabViewModel` implement `IActivatable`. |
| 0 | foundation | `SettingsDraft` (JSON-leaf three-way merge), `SettingsPageContext`, `PageSectionViewModel`, `SettingsPageViewModel` shell, `ISettingsPageHost`, `PageServices` (`IDialogService`, `IClipboard`, `IPropertyPicker`, `IScriptEditor`), tab shells, `FakeSettingsPageHost`; the original defect as a test. |
| 1 D | ShakeIt panels | Every effect view's code-behind reduced to wiring (behaviours in XAML); `MvvmRuleTests` tightened; `PanelCatchUpTests` (a change made while a panel is hidden shows when it opens). |
| 1 E | Slip Angle key data | `Core\SlipAngle\SlipAngleKeyDataModel` (display rule, default, `Shown`, `TrySeed`, `SlipAngleDisplayRuleKeyData`, `SlipAngleKeyDataContext`); `RuntimeDocument.LastSlipAngleGame/Car/Tier` + `RuntimeStore` / `RuntimeCache`; `ISlipAngleEffectHost.LearnedSlipAngleWindow`, `ISlipAngleTabHost.LearnedWindow`; plugin `GetLearnedSlipAngleWindowForDisplay`. |
| 1 A1 | Lock/Slip top half | `WheelChannelPageViewModel`, `TriggerThresholdViewModel`, `SourcesViewModel`, `SourceRowViewModel`, `CustomReferenceViewModel`, `AggregationViewModel`, `IWheelChannelPageHost`, `WheelChannelPageView`. |
| 1 A2 | Lock/Slip bottom half | `KeyDataPageViewModel` (+ `KeyDataPageRules`), `CurveEditorViewModel`, `PulseViewModel`, `IKeyDataPageHost`, `WheelChannelKeyDataView` / `CurveView` / `PulseView`. |
| 1 B | G-Force tab | `GForcePageViewModel`, `GForcePageAxisViewModel`, `GForcePageShakePreview`, `GForcePageView`, `GForcePageShakePreviewGraph`. |
| 1 C | General + shell | `GeneralViewModel`, `HealthListViewModel`, `IGeneralPageHost`, `GeneralPageView`, `SettingsPageView`. |
| 2 | integration | Seams reconciled (A1/A2 composition, one SharedSizeGroup pair and one ScrollViewer per tab, Viper-button flush of both tabs, Slip Angle / Clutch tabs, health refresh + `LogError`, single Apply broadcast, public `SettingsHub.SwitchSource`); `Integration\ShakeIt\PluginPageHost` + composite `IPluginSettingsPageHost`; `QAdvanceFeedback.SettingsPage.cs` (one page VM, `SettingsPageStateChanged` replaces every push); `Ui\Views\WpfPageServices.cs`; thin `SettingsControl`; old code-behind and partials deleted; source-text tests rewritten or deleted (§4); screenshot harness drives the plugin's own `GetWPFSettingsControl`. |
| 3 | docs + screenshots | `architecture.md` + zh-Hans, `slip-angle-design.md` §9.4 / §11, `shakeit-test-effect-mvvm-design.md`, the plan's "As built", both READMEs, this report; harness waits for MahApps toggle animations; screenshots regenerated (identical). |

Intermediate counts: D + W0 + E merged into live 2899; Wave 1 copies C 2901, A2 2916, A1 2950, D 2830;
after Wave 2 3050 (Wave 3 changed no code; re-run: 3050).

## 3. Rulings (and the cost if a ruling is wrong)

| Ruling | Why | Cost if wrong |
|---|---|---|
| Agents worked in private copies; the coordinator merged | Parallel builds in one tree break each other | Merge seams (found and fixed in Wave 2: composition, SharedSizeGroup names, double ScrollViewer) |
| ShakeIt-side change wins over an unapplied page edit of the same leaf, shows at once | Owner decision | A driver's unapplied edit of that one field is replaced (visible on the page; never silent for other fields) |
| Apply writes only leaves edited on the page, in place | Never revert a change made elsewhere | None known; a setting the page edits indirectly without writing the draft would not be applied (pinned by per-field VM tests) |
| Page VM created on first `GetWPFSettingsControl`, lives until `End()` | Subscription must never miss a change | Small: one draft copy of the settings in memory while SimHub runs |
| Plugin push calls → `PluginStateChanged` (`SettingsPageStateChanged`) | VMs re-read through the panels' rules | A state change the plugin forgets to signal shows late (until the next 1 Hz tick or tab show) |
| `IPluginSettingsPageHost` composite; area interfaces optional for VMs | Fakes implement one area; the plugin cannot forget a member | A host without an area silently degrades that area (by design, tested) |
| Apply broadcast once, by the plugin (VM does not `NotifyApplied`) | The plugin knows the game and Slip Angle tier | Panels would miss an Apply if `ApplySettings` stopped broadcasting (tested via the fake) |
| Page source switch = `SettingsHub.SwitchSource`; picking Custom keeps the current text | One switch path, identical settings | None vs the panel; differs from the old page, which switched without archives |
| Raw shadow is display-only | The readout can never be saved over the configuration | None known |
| Custom's Raw stand-in reference shown, saved by the next Apply | Never "---"; what is shown is what is saved (owner, 2026-11-20) | None - owner-confirmed |
| Lock Auto S90/S75 via the shared display rule (physical ratios) | Same numbers as the panel | Page values may differ from the old page's 0.90/0.70 display (intended: the old one was wrong) |
| Key data never written on Apply (immediate save only) | Owner, 2026-09-29 | None known |
| Wheel-tab subscriptions / timers only while the tab is shown; show = catch-up | No hidden work; catch-up covers the gap | Up to the next show for state nobody signalled |
| G-Force: hiding the tab stops the readout timer and the Test Effect; showing restarts the timer; readout fixed value = applied value | A hidden test must not drive motors | A driver switching tabs mid-test must restart it |
| General: ShakeIt-import override is not a setting, does not arm Apply | It is the import's argument | None |
| Health list re-read on show and after a failed page action, also logged to SimHub | Old `SafeUiAction` behaviour kept | None |
| Slip Angle: display rule = Lock/Slip's; Manual with nothing stored shows the default; context persisted (`LastSlipAngleGame/Car/Tier`); seed writes what is shown; caps 20° / 30° (Custom 20°) | Owner, 2026-11-19 | Custom sources using car-angle-like scales would be capped at 20° |
| Harness waits 600 ms after each tab select | The Lock "Auto Generate" ToggleSwitch was caught mid-animation | 4 s longer harness run |

## 4. Deleted tests and what now pins each rule

77 test methods of the old suite no longer exist by name (one renamed:
`LearnedValueSourceMatchTests.No_engine_path_reads_the_display_only_learned_fields` →
`..._learned_values`). Files deleted: `ApplyDirtyStateTests`, `PageFieldRefreshPlanTests`,
`SettingsControlDirtyTrackingTests`, `ShadowRestoreTests`, `Ui\PageTestEffectTests`. Abbreviations:
SDT = `Ui\SettingsDraftTests`, SPVM = `Ui\SettingsPageViewModelTests`, SPI = `Ui\Page\SettingsPageIntegrationTests`,
SPX = `Ui\Page\SettingsPageViewXamlTests`, KD = `Ui\Page\KeyDataPageViewModelTests`, SRC =
`Ui\Page\WheelChannelPageSourcesTests`, SEC = `Ui\Page\WheelChannelPageSectionsTests`, GF =
`Ui\Page\GForcePageViewModelTests`, GEN = `Ui\Page\GeneralPageViewModelTests`, CE = `Ui\Page\CurveEditorViewModelTests`.

| Deleted | Rule | Now pinned by |
|---|---|---|
| `ApplyDirtyStateTests` (8: fresh/mark/clean/loading scopes) | Dirty only from driver edits; loading never dirties | SDT `An_edit_makes_the_draft_dirty_and_editing_back_makes_it_clean`, `Default_settings_round_trip_exactly_so_a_fresh_draft_is_clean`, `An_external_change_of_an_unedited_leaf_is_adopted_and_the_draft_stays_clean`; SPVM `Section_view_models_refresh_all_properties_when_the_draft_is_reloaded` (a reload never writes, so no loading guard exists) |
| `PageFieldRefreshPlanTests` (6) | Own Apply echo refreshes nothing; external change refreshes; unapplied edit survives; broadcast-only fields re-sample | SDT `Nothing_changed_externally_adopts_nothing_and_raises_no_reload`, `An_external_change_of_another_leaf_keeps_the_unapplied_edit`; SPVM `A_page_origin_broadcast_does_not_clear_another_pending_edit`; SRC `A_source_availability_broadcast_re_samples_while_shown`. Null-args: moot (adopt is by diff, not by args) |
| `ShakeItEffects\ReviewFixTests` catch-up (3) | Page catches up on changes made while not listening | SPVM `Activate_catches_up_with_a_change_nobody_signalled`, `A_ShakeIt_side_change_before_the_page_was_ever_shown_reaches_the_page`; `Catch_up_ignores_projector_changes` superseded by design (every leaf adopts; ShakeIt wins) |
| `Ui\ShakeItSourceUiTests` page parts (7) | Re-sample on the runtime broadcast; algorithm block in dirty/Apply; panel↔page round trip; fingerprint-aware draft identity | SRC `A_source_availability_broadcast_re_samples_while_shown`, `The_algorithm_block_edits_the_draft_moves_the_key_and_applies_on_Apply`, `A_panel_side_algorithm_edit_reaches_the_block`; `ShakeItSourceUiTests.Page_draft_identity_equals_the_engine_key_for_the_same_channel`; KD `The_source_follows_the_draft_selection_not_the_applied_one`; `MvvmRuleTests` |
| `ClutchLearningParametersTests.Only_the_page_creates_the_view_model_with_learning_parameters` | Learning parameters on the page only | `ClutchLearningParametersTests.Page_view_model_shows_and_writes_learning_parameters_the_ShakeIt_one_does_not`; SPI `The_page_builds_the_Slip_Angle_and_Clutch_tabs_from_the_plugin_host_and_the_clutch_shows_learning` |
| `CustomSourceRestoreTests` (9 page-text tests) | Shadow keys as Raw; mode switch adopts identity; key data keyed by the shown identity; Apply + banner together; preset edit → Custom; reference layout / no watermark; Viper button span; both channels declare every element | KD `A_shadowed_channel_keys_as_Raw_and_shows_the_note`, `The_source_follows_the_draft_selection_not_the_applied_one`, `A_typed_value_lands_in_the_drafts_source_slot`; SPX `The_unapplied_banner_shows_exactly_while_the_page_is_dirty`, `Apply_and_restore_are_bound_commands_below_the_banner`; SRC `Editing_a_preset_sources_text_moves_the_channel_to_Custom_and_keeps_the_preset`; SPI `The_cold_start_reference_sits_under_the_key_data_and_lines_up_with_it` (asserts no Watermark), `The_Viper_game_button_spans_the_source_label_and_the_stretched_dropdown`, `Both_channels_share_one_view_so_no_notice_can_exist_for_one_channel_only` |
| `GForceShakeViewModelTests.The_page_binds_the_shake_block_and_no_code_behind_pokes_its_controls` | Shake block bound, no poking | `MvvmRuleTests.View_code_behind_only_wires_the_view_model`; GF `A_shake_switch_is_a_draft_edit`, `A_shake_mode_pick_resets_all_three_scales_and_the_trigger` |
| `GForceSharedOptionsTests.Settings_page_refreshes_*` (2) | ShakeIt-side lateral direction / integrate switches reach the page | GF `A_ShakeIt_side_lateral_direction_and_integration_switch_reach_the_tab` |
| `LearnedValueSourceMatchTests` (3 page tests) | Learned values / gate only for the shown identity; game id unconditional | KD `Learned_values_belong_to_the_source_they_were_learned_for_only`, `Learned_hints_show_only_in_Manual_and_for_an_unknown_source_only_once_the_gate_opened`; SPI `The_game_reaches_the_page_even_while_paused_or_in_a_game_menu` |
| `PageTestEffectTests` (2) | Page test uses the shared VM; stops when the page goes | GF `The_test_effect_uses_the_page_host`, `Hiding_the_tab_stops_the_test_effect` |
| `PanelReadoutSharingTests` page parts (2) | Page graph and readout through the shared helpers | CE `The_source_graph_samples_the_shown_key_data_through_the_working_curve_and_follows_it`; GF `The_readout_describes_the_page_mode_with_the_applied_fixed_value` |
| `PerSourceKeyDataFlagsTests` (17 page-text tests) | Immediate saves never dirty Apply; reset saves at once; Custom hidden by MODE; synchronous latch; game id while paused; game change re-resolves; shadow never saved; per-game blank / says so; both write paths latch; back to Auto keeps numbers; nothing writes under Auto; flush under the typed flags; pending save survives closing; reload is model→UI; immediate disk writes | KD `Nothing_here_ever_dirties_the_page`, `Reset_restores_the_sources_defaults_saves_at_once_and_drops_a_pending_save`, `Reset_shows_only_when_the_sources_configuration_differs_and_never_for_Custom`, `A_typed_value_latches_its_slot_synchronously_and_saves_700ms_later_never_dirtying_Apply`, `A_game_arriving_re_resolves_the_per_game_slot`, `Per_game_with_no_game_shows_blank_says_so_and_refuses_the_edit`, `Going_back_to_Auto_keeps_the_typed_manual_numbers_in_their_slot`, `Under_Auto_typing_writes_nothing`, `A_pending_save_is_written_when_the_view_goes_away`, `The_toggles_write_the_drafts_source_flags_into_the_live_settings_and_save_at_once`, `An_unrelated_reload_leaves_a_typed_value_on_screen`; SPI `The_game_reaches_the_page_even_while_paused_or_in_a_game_menu`, `The_Slip_Angle_key_data_applies_at_once_and_never_dirties_the_page`; SRC `A_shadowed_channels_readout_is_never_saved_over_its_configuration`, `A_plugin_state_change_re_samples_the_shadow`; `PerSourceKeyDataFlagsTests.A_driver_written_slot_stops_the_engines_one_time_seed` (kept) |
| `SettingsControlDirtyTrackingTests` (10) | One suppression mechanism; reflective sweep covers every input; NumericEditor everywhere; Test Effect excluded; key data via loading guard; Slip Angle curve paths | Superseded by the JSON-leaf draft (no per-control table, no suppression flag): SDT (whole class), SPVM `Edit_writes_the_draft_raises_the_property_and_marks_the_draft_edited`, GF `Every_number_property_writes_its_own_draft_field`, `The_test_effect_runs_on_the_unapplied_page_values_and_never_marks_dirty`, KD `Nothing_here_ever_dirties_the_page`, SPI `Every_numeric_input_of_the_page_is_the_wrapped_NumericEditor`. Slip Angle curve-path guard: moot (own VM) |
| `ShadowRestoreTests` (3) | Leaving the shadow restores the configuration; Custom's text is the configuration; shadow decided on the shown mode | SRC `The_shadow_ends_when_the_game_becomes_supported_and_the_configuration_comes_back`, `A_shadowed_channels_readout_is_never_saved_over_its_configuration`, `A_Viper_channel_on_an_unsupported_game_shows_Raw_disables_the_rows_and_keys_as_Raw` (the draft rows are never overwritten, so nothing needs restoring) |
| `ShippedGameListTests.The_button_is_hidden_for_a_shipped_title` | Viper game button hidden for shipped titles | SRC `The_game_button_is_hidden_for_a_shipped_title` |
| `ViperSourceTests.Plugin_availability_is_re_sampled_while_driving_not_once_at_construction` | Viper availability re-sampled | SPI `Viper_availability_is_re_sampled_on_the_plugin_push_not_once_at_construction` |

Acceptance (plan §5) is pinned by SPI `Acceptance_*` (5 tests), SPVM `Apply_after_a_ShakeIt_side_change_does_not_revert_it`,
`Ui\SlipAngle*` / `SlipAngle\SlipAngleKeyDataDisplayRuleTests` / `SlipAngleLastContextPersistenceTests`, and `MvvmRuleTests`.

## 5. Screenshots

The harness (`tools\screenshot-harness`) renders the six settings tabs (English) and the seven ShakeIt
panels / tests in English and zh-Hans (20 PNGs) from the plugin's own `GetWPFSettingsControl`.

- **Wave 2 run vs the pre-MVVM images:** one difference, in `settings-wheel-lock.png` (150 px, box
  x 316–331, y 619–643): the key data "Auto Generate" ToggleSwitch thumb caught mid-slide. The new page
  sets `IsChecked` by binding after load, so MahApps animates the thumb from Off; the old page set it
  before the first render.
- **Fix:** after selecting each tab the harness runs the dispatcher for 600 ms (the ShakeIt captures
  already did), so every storyboard has finished.
- **Wave 3 run vs `docs\images` (before):** all 20 PNGs identical, byte for byte. Regenerating
  `docs\images` therefore changed no file. The full-MVVM page renders exactly as the old page did.

## 6. Final review and fix pass

A fresh reviewer read the whole change set against the plan (old code-behind vs new view models, region by
region). Fixed, each with a test that failed first:

- **C1 (critical): the page read "unapplied changes" after any G-Force learning, and Apply could not clear it.**
  `GForceSettings.CurrentLearned{Lat,Accel,Decel}MaxG` are get-only reads of the live learners; they serialised
  as draft leaves the draft copy can never hold. `SettingsJsonLeaves` now uses a contract resolver that leaves
  out get-only value properties (get-only object/dictionary properties stay; the config file is unchanged).
  Test: `SettingsDraftTests.Runtime_read_only_values_never_make_the_draft_dirty_and_Apply_stays_clean`.
- **I1 (important): the page's G-Force Test Effect ignored a Lateral direction change while running.** The
  setter now drops the cached test copy, like every other edit. Test:
  `GForcePageViewModelTests.The_test_effect_follows_a_lateral_direction_change_made_while_it_runs`.
- Three comments had `docs\full-mvvm-plan.md` corrupted by a form-feed character; repaired.

Suite after the fix pass: 3052 passed, 0 failed; Release build 0 warnings, 0 errors.

Deferred minors (not fixed; for the owner):
- `SettingsPageViewModel.Dispose()` only unsubscribes the hub; a tab still active at `End()` keeps its timers
  (worst case one late `ApplySettings` call and a health entry; `End` already saves).
- `Apply()` does not adopt live changes first (no current path found that needs it).
- The manual-gate-per-source rule lost its direct test (the page fake ignores the identity); production passes
  the right identity.
- The UI thread now reads the manual gate (the old 1 Hz push read it on the data thread).
- `WheelChannelSettings.KeepPresetSourcesWhileShadowed` is no longer called but still tested.
- `IPropertyPicker` / `IScriptEditor` (PageServices.cs) are unused; the page uses `IWheelChannelPageHost.EditSourceAsync`.

Behaviour choices flagged by the review, confirmed by the owner (2026-11-20):
- A Custom channel with no stored cold-start reference shows the default (Plugin Internal's values) without
  marking the page dirty, and the next Apply saves it, as the old page did
  (`CustomReferenceViewModel.StoreShownDefaultIfMissing`, called from Apply; tests
  `Apply_saves_the_shown_default_reference_when_Custom_has_none_stored`,
  `Apply_does_not_save_a_default_reference_while_the_channel_is_not_on_Custom`).
- Re-picking a preset (e.g. Viper) restores that mode's archived rows (the hub's path, "one source-switch path").
  Kept as is: editing a preset's source text switches the channel to Custom and saves the edit there, so a
  preset's archive is never edited and restoring it gives the same text as regenerating it.
