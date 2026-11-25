# Per-wheel slip-angle sources from JSON (1.2.1)

Owner request, 2026-11-23. SimHub publishes no common per-wheel slip-angle property; each game's own raw
telemetry fields (`DataCorePlugin.GameRawData.*`) have to be named per game. Until 1.2.0 those rows were
hard-coded in `SlipAngleRawSources.cs`. From 1.2.1 they live in one JSON file that users can edit and share.

## What changed

| Area | 1.2.0 | 1.2.1 |
|---|---|---|
| Per-game rows | Built into `SlipAngleRawSourceTable` | Only `QAdvanceFeedback.SlipAngleSource.json`; no built-in rows |
| Shipped copy | - | The solution root's JSON, an embedded resource (`SlipAngleSourceCompiled`) |
| User copy | - | `SimHub\PluginsData\Common\QAdvanceFeedback.SlipAngleSource.json`, created from the shipped copy when missing |
| When read | - | At Init, and again when the running game changes (a hand edit applies without restarting SimHub) |
| Invalid file | - | Refused whole, never overwritten; shipped copy used; Health `SlipAngleSourceFile`; the page's editor is off |
| Editing | Code change | The Slip Angle tab's per-wheel telemetry source (saved by Apply), or the file by hand |
| Angle source | Per-game "Car angle" unit only | New **Angle source** dropdown: per-wheel when available, or always car angle |
| Version | 1.2.0.0 | 1.2.1.0 |

## The file

```json
{
  "about": "...",
  "version": 1,
  "games": [
    { "id": "ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO",
      "slipAngle": ["DataCorePlugin.GameRawData.Physics.slipAngle01", "...02", "...03", "...04"] },
    { "id": "FM?|FH?", "slipAngle": ["DataCorePlugin.GameRawData.TireSlipAngleFrontLeft", "..."], "unit": "ratio1" },
    { "id": "RFactor2", "slipAngle": ["...mLateralPatchVel"], "longitudinal": ["...mLongitudinalGroundVel"],
      "transform": "atan2PatchVelocity", "unit": "radians", "surface": ["...mSurfaceType"] },
    { "id": "GranTurismo7", "surface": ["DataCorePlugin.GameRawData.surfaceType01", "..."] }
  ]
}
```

- Paths are full SimHub property names, FL, FR, RL, RR (any raw-index remap already applied - F1 23+ and LFS are
  listed as raw 3, 4, 1, 2).
- `unit`: `degrees`, `radians`, `ratio1`, `ratio100`; absent = detected while driving (the existing body-angle
  check and the unit dropdown).
- `transform`: `direct` (default), `atanOfTangent` (LFS), `atan2PatchVelocity` (rF2, LMU, the NASCAR family;
  needs `longitudinal`).
- `surface`-only rows keep the per-wheel raw surface for games with no slip-angle field.
- `note` is free text (the old wheel-order / provisional remarks moved there). Unknown fields are kept on save.

**Units are the ones the code stated before 1.2.1** (owner: "stay whatever we wrote in our code"): the table's
radians (LFS, Drift21, rF2, LMU, NASCAR family) and the hard-coded lists' KartKraft degrees and Forza ratio1. Every
other row has no unit. The config's unit lists (`SlipAngle.SourceUnits`) are unchanged and still win.

The shipped file resolves every 1.2.0 game exactly as the 1.2.0 table did (25 games with a slip angle, the ACC long
name, 16 surface-only games); the spike-row tests (`SlipAngleRawSourcesTableTests`) now run against it, plus future
F1 / Forza ids and the ids a pattern must not take.

## The settings page (Slip Angle tab, Plugin Internal)

- **Angle source**: *Per-wheel tyre angle when available* (default) or *Always car angle*. A draft setting
  (`SlipAngleChannelSettings.AngleSource`, hub field `SlipAngleAngleSource`), applied by Apply. *Always car angle*:
  the reader reads no game's tyre field, the key data uses the car-angle identity and heading, and the per-wheel
  source and the **Used for this game** unit dropdown are hidden (tab and ShakeIt panel).
- **Per-wheel telemetry source** (plugin page only, never the ShakeIt panel), for the running game:
  - A game with a row shows its four properties (plain names, with the property picker; no NCalc/JavaScript - the
    value is read as it is, and Custom mode already covers expressions).
  - A game without one shows **Set Per-wheel SlipAngle telemetry source**, which opens the four boxes.
  - Apply writes the row (`SlipAngleSourceStore.Save`, re-reading the file first so hand edits are kept):
    four names set it (a new game gets a plain row with no unit -> Auto-detect); four empty remove the
    slip-angle source (surface paths stay); one to three are refused with a red line, the rest of Apply goes on.
  - **Restore default** only for a game the compiled copy has a slip-angle row for, whose file row differs (or
    while its boxes hold an edit). It fills the shipped row; Apply writes it back whole.
  - A computed row (rF2, LMU, NASCAR family) is shown read-only.
  - An unapplied edit belongs to the game it was typed for; the page stays dirty until it is saved.
- The unit dropdown moved under the per-wheel source and got a label (**Used for this game**): it shows what the
  game actually uses (its unit, Auto-detect, or the car angle).

## Calls made

1. A broken file falls back to the compiled copy for the session and the editor is off (owner: OK).
2. Clearing all four removes the game's slip-angle source (owner: yes).
3. Plain property names with a Pick button (owner: OK).
4. The file is re-read on a game change rather than watched continuously: cheap, and it is the moment a new
   game's row matters.
5. A shared catalog swap (`SlipAngleRawSourceTable.Use`) bumps a revision; the reader forgets its per-game
   missing/failed verdicts, so new paths are tried at once.
6. "Restore all defaults" does not touch the JSON file (it is not a plugin setting).

## Ids: names, wildcards, regex; merging on save (owner, 2026-11-23, second round)

- `aliases` is gone: `id` carries every game - `Game1|Game2|Game3`, wildcards `*` / `?` (`FM?|FH?`), and
  `regex:...`, which runs to the end of the id (so it may use `|`; combine it with names by putting it last) and
  must match the whole id. Case-insensitive. Owner: regex is marked `regex:`.
- Which row a game gets: the row naming it exactly, else the first row (file order) whose wildcard / regex matches,
  else none. A game named exactly by two rows, an empty name, or a bad regex makes the file invalid. An exact row
  with nothing else (`{"id":"FH6"}`) is "no per-wheel source" and overrides a pattern. Lookups are cached per game id.
- Saving game G (Apply / Restore default), the merge key being slipAngle + longitudinal + surface + unit + transform +
  provisional (id and note excluded): G leaves the row naming it exactly (a row left nameless is deleted); if the
  row G then matches through a pattern already has the new settings, nothing more is written; else if any row has
  exactly those settings G is added to its id (before a `regex:` tail); else a new row `{"id":"G", ...}` is added.
  Clearing a game a pattern still covers writes the exact empty row; a second cleared game joins it (`FH6|FH5`).
- Restore default compares the settings G resolves to (file vs compiled), however the ids are written.
- The shipped file is compacted to 18 rows (13 with a slip angle) - every game resolves exactly as in 1.2.0:
  `ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO` (same four paths),
  `regex:^F120(2[3-9]|[3-9]\d)[A-Za-z]*$` (F12023-F12099 and suffixed ids - SimHub 9.13.2's GameRegistry already
  has `F12026SeasonPack`; F12019-F12022 keep their exact surface-only row), `FM?|FH?`, the NASCAR family, and the
  surface-only families. Pattern rows' `note` names the games they match; the tab shows the note and
  "(G, from "<row id>")" when the row is shared or a pattern.

## Surface (owner, 2026-11-23, third round)

- `surfaceValues` (new, needs `surface`): raw value -> `sealed` / `loose` / `lowGrip`, case-insensitive, part of the
  merge key. The translation tables moved out of code (`SurfaceBucketMap.TryMapWheel` is gone): the shipped file
  carries rFactor 2's 0 / 2 / 3 / 4 / 5 and GT7's names, exactly as before; every other row is untranslated. The
  reader hands the translated wheels over as `SlipAngleInput.WheelSurface`; `SurfaceBucketMap.Vote` is the same strict
  majority.
- SimHub's own per-wheel `WheelInGrassOrGravel` (`ITelemetryFrame.WheelOnLooseSurface*`, already used by Lock / Slip)
  now drives Slip Angle's surface when the game's own field gives nothing (`SurfaceBucketMap.FromSimHubLoose`):
  Loose at 3+ wheels; Sealed at 0-1 wheels only once the game was seen reporting loose this session (SimHub has no
  capability flag, so all-false may mean "not filled in"); otherwise Unknown (held). Diagnostics source `SimHub`.
  In the owner's logs F1 25 reports it (192 of 6,256 frames on 2026-11-20); FH6 never did.
- Saved buckets: since any game can now learn Loose, only a Low-grip bucket of a game whose row cannot translate to
  `lowGrip` is still treated as left over from the old guessing and dropped on load (`SurfaceBucketMap.ReportsLowGrip`,
  set by `SlipAngleRawSourceTable.Use`; before any file is in use nothing is dropped).

## Settings UI rework (owner, 2026-11-23, fourth round)

- **Global Source Type Mode** (was "Angle source") is first; **Current Source** (was "Source") follows. "Always car angle"
  hides Current Source and everything under it, and now wins over Custom at runtime too (`UpdateSlipAngle`'s
  `custom`, `SettingsHub.SlipAngleIdentity` / `SlipAngleKeyDataHeaderKey` check it first) - otherwise a hidden Custom
  source would still drive the output.
- The per-wheel editor is **Telemetry Type** (None / Angle Only / Lateral + Longitudinal Velocity Component) + **Has
  Surface?** + the boxes each needs (`SlipAngleSourceEdit`, `SlipAngleSourceStore.Save(string, SlipAngleSourceEdit,
  bool)`): Angle Only writes `slipAngle` (LFS stays a tangent); Lateral + Longitudinal writes `slipAngle` /
  `longitudinal` / `atan2PatchVelocity` / `radians`; None removes the slip angle; Has Surface writes `surface`
  (keeping `surfaceValues`), unchecked removes both. Every shown group needs all four boxes. Computed rows are no
  longer read-only. The "Set Per-wheel SlipAngle telemetry source" button is gone: Telemetry Type None -> Angle Only
  does the same.
- One ComboBox width (340) for every dropdown on the tab; every box sits in the shared label column's second
  column, so it starts at the dropdowns' left edge.
- **Surface line** on the tab and the ShakeIt panel (`SlipAngleStatus.Surface` / `SurfaceSource` / `SurfaceWeight`,
  `SlipAngleLiveText.SurfaceLine` / `BorrowLine`): the surface key in use, its origin, "changing, NN%" while
  blending, and the borrowing tier while the key has no confirmed window.

## Fifth round (owner, 2026-11-24)

- **Alignment.** The tab's ComboBox style is `BasedOn` MahApps' own style (a nested view's StaticResource does not see
  the page's implicit style), so the dropdowns had no 4 px margin and sat 4 px left of the boxes. The style sets
  `Margin=4`; every dropdown and box now starts at the same x (measured: 245 px in the screenshot).
- **Why the car angle is used.** The orange line said "This game has no tyre slip angle" while a game WITH a source
  was still being checked (it is promoted after 5 s of moving, cornering data) and when its properties were missing.
  New `SlipAngleSourceState` (reader -> `SlipAngleInput` -> frame result -> `SlipAngleStatus`) and
  `SlipAngleLiveText.WarningText`: no source / Checking / NotReported / CarAngleChosen (no line). Shown on the tab
  and the ShakeIt panel.
- **Renames.** Telemetry Type "Angle Only" -> "Per-Wheel Angle" (每轮角度); "Has Surface?" -> "Has Per-Wheel Surface
  Telemetry?" (包含每轮路面遥测？); "Used for this game:" -> "Unit used for this game:" (此游戏使用的单位：).
- **Current Source removed** with the whole Slip Angle Custom source (settings, frame request fields, processor path,
  hub branches, tab rows and strings). Plugin Internal is the only source; a Custom config loads as Plugin Internal
  and its old fields are dropped on the next save. Values learned under a Custom identity stay in Parameters.json,
  unused.
- **Restore this game's source**: shown only for a game the compiled JSON has a row for (any row, surface-only
  included - `HasCompiledDefault`), enabled while the file's row or the typed edit differs, and written to the file
  at once (no Apply), like the page's other restores. The pending-restore state is gone.
- **No hard-coded sources left.** The unit lists' KartKraft / Forza defaults (`DefaultByAngle`, `DefaultRatio1`,
  `IsShippedDefault`) are gone - the JSON's `unit` is the only known unit. The vertical-acceleration game set
  (IRacing, Rennsport) moved to a JSON field `verticalAcceleration` (Rennsport's row, and a new `IRacing` row with
  only the flag); the tab has **Has Vertical Acceleration Data?** bound to it. SimHub's
  `FeedbackData.VerticalAcceleration` is one field for every game, but most games leave it at 0 - read as "falling",
  it would pause learning all the time - so it is read only where the row says so. It drives Slip Angle's hop
  exclusion (a bump pauses learning) and feeds the impact check. `ViperSupportedGames` (Lock/Slip) stays.
- **Gaps.** 16 px before the per-wheel block, 8 px before each checkbox and the unit row, 24 px before the key data.
  Each checkbox has a one-line description under it.

## Sixth round (owner, 2026-11-24): match levels, Source Unit, Source Type for this game

- **Which row a game gets** (`SlipAngleGameIdPattern.Level` / `PatternLevel`, `SlipAngleSourceCatalog.Resolve`,
  `ResolveIndex`): 1 an id that is exactly the name; 2 the name inside a multi-part id (`F12024|regex:...` too);
  3 a `?`-only wildcard; 4 a `*` wildcard; 5 a regex - then file order. A name stays unique across the file, so 1
  and 2 never compete; an exact name always overrides the patterns.
- **Saving always names the game** (`SlipAngleSourceStore.Place`): it leaves the row that named it and joins the
  first row with the same settings even when another row's pattern already covers it (`F12024|F1202?|F1203?` ->
  `F1202?|F1203?` + `F12024` -> back to `F1202?|F1203?|F12024`), else gets its own row. Nothing is written only for
  "no source" with no pattern covering the game.
- **Source Unit** (Per-Wheel Angle only, under Telemetry Type): Auto Detect / Angle Value (0 to 360) / Radian Value
  (0 to 6.28) / Slip Ratio (0 to 1) / (0 to 100) - the row's `unit`, saved by Apply (`SlipAngleSourceEdit.Unit`).
  Lateral + Longitudinal Velocity has no unit setting and no `unit` field: the catalog reads it as radians always
  (the shipped rF2 / LMU / NASCAR rows lost `"unit": "radians"`; a unit an older file still has there is ignored,
  also by the merge key).
- **Source Type for this game** (was "Unit used for this game"): the running game's own choice, saved to the
  plugin CONFIG (`SlipAngle.SourceUnits`) - never to the source JSON. Its default is the JSON's: a set unit ->
  Per-Wheel Angle (that unit) / Car Angle; Auto Detect -> Per-Wheel Angle (Auto / Angle / Radian / Ratio 0-1 /
  Ratio 0-100) / Car Angle; velocities -> Per-Wheel Angle (Radian) / Car Angle; Telemetry Type None -> hidden.
  Restore default shows only while the choice differs from the JSON's (tab and ShakeIt panel; the panel got the
  label too). A unit the JSON states now wins over the config lists (`SlipAngleSourceUnits.Resolve`).
- **Gaps**: the extra gap before Key data points is gone; "Source Type for this game" has the larger one (24) above it.

## Seventh round (owner, 2026-11-24)

- "General Source Type" -> **Global Source Type Mode** (全局来源类型模式); the ShakeIt panel's unit label is the short
  "Source Type:" and sits in the panel's label column.
- The orange line follows what is APPLIED: `SlipAngleSourceState.CarAngleChosen` split into `CarAngleAlways` ("Global
  Source Type Mode is "Always car angle"...") and `CarAngleForGame` ("Source Type for this game is Car Angle..."), both
  shown (they were silent).
- The mode is saved with the config and comes back after a restart (checked in the mock host: the file kept
  "Always car angle" and the page showed it); Restore all default settings puts it back to Per-wheel (tests:
  `RestoreDefaults_puts_the_global_source_type_mode_back_to_per_wheel`, the page-level
  `Restore_defaults_puts_the_global_source_type_mode_back_to_per_wheel`). The mock host's Debug and Release builds
  keep separate PluginsData folders.

## Screenshots

`tools\screenshot-harness` now captures the Slip Angle tab with a running game (AC EVO) and a temp copy of the
shipped source file, so `docs\images\settings-slip-angle.png` shows the angle source, the per-wheel telemetry source
(with the shared-row heading and note) and the "Used for this game" dropdown. The same run refreshed images that were
already stale before 1.2.1: `settings-general.png` (version 1.2.1.0), `shakeit-slipangle(.zh-Hans).png` and
`shakeit-gforce-test(.zh-Hans).png` (the Slip Angle curve shipped on 2026-11-22). Every other image was byte-identical.

## Verification

- `SlipAngleSourceStoreTests`: compiled copy embedded and equal to the solution root file; units; create when
  missing; the file as the only definition; broken files refused whole and never overwritten; reload on change and
  re-create on delete; save/refuse/remove/restore; hand edits and unknown fields kept; alias lookup; computed row
  edited becomes plain; *Always car angle* in the reader, identity, heading, unit dropdown and hub diff.
- `SlipAngleTelemetrySourceTests`: the tab's rows, pending/dirty, Apply, Restore, Set button, refused partial
  rows, clearing, read-only computed rows, *Always car angle* visibility and SaveTo, game changes, no file, broken file.
- `SlipAngleRawSourcesTableTests`: the spike rows against the JSON-built table.
- `LearnedValueSourceMatchTests.The_shipped_version_is_1_2_1_0`.
