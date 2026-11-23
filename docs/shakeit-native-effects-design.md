# QAdvanceFeedback as native ShakeIt effects — design

> **Superseded for the ShakeIt source (2026-11-19).** The ShakeIt Built-in source is now specified by
> [`docs\shakeit-source-design.md`](shakeit-source-design.md) (as built: `docs\shakeit-source-report.md`).
> It supersedes the section "ShakeIt source: ShakeIt's own built-in calculation" below, in particular
> resolution steps 2–3: the exported properties are no longer read, and there is no per-wheel Raw
> fallback. The channel reads the tap only while ShakeIt is set up and the game is supported, otherwise
> it uses Raw for all four wheels under Raw's learning key. The hidden containers are no longer
> configured "exactly like the export profile": their algorithm settings come from the ShakeIt
> algorithm block and their filters from this plugin's thresholds. The rest of this document still
> describes the native effects as built.

Date: 2026-11-16. Status: implemented in 1.2.0.0 (see `docs\shakeit-native-effects-report.md`);
live SimHub checks pending (see "Risks").

## Goal

Register QAdvanceFeedback's outputs as first-class ShakeIt effects (the way
[AZOM](https://github.com/giantorth/AZOM) registers `MozaClutchBitePointContainer`), so a
driver adds "QAdvanceFeedback WheelLock" etc. straight from ShakeIt's effect list instead of
building Custom Effects around published properties — and so the plugin can read ShakeIt's own
wheel lock/slip algorithm directly, retiring the "export properties" profile.

## Findings this design rests on (from decompiling `lib\SimHub.Plugins.dll`)

- A public class deriving `EffectsContainerBase<ToneOutput>` and carrying
  `[ShakeItContainerMetadata(order, name, description, category, OutputMode.Any)]` is found by
  SimHub's assembly scan. Its type name is saved as `ContainerType` in profiles **and** is the key
  of the device channel map (`ContainerType` + optional channel-group name) — **class names are
  frozen once shipped.**
- Per-frame pipeline: `EffectBase.GetEffectValue` (0..1) → `Filter.GetOutput` (0..100; no filter =
  ×100) → effect `LastOutput` → device output × container Gain.
- Channel routing is stored per `FFBPlacement` (9 values). One container cannot expose 8
  independently bindable outputs.
- Built-in aggregation (`EffectAggregator`, Corners/Front-Rear/Left-Right/Mono) combines by **max**.
  QAdvanceFeedback has its own weighted aggregation, so we bypass ShakeIt's
  (`AllowEffectsAggregations => false`) and swap our own effect list instead. The swap is
  signalled by writing the container's public, persisted `AggregationMode` string, whose setter
  calls `OnEffectsChanged()` and raises `Effects` (the channel-mapping model listens for it).
- The effect editor shows `Filter.EditControl` (parameter UI) and `Filter.ResponseCurveEditor`
  (graph) — both overridable, so a custom `FilterBase` can show a read-only graph in the native
  curve slot with no editable parameters.
- `ToneOutput` exposes `Frequency` ("when low"), `UseHighFrequency`/`HighFrequency` ("when high"),
  `UseWhiteNoise`/`WhiteNoise`. `SetAudioDefaults(ToneOutput)` runs only when the effect's output
  is first created (add / paste) — never over a driver's later edits. Motors (`VibrationOutput`)
  have no frequency.
- `WheelsLockContainer`/`WheelsSlipContainer` and their `FrontLeft..RearRight` `WheelSlipEffect`s
  are public. A private, never-added instance can be evaluated with the `GameData` +
  `CalibratedFeedbackData` ShakeIt hands any container, yielding ShakeIt's exact per-wheel 0..1
  value (all branches, shared per-game calibration). Requirements: call `LoadDefaultSettings()`
  (sets `IsLock`), set slip `MuteWhenLockEffectIsActive = false` (it dereferences `ParentProfile`).

## Effects

Category for all: `QAdvanceFeedback`. All `OutputMode.Any`. Every effect reports supported on
every game and returns 0 when the plugin's latest frame is older than 500 ms.

| ContainerType (frozen) | Display name | Outputs | Shaker defaults (ToneOutput) |
|---|---|---|---|
| `QAdvanceFeedbackWheelLockContainer` | QAdvanceFeedback WheelLock | routed (below) | low 30 Hz, high on 20 Hz, noise off |
| `QAdvanceFeedbackWheelSlipContainer` | QAdvanceFeedback WheelSlip | routed (below) | low 45 Hz, high on 30 Hz, noise off |
| `QAdvanceFeedbackGForceBottomFrontContainer` | QAdvanceFeedback G-Force BottomFront | Left=Bottom.FrontLeft, Right=Bottom.FrontRight | low 100 Hz, high on 60 Hz, noise 10 off |
| `QAdvanceFeedbackGForceBottomRearContainer` | QAdvanceFeedback G-Force BottomRear | Left=Bottom.RearLeft, Right=Bottom.RearRight | low 100 Hz, high on 70 Hz, noise 10 off |
| `QAdvanceFeedbackGForceBackLowContainer` | QAdvanceFeedback G-Force BackLow | Left=Back.LowLeft, Right=Back.LowRight | low 100 Hz, high on 70 Hz, noise 10 off |
| `QAdvanceFeedbackGForceBackTopContainer` | QAdvanceFeedback G-Force BackTop | Left=Back.TopLeft, Right=Back.TopRight | low 100 Hz, high on 60 Hz, noise 10 off |

All defaults also set `FrequencyBasedOnPreFilter = false`, `UsePrehemptiveMode = false`.

Reference channel map (Sensit! Haptic Core, from the owner's `.sichannels`): BottomFront 0/1,
BackLow 2/3, BackTop 4/5, BottomRear 6/7.

### WheelLock / WheelSlip routing

Stored in the container's own `AggregationMode` (persisted in the ShakeIt profile, per effect
instance — not a plugin setting). Default **Mono**.

| Routing | Placements | Values (pulsed projected, 0..100 → /100) |
|---|---|---|
| Corners (per wheel) | FL, FR, RL, RR | `LockWheels` / `SlipWheels` corners |
| Front/Rear | Front, Rear | `LockFront`, `LockRear` |
| Left/Right | Left, Right | `LockLeft`, `LockRight` |
| Mono (one channel) | All | `LockAll` |

Filter: `QAdvanceProjectionFilter` — `GetOutput(x) = clamp(x,0,1) × 100` (projection is already
applied by the plugin), empty `EditControl`, `ResponseCurveEditor` = read-only
`ProjectionCurvePlot`. The plot draws the plugin page's right graph ("Source value -> Projected
value": the source value through the key data normalization and the projection curve, sampled every
5) from the **applied** projector and pattern, and the key data values the panel's key data section
shows (`KeyDataPanelModel.Resolve`: learned SMax under Auto, the current source/game slot under
Manual, else the shipped reference, else the channel default). The sampling is
`Settings\SourceToProjectedGraph.cs`, which the page's right graph also uses, so the two always
agree. It redraws when the channel's projector, pattern, key data or source mode is broadcast, and
once a second under Auto (the learned SMax moves while driving). Minimum force is intentionally
dropped.

The effect list is rebuilt after a profile loads (`[OnDeserialized]`), whatever routing it restored.

G-Force effects: no filter (passthrough), aggregation hidden, `IsAllowedOutOfGame = true`, so the
plugin page's G-Force Test Effect also plays through them in the menus. With no fresh frame they
output 0.

### Panel settings (ShakeIt side, no text boxes)

- WheelLock / WheelSlip: **Source** dropdown + routing dropdown + key data points (see below) + the curve graph.
  Source options: Plugin Internal (Manual) and ShakeIt always; Viper only when the Viper plugin is
  available; Custom only when that channel has a configured Custom source
  (`CustomSources?.HasContent()`). The option list refreshes when availability changes.
- G-Force (same panel on all four), the plugin page's pattern: per axis (Accel/Decel/Lat) an Auto
  toggle, the max-G editor (same range/step as the page) and the readout label. The editor is
  **always editable**: under Fixed it is the value used, under Auto the default Auto falls back to
  until it has evidence. The label is `Settings\GForceReadout.cs`, shared with the page: "Fixed: x",
  "Default: x. Auto detected: y" or "Default: x. Auto: still using default (no data yet)". It
  refreshes once a second. Below the maxima: Lateral feedback direction (Normal/Reversed) and the
  Integrate Wheel Lock and Slip switch, with the page's own labels. Both are shared hub fields
  (`SettingField.LateralDirection`, `SettingField.IntegrateWheelLockAndSlip`); the page refreshes
  its combo and switch (and the shake controls the switch enables) when either changes in ShakeIt.

## ShakeIt source: ShakeIt's own built-in calculation

The ShakeIt source mode (label "ShakeIt Built-in") now prefers SimHub's own built-in lock/slip read
through a native effect, and keeps the export-based path as a fallback. Any QAdvanceFeedback effect
being evaluated drives a single `ShakeItBuiltInTap` (`Integration\ShakeIt\ShakeItBuiltInTap.cs`):
once per ShakeIt frame (`TapDeduper`, keyed on `GameData.FrameTime`, however many of our effects or
ShakeIt profiles exist) it evaluates a hidden lock and slip container pair and stores `Corners`
(0..100) with a timestamp.

The hidden containers are configured exactly like the owner's shipped export profile
(`docs\setup\QAdvanceFeedback - WheelLockSlip.siprofile`), so they reproduce the values that profile
exported: legacy iRacing algorithm on for both; lock `LockSensibility` 50, `BrakeFilter` 20; slip
`BrakeFilter` 99, `ThrottleFilter` 10, both filters on, lock-muting off.

Resolution order per ShakeIt-mode channel, every frame (`QAdvanceFeedback.ResolveWheel`):

1. a tap no older than 500 ms → the tap's wheel value;
2. otherwise the existing path, unchanged: the legacy
   `ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.<Wheel>` / `…WheelSlip.IRacing.<Wheel>`
   properties (the export profile keeps working);
3. otherwise this channel's own Raw value (the existing resolver fallback).

The ShakeIt mode's source identity is **unchanged** (the legacy export property names). Both paths
are the same algorithm with the same values, so everything learned under that identity carries over,
and `KnownSourceColdStartReference` still classifies it as ShakeIt. The tap is visible as diagnostics
only (`Diag.ShakeIt.TapActive`, `Diag.ShakeIt.WheelLock.<Wheel>`, `Diag.ShakeIt.WheelSlip.<Wheel>`,
appended at the end of the diagnostic/CSV columns). The settings page counts the ShakeIt source as
available when either the tap is fresh or the export properties are reported, and its setup text
recommends adding a native effect, with the export profile kept as the alternative.

## Two-way settings sync

Single source of truth: the plugin's `QAdvanceFeedbackSettings`. `SettingsHub` owns writes of the
**shared fields**: Lock/Slip SourceMode, Accel/Decel/Lat MaxMode, Fixed Accel/Decel/Lat MaxG; it
also broadcasts LockProjector/SlipProjector/SourceAvailability (read-only on the ShakeIt side).

- ShakeIt panel edit → `hub.Set(field, value, ShakeItPanel)` immediately: writes, applies to the
  running engines, saves the config file, raises `Changed(fields, origin)`. Source-mode writes go
  through the same steps the page uses (seed Custom reference when entering Custom, then
  `WheelChannelSettings.SwitchSourceMode`).
- Plugin page Apply (and every other page path that applies — key-data saves, Restore defaults) →
  `QAdvanceFeedback.ApplySettings()` diffs the shared fields against the last applied snapshot and
  calls `hub.NotifyApplied(changed, PluginPage)`. The hub's own writes update that snapshot without
  re-broadcasting.
- The page's 1 Hz source-availability check broadcasts `SourceAvailability` when Viper/ShakeIt
  availability changes; the ShakeIt source dropdown also re-reads availability whenever it opens.
- Plugin page receiving `Changed` from another origin: updates **only** those fields' controls
  (and their applied baseline) under the loading guard; all other unapplied edits stay as they
  are. Example: page has unapplied source + max-mode edits; ShakeIt changes source → page shows the
  new source, keeps the max-mode edit; Apply then only changes max-mode.
- Every open ShakeIt panel refreshes on any `Changed` (any origin, including File).
- `ConfigFileWatcher` (backup path): watches the config file, 300 ms debounce, ignores content
  identical to the hub's last save, loads it and `hub.NotifyFileChanged(fromDisk)` applies the
  differing shared fields with origin File. Last write per field wins. Hand edits to non-shared
  fields are out of scope.
- Handlers marshal to their own WPF dispatcher.

## Key data points in the ShakeIt panel

The WheelLock/WheelSlip panel also carries the channel's key data points. It follows the plugin
settings page's own pattern, for the channel's **applied** source:

- **Normalize Pattern:** SMax only (Max-Grip-Only), or SMax/S90/S75 (Mapping). Under SMax only, the
  S90/S75 rows are hidden, and S90/S75 are derived from SMax when saved.
- **Auto Generate:** on means the values are a **read-only** readout. They show the learned SMax
  (S90/S75 derived from it), else the source's shipped reference, else nothing. Off means they are
  editable, and show the slot the source's Global/Per-Game choice selects in the current game, else
  the shipped defaults as a starting point.
- **Manual + Per-Game with no game running:** shows the page's "no game is running" note; nothing is
  written.

The rules live in `Settings\KeyDataPanelModel.cs` and are shared with the page:

- `Resolve` decides what the section shows.
- `TryCommitManual` writes typed values; the page's own `PersistChannelIfManual` now calls it.

`TryCommitManual` writes only into the slot the stored flags select. It is refused under Auto, for an
invalid triple, and for Per-Game with no game. It latches the slot (seeded), so the plugin's one-time
learned seed stands down.

Sync goes through new hub fields (`Lock/SlipKeyDataAuto`, `Lock/SlipNormalizePattern`,
`Lock/SlipKeyDataValues`). The hub keys them by the current game (`QAdvanceFeedback.CurrentGameId`,
which is updated while paused or in menus too):

- **Panel edits** apply immediately. Values are debounced 700 ms, like the page; a pending edit is
  committed before a pattern or Auto change, and when the editor closes.
- **The panel refreshes** on hub changes, and once a second for the Auto readout and the plugin's
  key-data seed revision.
- **The page reacts** to an outside key-data change as it does to a game switch: reload, seed, then
  refresh. Its pending debounce for the other channel is committed first. An outside pattern change
  sets the page's pattern combo. A page Apply or key-data save broadcasts these fields like any
  other.

## Backward compatibility (hard requirement)

The new effects are additive. Every property the plugin publishes today (`QAdvanceFeedback.WheelLock.*`,
`QAdvanceFeedback.WheelSlip.*`, `QAdvanceFeedback.GForce.*`, diagnostics) keeps its name, value and
timing, so ShakeIt Custom Effects bound to them — and the shipped property-based profiles — work
exactly as before (the 62 product names are pinned by `AllPublishedPropertiesTests`). The new
`Diag.ShakeIt.*` names are diagnostics appended at the end only. The export-based profile still feeds
the ShakeIt source (fallback above), and `docs\shakeit-export-guide.md` stays. The property-based
profiles in `docs\setup` stay as they are.

## Out of scope

Syncing ShakeIt-profile-owned values (gain, frequencies, routing, channel bindings) into the
plugin; minimum force; non-shared field file watching.

## Risks needing a live SimHub check (spike first)

1. Our filter's controls really replace the native curve slot. Fallback: draw `ProjectionCurvePlot`
   at the top of our own edit control and return `null` from `ResponseCurveEditor`.
2. Effect-list swap via `AggregationMode` updates the channel-mapping UI. Fallback: always expose
   all placements for the chosen routing on reopen (rebuild on profile reload).
3. Hidden `WheelsLockContainer`/`WheelsSlipContainer` evaluate without a profile.
