# Settings screenshot capture rule change, and regeneration of all four screenshots

## STATUS: DONE

## The rule (now standing, for every future regeneration)

Per-tab, split by whether the tab is tall enough to need the extra room:

- **Wheel Lock, Wheel Slip, G-Force** (tall tabs) - capture ONLY the selected `TabItem`'s content
  (the `ScrollViewer`'s content element inside it), EXCLUDING the tab strip above and the
  Apply/Restore button row below. These tabs are tall; the surrounding chrome wastes vertical space
  and used to force clipping. Content-only gets the whole tab's settings into one image.
- **General** (short tab) - capture the FULL `SettingsControl` instead: tab strip, the General
  tab's content, AND the Apply/Restore row. Nothing is lost by including the chrome because this
  tab is short.

Both cases keep the pre-existing "measure at own full natural extent" technique (`Measure` with
`height = PositiveInfinity`, then an explicit `Arrange` at the resulting `DesiredSize`) rather than
accepting whatever height the hosting preview window imposes - this is what previously fixed
clipping on the tall tabs, and it turned out to matter for General too (see "Bug found and fixed"
below). The change here is WHAT element gets rendered, not how its height is measured.

**Durable home for this rule:** a new "Settings screenshot capture rule (standing rule)" section in
`docs\architecture.md` (added directly above the pre-existing "Where 'Private' used to be" section),
plus this file. `architecture.md` carries the compact statement of the rule plus a pointer to this
file for full detail/evidence, so a future regeneration (by me or anyone else) finds the rule from
the architecture map without being told again.

## Why this was needed

The Apply/Restore button row is a `DockPanel.Dock="Bottom"` `StackPanel` sibling of `MainTabs` (the
`TabControl`) in `QAdvanceFeedback\Settings\SettingsControl.xaml` - it lives OUTSIDE the
`TabControl` entirely. A prior pass's per-tab-content capture (rendering each tab's `ScrollViewer`
content directly, to defeat the ScrollViewer viewport's clipping on tall tabs) therefore never
included that row for ANY tab, General included - General's own screenshot was landing at a mere
**1024x215**, showing only the `GENERAL` group box content, with no tab strip and no buttons. The
owner's new rule intentionally keeps that content-only framing for the three tall tabs (it is
exactly what makes them fit in one untruncated image) but explicitly asks for the full control,
chrome included, on General specifically, since General has nothing to lose from the extra chrome
and gains the button row for context.

## Implementation

`...\scratchpad\qpreview\Program.cs` (the harness; stays outside the repo per the harness rule) now
branches per tab instead of applying one rule to all four:

- Detects the General tab via `TabItem.Name == "GeneralTab"` (the `x:Name` in the XAML), not the
  display header text, so a future copy-edit of the visible "General" label wouldn't silently break
  tab detection.
- For the three content tabs: unchanged from the prior pass - `FindScrollViewer(control)` locates
  the selected tab's `ScrollViewer`, then its `Content` `FrameworkElement` is measured/arranged at
  its own full extent and rendered directly. If no `ScrollViewer`/content is found, the harness now
  throws (`InvalidOperationException`) instead of silently falling back to a clipped or
  chrome-included capture - the "STOP and say so" requirement from the task. This path was not
  actually exercised as a failure in this run; every tab has its `ScrollViewer` as before.
- For General: renders the whole `control` (the `SettingsControl` root), but with an explicit
  `control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height))` before rendering.

## Bug found and fixed along the way

The first attempt at the General path used the same `h = Math.Max(control.DesiredSize.Height,
control.ActualHeight)` fallback the old code already had lying around (dead code before this change,
since nothing ever hit it - every tab used to take the ScrollViewer-content branch). That produced a
**1024x1421** image with the tab strip and General's content correctly at the top, but the
Apply/Restore row pinned to the very bottom of a huge blank gap - because `control.ActualHeight`
reflects the harness's preview `Window`'s explicit `Height="3000"` (sized generously for the tall
tabs), and `DockPanel`'s fill child (`MainTabs`) gets Arranged to fill that whole oversized slot at
Arrange time, even though `DesiredSize` (Measure time) was compact. Re-measuring with
`PositiveInfinity` alone does not undo an Arrange the parent `Window` already performed. Fixed by
explicitly re-`Arrange`-ing `control` at its own `DesiredSize` (the same "pin to natural extent"
technique the per-tab-content path already used, just applied to the whole control) - this collapsed
the dead space and put the button row directly under the content. Final General image: **1024x305**.
Caught by actually viewing the rendered PNG, not by assuming the measured height was correct -
exactly the kind of thing that would have shipped a bad screenshot if only file existence/size were
checked.

## Verification (viewed every image, not assumed)

All four PNGs were opened and visually inspected after generation.

- **`settings-wheel-lock.png`** - starts at the `TRIGGER THRESHOLD` group (first group on the tab),
  ends at `PULSE AT MAXIMUM` (last group). No tab strip, no Apply/Restore row.
- **`settings-wheel-slip.png`** - starts at `TRIGGER THRESHOLD`, ends at `PULSE AT MAXIMUM`. No tab
  strip, no Apply/Restore row.
- **`settings-gforce.png`** - starts at `MAXIMUM G`, ends at `WHEEL LOCK/SLIP SHAKE` (the
  Integrate-shake group, including its three now-enabled sub-fields). No tab strip, no Apply/Restore
  row.
- **`settings-general.png`** - shows the tab strip (`Wheel Lock | Wheel Slip | G-Force | General`,
  General highlighted as selected) at the top, the `GENERAL` group content in the middle, and the
  `APPLY` / `RESTORE ALL DEFAULT SETTINGS` buttons directly beneath it with no dead space.

## Final pixel dimensions

| File | Dimensions |
|---|---|
| `docs\images\settings-wheel-lock.png` | **1024 x 1749** |
| `docs\images\settings-wheel-slip.png` | **1024 x 1793** |
| `docs\images\settings-gforce.png` | **1024 x 1116** |
| `docs\images\settings-general.png` | **1024 x 305** |

The three content-only tabs are byte-for-byte/pixel-for-pixel identical to the prior pass's output
(same rendering path, unchanged) - only `settings-general.png` changed (was 1024x215, content-only;
is now 1024x305, full control with chrome).

## Filename rename

The harness derives each output filename from the tab's header text
(`"settings-" + header.Replace(' ', '-').ToLowerInvariant()`), so the G-Force tab's own output file
is `settings-g-force.png`. Both READMEs link `docs/images/settings-gforce.png` (no hyphen between
"g" and "force"). Copied `settings-g-force.png` from the harness's `out\` folder to
`docs\images\settings-gforce.png` (renaming, not just copying) - confirmed the exact spelling matches
what both READMEs already reference, so no README link edits were needed for the filename itself.

## READMEs

Checked both `README.md` and `README.zh-Hans.md`'s "Screenshots"/"截图" sections. Neither caption
("The Wheel Lock tab" / "车轮抱死标签页" etc., or the alt text) claimed the images showed the tab
strip or Apply button for the three content-only tabs - the existing wording was generic enough to
still read correctly under the new framing, so no caption rewrite was strictly required. Added one
clarifying sentence to each README's existing italic intro note (the one already explaining the
light MahApps theme) spelling out the framing difference between the three content-only tabs and
General, so a reader isn't left wondering why General's screenshot looks structurally different
(tab strip + buttons visible) from the other three:

> "Wheel Lock, Wheel Slip and G-Force are tall tabs, so their screenshots show only that tab's own
> settings (no tab strip, no Apply/Restore row) so the whole tab fits in one image; General is short
> enough that its screenshot instead shows the full control, tab strip and Apply/Restore row
> included."

(Chinese equivalent added to `README.zh-Hans.md`'s matching italic note.) Both READMEs still read
sensibly - no other screenshot-adjacent text needed changes.

## Build / tests (unchanged, as required)

No plugin source file was touched - only the out-of-repo harness (`...\scratchpad\qpreview\Program.cs`)
and this repo's docs (`docs\architecture.md`, `docs\screenshot-capture-rule.md`, `README.md`,
`README.zh-Hans.md`). Confirmed anyway, on the real solution:

- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**. Two projects built:
  `QAdvanceFeedback.Tests -> ...\net8.0\QAdvanceFeedback.Tests.dll` and
  `QAdvanceFeedback -> ...\net48\QAdvanceFeedback.dll` (the one shipped plugin DLL - the test project
  is a separate net8.0 test assembly, not a second copy of the plugin).
- `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj -c Release --no-build`:
  **Passed! - Failed: 0, Passed: 711, Skipped: 0, Total: 711**.

## Concerns

1. **General-tab detection is by `x:Name`, not position.** Robust to header text changes, but if a
   future refactor renames the `TabItem` itself away from `x:Name="GeneralTab"` without updating the
   harness, the harness would silently fall through to the "content-only" branch for General too
   (since `isGeneralTab` would be `false`) rather than erroring - it would then hit the ScrollViewer
   content path, which still works, so the failure mode is "General quietly loses its chrome again,"
   not a crash. Worth a quick sanity check on `Program.cs`'s tab-name constant if `SettingsControl.xaml`
   is ever restructured.
2. **Harness location confirmed out-of-repo.** `...\scratchpad\qpreview\` is a temp directory outside
   `C:\Development\Repos\Samples\simhub\QAdvanceFeedback`, so nothing from the harness (source,
   `bin`/`obj`, or the four working PNGs it wrote to its own `out\`) was added to the repo - only the
   four final PNGs were copied into `docs\images\`.
3. No git operations were performed (repo is not under Git per the environment info anyway), no
   recursive deletes were run near the project, and no plugin behaviour was touched - this was a
   tooling/documentation-only change, as scoped.
