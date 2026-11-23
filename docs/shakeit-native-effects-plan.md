# QAdvanceFeedback Native ShakeIt Effects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Register QAdvanceFeedback WheelLock, WheelSlip and four G-Force effects as native ShakeIt effects, read ShakeIt's own lock/slip algorithm directly, and keep ShakeIt-panel and plugin-page settings in live two-way sync.

**Architecture:** Six `EffectsContainerBase<ToneOutput>` subclasses (SimHub assembly scan) read the plugin's latest published frame. All pure logic (routing, defaults, source options, settings hub, file diffing, tap freshness) lives in SimHub-free code that the net8 test project link-compiles; only thin adapters touch `SimHub.Plugins.DataPlugins.ShakeItV3`. A `SettingsHub` owns writes of the shared fields and broadcasts per-field changes to every UI.

**Tech Stack:** C# / .NET Framework 4.8 / WPF, SimHub ShakeItV3 API (`lib\SimHub.Plugins.dll`), xunit on net8.0.

**Spec:** `docs/shakeit-native-effects-design.md`

## Global Constraints

- ContainerType class names are frozen once shipped: `QAdvanceFeedbackWheelLockContainer`, `QAdvanceFeedbackWheelSlipContainer`, `QAdvanceFeedbackGForceBottomFrontContainer`, `QAdvanceFeedbackGForceBottomRearContainer`, `QAdvanceFeedbackGForceBackLowContainer`, `QAdvanceFeedbackGForceBackTopContainer`.
- Metadata category `"QAdvanceFeedback"`, `OutputMode.Any`.
- Single-DLL distribution: every SimHub reference stays `<Private>false</Private>`; no new NuGet in the plugin project.
- `Core\` and the explicitly listed `Settings\` files stay SimHub-free; every new SimHub-free file is added to `QAdvanceFeedback.Tests.csproj` by explicit `<Compile Include>` (no wildcards under `Settings\`).
- Any ShakeIt-side code path is wrapped in the existing `SafeCall` / health registry pattern (`Core/Health`), so a SimHub API change degrades to 0 output, never a crash.
- Stale threshold: 500 ms (plugin frame for effects; tap for the ShakeIt source).
- Tone defaults (exact): Lock 30/20 Hz, Slip 45/30 Hz, BottomFront 100/60, BottomRear 100/70, BackLow 100/70, BackTop 100/60; G-Force `WhiteNoise = 10`, `UseWhiteNoise = false`; Lock/Slip `UseWhiteNoise = false`; all `UseHighFrequency = true`, `FrequencyBasedOnPreFilter = false`, `UsePrehemptiveMode = false`.
- Default Lock/Slip routing: `Mono`.
- All user-visible text through `Core/Localization` (`StringTableEn` + `StringTableZhHans`).
- Documentation lives directly under `docs\` (no sub-folders like `docs\superpowers`), named like the existing files (`<topic>-design.md`, `<topic>-report.md`). Every task that changes behaviour updates `docs\shakeit-native-effects-design.md` if the design moved, and ends with its own `docs\<topic>-report.md` (what was built, how it was verified, deviations): Task 1 → `shakeit-spike-report.md`, Task 3 → `settings-sync-report.md`, Task 4 → `shakeit-builtin-source-report.md`, Tasks 6–7 → `shakeit-native-effects-report.md`, Task 8 → `settings-page-sync-report.md`.
- Backward compatibility: every currently published property keeps its exact name and value (`AllPublishedPropertiesTests` must only ever gain names, never lose or rename one); Custom-Effect profiles bound to them keep working. ShakeIt source order: fresh tap → legacy `ShakeITMotorsV3Plugin.Export.*` (when all four are reported) → Raw.
- Repo is not under git today: commit steps apply once the owner has run `git init` (or skip them).
- Build: `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj -c Release`; tests: `dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj`.

## Review Focus

1. Same effect added twice, or to both Motors and Bass Shakers profiles → tap must still run hidden engines once per frame (dedupe on `FrameTime`). Test in Task 4.
2. ShakeIt-panel source change while the plugin page has unapplied edits to other fields → only the source control changes; other edits survive and Apply still applies them. Test in Task 8 (`PageFieldRefreshPlanTests`).
3. Hub's own save triggers the file watcher → must not re-broadcast (echo). Test in Task 3.
4. Game paused / not running / SimHub restarted mid-session → stale plugin frame or tap → effects output 0 and ShakeIt source falls back to Raw, not frozen values. Test in Task 4 and Task 2 (`FrameSnapshot` staleness).
5. Viper becomes unavailable while a channel is on Viper, or Custom has no content → dropdown must still show the current selection (not blank) while not offering it for new selection. Test in Task 2.

---

### Task 1: Live SimHub spike (risk check, throwaway)

Validates spec risks 1–3 before building on them. No tests; manual verification in SimHub.

**Files:**
- Create: `QAdvanceFeedback/Integration/ShakeIt/SpikeContainer.cs` (deleted at end of task)

- [ ] **Step 1:** Write `[ShakeItContainerMetadata(999, "QAF Spike", "spike", "QAdvanceFeedback", OutputMode.Any)] public class QafSpikeContainer : EffectsContainerBase<ToneOutput>` with: `AllowEffectsAggregations => false`; `AvailableEffects` yielding one effect per placement of the current `AggregationMode` (`"Corners"`→FL/FR/RL/RR, `"Mono"`→All); a `ComboBox` in `GetEditControl()` that sets `AggregationMode` and nulls `_availableEffectsCache`; a `FilterBase` subclass whose `GetEditControl()` returns an empty `UserControl` and `ResponseCurveEditor` returns a `UserControl` with a red `Border`; each effect returns the hidden-engine value `hiddenLock.FrontLeft.GetEffectValue(data, calibratedFeedback, hiddenLock)` (hidden `new WheelsLockContainer()` + `LoadDefaultSettings()`).
- [ ] **Step 2:** Build, copy the DLL to SimHub, add "QAF Spike" in ShakeIt Bass Shakers and Motors.
- [ ] **Step 3: Verify** — red border sits in the response-curve slot with no Gamma sliders; switching Mono↔Corners updates the channel-mapping rows without reopening; effect bar moves while braking to lock in a game (hidden engine works). Record results in the spec's Risks section; if a check fails, switch the plan to the listed fallback before Task 6.
- [ ] **Step 4:** Delete the spike file.

### Task 2: Pure effect mapping (routing, defaults, source options, frame snapshot)

**Files:**
- Create: `QAdvanceFeedback/Core/ShakeItEffects/EffectRouting.cs`, `ToneDefaults.cs`, `FrameSnapshot.cs`, `QAdvanceFeedback/Settings/ShakeItSourceOptions.cs` (Settings, because Core may not reference `SourceMode`)
- Modify: `QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj` (explicit Include for `Settings\ShakeItSourceOptions.cs`; Core files arrive via `Core\**\*.cs`)
- Test: `QAdvanceFeedback.Tests/ShakeItEffects/EffectRoutingTests.cs`, `ToneDefaultsTests.cs`, `ShakeItSourceOptionsTests.cs`, `FrameSnapshotTests.cs`

**Interfaces:**
- Produces:
  - `enum EffectRouting { Corners, FrontRear, LeftRight, Mono }`; `static class EffectRoutingNames { string ToAggregationMode(EffectRouting); EffectRouting Parse(string aggregationMode) /* null/unknown → Mono */ }` using the strings `"Corners"`, `"Front/Rear"`, `"Left/Right"`, `"Mono"`.
  - `enum EffectSlot { All, Front, Rear, Left, Right, FrontLeft, FrontRight, RearLeft, RearRight }` (same order as `FFBPlacement`, so the adapter casts).
  - `static IReadOnlyList<EffectSlot> EffectRoutingSlots.For(EffectRouting)`.
  - `static double ChannelValues.Lock(ProjectedWheelLockSlipResult r, EffectSlot s)` / `Slip(...)` → 0..100 pulsed values (All→`LockAll`, Front→`LockFront`, …, FrontLeft→`LockWheels.FrontLeft`).
  - `enum GForcePad { BottomFront, BottomRear, BackLow, BackTop }`; `static double ChannelValues.GForce(GForceOutput o, GForcePad pad, bool right)` → 0..100, null → 0. BottomFront=Bottom.FrontLeft/Right, BottomRear=Bottom.RearLeft/Right, BackLow=Back.LowLeft/Right, BackTop=Back.TopLeft/Right.
  - `sealed class ToneDefault { int LowHz; bool UseHigh; int HighHz; bool UseNoise; int NoisePercent }`; `static ToneDefault ToneDefaults.Lock, Slip; static ToneDefault ToneDefaults.For(GForcePad)`.
  - `static IReadOnlyList<SourceMode> ShakeItSourceOptions.For(bool viperAvailable, bool customConfigured, SourceMode current)` — order Manual, ShakeIt, Viper, Custom; Viper only if available or `current`; Custom only if configured or `current`.
  - `sealed class FrameSnapshot<T> { FrameSnapshot(T value, long timestampTicks); T Value; bool IsFresh(long nowTicks, long staleTicks) }` and `const long StaleMs = 500`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Parse_unknown_or_null_is_Mono() { Assert.Equal(EffectRouting.Mono, EffectRoutingNames.Parse(null)); Assert.Equal(EffectRouting.Mono, EffectRoutingNames.Parse("x")); }
[Fact] public void Corners_slots_are_four_wheels() => Assert.Equal(new[]{EffectSlot.FrontLeft,EffectSlot.FrontRight,EffectSlot.RearLeft,EffectSlot.RearRight}, EffectRoutingSlots.For(EffectRouting.Corners));
[Fact] public void Mono_slot_reads_LockAll() { /* build ProjectedWheelLockSlipResult with LockAll=42 */ Assert.Equal(42, ChannelValues.Lock(r, EffectSlot.All)); }
[Fact] public void BackTop_right_reads_Back_TopRight() { /* GForceOutput BackTopRight=55 */ Assert.Equal(55, ChannelValues.GForce(o, GForcePad.BackTop, right: true)); }
[Fact] public void GForce_null_reads_zero() => Assert.Equal(0, ChannelValues.GForce(GForceOutput.Empty, GForcePad.BottomRear, false));
[Theory] [InlineData(GForcePad.BottomFront,60)] [InlineData(GForcePad.BottomRear,70)] [InlineData(GForcePad.BackLow,70)] [InlineData(GForcePad.BackTop,60)]
public void GForce_high_hz(GForcePad p, int hz) { var d = ToneDefaults.For(p); Assert.Equal(100, d.LowHz); Assert.Equal(hz, d.HighHz); Assert.True(d.UseHigh); Assert.Equal(10, d.NoisePercent); Assert.False(d.UseNoise); }
[Fact] public void Lock_slip_tone() { Assert.Equal((30,20), (ToneDefaults.Lock.LowHz, ToneDefaults.Lock.HighHz)); Assert.Equal((45,30), (ToneDefaults.Slip.LowHz, ToneDefaults.Slip.HighHz)); }
[Fact] public void Options_hide_viper_and_custom_when_unavailable() => Assert.Equal(new[]{SourceMode.Manual, SourceMode.ShakeIt}, ShakeItSourceOptions.For(false,false,SourceMode.Manual));
[Fact] public void Options_keep_current_viper_even_if_unavailable() => Assert.Contains(SourceMode.Viper, ShakeItSourceOptions.For(false,false,SourceMode.Viper));
[Fact] public void Snapshot_stale_after_500ms() { var s = new FrameSnapshot<int>(1, 0); long ms = TimeSpan.TicksPerMillisecond; Assert.True(s.IsFresh(499*ms, 500*ms)); Assert.False(s.IsFresh(501*ms, 500*ms)); }
```

- [ ] **Step 2: Run** `dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj --filter FullyQualifiedName~ShakeItEffects` → FAIL (types missing).
- [ ] **Step 3: Implement** the four files per Interfaces.
- [ ] **Step 4: Run** tests → PASS.
- [ ] **Step 5: Commit** `feat: pure ShakeIt effect routing, tone defaults and source options`.

### Task 3: SettingsHub and ConfigFileWatcher

**Files:**
- Create: `QAdvanceFeedback/Settings/SettingsHub.cs`, `QAdvanceFeedback/Settings/ConfigFileWatcher.cs`
- Modify: `QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj` (explicit Includes for both)
- Test: `QAdvanceFeedback.Tests/SettingsHubTests.cs`, `ConfigFileWatcherTests.cs`

**Interfaces:**
- Produces:
  - `enum SettingField { LockSourceMode, SlipSourceMode, AccelMaxMode, DecelMaxMode, LatMaxMode, FixedAccelMaxG, FixedDecelMaxG, FixedLatMaxG, LockProjector, SlipProjector, SourceAvailability }`
  - `enum SettingsOrigin { PluginPage, ShakeItPanel, File, Runtime }`
  - `sealed class SettingsChangedEventArgs : EventArgs { IReadOnlyCollection<SettingField> Fields; SettingsOrigin Origin }`
  - `sealed class SettingsHub { SettingsHub(Func<QAdvanceFeedbackSettings> settings, Action<QAdvanceFeedbackSettings> applyAndSave); event EventHandler<SettingsChangedEventArgs> Changed; bool Set(SettingField f, object value, SettingsOrigin origin); void NotifyApplied(IEnumerable<SettingField> fields, SettingsOrigin origin); void NotifyFileChanged(QAdvanceFeedbackSettings fromDisk); static object Read(QAdvanceFeedbackSettings s, SettingField f); static IReadOnlyList<SettingField> Diff(QAdvanceFeedbackSettings before, QAdvanceFeedbackSettings after); string LastSavedHash { get; } }`
  - `Set` returns false and raises nothing when the value is equal; for `LockSourceMode`/`SlipSourceMode` it performs, in order: seed Custom reference when entering Custom (`KeyDataPointDefaults.SeedCustomFrom(KeyDataPointSettings.ClassifyExact(<current identity>, isLock), isLock)`, identity computed the way `SettingsControl.ComputeCurrentSourceIdentity` does — extract that into a static `SourceIdentity` helper both use), then `WheelChannelSettings.SwitchSourceMode(mode, isLock)`. Projector/SourceAvailability fields are broadcast-only (`Set` throws `InvalidOperationException`).
  - `applyAndSave` is the plugin's existing `ApplySettings()` body (rebuild projected engine, `GForce.ApplyTo`, `ConfigStore.Save`); the hub then stores `LastSavedHash` = SHA-256 of the saved file.
  - `sealed class ConfigFileWatcher : IDisposable { ConfigFileWatcher(string path, Func<string> lastSavedHash, Action<QAdvanceFeedbackSettings> onExternalChange, int debounceMs = 300) }` — `FileSystemWatcher` on the directory filtered to the file name, debounced timer, reads the file with retry on `IOException` (3 × 50 ms), skips when its hash equals `lastSavedHash()`, else `ConfigStore.Load` → callback.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Set_raises_only_that_field() { var hub = NewHub(out var s); SettingsChangedEventArgs got = null; hub.Changed += (_, e) => got = e;
  Assert.True(hub.Set(SettingField.AccelMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel));
  Assert.Equal(new[]{SettingField.AccelMaxMode}, got.Fields); Assert.Equal(SettingsOrigin.ShakeItPanel, got.Origin); Assert.Equal(GMaxMode.Fixed, s.GForce.AccelMaxMode); }
[Fact] public void Set_same_value_is_noop() { var hub = NewHub(out _); int n = 0; hub.Changed += (_, __) => n++; hub.Set(SettingField.AccelMaxMode, GMaxMode.Auto, SettingsOrigin.ShakeItPanel); Assert.Equal(0, n); }
[Fact] public void Set_saves_once() { int saves = 0; var hub = NewHub(out _, () => saves++); hub.Set(SettingField.FixedLatMaxG, 2.5, SettingsOrigin.ShakeItPanel); Assert.Equal(1, saves); }
[Fact] public void SourceMode_switch_loads_mode_sources() { var hub = NewHub(out var s); hub.Set(SettingField.LockSourceMode, SourceMode.ShakeIt, SettingsOrigin.ShakeItPanel);
  Assert.Equal(SourceMode.ShakeIt, s.Lock.SourceMode); Assert.Equal(ShakeItTapPropertyNames.GetWheelPropertyName(true, "FrontLeft"), s.Lock.SourceFrontLeft); }
[Fact] public void Diff_reports_changed_shared_fields_only() { var a = new QAdvanceFeedbackSettings(); var b = Clone(a); b.GForce.FixedDecelMaxG = 3; b.Lock.SlipFloorFactor += 1;
  Assert.Equal(new[]{SettingField.FixedDecelMaxG}, SettingsHub.Diff(a, b)); }
[Fact] public void FileChanged_applies_with_File_origin() { var hub = NewHub(out _); SettingsChangedEventArgs got = null; hub.Changed += (_, e) => got = e; var disk = new QAdvanceFeedbackSettings(); disk.GForce.LatMaxMode = GMaxMode.Fixed;
  hub.NotifyFileChanged(disk); Assert.Equal(SettingsOrigin.File, got.Origin); Assert.Contains(SettingField.LatMaxMode, got.Fields); }
// ConfigFileWatcherTests (temp dir):
[Fact] public async Task External_write_fires_once_after_debounce() { /* write file twice within 100 ms → exactly one callback within 1 s */ }
[Fact] public async Task Own_save_is_ignored() { /* lastSavedHash returns the hash of what we write → no callback within 1 s */ }
```

- [ ] **Step 2: Run** `dotnet test ... --filter "FullyQualifiedName~SettingsHub|FullyQualifiedName~ConfigFileWatcher"` → FAIL.
- [ ] **Step 3: Implement** both classes per Interfaces (the `SourceIdentity` extraction included; `SettingsControl` switched to call it).
- [ ] **Step 4: Run** full test suite → PASS (existing 994 + new).
- [ ] **Step 5: Commit** `feat: SettingsHub per-field sync and config file watcher`.

### Task 4: ShakeIt built-in tap replaces the export-based ShakeIt source

**Files:**
- Create: `QAdvanceFeedback/Core/ShakeItEffects/TapDeduper.cs` (pure), `QAdvanceFeedback/Core/ShakeItEffects/ShakeItTapPropertyNames.cs` (pure), `QAdvanceFeedback/Integration/ShakeIt/ShakeItBuiltInTap.cs` (SimHub)
- Modify: `QAdvanceFeedback/QAdvanceFeedback.cs` (`ResolveWheel` ShakeIt branch, publish tap properties, `Instance`), `MotorsExportAvailabilityProvider.cs` (kept: now gates the legacy fallback only), `Settings/WheelChannelSettings.cs` (`ApplyMotorsExportDefaults` → new names; migration in `RepairPresetSourcesIfStale`), `Core/Localization/StringTable*.cs` (`Sources.Mode.ShakeIt` = "ShakeIt (built-in)", `Sources.ShakeItUnavailable.Note` = add a QAdvanceFeedback effect to the active ShakeIt profile)
- Test: `QAdvanceFeedback.Tests/ShakeItEffects/TapDeduperTests.cs`, `QAdvanceFeedback.Tests/ShakeItSourceMigrationTests.cs`

**Interfaces:**
- Consumes: `FrameSnapshot<T>` (Task 2).
- Produces:
  - `sealed class TapDeduper { bool ShouldRun(DateTime frameTime) }` — true once per distinct `frameTime`.
  - `static class ShakeItBuiltInTap { void Sample(GameData data, CalibratedFeedbackData cfd, WheelChannelSettings lockCh, WheelChannelSettings slipCh); FrameSnapshot<(Corners Lock, Corners Slip)> Latest { get; } }` — holds one hidden `WheelsLockContainer` (after `LoadDefaultSettings()`) and one hidden `WheelsSlipContainer` (`MuteWhenLockEffectIsActive = false`), pushes `LockSensibility`, `BrakeFilter`, `ThrottleFilter` from settings each call, evaluates the four `WheelSlipEffect`s, stores values × 100 via `Volatile.Write`. Body wrapped in `SafeCall` under a new `HealthSubsystems.ShakeItTap`.
  - `static string ShakeItTapPropertyNames.GetWheelPropertyName(bool isLock, string wheelSuffix)` → `QAdvanceFeedback.ShakeIt.WheelLock.<Wheel>` / `QAdvanceFeedback.ShakeIt.WheelSlip.<Wheel>` (published name, without the plugin prefix SimHub adds: `ShakeIt.WheelLock.<Wheel>`). `MotorsExportPropertyNames` is left untouched — it is the legacy fallback's lookup.
  - `public static QAdvanceFeedback Instance { get; private set; }` set in `Init`, cleared in `End`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Dedupes_same_frame() { var d = new TapDeduper(); var t = DateTime.UtcNow; Assert.True(d.ShouldRun(t)); Assert.False(d.ShouldRun(t)); Assert.True(d.ShouldRun(t.AddMilliseconds(16))); }
[Fact] public void Old_export_names_migrate_to_builtin() { var ch = WheelChannelSettings.CreateLockDefaults(); ch.SourceMode = SourceMode.ShakeIt; ch.SourceFrontLeft = "ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft";
  Assert.True(ch.RepairPresetSourcesIfStale(isLockChannel: true)); Assert.Equal("QAdvanceFeedback.ShakeIt.WheelLock.FrontLeft", ch.SourceFrontLeft); }
```

- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** In `DataUpdate`, before Layer 4, publish the tap's eight values (fresh → values, stale → 0) through `PropertyPublisher` under the new names; in `ResolveWheel`, when `channel.SourceMode == SourceMode.ShakeIt`: fresh tap → tap corner; else if `MotorsExportAvailabilityProvider` reports the channel's four legacy export properties → resolve `MotorsExportPropertyNames.GetWheelPropertyName(...)`; else the Raw fallback branch (same as Viper-blocked). Source identity for learning follows the value actually used. Delete `docs/shakeit-export-guide.md` references from UI strings.
- [ ] **Step 4: Run** full suite → PASS; `dotnet build -c Release` → 0 warnings.
- [ ] **Step 5: Commit** `feat: ShakeIt source reads ShakeIt's own lock/slip via hidden engines`.

### Task 5: Plugin frame snapshot for effects

**Files:**
- Modify: `QAdvanceFeedback/QAdvanceFeedback.cs`

**Interfaces:**
- Consumes: `FrameSnapshot<T>` (Task 2).
- Produces: `public FrameSnapshot<ProjectedWheelLockSlipResult> LatestProjected { get; }` and `public FrameSnapshot<GForceOutput> LatestGForce { get; }` on the plugin, written with `Volatile.Write` right after `_publisher.UpdateProjected(projected)` and after `_gforceEngine.Compute(...)`; timestamps from `Stopwatch.GetTimestamp()` converted to ticks. `public SettingsHub Hub { get; }` created in `InitCore` with `applyAndSave` = current `ApplySettings` body; `ConfigFileWatcher` created there too and disposed in `End`.

- [ ] **Step 1: Implement** per Interfaces (covered by Task 2 snapshot tests; this is wiring).
- [ ] **Step 2: Run** full suite + Release build → PASS, 0 warnings.
- [ ] **Step 3: Commit** `feat: expose latest projected and G-force frames and settings hub`.

### Task 6: WheelLock / WheelSlip ShakeIt effects with read-only curve

**Files:**
- Create: `QAdvanceFeedback/Integration/ShakeIt/QAdvanceFeedbackWheelChannelContainerBase.cs`, `QAdvanceFeedbackWheelLockContainer.cs`, `QAdvanceFeedbackWheelSlipContainer.cs`, `QAdvanceFeedbackEffect.cs`, `QAdvanceProjectionFilter.cs`, `WheelChannelEffectPanel.cs` (code-built WPF, like AZOM)
- Create: `QAdvanceFeedback/Settings/ProjectionCurvePlot.xaml(.cs)` — extracted from `SettingsControl.RefreshCurvePlot` (polyline 0..100 via `OutputProjector.Project`, five anchor markers, `RenderGraphDecorations`, key-point markers); `SettingsControl` replaces its inline canvases with two `ProjectionCurvePlot` instances (`IsReadOnly=false` keeps today's behaviour)
- Modify: `Core/Localization/StringTable*.cs` (effect names/descriptions, panel labels)

**Interfaces:**
- Consumes: `EffectRouting*`, `ChannelValues`, `ToneDefaults`, `ShakeItSourceOptions` (Task 2); `SettingsHub` (Task 3); `ShakeItBuiltInTap.Sample` (Task 4); `LatestProjected`, `Hub`, `Instance` (Task 5).
- Produces:
  - `abstract class QAdvanceFeedbackWheelChannelContainerBase : EffectsContainerBase<ToneOutput>` with `protected abstract bool IsLock`; ctor sets `AggregationMode = "Mono"` **before** anything reads it (guards the `AggregationMode` getter's recursion into `EffectsAggregates`); `AllowEffectsAggregations => false`; `AvailableEffects` = one `QAdvanceFeedbackEffect` per `EffectRoutingSlots.For(EffectRoutingNames.Parse(AggregationMode))`; `public EffectRouting Routing { get; set; }` (`[JsonIgnore]`, writes `AggregationMode`, nulls `_availableEffectsCache`); `Filter => _filter` (a `QAdvanceProjectionFilter` for `IsLock`); `SetAudioDefaults` from `ToneDefaults.Lock/Slip`; `ProcessEffects` calls `ShakeItBuiltInTap.Sample(...)` then base.
  - `sealed class QAdvanceFeedbackEffect : EffectBase { QAdvanceFeedbackEffect(string name, FFBPlacement p, Func<double> value0100) }` — `GetIsSupportedOnCurrentGame => true`; `GetEffectValue` = `value0100() / 100` clamped. Reused by Task 7.
  - `sealed class QAdvanceProjectionFilter : FilterBase { QAdvanceProjectionFilter(bool isLock) }` — `GetOutput(x) = Clamp01(x) * 100`; `GetEditControl()` → empty `UserControl`; `ResponseCurveEditor` → `ProjectionCurvePlot { IsReadOnly = true }` bound to the channel's `ProjectorSettings`, redrawn on `Hub.Changed` containing `LockProjector`/`SlipProjector` (via its dispatcher). If Task 1 failed check 1, return `null` here and host the plot at the top of `WheelChannelEffectPanel`.
  - Panel: Source `ComboBox` filled from `ShakeItSourceOptions.For(viperAvailable, CustomSources?.HasContent() == true, current)`, selection → `Hub.Set(Lock/SlipSourceMode, mode, ShakeItPanel)`; Routing `ComboBox` (Per wheel / Front-Rear / Left-Right / One channel); refreshes on `Hub.Changed` (source fields, `SourceAvailability`).
  - Metadata: `[ShakeItContainerMetadata(200, "QAdvanceFeedback WheelLock", "<localized description>", "QAdvanceFeedback", OutputMode.Any)]`, Slip at order 201.

- [ ] **Step 1: Implement** per Interfaces; `ProjectionCurvePlot` extraction first, then confirm the plugin page renders identically (screenshot harness in `tools/screenshot-harness`, compare against the current capture).
- [ ] **Step 2: Run** full suite + Release build → PASS, 0 warnings.
- [ ] **Step 3: Verify in SimHub:** add both effects to a Bass Shakers profile → frequencies show 30/20 and 45/30; routing default Mono with one channel row; switching to Per wheel shows four rows; curve slot shows our read-only graph matching the plugin page; output bar tracks `QAdvanceFeedback.WheelLock.Projected.All` while braking; changing source in the panel updates the plugin page's source within a second.
- [ ] **Step 4: Commit** `feat: native QAdvanceFeedback WheelLock/WheelSlip ShakeIt effects`.

### Task 7: G-Force ShakeIt effects

**Files:**
- Create: `QAdvanceFeedback/Integration/ShakeIt/QAdvanceFeedbackGForceContainerBase.cs`, `QAdvanceFeedbackGForceContainers.cs` (the four sealed subclasses), `GForceEffectPanel.cs`

**Interfaces:**
- Consumes: `ChannelValues.GForce`, `GForcePad`, `ToneDefaults.For` (Task 2); `QAdvanceFeedbackEffect` (Task 6); `LatestGForce`, `Hub` (Task 5); `ShakeItBuiltInTap.Sample` (Task 4).
- Produces: `abstract class QAdvanceFeedbackGForceContainerBase : EffectsContainerBase<ToneOutput>` with `protected abstract GForcePad Pad`; `AvailableEffects` = Left (`FFBPlacement.Left`) and Right (`FFBPlacement.Right`) `QAdvanceFeedbackEffect`s; `AllowEffectsAggregations => false`; `Filter => null`; `SetAudioDefaults` from `ToneDefaults.For(Pad)`; `ProcessEffects` calls the tap then base. Subclasses at orders 210–213 with display names "QAdvanceFeedback G-Force BottomFront/BottomRear/BackLow/BackTop". Panel: three Auto/Fixed combos + three Fixed max-G `NumericEditor`s (same min/max/step as the plugin page's G-Force tab), each edit → `Hub.Set(..., ShakeItPanel)`; refreshes on `Hub.Changed`.

- [ ] **Step 1: Implement** per Interfaces.
- [ ] **Step 2: Run** full suite + Release build → PASS, 0 warnings.
- [ ] **Step 3: Verify in SimHub:** four effects appear; frequencies 100/60, 100/70, 100/70, 100/60 with white noise 10 off; bind per the reference map (BottomFront 0/1, BackLow 2/3, BackTop 4/5, BottomRear 6/7) and use the plugin's G-Force test effect to confirm each pad fires the right channels; switching Accel to Fixed in one panel updates the other three panels and the plugin page.
- [ ] **Step 4: Commit** `feat: native QAdvanceFeedback G-Force ShakeIt effects`.

### Task 8: Plugin page joins the hub (per-field refresh, partial Apply semantics)

**Files:**
- Create: `QAdvanceFeedback/Settings/PageFieldRefreshPlan.cs` (pure)
- Modify: `QAdvanceFeedback/Settings/SettingsControl.xaml.cs`
- Test: `QAdvanceFeedback.Tests/PageFieldRefreshPlanTests.cs` (+ explicit csproj Include)

**Interfaces:**
- Consumes: `SettingsHub`, `SettingField`, `SettingsOrigin` (Task 3).
- Produces: `static IReadOnlyList<SettingField> PageFieldRefreshPlan.FieldsToRefresh(SettingsChangedEventArgs e)` → empty when `Origin == PluginPage`, else the shared editable fields in `e.Fields` (Projector/SourceAvailability map to the existing availability/curve refresh calls). `SettingsControl` subscribes to `Hub.Changed` (weak/unsubscribed on `Unloaded`), and for each field writes only that control under `_dirty.BeginLoading()` — source mode via the same combo + `ApplySourceDefaultsForMode` path `OnSourceModeChanged` uses, so the four source boxes follow; max modes/fixed G via their existing setters. It does **not** call `MarkClean`. After `SaveToSettings` + `ApplySettings`, it calls `Hub.NotifyApplied(SettingsHub.Diff(beforeSnapshot, after), SettingsOrigin.PluginPage)` (snapshot = JSON clone taken at the start of Apply).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Own_origin_refreshes_nothing() => Assert.Empty(PageFieldRefreshPlan.FieldsToRefresh(Args(SettingsOrigin.PluginPage, SettingField.LockSourceMode)));
[Fact] public void External_source_change_refreshes_only_source() => Assert.Equal(new[]{SettingField.LockSourceMode}, PageFieldRefreshPlan.FieldsToRefresh(Args(SettingsOrigin.ShakeItPanel, SettingField.LockSourceMode)));
[Fact] public void Unapplied_edit_survives_external_source_change() {
  // Arrange: settings applied with AccelMaxMode=Auto; page working copy has AccelMaxMode=Fixed (unapplied) and LockSourceMode=Viper (unapplied).
  // Act: hub.Set(LockSourceMode, ShakeIt, ShakeItPanel); then simulate page Apply writing its working copy where LockSourceMode now = ShakeIt (refreshed).
  // Assert: settings.Lock.SourceMode == ShakeIt && settings.GForce.AccelMaxMode == Fixed; NotifyApplied fields == { AccelMaxMode }.
}
```

- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run** full suite → PASS. **Verify in SimHub** the spec's example: change source + Accel mode on the page without Apply, change source from a ShakeIt panel → page shows the new source and keeps the Accel edit; Apply → only Accel changes (check the config file).
- [ ] **Step 5: Commit** `feat: plugin settings page syncs per field with ShakeIt panels`.

### Task 9: Docs, shipped profiles, version

**Files:**
- Modify: `README.md`, `README.zh-Hans.md`, `docs/architecture.md`, `docs/setup/Setup Guide 设置指南.txt`, `QAdvanceFeedback/QAdvanceFeedback.csproj` (version 1.2.0.0)
- Delete: `docs/shakeit-export-guide.md` (link from README replaced)
- Create: `docs/setup/QAdvanceFeedback - WheelLockSlip (native).siprofile`, `docs/setup/G-Force/Sensit! Haptic Core - QAdvanceFeedback - GForce (native).siprofile` and updated `Sensit! Haptic Core.sichannels` entries keyed `QAdvanceFeedbackGForceBottomFrontContainer` → channel 0/1 (Left/Right), `…BackLowContainer` → 2/3, `…BackTopContainer` → 4/5, `…BottomRearContainer` → 6/7 — produced by exporting from SimHub after Task 7's binding, not hand-written.

- [ ] **Step 1:** Update docs: new effects, routing, frequencies, ShakeIt source = built-in (no export profile), sync behaviour, which settings live in the ShakeIt profile vs the plugin.
- [ ] **Step 2:** Export the profiles/channels from SimHub, commit them; keep the old files with a "legacy (property-based)" note.
- [ ] **Step 3: Run** full suite + Release build; confirm `bin\Release\net48` still contains only `QAdvanceFeedback.dll` + `.pdb`.
- [ ] **Step 4: Commit** `docs: native ShakeIt effects setup and profiles`.
