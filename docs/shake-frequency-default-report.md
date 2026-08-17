# G-Force "Integrate Wheel Lock and Slip" shake frequency default: 3 Hz -> 10 Hz

## The change

The owner tried the shipped 3 Hz shake frequency on real hardware from the driver's seat and
reports 10 Hz feels much better. This task raises the **default** shake frequency for the G-Force
"Integrate Wheel Lock and Slip" feature from 3 Hz to 10 Hz.

- **Default: 10 Hz** (was 3 Hz).
- **Bounds unchanged**: floor stays 1 Hz, ceiling stays 20 Hz
  (`Core.GForce.GForceShake.MinFrequencyHz`/`MaxFrequencyHz`). 10 sits comfortably inside them - no
  bound was touched.
- **The Layer 5 pulse's own, separate 200 ms (5 Hz) gap floor** (`Core.Projection.PulseSettings.MinGapMs`)
  on the Wheel Lock/Slip tabs is a different feature with a confusingly similar unit and was left
  completely untouched - confirmed both by inspection (its constant/value is unchanged in
  `PulseSettings.cs`) and by the pre-existing pinning tests
  (`GForceSettingsTests.MUTATION_b_the_pulse_gap_floor_is_a_different_200ms_setting_unaffected_by_this_change`,
  `GForceEngineShakeTests.The_pulse_gap_floor_is_a_different_setting_and_is_unaffected_by_the_shake_floor_change`,
  and the pulse's own `PulseSettingsTests`), all of which stayed green throughout.

## One default or two?

A recent task (`docs\integrate-default-report.md`) found that `IntegrateWheelLockAndSlip` carries
**two deliberately different defaults**: `GForceSettings.IntegrateWheelLockAndSlip` defaults ON,
while `GForceEngine.IntegrateWheelLockAndSlip`'s bare-constructor default stays OFF, as a
library-level "inert unless configured" baseline that every `GForceEngineShakeTests` "disabled"
fixture relies on. `GForceSettings.ApplyTo` is what actually makes a real install ON at Init/Apply.

`ShakeFrequencyHz` does **not** follow that same split pattern. Before this change, both
`GForceSettings.ShakeFrequencyHz` (backing field `_shakeFrequencyHz`) and
`GForceEngine.ShakeFrequencyHz` (its own backing field) already defaulted to the **same** value,
3.0 - there is no "inert unless configured" reason for a frequency default to differ the way there
is for an on/off switch (a frequency is meaningless/inert either way until the feature itself is
on). So the established, pre-existing pattern here is **parity, not split**. I followed that
pattern: both `GForceSettings.ShakeFrequencyHz`'s backing field and `GForceEngine.ShakeFrequencyHz`'s
backing field were changed from `3.0` to `10.0`, keeping them in sync exactly as they always were.
`GForceSettings.ApplyTo` still pushes the settings value onto the engine at Init and on every Apply,
same as before - this change doesn't touch that wiring.

Doc comments on both properties were updated to state the new default, the rationale (owner's real
hardware test), and to explicitly call out that this property's own split-vs-parity choice differs
from `IntegrateWheelLockAndSlip`'s (documented on both sides so a future reader does not assume the
two settings/engine pairs in this class must always follow the same convention).

## Tests rewritten (legitimate default changes, not weakened assertions)

- `GForceSettingsTests.cs`: `Shake_settings_now_default_to_on_3Hz_and_scale_1_5` renamed to
  `Shake_settings_now_default_to_on_10Hz_and_scale_1_5`; its `Assert.Equal(3.0, settings.ShakeFrequencyHz, 6)`
  changed to `10.0`; the comment updated to record the 3->10 Hz history alongside the earlier 5->3 Hz
  and floor 5->1 Hz changes.
- `GForceEngineShakeTests.cs`: `ShakeFrequencyHz_defaults_to_3_and_is_clamped_to_1_20_in_the_setter`
  renamed to `ShakeFrequencyHz_defaults_to_10_and_is_clamped_to_1_20_in_the_setter`; its
  `Assert.Equal(3.0, engine.ShakeFrequencyHz, 6)` changed to `10.0`; the section comment and the
  cross-reference comment in `IntegrateWheelLockAndSlip_defaults_to_off_on_the_bare_engine_itself`
  (which named the old settings-layer test) were updated to the new test name.
- `ConfigStoreTests.cs`: `Missing_file_yields_the_shake_settings_own_defaults`'s
  `Assert.Equal(3.0, loaded.GForce.ShakeFrequencyHz, 6)` changed to `10.0` - this is the
  "fresh install / global restore" round-trip path (`ConfigStore.Load` on a missing file returns
  `QAdvanceFeedbackSettings`'s plain-`new` defaults), so it directly proves the shipped default a
  driver actually gets.

Tests that merely **needed** a specific frequency to exercise something else already set it
explicitly rather than leaning on the default, so none of them needed a change:
- `GForceSettingsTests.ShakeFrequencyHz_is_clamped_to_1_20_in_the_setter_itself`,
  `MUTATION_a_a_shake_frequency_below_1Hz_must_never_be_readable_back`, and
  `ApplyTo_maps_every_shake_setting_onto_the_engine` all set `ShakeFrequencyHz` explicitly
  (0.1/999.0/15.0/0.0001/11.0).
- `GForceEngineShakeTests`' own setter-clamp tests set the value explicitly (0.1/100.0/12.0/0.001).
- `GForceShakeStabilityTests.NewShakingEngine` already set `ShakeFrequencyHz = ShakeHz` (a named
  constant, 10.0) explicitly - unaffected either way.
- `QAdvanceFeedbackSettingsTests.RestoreDefaults_resets_every_customised_field_across_all_four_tabs`
  touches `settings.GForce.ShakeFrequencyHz = 18.0` as a **non-default** value to prove
  `RestoreDefaults()` actually resets it (compared against `QAdvanceFeedbackSettings.CreateDefault()`,
  not a hardcoded literal) - 18.0 is still non-default after this change, so no edit was needed; the
  test automatically validates against whatever the real default is.
- `ConfigStoreTests.Save_then_Load_round_trips_the_shake_settings_when_explicitly_disabled` sets
  `ShakeFrequencyHz = 14.0` explicitly, by design, specifically to prove the round trip carries a
  non-default value.

## "Restore all default settings"

`QAdvanceFeedbackSettings.RestoreDefaults()`/`CreateDefault()` derive from the same plain
`new GForceSettings()` construction as everything else - there is exactly one internally-defined
default object, so the global Restore button and the shipped default can never drift apart. No
separate code change was needed there; `ConfigStoreTests.Missing_file_yields_the_shake_settings_own_defaults`
(updated above) and `QAdvanceFeedbackSettingsTests.RestoreDefaults_resets_every_customised_field_across_all_four_tabs`
(unaffected, see above) both verify the restore path now yields 10 Hz.

## READMEs

- `README.md` (English), G-Force tab section: "Shake frequency (1-20 Hz, default **3 Hz** -
  LOWERED from an original 5-20 Hz/default-5 range...)" rewritten to "default **10 Hz** - RAISED
  from an original 3 Hz default per the owner's own real-hardware seat time...", noting the 1-20 Hz
  bounds are unchanged.
- `README.zh-Hans.md` (Simplified Chinese), the mirrored G-Force tab paragraph: "默认 **3
  赫兹**——相比原先 5-20 赫兹区间及默认值 5 有所降低..." rewritten to "默认 **10 赫兹**——相比原先的 3
  赫兹有所提高，车主本人在真实硬件上试用 3 赫兹后反馈 10 赫兹的手感明显更好；1-20 赫兹的上下限本身未变".
  Edited directly (this file is plain UTF-8 markdown with literal CJK text, unlike the compiled-in
  string tables below, which store Chinese as `\uXXXX` escapes in C# source - a different
  convention that does need the Python/unicode-escape approach).

## In-app help/note text (both string tables)

Checked both `QAdvanceFeedback\Core\Localization\StringTableEn.cs` and `StringTableZhHans.cs` for
any mention of the specific default value. Neither one states a default:
`"GForce.Shake.Frequency.Label"` reads "Shake frequency (Hz, 1-20)" / "抖动频率（赫兹，1-20）" in both
tables - only the clamped **bounds**, never the shipped default - and `"GForce.Shake.Note"` describes
the on/off behaviour generically without a number. **No string-table edit was needed or made.** The
key-parity test (`Strings`/localization key-set test) stays green because nothing was touched.

## Screenshot

Regenerated `settings-gforce.png` using the harness at the scratchpad `qpreview` project (kept
entirely outside this repo, under the session's temp scratchpad directory - never copied in). Built
the harness in Release against the freshly-rebuilt `QAdvanceFeedback.dll` (Release, single-DLL) and
ran it; it wrote all four tab screenshots to its own directory, including `settings-g-force.png`.
Only that one file was copied into `docs\images\settings-gforce.png` (matching the README's link
spelling, no hyphen before "force") - the other three (`settings-general.png`,
`settings-wheel-lock.png`, `settings-wheel-slip.png`) were left in the harness's own output
directory and were **not** touched in `docs\images\`.

## Build/test verification

- `dotnet build QAdvanceFeedback.sln -c Debug` -> Build succeeded, **0 Warning(s)**, 0 Error(s).
- `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj -c Release` -> Build succeeded, 0 warnings;
  `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` (single-DLL
  distribution intact).
- `dotnet test QAdvanceFeedback.sln -c Debug` -> **567/567 passed**, 0 failed, 0 skipped.
- `Private\` (the withheld Layer 2/3 source, which is what separates the full 567-test count from
  the clean-clone's 523) was not modified - grepped for `ShakeFrequencyHz`/`GForce` under `Private\`
  and found only an unrelated doc-comment mention in `SimHubTelemetryAdapter.cs` listing consumer
  type names, no pinned frequency value and no test dependency on this default. The clean-clone
  build/test path is therefore unaffected by this change (not re-verified by physically removing
  `Private\`, since that directory pre-dates this task and moving/removing it was out of this
  task's scope - the static check above is the evidence).

## Mutation evidence

Reverted `GForceSettings._shakeFrequencyHz` from `10.0` back to `3.0` and reran the suite:

```
Failed!  - Failed: 2, Passed: 565, Skipped: 0, Total: 567
  GForceSettingsTests.Shake_settings_now_default_to_on_10Hz_and_scale_1_5   [FAIL] Expected 10, Actual 3
  ConfigStoreTests.Missing_file_yields_the_shake_settings_own_defaults      [FAIL] Expected 10, Actual 3
```

Restored the `10.0` default and reran: **567/567 passed**, 0 warnings, Release single-DLL build
unaffected.

## Concerns

- None outstanding. This is a scoped, mechanical default-value change: bounds untouched, pulse
  floor untouched and re-confirmed by its own dedicated tests, string tables needed no edit because
  neither ever stated the numeric default, and the one runtime-behaviour code path this touches
  (`GForceEngine.ShakeFrequencyHz`'s setter/clamp and `GForceSettings.ApplyTo`) is unchanged other
  than the two backing-field literals.
