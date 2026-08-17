# Curve-editor anchor help text: default-mapping reference replaces the live readout

## STATUS: DONE

## What changed

Each anchor row in the Wheel Lock / Wheel Slip curve editors used to show a LIVE readout next to the
raw/output spinners: `"at this input, the curve currently sends {0}"` (`Curve.LiveOutputFormat`) -
redundant with the spinner textboxes sitting right next to it, which already show the current values.

Replaced with a stable reference to the SHIPPED DEFAULT input->output mapping for that anchor
(`Curve.Anchor.DefaultFormat` = `"under default input value {0}, curve will return {1}"`), plus a
compact conditional warning (see below) that only appears when it is actually needed. The existing
behavioural "what this anchor means" prose (`Curve.Lock.Slightly.Desc`, `Curve.Lock.Ideal.Desc`, etc.)
was left untouched, per the brief - only the redundant live-value line was replaced.

Start/End also got their own default-value callouts appended to their existing Desc text (e.g.
`"...Raise it if light braking buzzes more than you want. Ships at {0} by default."`), since they
previously had no default-value statement anywhere in the row.

## Verified default anchor values (read from code, not copied from the prompt)

Source of truth: `QAdvanceFeedback\Core\Projection\ProjectorSettings.cs`'s `ApplyPreset(Curve, ...)`,
which is exactly what `WheelChannelSettings.CreateLockDefaults()`/`CreateSlipDefaults()` call to stamp
a fresh install's/`RestoreAllDefaults()`'s actual shipped curve. Cross-checked against
`OutputProjectorTests.cs`'s existing numeric assertions (`ApplyPreset_curve_slip_produces_slip_defaults_not_lock_defaults`,
etc.) - all three sources agree:

| Channel | Start | Slightly | Ideal (Moderate) | Critical | End |
|---|---|---|---|---|---|
| Wheel Lock | input 20 (output always 0) | 30 -> 10 | 60 -> 30 | 80 -> 80 | 100 (output always 100) |
| Wheel Slip | input 20 (output always 0) | 30 -> 8 | 60 -> 20 | 80 -> 75 | 100 (output always 100) |

These match the task brief's numbers exactly, and also already matched both READMEs' existing
"Curve (default)" tables and the pre-existing `Curve.Lock.AnchorNote`/`Curve.Slip.AnchorNote` prose -
no drift found this time, but the check was done against the actual field initialisers/`ApplyPreset`
switch, not assumed.

**Avoiding a 4th hand-typed copy of these numbers:** rather than hard-coding them again into the new
help-text format calls, added `ProjectorSettings.CreateShippedDefault(ProjectionChannel)` (a thin
wrapper around `new ProjectorSettings()` + `ApplyPreset(Curve, channel)`) and read the default
input/output for each anchor/row from THAT at render time. If these numbers ever drift again (the
brief notes they have, more than once), the new help text follows automatically instead of needing a
fourth manual edit. Added two focused xUnit tests
(`CreateShippedDefault_lock_matches_ApplyPreset_curve_lock` /
`..._slip_matches_ApplyPreset_curve_slip`) pinning that the new helper never diverges from
`ApplyPreset` itself.

## The dropped/clamped-anchor warning: option (a), kept alongside

Chose **option (a)**: kept a compact live indicator alongside the default text, shown ONLY when the
curve's actual output at the driver's own typed raw input differs from what they typed for that
anchor's output (`Curve.Anchor.LiveDiffersFormat` = `"note: the curve actually returns {0} here, not
what you typed - it may be clamped or dropped"`), appended after the default-mapping sentence.

**Why (a) over (b):** the discrepancy is inherently per-anchor (`OutputProjector.BuildControlPoints`
drops an anchor whose input is at/below `StartInput` or at/above `EndInput`, and always forces
outputs non-decreasing after sorting) - a single global banner (option b) would have to either name
which anchor is affected (duplicating this same per-row logic elsewhere) or stay vague, and would
still leave the three spinners themselves looking "normal" with no pointer to which one is the
problem. Keeping the warning on the exact row it affects, and only when it's true, means:
- silence is the common case (a driver who has not touched anything, or who has only made valid
  edits, never sees it - matches the "restating the current mapping in prose is redundant" spirit
  the owner asked for),
- it still reads as a warning, not routine noise, because it only shows up when something is actually
  wrong,
- no new UI chrome/control was needed - the same `TextBlock` that used to hold the live readout now
  holds the default text plus this conditional suffix.

**Verified working, not just implemented:** wrote a throwaway harness check
(`...\scratchpad\qpreview\VerifyAnchorWarning.cs`, run once via a temporary `VERIFY_ANCHOR=1` branch in
`Program.cs`, then removed - the harness's checked-in `Program.cs` is unchanged from before this task)
that loaded the real `SettingsControl` with fresh default settings, confirmed no warning appears on any
of the three anchor rows at baseline, then set Wheel Lock's `Critical` raw input to 100 (equal to
`EndInput`, so `BuildControlPoints` drops it) while leaving its output spinner at 80 - the curve then
actually returns 100 there, and the warning appeared exactly as designed
(`"...note: the curve actually returns 100 here, not what you typed - it may be clamped or
dropped"`), disappearing again once the raw value was moved back to a valid 80. All checks passed;
Slightly/Ideal rows were confirmed unaffected by editing Critical alone.

## Localization (both tables, key parity kept green)

- **Removed**: `Curve.LiveOutputFormat` (English and zh-Hans).
- **Added**: `Curve.Anchor.DefaultFormat`, `Curve.Anchor.LiveDiffersFormat` (both tables).
- **Changed** (appended a `Ships at {0} by default.` / `默认值为 {0}。` clause, same key): `Curve.Lock.StartPoint.Desc`,
  `Curve.Lock.EndPoint.Desc`, `Curve.Slip.StartPoint.Desc`, `Curve.Slip.EndPoint.Desc`.
- zh-Hans edits were generated as plain-ASCII `\uXXXX` escapes via a Python script
  (`io.open(..., encoding='utf-8')`), never through a bash heredoc - the resulting
  `StringTableZhHans.cs` was verified byte-for-byte to still be pure ASCII (`data.decode('ascii')`
  succeeds, 0 bytes > 127) after every edit, and each new/changed Chinese character was individually
  checked against `unicodedata.name(...)` before being committed to the file (this caught one real
  mistake - see "Concerns" below).
- `LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set` and the other three
  localization tests all still pass (see test count below).

## Code changes

- `QAdvanceFeedback\Core\Projection\ProjectorSettings.cs`: added `CreateShippedDefault(ProjectionChannel)`.
- `QAdvanceFeedback\Core\Localization\StringTableEn.cs` / `StringTableZhHans.cs`: string table changes above.
- `QAdvanceFeedback\Settings\SettingsControl.xaml.cs`:
  - `LocalizeStaticText`: Start/End Desc text now formatted with the shipped default input, read via
    `ProjectorSettings.CreateShippedDefault`.
  - `RefreshCurvePlot` / renamed `UpdateLiveOutputText` -> `UpdateAnchorHelpText`: builds the default
    sentence from `CreateShippedDefault`, then appends the conditional live-differs warning when
    `Math.Abs(actualOutput - typedOutput) > 0.05`.
- `QAdvanceFeedback\Settings\SettingsControl.xaml`: added `TextWrapping="Wrap"` (plus italic style and a
  small left margin) to the six per-anchor "Live" `TextBlock`s (`Lock`/`Slip` x `Slightly`/`Moderate`/`Critical`),
  since the new text is longer than the old one-line readout and can now include the appended warning.
- `QAdvanceFeedback.Tests\OutputProjectorTests.cs`: added the two `CreateShippedDefault` tests noted above.

No behavioural/algorithm change anywhere - `OutputProjector`/`ProjectorSettings`'s actual projection
logic is untouched; `CreateShippedDefault` only composes two already-existing, already-tested pieces
(`new ProjectorSettings()` + the existing `ApplyPreset`).

## READMEs

Checked both `README.md` and `README.zh-Hans.md` for the old "at this input, the curve currently
sends" phrasing (and Chinese equivalent) - neither README ever quoted it; both already state the
shipped default input->output table directly (the "Output shaping (the curve)" section/table, and the
"Curve (default)" preset paragraph), matching the verified numbers above exactly. No README edit was
needed.

## Concurrent-agent conflict

None. No overlap found with localization strings or the curve editor/projector files while working -
the only conflict-adjacent thing worth flagging is that this task DID touch
`QAdvanceFeedback\Core\Projection\ProjectorSettings.cs` (a purely additive new method,
`CreateShippedDefault`) and `QAdvanceFeedback.Tests\OutputProjectorTests.cs` (two new test methods
appended, nothing existing changed) - if the other agent's normalization/silence work also touches
either file, a merge would need to reconcile two purely-additive diffs, which should be low-risk.

## Verification

- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s)**. Two projects:
  `QAdvanceFeedback.Tests -> ...\net8.0\QAdvanceFeedback.Tests.dll` and
  `QAdvanceFeedback -> ...\net48\QAdvanceFeedback.dll` (the one shipped plugin DLL).
- `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj -c Release --no-build`:
  **Passed! - Failed: 0, Passed: 733, Skipped: 0, Total: 733**. (733 = the 726 in the task brief, plus
  whatever the concurrent normalization/silence agent had already added at the time this run started,
  plus the 2 new `CreateShippedDefault` tests added here.)
- Screenshots regenerated via the out-of-repo harness at `...\scratchpad\qpreview\` (unchanged
  `Program.cs`, rebuilt against the freshly-built `QAdvanceFeedback.dll`), per the standing capture
  rule in `docs\architecture.md` (tall tabs -> content-only, no tab strip, no Apply/Restore row).
  Only the two affected files were copied into the repo:
  - `docs\images\settings-wheel-lock.png`: **1024 x 1749**
  - `docs\images\settings-wheel-slip.png`: **1024 x 1793**
  (Same pixel dimensions as the prior versions - the new per-row text happens to wrap to the same
  number of lines as the old text at this control width - but the PNG bytes differ, confirmed via a
  binary diff against the previous files before overwriting, and the rendered content was visually
  inspected to show `"under default input value 30, curve will return 10"` etc. on each anchor row and
  `"Ships at 20/100 by default."` on Start/End, with no stray warning text since fresh defaults never
  disagree with themselves.) `settings-gforce.png`/`settings-general.png` were left untouched
  (verified by file modification time - only the two Wheel Lock/Slip images changed).
- No git operations were performed (repo is not under Git, confirmed), and no recursive deletes were
  run near the project.

## Concerns

1. **`Curve.Anchor.LiveDiffersFormat`'s epsilon (0.05)** is a plain floating-point tolerance against
   `FormatNum`'s `"0.#"` display rounding, not a documented/configurable value - fine for this UI-only
   warning, but worth knowing if `FormatNum`'s own rounding granularity ever changes.
2. **Wording choice**: `Curve.Anchor.DefaultFormat` uses the owner's own quoted phrasing verbatim
   ("under default input value {0}, curve will return {1}") rather than the more compact
   report-shorthand form ("default 30 -> 10") also shown in the brief - the brief's compact form read
   to me as a shorthand for describing three per-row facts, not as literal target UI text, and the
   owner's own quoted sentence is the more clearly-localizable, explicit wording; flagging in case a
   more compact wording was actually wanted.
3. The two `docs\images\settings-*.png` READMEs still reference "669 tests" in a couple of prose spots
   (pre-existing staleness, unrelated to this task) - left untouched since it is out of this task's
   scope and not something this change caused.
