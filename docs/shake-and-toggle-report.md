# QAdvanceFeedback - ShakeIt source-mode toggle fix + G-Force wheel lock/slip shake

## STATUS

Complete. Both features implemented, tested, and documented; both owner-confirmed decisions
(Manual mode's per-source reset default, and the Lock/Slip "larger wins" combination rule) are
implemented as settled requirements, not provisional.

## Test counts

- **Full working copy (both `Private\` folders present): 427/427 passing**
  (`dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj -v q`). Baseline before this
  task was 396; this task added 31 new tests (`GForceShakeTests`, `GForceEngineShakeTests`,
  `GForceSettingsTests` shake additions, `WheelChannelSettingsTests` ShakeIt-default/reset-mode
  additions, `ConfigStoreTests`/`QAdvanceFeedbackSettingsTests` shake-persistence additions) and
  renamed/retargeted several pre-existing default-value assertions rather than adding new tests for
  them.
- **Clean-clone (both `QAdvanceFeedback\Private\*.cs` and `QAdvanceFeedback.Tests\Private\*.cs`
  removed, leaving only `.gitignore`/`README.md` in each): 407/407 passing** (427 - 20, the same 20
  tests that live only in the two now-absent `Private\` test files, unaffected by this task).

## Build / 0-warning / single-DLL confirmation

- Full working copy: `MSBuild QAdvanceFeedback\QAdvanceFeedback.csproj -t:Rebuild -p:Configuration=Release -v:normal`
  -> **0 Warning(s), 0 Error(s)**; `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll`
  (177,152 bytes) + `.pdb`.
- Clean-clone scratch copy (files copied individually, excluding `obj`/`bin`, then the five
  `Private\*.cs` files removed by explicit path - never a recursive delete): same MSBuild command ->
  **0 Warning(s), 0 Error(s)**; the `csc.exe` compile-file list was inspected directly in the build
  log and confirmed the five `Private\*.cs` files were genuinely absent from the invocation.
  `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` (174,592 bytes) + `.pdb`.
- Whole solution (`dotnet build QAdvanceFeedback.sln`), both the real working copy and the
  clean-clone copy: **0 Warning(s), 0 Error(s)**.
- Re-verified green (build + full test suite, both copies, including a FRESH clean-clone snapshot
  taken after the coordinator's mid-task ToggleSwitch correction) a final time at the very end, after
  all mutations had been applied-and-reverted and all documentation/screenshot work was done.
- The clean-clone scratch copy's every FILE was removed individually by explicit path afterward
  (`rm -f` per path, never `rm -r`/`find -exec rm`/any recursive form). Its empty directory
  skeleton (~76 now-empty folders) could **not** be removed: this environment's own safety hook
  blocks every `rmdir` invocation outright - even a single, non-recursive `rmdir` on one already-empty
  directory - as "recursive directory deletion", regardless of flags or argument count. This is
  flagged as a genuine, environment-imposed limitation, not a shortcut taken: no recursive delete was
  attempted or performed anywhere, and the leftover empty folders contain no files and live entirely
  inside the designated system-temp scratchpad, never inside either project.

---

## FEATURE 1 - source mode toggle diagnosis and fix

### Why the toggle was invisible (diagnosed, confirmed by reading the code, not guessed)

It was **missing because it was hidden**, not rendered somewhere unexpected. `SettingsControl.xaml.cs`
resolved `_lockShakeItAvailable`/`_slipShakeItAvailable` once at construction
(`ShakeItSourceProvider.IsLockAvailable`/`IsSlipAvailable`, backed by
`ShakeItAvailabilityResolver.IsAvailable` - which requires ALL FOUR of the channel's expected ShakeIt
properties to already be registered and numeric) and then set, in `LocalizeStaticText`:

```csharp
LockSourceModePanel.Visibility = _lockShakeItAvailable ? Visibility.Visible : Visibility.Collapsed;
LockShakeItSetupNote.Visibility = _lockShakeItAvailable ? Visibility.Visible : Visibility.Collapsed;
```

i.e. the ENTIRE toggle (label, combo box, both mode-dependent notes) was `Collapsed` outright on any
system where SimHub was not ALREADY reporting the ShakeIt export - which is exactly the state of
every driver who has not yet followed the setup guide, i.e. everyone the first time they open the
settings dialog. This is precisely backwards: the toggle is what a driver needs in order to be told
*how* to set ShakeIt up in the first place, so hiding it until the export already exists made it
permanently invisible for its own primary purpose. This confirms the brief's own suspicion exactly -
the condition gating visibility was wrong and has been changed.

### What was changed

- The mode combo (`LockSourceModePanel`/`SlipSourceModePanel`) is now **always** `Visible` - the two
  `Visibility = availability ? ...` lines that used to hide it were deleted outright.
  `_lockShakeItAvailable`/`_slipShakeItAvailable` are still computed (once, at construction, same as
  before) but now drive only ONE thing: whether an inline "not published yet" note is shown - never
  whether the toggle itself renders.
- XAML reordered so the mode toggle (`LockSourceModePanel`/`SlipSourceModePanel`) is the FIRST child
  of each channel's Sources `GroupBox`, i.e. literally at the top of the section, per the brief.
- Toggle label text changed from "Use ShakeIt Motors output" to the brief's required
  **"ShakeIt Plugin Output Properties"** (`Sources.Mode.ShakeIt` key, both languages); "Manual" is
  unchanged.
- New `RefreshSourceModeUi(bool isLock)` (`SettingsControl.xaml.cs`) drives three mode-dependent
  notes from whatever the combo currently shows, called at construction, on every mode change
  (`OnSourceModeChanged`/`ApplySourceDefaultsForMode`), and after loading settings:
  - **ShakeIt mode selected:** shows the concise step-by-step setup guide
    (`Sources.ShakeItSetup.Lock`/`.Slip` - enable ShakeIt Motors, create a profile, add the
    "Wheels lock"/"Wheels slip" effect, tick "Use legacy IRacing algorythm", tick "Export output
    value as a property" with `WheelLock.IRacing`/`WheelSlip.IRacing`, tick "Disable output"). If
    `_lockShakeItAvailable`/`_slipShakeItAvailable` is currently false, an ADDITIONAL inline note
    (`Sources.ShakeItUnavailable.Note`, reusing the control that used to hold an unrelated generic
    tip) explains SimHub is not reporting the four properties yet and that this channel automatically
    falls back to its own Raw values in the meantime (rewritten from the old wording, which wrongly
    said the channel "stays on Manual" - it does not; it stays in ShakeIt mode and falls back
    per-source at the resolver level).
  - **Manual mode selected:** both ShakeIt notes are hidden; a new note
    (`Sources.Mode.ManualNote.Lock`/`.Slip`) is shown instead: "point each wheel's Source field at
    any SimHub property that reports a 0-100 value for that wheel - 0 means the wheel isn't
    locking/slipping at all, 100 means it's fully locked/spinning" (an improved rewording of the
    brief's own suggested text).

### Default changed globally from Manual to ShakeIt

Per the brief's explicit instruction, `WheelChannelSettings.SourceMode`'s default changed from
`Manual` to `ShakeIt`, and `CreateLockDefaults`/`CreateSlipDefaults` (which back both the fresh-install
shipped defaults AND the global "Restore all default settings" button, via
`QAdvanceFeedbackSettings.CreateDefault`) now call `ApplyShakeItDefaults` instead of
`ResetSourcesToDefault`. Persisted (already an existing, additive JSON field - no migration needed)
and covered by the global restore (verified by a dedicated `RestoreDefaults` test asserting the four
new shake settings AND `SourceMode` all land back on their shipped defaults).

### Manual mode's per-source reset default: `Raw.<wheel>` - owner-confirmed, not provisional

**`QAdvanceFeedback.WheelLock.Raw.<Wheel>` / `QAdvanceFeedback.WheelSlip.Raw.<Wheel>`** (Layer 3's own
output) - the coordinator raised this with the owner before implementation per the brief's explicit
instruction, and the owner confirmed `Raw`, not `Projected`. The circularity concern was correct:
`Projected` is Layer 5's OUTPUT and these Source fields are Layer 4's INPUT, so defaulting to
`Projected` would create Layer 5 -> Layer 4 -> Layer 5. `WheelChannelSettings.ResetSourcesToDefault`
(unchanged from before this task) already implemented `Raw` correctly - the actual bug fixed here was
elsewhere (see below), not in which property this method points at.

### THE PER-SOURCE RESET BUG (found and fixed): it used to ignore the current mode

Before this task, the per-channel "Reset to default" button (`ResetSourcesToDefault` in
`SettingsControl.xaml.cs`) unconditionally rebuilt the four fields from
`WheelChannelSettings.CreateLockDefaults()`/`CreateSlipDefaults()` and then forced
`SetSourceModeCombo(isLock, SourceMode.Manual)` - i.e. clicking Reset while in ShakeIt mode silently
switched the channel to Manual. Fixed:

- New model method `WheelChannelSettings.ResetSourcesForCurrentMode(bool isLockChannel)`: dispatches
  to `ApplyShakeItDefaults` (if `SourceMode == ShakeIt`) or `ResetSourcesToDefault` (otherwise),
  **never changing `SourceMode` itself** - both branches' own `SourceMode = ...` assignment is simply
  a no-op re-assertion of whatever was already selected.
- `SettingsControl.xaml.cs`'s `ResetSourcesToDefault(bool isLock)` and `OnSourceModeChanged(bool isLock)`
  were unified into one shared `ApplySourceDefaultsForMode(bool isLock, SourceMode mode)`: it builds a
  scratch `WheelChannelSettings`, sets its `SourceMode` to whatever mode is relevant (the combo's
  CURRENT selection for Reset, or the NEWLY picked selection for a mode change), calls
  `ResetSourcesForCurrentMode`, and copies the resulting Source/ScriptType fields into the four text
  boxes - so the UI layer never re-implements the mode-dispatch logic itself, only tested Core logic
  does.
- Unit-tested directly in Core (no WPF needed): `ResetSourcesForCurrentMode_in_ShakeIt_mode_...`,
  `ResetSourcesForCurrentMode_in_Manual_mode_...`, and
  `ResetSourcesForCurrentMode_never_switches_the_mode_itself_either_way` (the last one is exactly the
  mutation-evidence test - see below).

---

## FEATURE 2 - G-Force "Integrate Wheel Lock and Slip" shake

### New pure math class: `Core\GForce\GForceShake.cs`

`GForceShake.Apply(gForceValue0100, wheelContribution, frequencyHz, phaseSeconds, out left, out right)` -
stateless, SimHub-free, fully unit-tested. Implements the owner's formula
(`band = G * (wheel/100) * scale`, `half = band/2`, `output_L/R = effectiveCentre ± half*sin(2*pi*f*t)`)
with clamp-by-SHIFTING: `effectiveCentre = Clamp(centre, half, 100-half)` when `half <= 50` (band fits
inside 0-100 somewhere), which reproduces all three required cases (no-op, shift-down, shift-up) in
one formula; only when `half > 50` (band wider than the whole 0-100 range) does it fall back to a
fixed centre of 50 with the final output squashed to `[0,100]`, per the owner's own explicit exception.

### Verified against the owner's worked examples (unit tests, exact arithmetic)

1. G=40, wheel=30, scale=1.0 -> band 12, range 34..46, centre 40. **Passes** (no clamp needed).
2. G=80, wheel=60, scale=1.0 -> band 48, raw range 56..104, shifted to 52..100, centre 76.
   **Passes** (single-sided shift).
3. G=60, wheel=90, scale=3.0 -> band 162 (half 81 > 50) -> both ends out, capped to 0..100, centre 50.
   **Passes** (squash fallback).

**Flagging the arithmetic inconsistency in the owner's message, as instructed:** the owner's own third
worked example text ("G=40, wheel=90, scale=1.5 ... 40 * 0.9 * 2.5 = 90") uses a scale of 2.5 where
the stated scale is 1.5 - `40 * 0.9 * 1.5 = 54`, not `90` (`90` requires `2.5`, not `1.5`). This is a
genuine discrepancy in the owner's own message, not a mistake in this implementation - the formula is
implemented consistently as `G * (wheel/100) * scale` everywhere, and the SEPARATE, internally
consistent example 3 above (confirmed by the coordinator as the one to test) is what the test suite
actually asserts.

### Lock/Slip combination: the LARGER contribution wins - owner-confirmed

`contribution = Max(lockAll/100 * WheelLockShakeScale, slipAll/100 * WheelSlipShakeScale)`, gated by
`IntegrateWheelLockAndSlip`. Confirmed by the owner (via the coordinator) as the settled rule, not
summed - so a frame where both channels are briefly non-zero can never double the shake amplitude.
Verified with a dedicated test (`When_both_lock_and_slip_are_non_zero_...`) that feeds Lock=30/Slip=30
(equal contributions) against a Lock=30/Slip=0 baseline and asserts the oscillation band is IDENTICAL
in both cases - which a sum-based rule would not satisfy (it would produce a visibly wider band with
both channels active). A second test confirms Slip alone (Lock=0) can also drive the shake.

### Which G-force signal drives the shake

`projected.LockAll`/`projected.SlipAll` - Layer 5's own final, already-curve-shaped, published
`WheelLock.Projected.All`/`WheelSlip.Projected.All` values (0-100), read directly in
`QAdvanceFeedback.cs`'s `DataUpdate` right after they are computed, and passed as new optional
parameters to `GForceEngine.Compute`. This is the most user-relevant signal available at that point
in the pipeline (what a driver would already see bound to a dashboard), rather than Layer 3's Raw or
Layer 4's Normalized values - a design decision flagged here for the owner to revisit if a different
tier is actually wanted.

### Which pads shake, and how they compose with the existing lateral bias

Applied uniformly to all four pad pairs (Bottom Front, Bottom Rear, Back Low, Back Top) - not
selectively, since the mechanics make selectivity automatic: `band` is proportional to the pair's own
current (pre-lateral) level, so a pair currently at/near 0 gets `band ~= 0` and is inert by
construction, satisfying "apply to every pad pair that currently has output" without any extra
branching. The shake is computed on each pair's UNBIASED level (before the existing lateral-bias
multiply), producing shaken left/right centres that the EXISTING `leftFactor`/`rightFactor` lateral
multiply is then applied to, exactly where the old code multiplied the plain level - so lateral bias
and shake compose the same way they always composed with the plain level, and the final
`ClampMath.To0100` still guarantees the 0-100 bound. Verified byte-identical to the pre-existing
expression when the feature is off/inert (see mutation-adjacent evidence below) and never leaves
0-100 across a wide sweep even with an aggressively large scale (3.0) and near-saturation wheel values.

### Time source - injectable, driven by frame dt, no wall-clock

`GForceEngine` holds a private `_shakeActive`/`_shakePhaseSeconds` pair, advanced inside `Compute`
from the SAME `ITelemetrySample.Dt` every other per-frame filter in this engine already uses -
mirroring `PulseGenerator`'s own "freshly active starts at t=0, then advances by dt" convention
exactly: the very first frame after the checkbox goes active (or after `Reset()`) always evaluates at
phase 0 (`sin(0)=0`, left==right==centre), guaranteeing "the wave starts from the current G-force
value so the transition into shaking is continuous" literally, not just as an emergent property of
`band==0`. Fully unit-testable with synthetic per-frame `dt` values - no `Thread.Sleep`, no wall-clock
dependency anywhere.

### Settings (`Settings\GForceSettings.cs`, wired via `ApplyTo`, all persisted, all covered by the global restore)

- `IntegrateWheelLockAndSlip`: **default OFF**. Chosen deliberately, not left unconsidered: this is a
  new, intrusive change to the existing G-force FEEL (it superimposes an oscillation on every pad
  pair), and this plugin family's established convention is that anything which changes existing feel
  ships off by default (`PulseSettings.Enabled` is the direct precedent, also off by default, also
  documented as a deliberate choice). A driver opts in.
- `ShakeFrequencyHz`: default **5 Hz**, clamped to **[5, 20]** in the SETTER itself (both
  `GForceSettings.ShakeFrequencyHz` and `GForceEngine.ShakeFrequencyHz`), not only a UI spinner range -
  a hand-edited config file cannot smuggle in an out-of-band frequency.
- `WheelLockShakeScale` / `WheelSlipShakeScale`: default **1.0** each, clamped to **>= 0** in the
  setter (negative values floor to 0).

### UI

New "Wheel lock/slip shake" group box on the G-Force tab, positioned after "Left/right direction":

- **A `mah:ToggleSwitch` (MahApps), not a `CheckBox`** - a mid-task owner correction: this enables a
  distinct FUNCTION of the G-Force channel, not a minor option, and should read that way. Chosen over
  restyling a plain `ToggleButton` because MahApps.Metro (already referenced by this project) ships a
  purpose-built `ToggleSwitch` control with first-class `OnLabel`/`OffLabel` properties - exactly what
  "both states must be clearly labelled, not inferred from a highlight" calls for - and it is already
  keyboard-accessible (a `ToggleButton`-based lookless control, focusable and space/enter-activatable
  by default, like every other `ToggleButton`/`CheckBox` in this settings control). The Wheel Lock/Slip
  tabs' own source-mode selector is a `ComboBox` (a genuine two-way MODE picker, not a binary on/off
  switch), so there was no existing "toggle switch" visual on those tabs to match instead.
- **Visual prominence:** a bold, larger-font header (`GForce.Shake.Enable`, "Integrate Wheel Lock and
  Slip") sits directly above the switch, which itself sits above the frequency/scale settings it
  governs - the switch is the first thing in the group, not inline among the numeric fields.
- **Both states spelled out:** `OnLabel`/`OffLabel` are set to localized "On"/"Off" text
  (`GForce.Shake.On`/`.Off`, both languages) - the switch always shows a literal word, never relying
  on a colour/position cue alone.
- **Frequency/both scales are disabled (greyed out) while the switch is off**, and re-enabled the
  instant it is turned on: `RefreshGForceShakeControls()` sets `IsEnabled` on all three controls from
  `GForceShakeEnabled.IsChecked`, wired to the switch's `Checked`/`Unchecked` events, called once at
  construction and again after every `LoadFromSettings`/`RestoreAllDefaults`/`SaveToSettings` cycle.
  **This is pure UI wiring with no testable seam**: the "logic" is a one-line `IsEnabled = isChecked`
  assignment on three WPF controls, not a computation - there is nothing non-trivial to extract into
  Core, and (like every other purely visual behaviour in this settings control - see
  `docs\refinements-report.md`'s own "Unverified" section for the three earlier UI bugs) it cannot be
  exercised without a live, rendered WPF window. Stated plainly rather than inventing an artificial
  seam to claim test coverage that would not mean anything.
- Persistence, defaults (5 Hz / 1.0 / 1.0), the 5-20 Hz enforcement, the shake mechanics, and coverage
  by the global "Restore all default settings" are all UNCHANGED from the checkbox version - only the
  visual control changed.

Screenshot confirms correct rendering, including the header, the "Off" label, and the three governed
controls visibly greyed out while off (see below).

---

## MUTATION EVIDENCE (all three applied, tests run, then reverted and diff-verified byte-identical)

| # | Mutation | Result |
|---|---|---|
| (a) | `GForceShake.Apply`'s clamp-by-shift replaced with clamp-by-squash (always centre on the raw value, clamp final output) | **3 tests fail**: `Worked_example_2_G80_wheel60_scale1_band48_shifted_to_52to100_centre76` (the mandated example), `Band_width_is_preserved_under_shifting_across_a_sweep_of_centres`, and the dedicated `MUTATION_a_clamp_by_squashing_instead_of_shifting_would_fail_example_2` |
| (b) | `GForceEngine.ShakeFrequencyHz`'s setter floor/ceiling removed entirely (`value` stored unclamped) | **2 tests fail**: `ShakeFrequencyHz_defaults_to_5_and_is_clamped_to_5_20_in_the_setter`, `MUTATION_b_a_frequency_below_5Hz_must_never_be_readable_back` |
| (c) | `WheelChannelSettings.ResetSourcesForCurrentMode` changed to ignore `SourceMode` and always call `ResetSourcesToDefault` (forcing Manual) | **2 tests fail**: `ResetSourcesForCurrentMode_in_ShakeIt_mode_restores_the_four_ShakeIt_names_and_stays_in_ShakeIt_mode`, `ResetSourcesForCurrentMode_never_switches_the_mode_itself_either_way` |

Each mutation was applied to a single file (backed up first), the relevant filtered test run
observed to fail, then restored from the backup and `diff`-verified byte-identical to the
pre-mutation original before moving on. The full 427-test suite and the 0-warning/single-DLL Release
build were re-verified green after all three reverts.

## EVIDENCE checklist (all present)

- The three worked examples: `GForceShakeTests.Worked_example_1/2/3_...`.
- Band width preserved under shifting: `Band_width_is_preserved_under_shifting_across_a_sweep_of_centres`
  (sweeps centre 0-100 at a fixed sub-100 band, asserts `left-right == band` exactly at every point).
- Centre relocation: covered directly inside the worked-example-2 assertions (`(left+right)/2 == 76`).
- Frequency floor/ceiling enforced in the SETTER (both `GForceSettings` and `GForceEngine`):
  `ShakeFrequencyHz_is_clamped_to_5_20_in_the_setter_itself` / `..._defaults_to_5_and_is_clamped_...`.
- Scale of 0 produces no shake: `Scale_of_zero_produces_no_shake_even_with_a_full_wheel_value`.
- Checkbox off produces byte-identical output: `Checkbox_off_produces_byte_identical_output_regardless_of_wheel_values_passed_in`
  (asserts 12 decimal places of agreement against a baseline engine that never even sees the new
  parameters, across 50 varying frames).
- Left/right are exact mirrors: `Left_and_right_are_exact_mirrors_around_the_effective_centre_at_every_phase`
  (pure math) and `Left_and_right_stay_exact_mirrors_of_each_other_around_their_own_centre_when_shaking`
  (full engine).
- Toggle persists, per-source reset follows mode, on BOTH channels: `ConfigStoreTests.Save_then_Load_round_trips_the_shake_settings`,
  `WheelChannelSettingsTests.ResetSourcesForCurrentMode_*` (both modes, both explicitly re-affirmed
  by the "never switches the mode itself either way" test which exercises both directions in one
  assertion pair).

---

## Documentation updated

- `README.md` / `README.zh-Hans.md`: the "Using ShakeIt Motors as a source" section rewritten
  (always-visible toggle, new "ShakeIt Plugin Output Properties" label, global default changed,
  create-profile/Disable-output steps, Manual-mode note, mode-aware per-source reset); a new
  paragraph added to the G-Force tab section describing "Integrate Wheel Lock and Slip" and its three
  settings/combination rule.
- `docs\shakeit-export-guide.md`: the "Automatic: the toggle" section rewritten to describe the
  corrected always-visible behaviour (was previously wrong in exactly the way that caused this bug -
  it documented the old "toggle only appears once available" behaviour as intended); the two other
  "Use ShakeIt Motors output" label mentions updated to the new label text.
- `docs\refinements-report.md`: a short banner note added at the top (this project's own established
  convention for a historical report a later task supersedes - see that report's own tier-rename
  section for the precedent) pointing at this report; the body below the banner is left as a
  historical record, not rewritten throughout.
- Screenshots regenerated using the existing offline WPF harness (kept at
  `...\scratchpad\qpreview`, outside both repos, reused as instructed) against the freshly rebuilt
  Release DLL: `settings-wheel-lock.png`, `settings-wheel-slip.png`, `settings-general.png` written
  directly under their expected names; the G-Force tab (harness names it `settings-g-force.png` from
  the tab header text) was renamed to **`settings-gforce.png`** on copy into `docs\images\`, matching
  the README's existing links. All four visually reviewed after regeneration: the Wheel Lock/Slip
  tabs show the toggle at the top defaulting to "ShakeIt Plugin Output Properties" with the setup
  guide and "not published yet" note both visible (the harness has no live `PluginManager`, so
  availability genuinely resolves false, exercising exactly that code path); the G-Force tab shows
  the new "Wheel lock/slip shake" group with its bold header, the `ToggleSwitch` showing "Off", and
  the frequency/both scale spinners visibly greyed out at their 5 Hz/1.0/1.0 defaults - confirming
  the disable-when-off wiring renders correctly. Regenerated a SECOND time after the coordinator's
  mid-task ToggleSwitch correction, from a freshly rebuilt `Preview.csproj` (its own `bin` had to be
  rebuilt, not just the referenced plugin DLL, since it copies the reference at ITS OWN build time -
  a `--no-build` run after only rebuilding the plugin silently reused the stale copy on the first
  attempt, caught by reviewing the rendered image before finalising).

## Concerns / not done / unverified

1. **Nothing in this task has been run inside a live SimHub session, against a real game, or on
   rumble hardware** - unverifiable in this environment, consistent with every prior report in this
   project family. All 427 tests are pure, offline, hand-computed-expectation unit tests; the four
   screenshots come from an offline WPF harness with `PluginManager` null, not a live SimHub process.
2. **The owner's third worked-example message has an internal arithmetic inconsistency** (stated
   scale 1.5, arithmetic using 2.5) - flagged above rather than silently "corrected" to match; the
   formula itself is implemented and tested consistently.
3. **Design decision, flagged for the owner's review:** the shake reads Layer 5's `Projected.All`
   values (not Raw or Normalized) as the wheel lock/slip signal driving the shake - reasonable given
   it is the most user-facing tier, but not explicitly specified in the brief.
4. **Design decision, flagged for the owner's review:** the shake is computed on each pad pair's
   UNBIASED (pre-lateral) level and the existing lateral-bias factor is applied afterward on top of
   the shaken left/right values - meaning a pair under a strong simultaneous lateral bias will not
   have perfectly symmetric shake amplitude left-to-right at the FINAL published value (only at the
   pre-lateral stage). This was not addressed in the brief's mechanics section (which does not
   mention lateral G at all) and is the most defensible composition available without discarding the
   pre-existing lateral feature.
5. **The clean-clone scratch copy's empty directory skeleton could not be removed** due to this
   environment's safety hook blocking every `rmdir` invocation outright, even non-recursive - see the
   build-confirmation section above for the full explanation. No recursive delete was attempted.
6. **The six other historical `docs\*-report.md` files were not touched** for this task (only
   `docs\refinements-report.md` got a banner note, since it is the one whose own body is now
   factually superseded by this report) - consistent with this project's established convention of
   not rewriting historical reports wholesale.
