# QAdvanceFeedback — packaging & functional-change report

> **Naming note (added later, see docs/refinements-report.md):** the property tiers this report calls "Reliable" and "Final" were subsequently renamed to **"Normalized"** and **"Projected"** respectively (published names `WheelLock.Normalized.*`/`WheelLock.Projected.*` etc.) - this report is left as a historical record of what was true at the time and is NOT updated throughout for the new names.


## STATUS

Complete. All four functional changes implemented and tested; the project is GitHub-ready (solution
file, fetch script, `.gitignore`, LICENSE, bilingual README with real screenshots). Verified by a
from-scratch `MSBuild -t:Clean` + `-t:Restore,Rebuild` of `QAdvanceFeedback.sln` and a full test run.

## Final test count

**330 tests, all passing** (`dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj -v q`) —
up from the 309 reported after the final-assembly task. The 21 new tests: `KeyedGripLearnerTests` (7),
3 new per-game/car isolation tests in `ReliableWheelLockSlipEngineTests`, `RuntimeStoreTests` rewritten
for the keyed API plus 3 new legacy-import tests, `ConfigStoreTests` +3 (legacy config import), and
`QAdvanceFeedbackSettingsTests` +3 (`RestoreDefaults`/`CreateDefault`).

## Build verification

- `MSBuild QAdvanceFeedback.sln -t:Clean` then `-t:Restore,Rebuild -p:Configuration=Release -v:m` →
  **0 Warning(s), 0 Error(s)**, both projects (`QAdvanceFeedback`, `QAdvanceFeedback.Tests`) build.
- `bin\Release\net48\` contains **exactly two files**: `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb`
  — single-DLL distribution confirmed after every change in this task, including the new
  `System.Windows.Interactivity` reference the settings UI already depended on (unaffected).
- `dotnet test` → 330/330 passed.
- `bash -n tools/fetch-simhub-refs.sh` → syntactically valid.

## Functional change 1 — per-game/per-car learned parameters

- **`Core/Reliable/KeyedGripLearner.cs`** (new): one `GripLearner` per `(gameId, carId)` key, mirroring
  `Core/GForce/GForceMaxLearner`'s own key format/separator (`"|#|"`). `ReliableWheelLockSlipEngine`
  now holds two `KeyedGripLearner` instances (`LockLearners`/`SlipLearners`) instead of two bare
  `GripLearner`s; `Compute` takes optional `gameId`/`carId` parameters (default `""`, so every
  pre-existing test/call site keeps compiling unchanged — they simply share one isolated `""|#|""` key,
  same as before).
- **G-force AUTO maxima**: verified, not touched — `GForceMaxLearner` already keyed on `(gameId, carId)`
  before this task; confirmed by re-reading `Core/GForce/GForceMaxLearner.cs` and its existing tests.
- **Persistence**: `Core/Runtime/RuntimeDocument.cs` now stores `Dictionary<string, GripLearnerState>`
  for Lock and Slip (was two flat scalar fields per channel); `RuntimeCache`/`RuntimeStore` updated to
  match. Mechanics unchanged from the sibling project's pattern and reused directly: in-memory cache
  updated every qualifying frame (never touches disk), a background `Timer` flushes dirty-only every 5s
  (`RuntimeStore.DefaultFlushInterval`), atomic temp-file-plus-`File.Move` writes, a corrupt file
  degrades to empty rather than throwing, and `QAdvanceFeedback.End` does one final synchronous
  `Flush()`.
- **Load-on-switch**: not a separate code path — because the store is keyed, `ReliableWheelLockSlipEngine.Compute(sample, lockSources, slipSources, gameId, carId)` and `GForceSettings`'s existing
  per-key lookups simply read/write whichever key is current every frame; switching game or car changes
  which key the very next frame touches, with no explicit "load on switch" step needed or possible to
  forget.
- **Rename + import-once**: `QAdvanceFeedback.Parameters.json` (was `plugin.QAdvanceFeedback.runtime.json`).
  `RuntimeStore`'s constructor accepts a `legacyPath`; if the new file doesn't exist yet but the old one
  does, its flat global Lock/Slip peak+sample-count is imported and exposed via
  `TryGetLegacyLockSeed`/`TryGetLegacySlipSeed`. **Judgment call, flagged**: since the pre-per-car file
  has no game/car association at all, its value cannot be assigned to one specific car — instead it
  seeds `KeyedGripLearner.SeedLegacy`, which is used as the cold-start value for the **first brand-new**
  `(game, car)` key each channel encounters after the upgrade, rather than being discarded outright.
  Every subsequent key created afterwards gets the ordinary generic seed (`GripLearner.SeedPeakG`), not
  the legacy value repeatedly.
- **Tests**: `KeyedGripLearnerTests` covers isolation (game-A/car-1 vs game-A/car-2 vs game-B/car-1),
  switch-back restoration, read-without-creating, export/import round-trip, `SeedLegacy` (seeds a
  brand-new key, never overwrites an already-imported one), and reset. `ReliableWheelLockSlipEngineTests`
  adds three tests exercising the same isolation/restoration behaviour through the public `Compute` API
  with a single long-lived engine instance (matching how the plugin actually drives it).
  `RuntimeStoreTests` was rewritten for the keyed save/load API and gained dirty-only-flush,
  corrupt-file, and legacy-import (present/absent/already-migrated) tests.

## Functional change 2 — config file rename

`QAdvanceFeedback.config.json` (was `plugin.QAdvanceFeedback.config.json`). `ConfigStore.Load` gained an
optional `legacyPath` parameter (inserted as the **third** parameter, after `logWarning`, specifically so
every existing call site — including tests — keeps compiling unchanged); when the new file is absent and
the old one is present, the legacy file is loaded and **immediately saved back out under the new name**,
so the import happens exactly once and the plugin's own `Save` path is reused rather than a second write
routine. Tested: import-when-new-absent, ignored-once-new-exists, missing-both-degrades-to-defaults.

## Functional change 3 — restore-defaults buttons

- **`QAdvanceFeedbackSettings.CreateDefault()`/`RestoreDefaults()`** (new): the single internally-defined
  default object every "restore" path derives from — the class's own field initialisers already call
  `CreateDefault()`-equivalent construction, and `RestoreDefaults()` resets an existing instance in place
  (swaps in fresh `Lock`/`Slip`/`GForce`/`General` sub-objects) so the plugin's and settings UI's shared
  live reference stays valid.
- **"Restore all default settings" button**: added to `SettingsControl.xaml`, in a horizontal
  `StackPanel` beside Apply, **outside** the `TabControl` — visible/available from every tab by
  construction, not by four copies. Wired to a confirmation `MessageBox` (`Common.RestoreAllConfirm`,
  new localisation key, both languages) before calling `Settings.RestoreDefaults()` → `ApplySettings()`
  (persists immediately, same as the sibling project's own destructive "Reset all learned profiles"
  pattern) → `LoadFromSettings()` to refresh every visible field.
- **Apply saves globally**: verified, not changed — `SaveToSettings()` already reads/writes all four
  tabs' fields into the one shared `QAdvanceFeedbackSettings` object regardless of which tab is showing,
  confirmed by re-reading the existing code before touching anything.
- **Per-channel source reset**: already existed (`LockResetSources`/`SlipResetSources`, wired to
  `WheelChannelSettings.CreateLockDefaults()`/`CreateSlipDefaults()`) — verified, not duplicated.
- **Curve preset defaults**: verified against the brief's exact numbers and found already correct
  (`ProjectorSettings.ApplyPreset`): Lock Curve = start 20/end 100/30→10/60→30/80→80; Slip Curve =
  start 20/end 100/30→8/45→25/75→75; Linear = straight line from (Start,0) to (End,100) preserving
  Start/End. Already covered by `WheelChannelSettingsTests` — no test gap found, none added.
- **Localisation**: two new keys (`Common.RestoreAllDefaults`, `Common.RestoreAllConfirm`) added to both
  `StringTableEn.cs` (direct edit, pure ASCII) and `StringTableZhHans.cs`. The zh-Hans edit followed the
  standing rule exactly: raw UTF-8 Chinese text was written directly via the file-write tool into a
  throwaway source module (`qaf_zh_source_restore.py`, in system temp, never committed), then a
  **pure-ASCII** generator script (`qaf_apply_zh_generated.py`, explicit `\uXXXX` escapes only — verified
  by reading its own source back) applied the two entries to `StringTableZhHans.cs` via
  `io.open(..., encoding='utf-8')`. Verified afterwards: no `U+FFFD` replacement characters, and
  `LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set` passes (it iterates
  both tables' keys, so the new pair is covered automatically).

## GitHub readiness — files added

1. **`QAdvanceFeedback.sln`** — both projects, new GUIDs, same Debug/Release × Any CPU/x64/x86
   configuration matrix as the sibling's `.sln`. Verified: opens/builds cleanly via
   `MSBuild QAdvanceFeedback.sln -t:Restore,Build`.
2. **`tools/fetch-simhub-refs.sh`** — ported from the sibling's script, targeting **SimHub 9.11.22**
   (documented in the script's own header; every DLL in this repo's `lib\` matches that release
   byte-for-byte, confirmed by comparing file sizes against the sibling project's own `lib\`, which was
   fetched the same way). **One fix over the sibling's script**: its copy loop lists only six DLLs and
   omits `System.Windows.Interactivity`, even though that DLL is present in its own `lib\` and is one of
   the seven assemblies this brief explicitly names — this is a gap in the sibling script itself, not a
   difference in what it actually needs; this project's script copies all seven. `bash -n` verified
   syntactically valid.
3. **`.gitignore`** — `bin/`, `obj/`, `.vs/`, `*.user`, `lib/` (with a comment explaining the fetch
   script), plus scratch dirs (`.simhub-refs-work/`, `.claude/`, `.github/`).
4. **`LICENSE`** — MIT. The sibling project (`reliable-wheel-lock`) has **no LICENSE file at all**, so
   per the brief's own fallback instruction ("use MIT unless the sibling uses something else") MIT was
   applied here, plus a short non-affiliation note matching the README's own "References and copyright"
   section.
5. **`README.md`** — WHY (grounded in the raw-slip-cliff problem and the ShakeIt-reproduction design,
   including an explicit "did we consider reading ShakeIt's own values" section stating the
   already-answered decompilation finding verbatim so it cannot be re-litigated) → five-layer
   architecture + G-force channels + complete published property list (71 names) → HOW TO USE (custom
   ShakeIt effect requirement, 0–100 scale, per-rig-type bindings, 8-pad G-force mapping with the Hz
   convention) → CONFIGURATION (one subsection per tab, each setting's real-world impact) →
   SCREENSHOTS → Building from source → References and copyright → Known limitations/unverified.
6. **`README.zh-Hans.md`** — full Simplified Chinese translation, cross-linked both ways with
   `README.md`. Written via the same UTF-8-source-file + pure-Python-io.open pipeline as the string
   table (see Functional change 3 above), never through a bash heredoc. Verified: no `U+FFFD`, both
   cross-links present.
7. **`docs/images/*.png`** (4 files: `settings-wheel-lock.png`, `settings-wheel-slip.png`,
   `settings-gforce.png`, `settings-general.png`) — real renders of the actual `SettingsControl`,
   produced by a throwaway WPF harness built and run **outside the repo** (system temp, never
   committed): merges MahApps' `Styles/Fonts.xaml`, `Styles/Colors.xaml`,
   `Styles/Accents/{BaseLight,Blue}.xaml`, `Styles/Controls.xaml` and `Themes/NumericUpDown.xaml` (the
   exact dictionary names were confirmed by enumerating `MahApps.Metro.dll`'s embedded `.g.resources`
   directly, not guessed), reflectively injects a fresh `QAdvanceFeedbackSettings` into a freshly
   constructed plugin instance's private `_settings` field (no live `PluginManager`/SimHub needed),
   walks the real `TabControl` in the visual tree, and renders each tab via `RenderTargetBitmap`. All
   four PNGs were visually inspected after generation and show the real settings UI correctly
   (including the new "Restore all default settings" button).

## Learned parameters — confirmed behaviour

- **Keys on game AND car**: yes — `KeyedGripLearner.MakeKey(gameId, carId)` for Lock/Slip;
  `GForceMaxLearner.MakeKey(gameId, carId)` (pre-existing) for the G-force maxima. Switching either
  automatically starts using that combination's own key on the very next frame.
- **Background sync**: yes — `RuntimeStore`'s `Timer` flushes the in-memory cache to
  `QAdvanceFeedback.Parameters.json` every 5 seconds (`DefaultFlushInterval`), only when
  `RuntimeCache.SnapshotIfDirty()` returns non-null (an unchanged cache is never rewritten), off the
  timer callback thread via `Task.Run`, plus a final synchronous `Flush()` in `QAdvanceFeedback.End`.

## Restore-defaults buttons — what they cover

- **"Restore all default settings"** (beside Apply, outside the `TabControl`): every field on **all
  four tabs** — Wheel Lock and Wheel Slip (sources, script types, curve anchors/preset, pulse), G-Force
  (both max-G modes/values, all four sustain percentages, all three motion-feel constants, lateral
  direction), and General (both toggles) — via one shared default object. Gated behind a confirmation
  dialog (destructive, immediate, persists on confirm).
- **Per-channel "Reset to default"** (Wheel Lock tab, Wheel Slip tab, pre-existing): only that channel's
  four source fields and script types — leaves the curve, pulse, and every other tab untouched.

## Final on-disk file names

- Config: **`QAdvanceFeedback.config.json`** (was `plugin.QAdvanceFeedback.config.json`).
- Learned parameters: **`QAdvanceFeedback.Parameters.json`** (was `plugin.QAdvanceFeedback.runtime.json`).

Both resolved via `PluginManager.GetCommonStoragePath(...)` at `Init`, never a hard-coded path; both
import their respective old-named file exactly once if present, then never touch it again.

## Concerns / unverifiable / judgment calls

1. **Nothing in this task has been run inside a live SimHub session, against a real game, or on real
   hardware** — cannot be, per the task constraints. All 330 tests are pure, offline, hand-computed-
   expectation unit tests, plus real-temp-file tests for `ConfigStore`/`RuntimeStore`.
2. **The legacy-seed design for Functional change 1's file-rename import is a judgment call, stated
   plainly**: the old runtime file's single global peak/sample-count cannot be attributed to any
   specific (game, car) — it was never recorded against one. Seeding it as the cold-start value for the
   *first* brand-new key each channel meets, rather than the generic 1.0 seed, is the least-destructive
   reading of "import it once rather than discarding learned data" I could find; a stricter reading
   (discard it entirely once truly per-car keying exists) is a one-line change (`SeedLegacy` call removal
   in `QAdvanceFeedback.Init`) if that's not the intended behaviour.
3. **The screenshot harness's exact MahApps resource-dictionary paths** (`Styles/Controls.xaml`,
   `Themes/NumericUpDown.xaml`, etc.) were confirmed empirically by enumerating the actual
   `MahApps.Metro.dll` shipped in `lib\` (via a small reflection tool), not from any MahApps
   documentation — they are correct for **this specific DLL version** (matching SimHub 9.11.22) but are
   not guaranteed to be the right paths for a different MahApps release.
4. **MIT was chosen for LICENSE only because the sibling project has none** — if the actual intended
   license is something else, this is a one-file replacement.
5. **The "washout" G-force model credit in the README is described in general terms** ("adapted from
   the classical motion-cueing literature... borrows the idea, not a literal rig's filter
   implementation") rather than citing a single specific paper/book, because `docs/gforce-report.md`/
   `docs/wiring-ui-report.md` (this task's own inputs) do not name one specific source either — stated
   as an adaptation of a known technique, not attributed to a work I have not actually verified was the
   source.
6. **`tools/fetch-simhub-refs.sh` itself was not executed in this task** (this machine already has
   `lib\` populated, matching SimHub 9.11.22 byte-for-byte) — its logic was ported from the sibling's
   already-working script plus the one documented fix (`System.Windows.Interactivity`), and it passed
   `bash -n`, but a genuine "clean clone, no SimHub installed" run of it is unverified here.
7. **Every other item already flagged in `docs/layer123-report.md`, `docs/layer45-report.md`,
   `docs/gforce-report.md` and `docs/wiring-ui-report.md`** (the `LongitudinalG`/`LateralG` sign
   conventions, the per-wheel redistribution heuristic, the washout time constants' offline-only
   calibration, etc.) still apply unchanged — this task did not re-verify or re-litigate any of them.
