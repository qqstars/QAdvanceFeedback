# Settings screenshot re-styling (real SimHub/MahApps look, not default WPF chrome)

## STATUS: DONE

`docs\images\settings-wheel-lock.png` and `docs\images\settings-wheel-slip.png` are regenerated and
now render with real MahApps-based dark styling (dark backgrounds, blue GroupBox headers, styled
spinners/buttons/dropdowns) instead of default grey WPF/Aero chrome. Both images opened and visually
inspected (not just "harness exited 0") - see "Visual verification" below.

## Root cause

The prior ad-hoc harness created a bare `Application`/`Window` and rendered the real
`Settings\SettingsControl` directly, never merging any resource dictionary. `SettingsControl.xaml`'s
own styles (`<Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">` etc.) still
parsed and loaded fine in that state, because WPF resolves `{StaticResource {x:Type TextBox}}`
against the OS theme's own built-in generic dictionary (Aero/Aero2) when nothing else defines it -
that OS-theme fallback is exactly what "default WPF chrome" in the brief means. It is not a crash or
a missing-resource exception; it is a silently-wrong resource resolution.

## Investigation: the brief's named SimHub dictionaries do not exist in this assembly

The brief's "already found" section named `styles/simhubstyles.baml`, `themes/generic.baml`,
`themes/genericshtitledgroup.baml`, `themes/genericshtabcontrol.baml`, `themes/genericshtabitem.baml`,
and `themes/genericshlistbox.baml` as dictionaries to merge. I enumerated
`lib\SimHub.Plugins.dll` (version `1.0.9708.30848`, matching SimHub 9.11.22 per
`tools\fetch-simhub-refs.sh`) directly - `SimHub.Plugins.g.resources` contains exactly 218 embedded
BAML/resource entries, and **none of those six names are among them**. The only `styles/*.baml` entry
in the whole assembly is `styles/shmessagebox.baml`; the only `themes/genericsh*.baml` entries are
`genericshakeiteffectlistheader`, `genericshmetrowindow`, `genericshdaynighttoggle`,
`genericshsubtitledbox`, `genericscroll`, `genericshakeitlocationpreview`,
`genericshstickyheaderscrollviewer` - a different, unrelated set (default styles for SimHub's OWN
custom control types).

Cross-checked against `Settings\SettingsControl.xaml` itself: its only `xmlns` besides the standard
WPF/x namespaces is `xmlns:mah="clr-namespace:MahApps.Metro.Controls;assembly=MahApps.Metro"`, and the
only non-stock-WPF control types it uses anywhere are `mah:NumericUpDown` and `mah:ToggleSwitch` (grep
of every `mah:` usage, and of "SimHub" in the file, confirms this - no `SHTitledGroup` or any other
SimHub custom control appears). So even if those six SimHub dictionaries existed, this control
doesn't reference any type they would style. **Conclusion: no `SimHub.Plugins.dll` resource
dictionary needs to be (or can be) merged for this control** - its entire dark/styled look comes from
MahApps' own implicit type styles (`Controls.xaml` defines `<Style TargetType="{x:Type TextBox}">`
etc., which is exactly what `BasedOn="{StaticResource {x:Type TextBox}}"` picks up). This is a
correction of the brief's own "already found" section, based on direct verification, not a guess.

## What actually fixed it

Enumerated `lib\MahApps.Metro.dll` (version `1.5.0.23`) the same way to get real paths rather than
assume a version's layout. The dictionaries merged, in this order, into a live
`Application.Resources.MergedDictionaries` (all five load without error):

```
pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml
pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml
pack://application:,,,/MahApps.Metro;component/Styles/Colors.xaml
pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseDark.xaml
pack://application:,,,/MahApps.Metro;component/Styles/Accents/Blue.xaml
```

A `--dump-resources` diagnostic mode built into the harness (see below) confirmed, by reading the
*actual* merged `ResourceDictionary` keys/values at runtime rather than assuming names, that this
produces a genuinely dark palette:

| Key | Resolved color |
|---|---|
| `WhiteBrush` | `#FF252525` (dark grey - MahApps' historically confusing naming: this is the *background* brush, inverted per-theme) |
| `WindowBackgroundBrush` / `ControlBackgroundBrush` | `#FF252525` |
| `BlackBrush` / `TextBrush` / `LabelTextBrush` | `#FFFFFFFF` (white - the *foreground* brush) |
| `AccentColorBrush` | `#CC119EDA` (the blue accent, visible in the GroupBox headers) |

Two more things were required beyond just merging dictionaries:

1. **Host container.** The control is hosted in a real `MahApps.Metro.Controls.MetroWindow`
   (consistent with SimHub's own UI being MahApps-based) rather than a bare `Window`, so the
   MahApps window-level chrome styles have a correctly-typed host to apply to.
2. **Explicit background on the captured element.** `RenderTargetBitmap.Render(element)` only
   renders that element and its descendants - an ancestor `Window`'s themed `Background` does not
   "show through" for a captured subtree whose own root has no `Background` set (the `StackPanel`
   that is each tab's `ScrollViewer.Content` is `Background="Transparent"` by default). Without this,
   the GroupBoxes/controls would be correctly dark-styled but floating on a transparent/white PNG
   background - a half-styled result, which the brief explicitly said not to ship. Fixed by setting
   that root `Panel`'s `Background` to the resolved `WhiteBrush` (`#FF252525`) immediately before
   rendering - this is done in the **harness**, not in `SettingsControl.xaml`, so it doesn't touch
   the shipped control's own behaviour inside a real SimHub host (where the surrounding SimHub window
   already supplies that dark background).

## A second, unrelated standalone-hosting problem: `PluginManager`

`SettingsControl`'s constructor takes a live `SimHub.Plugins.PluginManager`. Its real parameterless
constructor throws `FileNotFoundException` for `WoteverCommon, Version=1.1.0.0` - a SimHub-internal
assembly not present in `lib\` and not part of this plugin's own reference set; it is only ever
supplied by a real running SimHub host process. Worked around with
`FormatterServices.GetUninitializedObject(typeof(PluginManager))`, which yields a correctly-typed
instance without running that constructor. Verified this is safe for this specific use: every call
`SettingsControl` makes through `PluginManager` goes through
`MotorsExportAvailabilityProvider.SafeGet`, which already wraps `pluginManager?.GetPropertyValue(...)`
in `try`/`catch` and degrades to "not available" on any failure (see that class's own remarks) - the
same "no live SimHub telemetry" state any offscreen harness is in regardless of this workaround.

## Visual verification (opened both PNGs, not just checked exit code / file size)

**`settings-wheel-lock.png`** - dark background throughout, blue-headed GroupBoxes (`TRIGGER
THRESHOLD`, `SOURCES`, `WHEEL COMBINATION`, `OUTPUT SHAPING`, `PULSE AT MAXIMUM`), styled
NumericUpDown spinners (dark field, +/- buttons), styled ComboBox/TextBox/Button. Full panel top to
bottom with nothing cropped: starts at `TRIGGER THRESHOLD`, ends at `PULSE AT MAXIMUM`, and the curve
editor's every anchor row (Start value, Slightly, Ideal, **Max Grip**, End value) is present with its
description text, plus the curve plot itself with all markers. **"Max Grip" is visible and correctly
spelled** (row reads "Max Grip: 80 [+/-] 80 [+/-] under default input value 80, curve will return 80").

**`settings-wheel-slip.png`** - same dark styling, same full top-to-bottom framing (`TRIGGER
THRESHOLD` through `PULSE AT MAXIMUM`), curve editor's **Max Grip** row present and correctly spelled.

(The curve-plot canvas itself keeps its own white/light background in both images - that is the
plot's own pre-existing, deliberate design for line/marker contrast, unrelated to and unchanged by
this task; only the surrounding chrome was ever unstyled.)

## SHA-256 (before -> after)

| File | Before | After |
|---|---|---|
| `docs\images\settings-wheel-lock.png` | `48a121182684a507454c40cf1da3059fb20903db9a81eaa29fd81699127e86ce` | `643f936911c7e4624450da77b75af82ba132cd3ff5e82f15cafe4220b1a40929` |
| `docs\images\settings-wheel-slip.png` | `c6bd20a237165a418dc4c654f9caf30123ed5821b46ec6d572487383b12df18d` | `a28802e1dcfac26100c6c649b97782dfceea249cabc572d89d54296d1b86d264` |

Both changed; both copied from the harness's own `screenshot-out\` folder directly into
`docs\images\`, not left stale in a temp/build folder.

## Reusable harness: `tools\screenshot-harness\`

Persisted in-repo (was previously an ad-hoc, out-of-repo scratch harness that no longer exists between
sessions - see the older `docs\screenshot-capture-rule.md` /
`docs\apply-dirty-and-screenshots-report.md` reports, which had to re-describe it each time). Now:

- `tools\screenshot-harness\ScreenshotHarness.csproj` - a `net48`/`UseWPF` console project. Its own
  header comment documents the `lib\*.dll` dependency (matching the convention
  `tools\fetch-simhub-refs.sh` already set) and the exact build/run commands.
- `tools\screenshot-harness\Program.cs` - merges the MahApps dictionaries above, builds the plugin +
  settings + `PluginManager` per the workarounds above, hosts `SettingsControl` in a `MetroWindow`,
  and captures all four tabs per the existing standing rule in `docs\architecture.md`'s "Settings
  screenshot capture rule" section (Wheel Lock/Wheel Slip/G-Force: selected tab's content only;
  General: whole control). Also has a `--dump-resources` diagnostic mode (lists every merged resource
  key/value) kept in for future MahApps-version-bump troubleshooting.
- **Deliberately NOT added to `QAdvanceFeedback.sln`** - build/run it directly:
  ```
  dotnet build QAdvanceFeedback.sln -c Release
  dotnet build tools\screenshot-harness\ScreenshotHarness.csproj -c Release
  tools\screenshot-harness\bin\Release\net48\ScreenshotHarness.exe
  ```
  Output PNGs land in that exe's own `screenshot-out\` folder; copy the ones you want into
  `docs\images\` (this run copied only the two files this task scoped).
- `docs\architecture.md`'s "Settings screenshot capture rule" section now points here instead of
  describing a throwaway/out-of-repo harness.

## Build / test health (re-verified after this change)

- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**.
- `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `.pdb` - single-DLL
  distribution intact.
- `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj -c Release --no-build`:
  **Passed! - Failed: 0, Passed: 856, Skipped: 0, Total: 856**.
- `QAdvanceFeedback.sln` itself was not edited - confirmed `tools\screenshot-harness` is not
  referenced anywhere in it.

## Concerns

1. **`settings-gforce.png` and `settings-general.png` are now inconsistent with the two regenerated
   images.** They were captured by the same old unstyled ad-hoc harness and still show default
   grey WPF chrome; this task's scope was explicitly limited to the wheel-lock/wheel-slip pair, so
   they were left untouched. The new harness renders all four tabs in one run (see its own
   `screenshot-out\settings-g-force.png` / `settings-general.png`) - regenerating those two, and
   copying them over, would remove the inconsistency in one more command whenever the owner wants it.
2. **The `PluginManager` workaround (`GetUninitializedObject`) is harness-only.** It produces an
   object whose fields are all default/zeroed, not a functioning `PluginManager` - fine for this
   screenshot-rendering purpose (every call through it is already exception-safe and degrades to "not
   available"), but it must not be copied into any other tooling that expects a real, working
   `PluginManager`.
3. **MahApps dictionary versions are pinned to the copy in `lib\`** (`1.5.0.23`, matching SimHub
   9.11.22). A future `tools\fetch-simhub-refs.sh` run against a different SimHub version could ship a
   different MahApps version whose resource paths/keys have moved; the harness's `--dump-resources`
   mode is there specifically to re-verify this quickly if screenshots start looking wrong again after
   a version bump.
