# ShakeIt source via the native effect + Slip Angle Start value — implementation plan

> **For agentic workers:** execute task-by-task with a review after each. **This repo is not managed
> with git: NEVER commit, branch, stash or run any git write command.** Edit files on disk only.

**Goal:** (1) Slip Angle Start value + landing default swap; (2) ShakeIt source read only through our
native effect, detected live, with a clean per-channel Raw fallback, ShakeIt algorithm settings, and
the export-property source removed.

**Specs:** `docs\shakeit-source-design.md` (ShakeIt source), `docs\slip-angle-design.md` §8, §9.2,
§9.4, §9.6, §9.7 (Start value, landing swap).

## Global constraints
- No git writes. Test: `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj`; build:
  `dotnet build QAdvanceFeedback\QAdvanceFeedback.csproj -c Release` — 0 warnings. Baseline before
  this plan: 2536 tests passing.
- Layering (only root/Integration files touch SimHub types), MVVM standing rule, EN + zh-Hans
  strings, resilience model (never throw on data or ShakeIt thread), `docs\architecture.md`
  maintenance rule — all from `docs\architecture.md`.
- Existing test assertions unedited, except tests asserting the removed export path (Task C) — each
  replacement listed in the task report.
- Learning continuity: at default ShakeIt algorithm settings the ShakeIt source identity string must
  equal today's.

## Task A — Slip Angle Start value + landing swap
Files: `Core\SlipAngle\SlipAngleNormalizer.cs` (+ inverse `SourceFor`), `Core\SlipAngle\SlipAngleWindow.cs`
(add `Start` = ⌊0.5·Min⌋, null/omitted when 0), `Core\Projection\ProjectorSettings.cs` (SlipAngle
preset StartInput 15), `Core\SlipAngle\SlipAngleGate.cs` (MaxFlightSeconds 1.0, MaxWobbleSeconds 0.5),
Slip Angle tab + panel VMs/views (read-only Start line), `Settings\SlipAngleSourceToProjectedGraph.cs`
and graph markers (15), strings, tests.
Tests: N(Start)=15 for Min 5 (Start 2) and Min 9 (Start 4); Min 1.5 omits the knot and stays
monotone; inverse round-trip through the new knot; preset StartInput 15 (Lock/Slip presets
unchanged); gate defaults 1.0/0.5 and 200 ms airborne → 100 ms wobble; tab/panel show "Start: 2° (50%
of Min)" / "—"; Test Effect dot still on the curve; graph markers include 15.

## Task B — ShakeIt detection and availability state
Files: new pure `Core\ShakeItEffects\ShakeItSourceAvailability.cs` (state machine: NotSetUp /
SetUpIdle / Supported / NotSupported per spec §3–4; heartbeat ≤ 2 s; staleness 3 s in-game; game
switch re-check), new Integration `ShakeItProfileInspector.cs` (public API: both ShakeIt plugins'
`Settings.CurrentProfile.GetAllContainers()`, our container types, `IsEnabled` incl. parent group;
1 Hz on UI thread, guarded, volatile snapshot), heartbeat + tap feeding from EVERY QAdvanceFeedback
container (WheelLock, WheelSlip, 4×G-Force, Clutch Bite, Slip Angle), game-support check via the
hidden containers' `GetIsSupportedOnCurrentGame(caps)`, plugin snapshot + debounced
`SourceAvailability` broadcast.
Tests: every state transition; heartbeat expiry; staleness; game switch; inspector logic behind an
interface with fakes.

## Task C — Source resolution, per-channel Raw fallback, export path removed
Files: `QAdvanceFeedback.cs` (`ResolveWheel` and source-identity selection: ShakeIt-mode channel
reads the tap when state is Supported and values fresh, else Raw **with
`RawSourceFallback.RawIdentity`**, decided per channel), remove `MotorsExportAvailabilityProvider` /
export reads for the ShakeIt source and the page's export setup text, keep the ShakeIt identity
string. Tests: per-channel fallback uses Raw identity; ShakeIt learning never gets Raw values;
identity unchanged at defaults; replaced export-path tests listed.

## Task D — ShakeIt algorithm settings (model, hidden containers, filters, mute, fingerprint)
Files: `Settings\ShakeItAlgorithmSettings.cs` (Lock: legacy on, sensibility 50, sensibility-ex,
lock-only, low-quality level; Slip: legacy on, mute off, low-quality level — ShakeIt defaults for the
rest; ranges verified against SimHub's editor), on `WheelChannelSettings` as `ShakeItAlgorithm`
(old config loads defaults); `ShakeItBuiltInTap` configures hidden containers from an immutable
snapshot of these + our thresholds (filter mapping per spec §6, rounding, Slip brake filter off at
100, throttle filter off at 0 — semantics verified in SimHub code first, stop and report if they
differ); mute emulation; source-identity fingerprint (empty at defaults); new `SettingsHub` fields
for the algorithm settings. Tests: mapping table, rounding, fingerprint, mute, load/normalise, hub.

## Task E — UI: source list visibility, notes, algorithm block
Files: Wheel Lock / Wheel Slip tab source lists and ShakeIt panel `Sources` lists (show ShakeIt only
when set up, or when currently selected — marked); VM-bound notes per spec §4; the "ShakeIt
algorithm" block (VM, shown only for ShakeIt source) on both tabs and both panels, hub-synced; strings
EN + zh. Tests: visibility matrix, hidden-but-selected, block visibility, hub round-trip, MVVM /
DirtyTracking / Localization green.

## Task G — Internal source always learns, every source isolated
Spec: `docs\internal-source-learning-design.md` (approved 2026-11-19). Runs after Task E, before F.
Files: `Core\Normalized\NormalizedWheelLockSlipEngine.cs` (feed the internal key every frame through
the same gates; retire `RawFallbackSourceIdentity`; quiet-source check reads the internal key),
`QAdvanceFeedback.cs` (internal feed + Custom-matching-Raw), runtime load path (one-time
`__layer3_raw_fallback__` merge), Slip Angle frame processor/plugin (Plugin Internal always fed).
Tests: per spec §5.

## Task F — Docs and screenshots
`docs\architecture.md` + `.zh-Hans.md` (ShakeIt source section rewritten, file map, Slip Angle Start
+ landing, internal-source learning rule from Task G), both READMEs (no export profile; add-our-effect instruction; Start value),
`docs\shakeit-native-effects-design.md` (pointer to the new design), `docs\shakeit-export-guide.md`
(retired banner), `docs\slip-angle-report.md` (Start + landing swap), a short
`docs\shakeit-source-report.md`, remove the `.superpowers/` line from `.gitignore`, regenerate
affected screenshots via `tools\screenshot-harness`. Final: full suite + Release build.
