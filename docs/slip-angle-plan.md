# Slip Angle (+ test sliders, fallback warning, shared cores, G-Force) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a learned, drift-aware single-channel Slip Angle effect built on the WheelLock/WheelSlip pattern, plus Lock/Slip Test Effect group sliders, a Lock/Slip fallback warning, shared protection/crossing cores, and G-Force "Integrate Slip Angle".

**Architecture:** Approach A of the spec — a dedicated Slip Angle stack (Layer 2 adapter fields → Layer 3 raw/combination → Layer 4 learners/normalizer → Layer 5 projection/drift), reusing the existing generic pieces, with four behaviour-preserving extractions (tier resolver, `CrossingGate`, `CorroboratedLevelEstimator`, new `ImpactGuard`) proven bit-identical against frozen copies before any Slip Angle code uses them.

**Tech Stack:** C# (plugin net48, WPF + MahApps, SimHub SDK in `lib\`), tests net8.0 + xUnit 2.9 link-compiling pure sources.

**Spec:** `docs\slip-angle-design.md` (read it alongside this plan; § numbers below refer to it).

## Global Constraints

- **No git repo here** (`git status` fails). Every "Checkpoint" step = full suite green + plugin builds; if the owner initialises git, commit at each checkpoint with the org attribution lines.
- Test: `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj` (filter with `--filter "FullyQualifiedName~<Name>"`). Build: `dotnet build QAdvanceFeedback\QAdvanceFeedback.csproj -c Release` — **0 warnings** (shipped rule).
- `Core\**` is auto link-compiled into tests; any new pure file under `Settings\` or a root file needs an explicit `<Compile Include=... LinkBase=...>` in `QAdvanceFeedback.Tests.csproj`. `Ui\ViewModels\*.cs` and `Ui\Mvvm\*.cs` are auto.
- Only Layer 2 (`SimHubTelemetryAdapter.cs` and other root SimHub-facing files) may reference SimHub types. `Core\` has no serializer attributes.
- MVVM rule (`docs\architecture.md` "UI: MVVM"): VMs in `Ui\ViewModels`, code-behind wiring only, time via `ITicker`, UI thread via `IUiDispatcher`, no event handler setting another control's property.
- Clean-room: never paste decompiled SimHub code into the repo; scratch decompiles stay in the scratchpad.
- Frozen names: ShakeIt container type `QAdvanceFeedbackSlipAngleContainer`; existing container/enum/property names unchanged; `ShakeApplyMode` value names unchanged.
- New published names are additive; diagnostics/CSV columns are appended at the END only.
- Every UI string in both `StringTableEn.cs` and `StringTableZhHans.cs` (`LocalizationTests` enforces parity).
- Existing tests must pass **unedited** through every extraction task (Tasks 6–9). The only existing test files that may change are the pinned-name lists (`AllPublishedPropertiesTests`, `PublishedPropertyNamesTests`, Task 20) and G-Force default/label tests (Task 25), each change additive.
- Spec values are authoritative: copy numbers from the spec tables, never re-derive.

## Review Focus

1. **Front slip-angle vs rear sign/units mismatch from a game table entry** (rad vs deg, wheel order) — expect Raw.All in plausible 0–30° on a normal lap; Task 10 pins a unit/order assertion per verified game and a "values > 45° for > 1 s ⇒ not native" guard.
2. **Telemetry with LocalVelocity but no yaw rate** (fallback inputs partially null) — expect Raw.All null (no output, no learning), never a fake 0 or NaN; Task 11 test.
3. **Reverse driving / standing still** (Vfwd ≤ 0, tiny speed) — expect no output (speed fade) and no learning; `max(Vfwd, 1 m/s)` must not create huge angles that teach; Task 11 + Task 12 tests.
4. **Settings file from the previous version** (no `SlipAngle` object, no `IntegrateSlipAngle`, RuntimeDocument v11) — expect clean load with Slip Angle defaults, `IntegrateSlipAngle == true`, learned Lock/Slip state intact; Task 19 + Task 15 tests.
5. **Drift held on bumpy gravel with ±6-point noise** — expect the Drifting/Recovering state not to chatter (≤ 1 transition per second of steady-noise drift); Task 17 test.

---

## File Structure

New pure files (Core, auto-linked into tests):
- `Core\Normalized\CorroboratedLevelEstimator.cs` — extracted corroboration core + `CorroborationParameters`.
- `Core\Normalized\CrossingGate.cs` — shared crossing core + `CrossingGateParameters`, `TeachingWeightMode`.
- `Core\Normalized\ColdStartTierResolver.cs` — extracted tier resolution.
- `Core\Normalized\ImpactGuard.cs` — impact window + yaw consistency + `ImpactGuardParameters`.
- `Core\SlipAngle\` (new folder): `SlipAngleInput.cs` (per-frame inputs), `SlipAngleRawCalculator.cs`, `SlipAngleGate.cs`, `GripCurveBins.cs`, `SlipAngleWindowLearner.cs`, `SlipAngleWindow.cs` (Min/Best/Max value type), `SurfaceBucketClassifier.cs`, `KeyedSlipAngleLearner.cs`, `SlipAngleNormalizer.cs`, `SlipAngleNativeSupport.cs`, `SlipAnglePublishedNames.cs`.
- `Core\Projection\DriftAttenuator.cs`, `Core\Projection\DriftSettings.cs`, `Core\Projection\SlipAngleProjectedEngine.cs`.
- `Core\BranchNativeness.cs`.
New Settings files (explicitly linked): `Settings\SlipAngleChannelSettings.cs`, `Settings\SlipAngleKeyDataSettings.cs`, `Settings\KeyDataNormalizationInverse.cs`, `Settings\TestGroupSliders.cs`, `Settings\SlipAngleTestEngine.cs`, `Settings\SlipAngleSourceToProjectedGraph.cs`.
New UI: `Ui\ViewModels\SlipAngleTabViewModel.cs`, `Ui\ViewModels\SlipAngleEffectViewModel.cs`, `Ui\ViewModels\SlipAngleTestViewModel.cs`, `Ui\Views\SlipAngleTabView.xaml(.cs)`, `Ui\Views\SlipAngleEffectView.xaml(.cs)`, `Integration\ShakeIt\QAdvanceFeedbackSlipAngleContainer.cs`.
New Layer 2: `SlipAngleRawSources.cs` (root, SimHub-facing table reader).
Modified: `Core\Normalized\GripLearner.cs`, `SlipCrossingGate.cs`, `LockCrossingGate.cs`, `KeyedScaleLearner.cs`, `Core\ITelemetryFrame.cs`, `Core\TelemetryFrame.cs`, `SimHubTelemetryAdapter.cs`, `Core\Runtime\RuntimeDocument.cs`, `RuntimeStore.cs`, `Settings\QAdvanceFeedbackSettings.cs`, `Settings\GForceSettings.cs`, `Settings\SettingsHub.cs`, `Core\GForce\GForceEngine.cs`, `Core\Projection\ProjectorSettings.cs`, `Core\ShakeItEffects\TestEffectSession.cs`, `Ui\ViewModels\LockSlipTestViewModel.cs`, `Settings\TestBalls.cs`, `Ui\ViewModels\WheelChannelEffectViewModel.cs`, `Ui\ViewModels\GForceEffectViewModel.cs`, `Ui\ViewModels\GForceTestViewModel.cs`, `QAdvanceFeedback.cs`, `PropertyPublisher.cs`, `PropertyPublisher.State.cs`, `Core\AllPublishedProperties.cs`, `Settings\SettingsControl.xaml(.cs)`, both string tables, both architecture docs, both READMEs.

---

## Phase 0 — Spike

### Task 0: Verify per-game raw telemetry (spec §12)

**Files:** Create `docs\slip-angle-spike-report.md`; modify `docs\slip-angle-design.md` §9.1 table.

- [ ] **Step 1:** Extract the full SimHub DLL set for 9.13.2 into the scratchpad using the method in `tools\fetch-simhub-refs.sh` (all `{app}\*.dll`, not only the seven refs). Do NOT write into `lib\`.
- [ ] **Step 2:** With `ilspycmd`, locate each game's raw-data type (ACC, AC, rF2, LMU, F1 23/24/25, EA WRC, Dirt Rally 2, iRacing, AMS2) and record, per game: slip-angle field path as seen under `DataCorePlugin.GameRawData.*`, element type, wheel order, unit (rad/deg), and any per-wheel surface-type field. Also record units of `FeedbackData.YawChangeVelocity`, `LocalVelocity`, `VerticalAcceleration`, and the semantics of `IsFlying` / `FlightIsOnGround`.
- [ ] **Step 3:** Write the report (table per game: path, order, unit, transform, surface field, verdict native/none). Update spec §9.1's table to exactly the verified rows; drop any game not verified.
- [ ] **Step 4:** Delete the scratch decompiles. Checkpoint: no repo files besides the two docs changed.

---

## Phase 1 — Lock/Slip Test Effect group sliders (spec §6)

### Task 1: Inverse normalization

**Files:** Create `Settings\KeyDataNormalizationInverse.cs` (+ csproj link); Test `QAdvanceFeedback.Tests\KeyDataNormalizationInverseTests.cs`.

**Interfaces:** Produces `static double KeyDataNormalizationInverse.SourceFor(double normalized0100, bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75)` — smallest source in [0,100] with `KeyDataNormalization.Normalize(...) >= normalized0100`; returns 100 when target ≥ `Normalize(100)`; 0 for target ≤ 0 or NaN.

- [ ] **Step 1: Tests** — `Round_trip_is_exact_within_1e-6_for_1000_random_sources` (Lock Mapping, Lock MaxGripOnly, Slip; key data 75/60/45 and null); `Saturated_target_returns_smallest_source_reaching_it`; `Non_finite_target_returns_zero`.
- [ ] **Step 2:** Run → FAIL (type missing).
- [ ] **Step 3:** Implement by bisection (60 iterations) over the monotone `Normalize`.
- [ ] **Step 4:** Run → PASS.

### Task 2: Group slider maths

**Files:** Create `Settings\TestGroupSliders.cs` (+ link); Test `QAdvanceFeedback.Tests\ShakeItEffects\TestGroupSlidersTests.cs`.

**Interfaces:**
- Consumes Task 1; `Aggregator.Compute(Corners, AggregationWeights)`; `KeyDataNormalization.Normalize`.
- Produces `enum TestGroup { All, Front, Rear, Left, Right }`; `static IReadOnlyList<TestGroup> TestGroupSliders.GroupsFor(EffectRouting)` (Mono→[All], FrontRear→[Front,Rear], LeftRight→[Left,Right], Corners→[]); `static double TestGroupSliders.SourceFor(TestGroup, Corners sources0100, AggregationWeights, bool isLock, bool maxGripOnly, KeyDataView keyData)` = `Inverse(Aggregator.Compute(N(each wheel)).<group>)`; `static Corners TestGroupSliders.Write(TestGroup, double value, Corners current)` (All→all four; Front→FL,FR; Rear→RL,RR; Left→FL,RL; Right→FR,RR).

- [ ] **Step 1: Tests** — `Groups_follow_routing` (four cases); `Write_sets_only_member_wheels`; **`Dot_is_always_on_the_curve`**: for 500 random wheel spreads × random weights (0–1.5 each) × {Lock Mapping, Lock MaxGripOnly, Slip} × key data {null, 80/68/50}: for every group g, `Projected(N⁻¹-source of g)` evaluated with four equal wheels at that source through `LockSlipTestEngine.Step` with weights summing to 1 equals the engine's group value within 1e-6 — and for arbitrary weights `KeyDataNormalization.Normalize(SourceFor(g)) == min(aggNormalized_g, Normalize(100))` within 1e-6.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.

### Task 3: View model + view + dot x

**Files:** Modify `Ui\ViewModels\LockSlipTestViewModel.cs`, `Settings\TestBalls.cs`, the Lock/Slip panel XAML (`Ui\Views\WheelChannelEffectView.xaml` test section / `TestControls.cs`), string tables; Test `QAdvanceFeedback.Tests\Ui\LockSlipTestViewModelTests.cs` (new cases only).

**Interfaces:** Produces on `LockSlipTestViewModel`: `double SingleChannel`, `FrontChannel`, `RearChannel`, `LeftChannel`, `RightChannel` (two-way); `bool ShowSingle`, `ShowFrontRear`, `ShowLeftRight` (bound visibility, from `GroupsFor(routing)`). `TestBalls.For(...)` x for a group slot = `TestGroupSliders.SourceFor` (y unchanged).

- [ ] **Step 1: Tests** — `Dragging_single_sets_all_four_wheels`; `Dragging_front_sets_FL_FR_only`; `Dragging_a_wheel_recomputes_group_values_once` (count PropertyChanged for `SingleChannel` == 1 per wheel set); `Routing_change_switches_visible_slider_set`; `Ball_x_equals_group_slider_value` (Mono, FrontRear); `Turn_off_resets_group_sliders_to_zero`; MVVM: `MvvmRuleTests` stays green.
- [ ] **Step 2:** FAIL. **Step 3:** Implement: group setter writes wheels via backing fields, then raises wheel + group notifications once (re-entrancy guard flag). Strings: `Test.Slider.Single|Front|Rear|Left|Right` EN/zh. Help line for weights-not-summing-to-1 caveat (spec §6). **Step 4:** PASS. **Step 5: Checkpoint.**

---

## Phase 2 — Lock/Slip fallback warning (spec §7)

### Task 4: Branch nativeness

**Files:** Create `Core\BranchNativeness.cs`; Test `QAdvanceFeedback.Tests\BranchNativenessTests.cs`.

**Interfaces:** Produces `enum SourceNativeness { Unknown, Native, Fallback }`; `static SourceNativeness BranchNativeness.ForLock(string selectedBranch)` / `ForSlip(string)`.

- [ ] **Step 1: Tests** — one `[Theory]` per channel covering every `WheelSlipBranchNames` constant exactly per spec §7 table (Lock native: `WheelSpeedLock`, `Rps`, `RpsAndDummyRadius`; Slip native: `PrecalibratedSlip`, `SlipData`, `WheelsSpeed`; fallback both: `BrakingVsSpeed`, `RpmVsSpeedLegacy`, `RpmVsSpeedAssumedLegacy`, both `NoCapabilitiesSupplied*`; `DirectSlipFeedback`, `NoBranchMatched`, null → Unknown); `Every_branch_constant_is_classified` (reflection over the class's consts — fails if a new branch is added unclassified).
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 5: Warning surfaces

**Files:** Modify `QAdvanceFeedback.cs` (publish current nativeness), `Settings\SettingsHub.cs` (`SourceAvailability` payload gains `LockNativeness`, `SlipNativeness`, later `SlipAngleNativeness`), `Ui\ViewModels\WheelChannelEffectViewModel.cs`, Wheel Lock/Slip tab VM-bound label in `Settings\SettingsControl.xaml(.cs)`, string tables; Test `QAdvanceFeedback.Tests\Ui\PanelViewModelTests.cs` (new cases), `SettingsHubTests.cs` (new cases).

**Interfaces:** Produces `bool ShowFallbackWarning` + `string FallbackWarning` on the panel VM and the tab's binding source; string key `Source.FallbackWarning.LockSlip` = "Not natively supported by this game — using the fallback estimate (speed/RPM)." (zh supplied).

- [ ] **Step 1: Tests** — `Warning_shown_only_for_plugin_internal_and_fallback` (SourceMode Manual + Fallback → true; ShakeIt/Viper/Custom + Fallback → false; Manual + Native/Unknown → false); `Hub_broadcasts_nativeness_change_once`.
- [ ] **Step 2–4:** FAIL → implement (orange `Foreground` via existing warning style; bound `Visibility`) → PASS. **Step 5: Checkpoint.**

---

## Phase 3 — Shared cores (spec §4, §9.5a, §9.5b)

### Task 6: `CorroboratedLevelEstimator`

**Files:** Create `Core\Normalized\CorroboratedLevelEstimator.cs`; modify `GripLearner.cs`; Test `QAdvanceFeedback.Tests\CorroboratedLevelEstimatorEquivalenceTests.cs` + `QAdvanceFeedback.Tests\Frozen\FrozenGripLearnerCorroboration.cs` (verbatim copy of today's `AdaptivePeakState`, `RaiseConfidenceForHits`, `LowerConfidenceForHits`, `ApplyEvidenceWeightedObservation` and its constants, renamed `Frozen*`, `internal`).

**Interfaces:** Produces
```csharp
public sealed class CorroborationParameters {
  public double PlausibleCap; public double MaxPlausibleJump; public double RaiseTolerance;
  public double LowerTolerance; public double ToleranceFloor; public int RaiseCooldown;
  public int LowerCooldown; public double ConfidenceBase;
  public static CorroborationParameters GripLearnerDefaults { get; } // 8.0, 3.0, 0.30, 0.15, 0.05, 3, 40, 0.10
}
public struct CorroboratedLevelState { /* Level, RaiseCandidate, RaiseHits, LowerCandidate, LowerHits, Cooldown, LastRaw, HasLastRaw, QuietStreak */ public static CorroboratedLevelState Seeded(double level); }
public static class CorroboratedLevelEstimator {
  public static void Observe(ref CorroboratedLevelState s, double value, CorroborationParameters p);
  public static double RaiseConfidenceForHits(int hits, CorroborationParameters p);
  public static double LowerConfidenceForHits(int hits, CorroborationParameters p);
}
```
`GripLearner` keeps its public constants (`MaxPlausibleG` etc.) and persisted field names; internally holds `CorroboratedLevelState` and calls `Observe(..., CorroborationParameters.GripLearnerDefaults)`.

- [ ] **Step 1: Test** `Bit_identical_to_frozen_copy_over_randomised_streams` — 200 seeds × 5,000 values mixing: plateaus with ±10% noise, step ups/downs, single spikes > 3 g, NaN/∞/negatives (filtered by caller exactly as `GripLearner.Observe` does), values at saturation caps (pre-set hits = 999,999); after each call assert every state field `==` (exact double equality).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; replace the private struct/method in `GripLearner` with the shared one. **Step 4:** New test PASS **and** full suite PASS unedited (`GripLearnerTests`, `AdaptivePeakLearnerTests`, `RegressionFixTests`, `Part5OverflowAndNonStoppingLearningTests` in particular). **Step 5: Checkpoint.**

### Task 7: `CrossingGate`

**Files:** Create `Core\Normalized\CrossingGate.cs`; reduce `SlipCrossingGate.cs` and `LockCrossingGate.cs` to wrappers; Test `QAdvanceFeedback.Tests\CrossingGateEquivalenceTests.cs` + `QAdvanceFeedback.Tests\Frozen\FrozenSlipCrossingGate.cs`, `FrozenLockCrossingGate.cs` (verbatim copies, renamed).

**Interfaces:** Produces
```csharp
public enum TeachingWeightMode { Fixed, MultiplyConfidence }
public sealed class CrossingGateParameters {
  public int TrendFrames; public double SourceCeiling; public double MinFrameRise; public double Rise;
  public double GFall; public double HoldSeconds; public double NominalFrameSeconds; public int HoldMaxFrames;
  public double MinGForCollapseTest; public TeachingWeightMode WeightMode;
  public static CrossingGateParameters SlipShipped { get; }  // 5, 100, 1e-6, 1.0, -0.05, 1.0, 1/60, 500, 0.05, Fixed
  public static CrossingGateParameters LockShipped { get; }  // same values, MultiplyConfidence
  public static CrossingGateParameters SlipAngle { get; }    // 5, 45.0, 1e-6, 0.5, -0.05, 1.0, 1/60, 500, 0.05, Fixed
}
public sealed class CrossingGate {
  public CrossingGate(CrossingGateParameters p);
  // mutable tunables exactly as today: MaxGCollapseFractionPerSecond, MinSaturatedWheelsForRejection, WalkBackGBandFraction, NonCrossingWeight
  public bool Observe(double basis, double fallbackBasis, double g, bool atLimit, double dtSeconds, /* remaining params exactly as today's Observe */);
  public bool Qualified { get; } public bool CrossedThisFrame { get; }
  public double TeachingWeight(double confidence = 1.0);   // Fixed ignores the argument
  public double TeachingBasis(double live); public double TeachingFallbackBasis(double liveFallback);
  public void Reset(); public void RecordCandidate(string key, bool qualified); public long ValidFrames(string key); public long TotalFrames(string key);
}
```
`SlipCrossingGate` / `LockCrossingGate` keep every public member and constant they have today and delegate to an inner `CrossingGate` built from their shipped parameters.

- [ ] **Step 1: Test** `Slip_and_Lock_wrappers_are_bit_identical_to_frozen_gates` — 200 seeds × 3,000 frames per gate; streams include crossings, held source, runaway after crossing, saturated wheels (0–4), dt ∈ {1/60, 0, NaN, 1e-9, 0.5}, NaN/∞ basis/G, back-to-back crossings, every tunable at default and at a random non-default; after each frame assert equal `Observe` return, `Qualified`, `CrossedThisFrame`, `TeachingWeight(c)` for c ∈ {0, 0.37, 1}, `TeachingBasis`, `TeachingFallbackBasis`, counts.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS + full suite unedited (all `CrossingGate*Tests`, `SlipCrossing*`, `LockCrossingGateTests`, `SlipSMaxCrossingTeachingTests`). **Step 5: Checkpoint.**

### Task 8: `ColdStartTierResolver`

**Files:** Create `Core\Normalized\ColdStartTierResolver.cs`; modify `KeyedScaleLearner.cs` (`ResolveTier`/`ResolveReference` delegate); Test `QAdvanceFeedback.Tests\ColdStartTierResolverTests.cs`.

**Interfaces:** Produces `sealed class ColdStartTierResolver` with `ColdStartTier Resolve(string gameId, string carId, string sourceIdentity, string surfaceBucket, Func<string /*key*/, bool /*hasEvidence*/> hasEvidence, IEnumerable<string> knownKeys, out string borrowedKey)` and key helpers `static string Key(string game, string car, string source, string surface)` + parse; most-specific-tier-first ordering as today (Tier4 > Tier3 > Tier2 > Tier1). Cache semantics (`lockToCache`) stay in `KeyedScaleLearner`.

- [ ] **Step 1: Tests** — `Picks_most_specific_tier` (4 cases); `Same_source_only`; plus **full suite unedited** (`TieredColdStartReferenceTests`, `ColdStartAndCrossCarSeedTests`, `RealLogTieredColdStartReportTests`).
- [ ] **Step 2–4:** FAIL → extract → PASS. **Step 5: Checkpoint.**

### Task 9: `ImpactGuard`

**Files:** Create `Core\Normalized\ImpactGuard.cs`; Test `QAdvanceFeedback.Tests\ImpactGuardTests.cs`.

**Interfaces:** Produces
```csharp
public sealed class ImpactGuardParameters {
  public bool Enabled; public double PlausibleCapG; public double MaxJumpG; public int RetroFrames;
  public double ExclusionSeconds; public double YawResidualG; public double NominalFrameSeconds;
  public static ImpactGuardParameters SlipAngle { get; } // true, 8.0, 3.0, 5, 1.5, 0.3, 1/60
  public static ImpactGuardParameters Disabled { get; }  // Enabled = false (Lock/Slip)
}
public readonly struct ImpactVerdict { public bool LearnAllowed { get; } public bool ImpactThisFrame { get; } public bool RetroInvalidate { get; } public double ExclusionRemaining { get; } public double YawResidualG { get; } }
public sealed class ImpactGuard {
  public ImpactGuard(ImpactGuardParameters p);
  public ImpactVerdict Observe(double latG, double longG, double vertG, double speedMps, double? yawRateRadPerSec, double? dVlatDt, double dtSeconds);
  public void Reset();
}
```
Yaw residual = `|latG − (speedMps·yaw + dVlatDt)/9.80665|`; skipped (not failing) when yaw or dVlatDt is null. `RetroInvalidate` true on the impact frame so callers clear their last `RetroFrames` frames (the crossing gate: `Reset()`; learners: drop buffered frames).

- [ ] **Step 1: Tests** — `Disabled_always_allows`; `Jump_over_3g_on_any_axis_opens_1_5s_window` (lat, long, vert separately; allowed again at 1.5 s + one frame); `Over_8g_rejected_without_window`; `Non_finite_rejected`; `Zero_or_NaN_dt_ages_by_nominal_frame`; `Yaw_residual_over_0_3g_blocks_learning`; `Missing_yaw_skips_residual_check`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5: Checkpoint (Lock/Slip bit-identical: whole suite green unedited).**

---

## Phase 4 — Slip Angle channel (spec §8–§9)

### Task 10: Telemetry inputs + native table + native detection

**Files:** Modify `Core\ITelemetryFrame.cs`, `Core\TelemetryFrame.cs` (new nullable members, appended ctor optional params), `SimHubTelemetryAdapter.cs`; create `Core\SlipAngle\SlipAngleInput.cs`, `Core\SlipAngle\SlipAngleNativeSupport.cs`, `SlipAngleRawSources.cs` (root); Tests `QAdvanceFeedback.Tests\SlipAngle\SlipAngleNativeSupportTests.cs`, `SlipAngleRawSourcesTableTests.cs` (pure table part).

**Interfaces:**
- `ITelemetryFrame` adds: `double? ForwardLocalVelocity`, `double? YawRateRadPerSec`, `double? VerticalG`, `bool IsAirborne`, `bool[] WheelsOnKerbs` (length 4 or null), `double? SurfaceGripHint` reserved null. (Units per Task 0 report; convert at the adapter.)
- `SlipAngleInput { Corners? NativeWheelDeg; bool[] KerbWheels; ... }` built per frame by Layer 2.
- `static class SlipAngleRawSourceTable` (pure, Core-free strings): `bool TryGet(string gameId, out SlipAngleRawSourceEntry e)` with `PropertyPaths[4]`, `Unit` (Rad|Deg), `Transform` (Direct|Atan2PatchVel), `WheelOrder`, optional `SurfacePaths[4]` — rows exactly from Task 0.
- `SlipAngleNativeSupport.Observe(string gameId, Corners? wheels, double speedKmh, double dt)` / `bool IsNative(string gameId)`: promote after 5 s of moving frames (> 30 km/h) with finite, non-constant values all ≤ 45°; never demote within a session; reject promotion if any wheel > 45° for > 1 s.

- [ ] **Step 1: Tests** — promotion after 5 s; constant-zero never promotes; NaN frames do not count; > 45° sustained blocks; per-game isolation; table rows round-trip units (rad→deg) and wheel order to FL/FR/RL/RR.
- [ ] **Step 2–4:** FAIL → implement (adapter reads paths via `PluginManager.GetPropertyValue`, guarded like `WheelSourceResolver`, caching a per-game "path missing" verdict) → PASS.

### Task 11: Raw calculator (spec §9.1, §9.3)

**Files:** Create `Core\SlipAngle\SlipAngleRawCalculator.cs`; Test `QAdvanceFeedback.Tests\SlipAngle\SlipAngleRawCalculatorTests.cs`.

**Interfaces:** Produces `SlipAngleRawResult Compute(SlipAngleInput input, ITelemetryFrame frame, bool isNative, double k, double wMax, double wMin)` with `double? FrontDeg, RearDeg, AllDeg; SourceNativeness Tier; Corners? WheelsDeg`. Constants: `RearAxleDistanceMetres = 1.3`, `MinForwardSpeedMps = 1.0`, `MinK = 0.5`, `MaxK = 2.0`.

- [ ] **Step 1: Tests** — `Axle_is_mean_of_two_wheels`; `Kerb_wheel_dropped_other_used`; `Both_wheels_on_kerb_axle_null`; `Equalisation_scales_front_by_k` (front 4°, k 1.5 → α_F′ 6); `All_is_max_min_blend` (α_F′ 6, α_R 8 → 0.85·8 + 0.15·6 = 7.7); `k_clamped_0_5_to_2_0`; `Fallback_uses_rear_estimate_atan2` (Vlat 2, b·r 0.65 → `atan2(1.35, Vfwd)`); `Fallback_with_null_yaw_is_null` (Review Focus 2); `Reverse_or_standstill_uses_1mps_floor_and_never_NaN` (Review Focus 3); `Native_null_wheels_falls_back`.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 12: Gating (spec §9.2)

**Files:** Create `Core\SlipAngle\SlipAngleGate.cs`; Test `...\SlipAngleGateTests.cs`.

**Interfaces:** `enum TriggerBy { Brake, Throttle, Any }`; `sealed class SlipAngleGateSettings { TriggerBy TriggerBy = Any; double BrakeThreshold = 5; double ThrottleThreshold = 10; double MaxFlightSeconds = 0.5; double MaxWobbleSeconds = 1.0; }`; `SlipAngleGateVerdict Step(ITelemetryFrame f, double dt, SlipAngleGateSettings s)` → `bool PedalOpen; double SpeedFade (0..1 over 10→20 km/h); bool Airborne; double LandingFade (S-curve 0→1 across wobble); bool InWobble; bool HopExcluded; bool LearnSpeedOk (≥ 30 km/h)`.

- [ ] **Step 1: Tests** — `Any_is_or_of_brake_and_throttle`; `Brake_only_ignores_throttle`; `Wobble_scales_with_flight` (0.2 s air → 0.4 s wobble; 0.8 s air → 1.0 s); `Landing_fade_is_s_curve_0_to_1`; `Hop_over_0_5g_excludes_learning_200ms`; `Speed_fade_10_to_20`; `Airborne_resets`.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 13: Grip curve bins (spec §9.5.2–4)

**Files:** Create `Core\SlipAngle\GripCurveBins.cs`, `Core\SlipAngle\SlipAngleWindow.cs`; Test `...\GripCurveBinsTests.cs`.

**Interfaces:** `readonly struct SlipAngleWindow { double Min, Best, Max; double End => 1.5*Max; bool IsValid; static SlipAngleWindow ShippedDefault => (5, 8, 10); }`; `sealed class GripCurveBins` (0.25° bins, 0–30°, per-bin decaying P90 of |latG| advanced only on that bin's own observations, ≥ 20 samples to count): `void Observe(double alphaDeg, double absLatG)`, `double PeakG`, `bool TryBestAndMin(double maxDeg, out double best, out double min)` (plateau ≥ 97% below `maxDeg`, Best = midpoint; Min = first rising bin ≥ 90% of peak; else `Min = Best·5/8`; enforce gaps ≥ 0.2°), `bool IsStillRisingAtTop`, `double ExpectedLatG(double alphaDeg)`.

- [ ] **Step 1: Tests** — synthetic Pacejka-like curve peaking at 8° → Best ≈ 8 ± 0.5, Min ≈ 5 ± 0.5 with max 10; `Bins_ignore_alpha_above_max`; `Unvisited_bins_do_not_decay` (feed 10,000 frames at 2° only after a full curve → Best unchanged); `Drift_volume_does_not_outvote_limit` (10× more frames at 15° with low G → Best unchanged); `Rising_at_top_detected`.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 14: Window learner — Max via crossings, confirmation, maturity, protection (spec §9.5.4–6, §9.5a, §9.5b)

**Files:** Create `Core\SlipAngle\SlipAngleWindowLearner.cs`; Test `...\SlipAngleWindowLearnerTests.cs`.

**Interfaces:** Consumes Tasks 6, 7 (`CrossingGateParameters.SlipAngle`), 9 (`ImpactGuardParameters.SlipAngle`), 13. Produces
```csharp
public sealed class SlipAngleWindowLearner {
  public SlipAngleWindowLearner(SlipAngleWindow prior);
  public void Observe(double alphaDeg, double absLatG, ImpactVerdict impact, bool learnAllowed, double dt);
  public SlipAngleWindow Published { get; }      // prior (raise-only) until confirmed, then learned
  public bool PeakConfirmed { get; }             // ≥ 3 corroborated crossings within ±15%
  public double Maturity { get; }                // clamp01(min(obs/50, corners/3))
  public double WindowQuality { get; }           // ≤ 0.6 while unconfirmed
  public int CrossingCount { get; } public long Observations { get; } public int Corners { get; }
  public SlipAngleLearnerState Export(); public void Import(SlipAngleLearnerState s);  // for persistence
}
```
Rules: candidate = |latG| ≥ 0.85·PeakG; crossing via `CrossingGate`; Max = `CorroboratedLevelEstimator` over crossing snapshots (degrees params: cap 45, jump 15, raise 0.30, lower 0.15, floor 0.2, cooldown 3/40, base 0.10); curve-plausibility: arm only if latG ≤ 1.15·ExpectedLatG(α) and ≤ 1.15·PeakG (skip when bins empty); impact retro-invalidate → gate `Reset()`; observation = latG ≥ 0.70·PeakG at most 10/s; corner = rise > 70% then fall < 40%; unconfirmed raise-only prior scaling (Best ≥ highest still-rising α).

- [ ] **Step 1: Tests** —
  - **`Owner_crash_scenario_is_not_a_crossing`**: true curve peaks at 8°; at 4° inject lateral G jump +3.5 g in one frame, then α rises to 9° while G ramps down over 1 s → `CrossingCount == 0`, `Published.Max` unchanged.
  - `Soft_contact_blocked_by_yaw_residual` (residual 0.5 g, no jump).
  - `Cautious_driver_never_lowers_prior`: data only to 5° with rising G → `Published == ShippedDefault`, `PeakConfirmed == false`.
  - `Still_rising_beyond_prior_best_raises_window` (rising at 9.5° → Best ≥ 9.5, scaled proportionally).
  - `Three_consistent_crossings_confirm` / `Inconsistent_crossings_do_not_confirm` (8°, 12°, 5°).
  - `Single_fake_crossing_cannot_lower_max`.
  - `Onset_snapshot_taught_not_runaway` (α runs 8→20° inside hold → Max ≈ 8).
  - `Matures_in_three_corners` (3 corners × 2 s at 0.9·peak → Maturity == 1; 1 corner → Maturity ≤ 0.34).
  - `Learning_continues_after_maturity` (shifted curve later still moves Best).
  - `Landing_wobble_frames_never_teach` (learnAllowed false → no state change).
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 15: Surface classifier + keyed learner + persistence (spec §9.5.1, §9.5.7–9)

**Files:** Create `Core\SlipAngle\SurfaceBucketClassifier.cs`, `Core\SlipAngle\KeyedSlipAngleLearner.cs`; modify `Core\Runtime\RuntimeDocument.cs` (Version 11 → 12, new `SlipAngle` section), `RuntimeStore.cs`; Tests `...\SurfaceBucketClassifierTests.cs`, `...\KeyedSlipAngleLearnerTests.cs`, `QAdvanceFeedback.Tests\RuntimeStoreTests.cs` (new cases).

**Interfaces:**
- `enum SurfaceBucket { Sealed, Loose, LowGrip }`; `SurfaceBucketClassifier.Step(double alphaDeg, double absLatG, SurfaceBucket? gameReported, Func<SurfaceBucket, double /*expectedLatG at α*/ > expected, double sealedPeakG, double dt)` → `(SurfaceBucket active, double wSealed, double wLoose, double wLowGrip)`; ~3 s rolling window of frames with α ≥ 2°; prior split ≥ 0.85 / 0.60 of sealed peak; blend over 1.5 s; hysteresis (switch only after the new bucket wins for 1.0 s).
- `KeyedSlipAngleLearner`: key `game|car|src|target|surface` (`target ∈ front, rear, all`); `Observe(...)` per frame for all three targets of the active bucket (weighted by blend for publishing only — learning goes to the dominant bucket); `SlipAngleWindow Window(game, car, src, blend)`; `double K(game, car, src, blend)` = Best_rear / Best_front (1.0 until both confirmed, slew ≤ 0.2/s); `ColdStartTier Tier(...)` via Task 8 resolver, Tier 1 = `ShippedDefault`; `ColdWarmBlend` live vs persisted; `Export()/Import()` for `RuntimeDocument`.

- [ ] **Step 1: Tests** — `Gravel_frames_switch_to_loose_within_2s`; `Short_dirt_cut_does_not_flip` (0.5 s); `Game_reported_surface_overrides_inference`; `Gentle_sealed_driving_stays_sealed` (low G at low α consistent with sealed curve); `Unseen_bucket_borrows_tier4`; `K_is_one_until_both_axles_confirmed`; `K_slew_limited`; `Persist_round_trip_bit_exact`; **`Loads_v11_document_without_slip_angle_section`** (Review Focus 4).
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 16: Normalizer + key data (spec §8, §9.4)

**Files:** Create `Core\SlipAngle\SlipAngleNormalizer.cs`, `Settings\SlipAngleKeyDataSettings.cs` (+ link); Tests `...\SlipAngleNormalizerTests.cs`, `QAdvanceFeedback.Tests\SlipAngleKeyDataSettingsTests.cs`.

**Interfaces:**
- `static double SlipAngleNormalizer.Normalize(double alphaDeg, SlipAngleWindow w)` — monotone cubic through (0,0),(Min,30),(Best,60),(Max,80),(End,100), clamped 100; `static double NormalizeUnclamped(double alphaDeg, SlipAngleWindow w)` — linear beyond End at 20 points per 0.5·Max; `static double SourceFor(double normalized, SlipAngleWindow w)` (inverse, for Test Effect/graph).
- `SlipAngleKeyDataSettings` mirrors `KeyDataPointSettings` slot model: `bool AutoGenerate = true; bool PerGame; TryGetSlot(game, source, out SlipAngleWindow)`; `bool TryCommitManual(...)`; validation `0 < Min < Best < Max`, gaps ≥ 0.2, range 0.5–45, step 0.1; `ClearSlot`; seeded latch.
- Resolution: Auto → learned window; Manual → slot applied only when `ManualOverrideGate.IsReady(key, maturity)` (reuse as-is; 30 s + 0.95), else learned.

- [ ] **Step 1: Tests** — `Anchors_map_to_30_60_80_100` (5/8/10 → N(5)=30, N(8)=60, N(10)=80, N(15)=100); `Unclamped_extends_linearly` (N_u(20) = 100 + 20·(5/5) = 120); `Inverse_round_trip`; `Manual_withheld_until_gate_ready`; `Invalid_triples_rejected` (5/5/10, 0/8/10, 9/8/10); `Per_game_without_game_refused`.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 17: Drift attenuator (spec §9.7)

**Files:** Create `Core\Projection\DriftAttenuator.cs`, `Core\Projection\DriftSettings.cs`; Test `QAdvanceFeedback.Tests\DriftAttenuatorTests.cs`.

**Interfaces:** `sealed class DriftSettings` with spec §9.7 table defaults and ranges: `HoldSeconds 0.25 [0.05,2]; StabilityRange 20 [2,60]; ToPercent 40 [0,100]; DurationSeconds 0.12 [0,2]; Gamma 2.0 [0.5,6]; SmoothingSeconds 0.05 [0,0.5]; SwingMargin 8 [1,40]; TrendConfirmSeconds 0.08 [0,0.5]` (plus `MaxFlightSeconds`, `MaxWobbleSeconds` live in `SlipAngleGateSettings`). `enum DriftState { Normal, Drifting, Recovering }`. `DriftAttenuator.Step(double nUnclamped, double calc, double dt, DriftSettings s)` → `double` output; `DriftState State`; `double Factor`; `void Reset()`.

- [ ] **Step 1: Tests** — `Normal_passthrough_below_80`; `Hold_then_s_ramp_to_40` (N_u 90 held 0.25 s → factor follows `t^2/(t^2+(1−t)^2)` over 0.12 s, ends 0.40); **`Owner_example_80_to_32`**; **`Owner_example_release_at_factor_0_8_gives_64`**; `Escalation_over_stability_range_stays_full`; `Slow_stabilising_slide_detected_later` (sliding window); `Decrease_freezes_factor_and_min_rule`; `Increase_in_recovering_returns_calc_directly`; `Recovering_ends_below_30`; **`Gravel_noise_does_not_chatter`** (N_u 95 ± 6 uniform noise at 60 Hz for 5 s → ≤ 5 state transitions; Review Focus 5); `Zero_dt_safe`; `Reset_returns_normal`.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 18: Projected engine (spec §9.6)

**Files:** Modify `Core\Projection\ProjectorSettings.cs` (`ProjectionChannel.SlipAngle` + shipped preset); create `Core\Projection\SlipAngleProjectedEngine.cs`; Test `QAdvanceFeedback.Tests\SlipAngleProjectedEngineTests.cs`.

**Interfaces:** Preset: anchors input 30/62/78, flatten 3/2/2, outputs 10/30/60, Start 0→0, End 100→100, pulse off. `SlipAngleProjectedResult Compute(double normalized, double nUnclamped, double maturity, ColdStartTier tier, SlipAngleGateVerdict gate, double dt)` → `double FinalAll` (published), `double PulseFreeAll`, `double PreDriftAll`, `DriftState`, `double DriftFactor`. Order: project → `ColdStartScale.ApplyAmplitudeScale(p, ColdStartScale.Compute(maturity, ColdStartTierFloors.FloorFor(tier)))` → drift → × `LandingFade` × `SpeedFade` → pedal gate (closed ⇒ 0; drift keeps tracking) → pulse.

- [ ] **Step 1: Tests** — `Preset_outputs_10_30_60_at_min_best_max`; `Cold_tier1_caps_full_reading_at_50`; `Warm_reaches_100`; `Pedal_closed_outputs_zero_but_drift_tracks`; `Landing_fade_multiplies`; existing `OutputProjectorTests` unchanged for Lock/Slip.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5: Checkpoint.**

### Task 19: Settings model

**Files:** Create `Settings\SlipAngleChannelSettings.cs` (+ link); modify `Settings\QAdvanceFeedbackSettings.cs`, `ConfigStore.cs`; Test `QAdvanceFeedback.Tests\SlipAngleChannelSettingsTests.cs`, `QAdvanceFeedbackSettingsTests.cs` (new cases).

**Interfaces:** `SlipAngleChannelSettings { SourceMode SourceMode = Manual /* Plugin Internal */ | Custom; WheelSourceSet CustomSources; SlipAngleGateSettings Gate; double WeightMax = 0.85; double WeightMin = 0.15; SlipAngleKeyDataSettings KeyData; ProjectorSettings Projector = CreateShippedDefault(SlipAngle); PulseSettings Pulse; DriftSettings Drift; bool CrossingGateEnabled = true; }`. `QAdvanceFeedbackSettings.SlipAngle` (never null after load); `RestoreDefaults` carries `SlipAngle.KeyData` across.

- [ ] **Step 1: Tests** — `Old_config_without_slip_angle_loads_defaults` (Review Focus 4); `Restore_defaults_keeps_key_data`; `Round_trip_json`; `Drift_values_clamped_to_ranges`.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 20: Composition root + published properties

**Files:** Modify `QAdvanceFeedback.cs` (per-frame Slip Angle pipeline after Normalized/Projected Lock/Slip, before G-Force), `PropertyPublisher.cs`, `PropertyPublisher.State.cs`; create `Core\SlipAngle\SlipAnglePublishedNames.cs`; modify `Core\AllPublishedProperties.cs`; Tests `AllPublishedPropertiesTests.cs` (additive), `PropertyPublisherStructureTests.cs`, `CsvExportWriterTests.cs` (column count).

**Interfaces:** Product names: `QAdvanceFeedback.SlipAngle.Raw.All`, `QAdvanceFeedback.SlipAngle.Normalized.All`, `QAdvanceFeedback.SlipAngle.Projected.All`. Diagnostics (appended): per-wheel α ×4, front/rear α, k, front/rear learned Best, source tier, drift state, drift factor, window Min/Best/Max, maturity, window quality, borrowing tier, wobble remaining, pedal gate open, impact flag, exclusion remaining, yaw residual, surface bucket, surface weights ×3, surface source. `PropertyPublisher.UpdateSlipAngle(SlipAngleFrameResult)`; Custom source resolution reuses `WheelSourceResolver` per wheel (degrees, no 0–100 clamp — clamp to 0–45).

- [ ] **Step 1: Tests** — pinned product list gains exactly three names at the end; diagnostic list gains the listed names at the end; CSV header/row counts match.
- [ ] **Step 2–4:** FAIL → implement (wrap the pipeline in the existing per-subsystem try/catch → `HealthRegistry` with new `HealthSubsystems.SlipAngle` + localized impact text) → PASS. Build 0 warnings. **Step 5: Checkpoint.**

### Task 21: SettingsHub fields

**Files:** Modify `Settings\SettingsHub.cs`, `QAdvanceFeedback.cs` (`ApplySettings` diff); Test `SettingsHubTests.cs` (new cases).

**Interfaces:** New `SettingField` values appended: `SlipAngleTriggerBy, SlipAngleBrakeThreshold, SlipAngleThrottleThreshold, SlipAngleKeyDataAuto, SlipAngleKeyDataValues, SlipAngleProjector, IntegrateSlipAngle`; `SourceAvailability` carries `SlipAngleNativeness`.

- [ ] **Step 1: Tests** — each field round-trips panel→hub→settings→broadcast with origin; page Apply broadcasts only changed fields; existing enum values keep their ordinals.
- [ ] **Step 2–4:** FAIL → implement → PASS.

### Task 22: Settings page "Slip Angle" tab (spec §9.9)

**Files:** Create `Ui\ViewModels\SlipAngleTabViewModel.cs`, `Ui\Views\SlipAngleTabView.xaml(.cs)`, `Settings\SlipAngleSourceToProjectedGraph.cs` (+ link); modify `Settings\SettingsControl.xaml` (new TabItem hosting the view), string tables; Tests `QAdvanceFeedback.Tests\Ui\SlipAngleTabViewModelTests.cs`, `SettingsControlDirtyTrackingTests.cs` (stays green).

**Interfaces:** VM exposes: fallback warning (bound), source mode + custom fields, `TriggerBy` + `ShowBrakeThreshold`/`ShowThrottleThreshold`, weights, `Auto`, `PerGame`, `Min/Best/Max` + `ValuesEditable` (= !Auto), learned hints, status text (maturity, "Peak not reached yet — using the borrowed/default window", "Max not yet confirmed"), reset command, projector editor binding (reuse the Lock curve editor control), pulse, drift/landing/noise numeric editors with §9.7 ranges, graph sample arrays from `SlipAngleSourceToProjectedGraph.Sample(window, projector, step 0.25°)`. Edits mark dirty; Apply via existing page path.

- [ ] **Step 1: Tests** — `Auto_makes_editors_read_only`; `Trigger_by_controls_threshold_visibility` (Brake→brake only; Throttle→throttle only; Any→both); `Status_text_for_unconfirmed_peak`; `Graph_samples_hit_10_30_60_at_min_best_max`; `Values_validate_like_key_data_settings`.
- [ ] **Step 2–4:** FAIL → implement (layout mirrors the Wheel Lock tab section order exactly; shared label column) → PASS. Screenshot-harness renders the tab (Task 26).

### Task 23: Native ShakeIt effect (spec §9.10)

**Files:** Create `Integration\ShakeIt\QAdvanceFeedbackSlipAngleContainer.cs`, `Ui\ViewModels\SlipAngleEffectViewModel.cs`, `Ui\Views\SlipAngleEffectView.xaml(.cs)`; modify `Integration\ShakeIt\QAdvanceProjectionFilter.cs` (or a sibling filter for degrees x-axis), string tables; Tests `QAdvanceFeedback.Tests\ShakeItEffects\NativeEffectDefaultsTests.cs` (new case), `QAdvanceFeedback.Tests\Ui\SlipAngleEffectViewModelTests.cs`.

**Interfaces:** `[ShakeItContainerMetadata(..., "QAdvanceFeedback Slip Angle", ..., "QAdvanceFeedback", OutputMode.Any ...)] public class QAdvanceFeedbackSlipAngleContainer : EffectsContainerBase<ToneOutput>` — one placement, value = `Projected.All/100`, 0 when the plugin frame is older than 500 ms, `SetAudioDefaults`: low 40 Hz, `UseHighFrequency` true 25 Hz, noise off, `FrequencyBasedOnPreFilter = false`, `UsePrehemptiveMode = false`. Panel VM: TriggerBy + thresholds, Auto, Min/Best/Max (`ValuesEditable`), warning, status, `Test` (Task 24); hub sync (Task 21), 700 ms debounce on values, 1 s refresh.

- [ ] **Step 1: Tests** — defaults pinned; `Panel_values_read_only_under_auto`; `Panel_edit_reaches_hub_with_ShakeItPanel_origin`; `Stale_frame_outputs_zero` (pure helper).
- [ ] **Step 2–4:** FAIL → implement → PASS. Build 0 warnings.

### Task 24: Slip Angle Test Effect (spec §9.11)

**Files:** Create `Settings\SlipAngleTestEngine.cs` (+ link), `Ui\ViewModels\SlipAngleTestViewModel.cs`; modify `Core\ShakeItEffects\TestEffectSession.cs` (`TestKind.SlipAngle` appended), `Ui\ViewModels\ITestEffectHost.cs` (`void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull)`), `QAdvanceFeedback.cs`, `SlipAngleEffectView.xaml`; Tests `QAdvanceFeedback.Tests\Ui\SlipAngleTestViewModelTests.cs`, `ShakeItEffects\SlipAngleTestEngineTests.cs`.

**Interfaces:** Slider `SourceDeg` 0 → `3.0 × Max`, 2× Lock/Slip slider length; tick at `End` labelled "100%". Engine: real normalizer + preset projector + real `DriftAttenuator` with configured `DriftSettings`; maturity 1, no gate, no landing. Dot: `x = Min(SourceDeg, End)`, `y = PulseFreeAll` (post-drift). Session/auto-off identical to `LockSlipTestViewModel` (10 s, release on off/unload, `TestKind.SlipAngle`).

- [ ] **Step 1: Tests** — `Below_tick_dot_on_curve` (y == projected(N(x)) ± 1e-9); `Past_tick_dot_top_right`; `Holding_past_max_drops_dot_to_40_percent_of_curve` (after 0.25 s + 0.12 s with ticker); `Auto_off_after_10s`; `Other_test_starting_stops_this_one`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5: Checkpoint.**

---

## Phase 5 — G-Force (spec §10)

### Task 25: Integrate Slip Angle

**Files:** Modify `Core\GForce\GForceEngine.cs`, `Settings\GForceSettings.cs`, `Ui\ViewModels\GForceEffectViewModel.cs`, `Ui\ViewModels\GForceTestViewModel.cs`, `Settings\SettingsControl.xaml(.cs)` (G-Force tab toggle + scale), `QAdvanceFeedback.cs` (pass `SlipAngle.Projected.All`), string tables; Tests `GForceEngineShakeModeTests.cs` (new cases), `GForceSettingsTests.cs` (new cases), `Ui\GForceTestViewModelTests.cs` (new cases), `ShakeItEffects\GForceSharedOptionsTests.cs` (new case).

**Interfaces:** `GForceEngine.IntegrateSlipAngle` (bare ctor default false, like `IntegrateWheelLockAndSlip`), `WheelSlipAngleShakeScale`; `Compute(..., double slipAngleAll0100 = 0.0)` appended optional param. `gate = (IntegrateWheelLockAndSlip && Max(lock,slip) ≥ T) || (IntegrateSlipAngle && angle ≥ T)`; `drive = Max` of enabled scaled contributions. `GForceSettings.IntegrateSlipAngle = true` (missing field loads true), `WheelSlipAngleShakeScale = DefaultShakeScaleFor(mode)`. Labels: "Higher of G-Force or wheel lock/slip/angle" + updated descriptions (EN/zh). G-Force Test Effect gains an Angle bar.

- [ ] **Step 1: Tests** — `Angle_alone_triggers_when_lock_slip_off`; `Drive_is_max_of_three_scaled`; `Lock_slip_behaviour_identical_when_angle_zero` (bit-identical vs current outputs over existing mode fixtures); `Missing_field_loads_true`; `Angle_bar_feeds_test_engine`.
- [ ] **Step 2–4:** FAIL → implement → PASS (existing G-Force tests unedited except additive label/default pins). **Step 5: Checkpoint.**

---

## Phase 6 — Docs, screenshots, report

### Task 26: Documentation and screenshots

**Files:** Modify `docs\architecture.md`, `docs\architecture.zh-Hans.md` (subsystem table row, layer notes, new "Slip Angle: how it works and why", shared-cores section, file map for every new file), `README.md`, `README.zh-Hans.md`; extend `tools\screenshot-harness` (Slip Angle tab, Slip Angle ShakeIt panel EN + zh-Hans); create `docs\slip-angle-report.md` (what shipped, spike results, equivalence-test evidence, deviations).

- [ ] **Step 1:** Update docs per the maintenance rule; verify the file map against the real directory listing.
- [ ] **Step 2:** Run the harness; produce `docs\images\settings-slip-angle.png`, `docs\images\shakeit-slipangle.png`, `docs\images\shakeit-slipangle.zh-Hans.png`, regenerate G-Force images if its tab changed.
- [ ] **Step 3:** Final verification: full suite green, Release build 0 warnings, `LocalizationTests` parity green. **Checkpoint.**
