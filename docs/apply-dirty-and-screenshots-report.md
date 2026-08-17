# Apply-button dirty tracking + screenshot harness fix

## STATUS: DONE

Both changes implemented, built, and verified. 0 warnings, single-DLL, all tests green.

## Test counts

- **Full (with `Private\` present, this clone):** 589 passed / 589 total (was 581; +8 new
  `ApplyDirtyStateTests`).
- **Clean-clone (no `Private\`):** expected 544 (was 536; +8). Not independently re-verified by
  physically removing `Private\` in this run (that would require a directory move/delete near the
  project, which the task's own absolute rules caution against doing casually) - inferred instead
  from the fact that `ApplyDirtyState.cs` and its test file have no dependency on anything under
  `Private\` and are unconditionally `<Compile Include>`d (no `Exists()` guard needed, since they
  aren't withheld code), so they add exactly +8 to whichever baseline currently builds.

## Build health

- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**.
- `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `.pdb` - single-DLL
  distribution intact (every SimHub/MahApps/Newtonsoft reference stays `<Private>false</Private>`,
  unchanged).

---

## 1. Apply-button dirty tracking

### Design

A new tiny, SimHub/WPF-free class, `QAdvanceFeedback\Settings\ApplyDirtyState.cs`, owns the flag:

- `IsDirty` (bool, read-only outside the class).
- `MarkDirty()` - called by every control's change handler; a no-op while loading.
- `MarkClean()` - called after a successful Apply (and after Restore, see below).
- `BeginLoading()` - returns an `IDisposable` "loading scope". It is a **re-entrant depth counter**,
  not a bool, because `SettingsControl.LoadFromSettings` wraps its whole body in one scope but calls
  into helpers (`LoadAnchorControls`, `SetSourceModeCombo`, `SyncPresetCombo`,
  `RefreshGForceModeControls`, `RefreshGForceShakeControls`) that each open/close their *own* nested
  scope. With a plain bool the first inner call to finish would clear the flag while the outer load
  still had more assignments left to make, letting those remaining assignments falsely mark dirty.
  The `ApplyDirtyStateTests.Nested_BeginLoading_does_not_end_the_outer_scope_early` test is exactly
  this scenario.

`SettingsControl.xaml.cs` holds one instance (`_dirty`), and exposes two private wrappers,
`MarkDirty()`/`MarkClean()`, that also flip `ApplyButton.IsEnabled` - the single centralised path
every handler calls, per the brief's own ask.

### Every control wired (enumerated, not sampled)

| Control type | Count | How it reaches `MarkDirty()` |
|---|---:|---|
| `mah:NumericUpDown` (plain spinners: brake/sensibility/aggregation weights/pulse gap+min/GForce fixed maxima/sustain%/motion tau+gain/shake freq+scales) | 29 | `WireDirtyTracking()` - `ValueChanged` |
| `mah:NumericUpDown` (curve anchor spinners: Start/Slightly/Moderate/Critical/End raw+output, both channels) | 16 | `OnAnchorRawChanged`/`OnAnchorOutputChanged` (existing handlers, `WireAnchorEvents`) |
| `TextBox` (4 source fields × 2 channels) | 8 | `WireDirtyTracking()` - `TextChanged` (also covers the property-picker and script-editor buttons, since both write into this same TextBox) |
| `CheckBox` (Pulse enable ×2, Enable diagnostics, Export CSV) | 4 | `WireDirtyTracking()` - `Checked`/`Unchecked` |
| `ComboBox` (Lock/Slip source mode) | 2 | `ApplySourceDefaultsForMode`, reached via `OnSourceModeChanged` |
| `ComboBox` (Lock/Slip curve preset) | 2 | the existing `SelectionChanged` lambda in `WireAnchorEvents` |
| `ComboBox` (GForce accel/decel mode, lateral direction) | 3 | constructor, alongside `RefreshGForceModeControls()` |
| `mah:ToggleSwitch` (GForce shake enable) | 1 | constructor, alongside `RefreshGForceShakeControls()` |
| Script-type toggle buttons ("P"/"J"/"N") | 8 | `ScriptTypeToggle_Click`, and `EditInto` (explicit call - see below) |
| Per-source "Reset to default" buttons | 2 | `ApplySourceDefaultsForMode` (explicit call, unconditional - only ever reached from a user action) |

Two edge cases needed an **explicit** `MarkDirty()` call rather than relying purely on
TextChanged/ValueChanged, because WPF's dependency-property setters no-op when the new value equals
the old one:
- `EditInto` (script editor round-trip): if the edit changes only the script dialect and the
  expression text comes back identical, `TextChanged` never fires.
- `ApplySourceDefaultsForMode` (mode switch / per-source reset): if the mode being applied already
  matches what's showing, none of the four TextBoxes actually change.

### Loading vs. editing

`LoadFromSettings()`'s entire body now runs inside one `_dirty.BeginLoading()` scope.
`RefreshGForceModeControls()`/`RefreshGForceShakeControls()` are self-guarded the same way (they are
called both from genuine edits - preceded by an explicit `MarkDirty()` - and from pure reload paths).
The old scattered `_isUpdatingUi` bool (used for the same anti-cascade purpose already, just not
exposed as dirty tracking) was replaced entirely by `_dirty.IsLoading`/`_dirty.BeginLoading()`.

### "Restore all default settings" - resolved against actual behaviour, not the brief's assumption

The brief asked for Restore to leave Apply **enabled** ("the restore is not persisted until Apply").
Checking the actual code (`RestoreAllDefaults`, and its own pre-existing doc comment) shows this is
not true: it calls `_plugin.ApplySettings()`, which calls `ConfigStore.Save(...)` and persists to disk
immediately - the same doc comment already said so before this task ("Destructive and immediate (it
also persists, exactly like the ordinary Apply flow)"). So by the time `RestoreAllDefaults` returns,
the restored defaults are already on disk; there is nothing left for a subsequent Apply click to do.
Per the brief's own escape hatch ("unless you determine restore already saves... match the actual
behaviour rather than assuming"), this leaves Apply **disabled** after Restore, exactly like a normal
Apply click - not enabled. This is a deliberate deviation from the brief's stated test list ("restore
enables it"); flagging it here rather than silently picking one.

### Unit-testable seam

`ApplyDirtyState` itself is unit tested exhaustively (`QAdvanceFeedback.Tests\ApplyDirtyStateTests.cs`,
8 tests: fresh-not-dirty, MarkDirty, MarkClean, suppressed-while-loading, dirty-after-loading-ends,
the nested-scope reentrancy case, `IsLoading` reflecting open scopes, MarkClean-always-wins). It has no
WPF/SimHub reference, so it link-compiles into the SimHub-free Tests project like every other Settings
file there.

The **wiring** in `SettingsControl.xaml.cs` (which control's event calls `MarkDirty()`) is code-behind
on a net48 WPF `UserControl`; the Tests project is net8.0 with no WPF and deliberately no project
reference to the plugin assembly (link-compiles individual SimHub-free files only, by explicit
convention already documented in its own `.csproj`). Instantiating `SettingsControl` from xunit would
require either switching the Tests project's TFM or adding a `ProjectReference` that pulls in
SimHub/MahApps - out of scope for this task and a bigger structural change than the ask warranted. So:
**that part is not xunit-testable without a larger restructuring, and is not being claimed as such.**
It was instead verified by:
1. Full enumeration above (every named input control in the XAML accounted for).
2. A throwaway, out-of-repo WPF harness check (`qpreview\VerifyDirty.cs`, run with `VERIFY_DIRTY=1`)
   that instantiates the real `SettingsControl`, drives a real control on each of the four tabs, and
   asserts on the real `ApplyButton.IsEnabled`. All 11 checks passed:
   ```
   PASS: fresh load leaves Apply disabled
   PASS: Wheel Lock tab: NumericUpDown change enables Apply
   PASS: Apply click disables Apply again
   PASS: Wheel Slip tab: TextBox change enables Apply
   PASS: Apply disables again after Wheel Slip edit
   PASS: G-Force tab: ComboBox change enables Apply
   PASS: Apply disables again after G-Force edit
   PASS: General tab: CheckBox change enables Apply
   PASS: Apply disables again after General edit
   PASS: second fresh instance also starts with Apply disabled
   PASS: (setup) control is dirty before Restore
   ```
   (Restore's own confirmation `MessageBox.Show` is modal and blocks a headless run with no "click
   Yes" automation available here, so that one path is verified by code reading only - see above -
   not by this harness.)

### Mutation evidence

Temporarily changed `ApplyDirtyState.MarkDirty()` to ignore `IsLoading` (i.e., the load path now marks
dirty). Result: **2 of 8 `ApplyDirtyStateTests` failed** (`MarkDirty_while_loading_is_suppressed`,
`Nested_BeginLoading_does_not_end_the_outer_scope_early`), confirming the tests actually exercise the
guard. Reverted immediately afterward; confirmed 589/589 green again.

---

## 2. Screenshot harness fix

### Approach used: (a) - render the ScrollViewer's content at full extent

Chosen over the vertical-strip-stitching alternative because it produces one seamless image with no
stitching-boundary/duplicate-band risk to get wrong.

In the harness (`qpreview\Program.cs`, kept outside the repo per instructions), for each tab:
1. Find that tab's `ScrollViewer` via the existing (previously unused) `FindScrollViewer` helper.
2. Take its `Content` (the `StackPanel`) and `Measure` it with `(actualWidth, PositiveInfinity)` -
   its true, un-clipped height.
3. `Arrange` that content at `(actualWidth, measuredHeight)` and `UpdateLayout()`.
4. Render **that content element**, not the ScrollViewer, into the `RenderTargetBitmap` - bypassing
   the viewport clip entirely.

Verified the bottom-most controls are actually present (not just a plausible pixel count) by reading
back each rendered PNG: Wheel Lock/Wheel Slip both show the "Pulse at Maximum" group (gap/min-value
spinners) at the very bottom; G-Force shows the full "Wheel Lock/Slip Shake" group including all three
shake spinners; General shows both checkboxes with their full note text.

### Regenerated screenshots - final pixel dimensions

| File | Dimensions |
|---|---|
| `docs\images\settings-wheel-lock.png` | **1024 × 1749** |
| `docs\images\settings-wheel-slip.png` | **1024 × 1793** |
| `docs\images\settings-gforce.png` | **1024 × 1116** (harness itself writes `settings-g-force.png`; renamed on copy to match the README's actual link, `settings-gforce.png`) |
| `docs\images\settings-general.png` | **1024 × 215** |

### README check

Both `README.md` and `README.zh-Hans.md` use the images as plain, sequential
`![alt](docs/images/...)` blocks with no width/height attributes and no side-by-side/grid layout that
assumes a particular aspect ratio - they read sensibly with the much taller images. No README changes
needed.

---

## Concerns / things worth the owner's attention

1. **Restore-all-defaults dirty-state deviates from the brief's stated test list.** The brief said
   "restore enables it"; actual code has Restore persist immediately (pre-existing behaviour, not
   changed by this task), so it now leaves Apply **disabled**, matching what actually happened rather
   than the brief's assumption. If the owner instead wants Restore to stop persisting immediately (so
   the driver can still Cancel-by-not-clicking-Apply after seeing the restored defaults), that is a
   separate, larger behavioural change to `RestoreAllDefaults`/`ApplySettings` not attempted here, since
   it wasn't asked for and isn't a pure dirty-tracking change.
2. **The control-wiring correctness itself (as opposed to the underlying flag class) is not covered by
   the permanent xunit suite** - see "Unit-testable seam" above. If the owner wants a maintained
   regression test for that (as opposed to the throwaway harness check done here), it would need either
   a net48/WPF-capable test project or a `ProjectReference` from the existing Tests project, both larger
   structural changes than this task's scope.
3. Clean-clone test count (544) is inferred, not independently re-run, to avoid touching/removing the
   `Private\` folder near the project.
