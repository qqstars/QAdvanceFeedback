# G-Force Sweep Speed (TransientGain) default: 1.5 -> 1.2

## STATUS: DONE

## Test counts
- Full run (this working tree, includes any concurrent work): **581 / 581 passed**, 0 failed, 0 skipped (both `Debug` and `Release` configurations built and tested).
- Baseline quoted in the task (568 full / 523 clean-clone) does not match the 581 seen here - this
  tree has a concurrent agent adding `Diag.Telemetry.*` tests at the same time, which explains the
  higher count. Not caused by this change; flagged under Concerns below.

## Build
- `dotnet build -c Debug` and `dotnet build -c Release`: **0 Warning(s), 0 Error(s)** both times,
  including a full `dotnet clean` + rebuild pass.
- Single DLL confirmed: `QAdvanceFeedback\bin\<Config>\net48\` contains exactly
  `QAdvanceFeedback.dll` (+ its `.pdb`) - no extra assemblies.

## One default or two?
**Two** defaults existed, and both were changed together (the PARITY pattern, matching
`ShakeFrequencyHz`'s precedent - not the `IntegrateWheelLockAndSlip` settings/engine split, since
this is a tuning constant, not an opt-in feature):
- `QAdvanceFeedback\Settings\GForceSettings.cs` - backing field `_transientGain`: `1.5` -> `1.2`.
- `QAdvanceFeedback\Core\GForce\GForceEngine.cs` - auto-property `TransientGain { get; set; }`: `1.5` -> `1.2`.

`GForceSettings.ApplyTo(GForceEngine)` copies the settings value onto the engine on Init/Apply, so
both objects now agree at `1.2`, and the settings UI's "Restore all default settings" (which derives
from a fresh `QAdvanceFeedbackSettings` -> fresh `GForceSettings()`) picks up `1.2` automatically -
no separate hardcoded default existed anywhere else to update.

## Untouched (verified)
`MinStageProgressPerSecond` (1.0), `MaxStageProgressPerSecond` (5.0), `TransientTimeConstantSeconds`
(0.08), and `SustainTimeConstantSeconds` (0.15) were not edited - confirmed by re-reading
`GForceEngine.cs` after the change; only the `TransientGain` initializer/field values and doc
comments referencing them changed.

## Tests changed
- `QAdvanceFeedback.Tests\GForceSettingsTests.cs`,
  `Washout_filter_settings_default_to_the_reasoned_values`: `Assert.Equal(1.5, settings.TransientGain, 6)`
  -> `Assert.Equal(1.2, ...)`. This was pinning the OLD default as a genuine assertion (not a test
  that merely needed a specific gain) - updated to match the new shipped default.
- `QAdvanceFeedback.Tests\GForceEngineTests.cs`: the class-level XML-doc remark listing "defaults
  used throughout" was updated from `TransientGain=1.5` to `TransientGain=1.2` for accuracy (doc-only,
  not an assertion).
- No other `GForceEngineTests.cs` test needed a change: every test that exercises the staged-sweep
  timing either (a) relies on `MaxStageProgressPerSecond`/`MinStageProgressPerSecond` capping/flooring
  the rate regardless of gain (e.g. the impact-magnitude and large-coasting-delta tests, where
  `observedRate * gain` already exceeds the 5.0/s cap at both 1.2 and 1.5), (b) uses only relative
  comparisons (large-delta-sweeps-faster-than-small-delta, stage-ordering, no-discontinuity), unaffected
  by the exact gain value, or (c) runs long enough (hundreds of frames) to reach the fully-swept
  steady state regardless of sweep speed, guaranteed by the 1.0/s floor.
- `GForceSettings.cs` and `GForceEngine.cs` doc comments were updated to state the new default and
  the reasoning (owner's own hardware testing), and a one-sentence addition was made to
  `GForceEngine.TransientGain`'s remarks about the gain/cap interaction (see below).

## "Restore all default settings"
Verified: `QAdvanceFeedbackSettings.CreateDefault()`/`RestoreDefaults()` both construct a fresh
`GForceSettings()`, whose backing field now defaults to `1.2` - the global restore button returns
`1.2` with no separate code path to update.

## READMEs
Both `README.md` and `README.zh-Hans.md`'s G-Force "Motion feel" **Impact** paragraph were updated:
- Added the one-sentence gain/cap-interaction consequence (see below), and
- Added an explicit stated default - `**1.2** (lowered from an original 1.5 ...)` - matching this
  README's own established convention for documenting every other tunable's default with reasoning
  (`Shake frequency`, `Wheel Lock/Slip scale`, etc.). Note: neither README previously quoted a numeric
  default for Sweep speed at all (only the "capped at 5 sweeps/second" note existed), so there was
  nothing literal to "update" there, but adding the explicit default keeps this setting consistent
  with its neighbors in the same document.

## In-app help text (both string tables)
`GForce.Motion.Note` does not quote the TransientGain numeric default in either language, so no
default-value edit was required there. Per the "worth adding" note, one sentence was appended to
this same string in **both** `StringTableEn.cs` and `StringTableZhHans.cs` stating the practical
consequence: raising Sweep speed lowers the input rate at which the existing cap kicks in, so above
that point gentle and violent inputs feel more alike. The `zh-Hans` edit was made via a Python script
(`io.open(..., encoding='utf-8')`), building the new sentence from decimal Unicode codepoints and
emitting literal `\uXXXX` C#-escape text (matching this file's own existing all-ASCII, `\u`-escaped
convention) - no raw CJK bytes were ever passed through a bash heredoc. The key-parity test
(`LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set`) still passes (no
keys were added or removed, only an existing value's text was extended in both tables).

## Screenshot
Regenerated via the harness at
`...\scratchpad\qpreview` (`dotnet run -c Release`, `PREVIEW_DIR` pointed at the harness's own
folder) against a freshly rebuilt, 0-warning `Release` `QAdvanceFeedback.dll` carrying the `1.2`
default. The harness wrote `settings-g-force.png` (plus the other three tab screenshots, all inside
the harness's own temp folder, never the repo). Only `settings-g-force.png` was copied into
`docs\images\settings-gforce.png` (the filename the READMEs actually link to) - the other three files
already in `docs\images\` (`settings-general.png`, `settings-wheel-lock.png`,
`settings-wheel-slip.png`) were left with their original timestamps/sizes, untouched. The harness
itself remains only in the scratchpad, never copied into the repo.

## Mutation evidence
Reverted `GForceSettings.cs`'s `_transientGain` field to `1.5` and ran
`dotnet test --filter FullyQualifiedName~GForceSettingsTests`:

```
[xUnit.net]     QAdvanceFeedback.Tests.GForceSettingsTests.Washout_filter_settings_default_to_the_reasoned_values [FAIL]
Expected: 1.2 (rounded from 1.2)
Actual:   1.5 (rounded from 1.5)
Failed!  - Failed: 1, Passed: 33, Skipped: 0, Total: 34
```

Reverted back to `1.2`, rebuilt (`Debug` and `Release`, both 0 warnings), and re-ran the full suite:
**581 / 581 passed**.

## Concerns
1. **Test-count baseline mismatch**: the task quoted 568 full / 523 clean-clone; this run shows 581.
   A concurrent agent is adding `Diag.Telemetry.*`-gated tests in the same working tree per the task's
   own note, which plausibly explains the higher count - not attributable to this change (verified the
   mutation test isolates cleanly to the one `GForceSettingsTests` assertion touched).
2. **Pre-existing, unrelated doc staleness noticed but left alone (out of scope)**: `GForceEngineTests.cs`'s
   class remarks state `SustainTimeConstantSeconds=0.30` as a "default used throughout," but the actual
   code default is `0.15` - this mismatch predates this change and is unrelated to `TransientGain`; not
   touched, since fixing it wasn't requested and isn't part of this default change.
3. No conflicts encountered with the concurrently-running diagnostics/telemetry/CSV-export agent's
   files - `GForce.Motion.Note` and the files touched here (`GForceSettings.cs`, `GForceEngine.cs`,
   `GForceSettingsTests.cs`, `GForceEngineTests.cs`, both READMEs, both string tables) are disjoint
   from CSV export / published-name test files.
