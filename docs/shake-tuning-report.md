# Shake tuning report — G-Force "Integrate Wheel Lock and Slip"

## STATUS

Complete. Both requested changes made: (1) the shake frequency floor lowered 5→1 Hz with the default
raised off the floor to 3 Hz (ceiling unchanged at 20 Hz); (2) the driver's "shake intensity" request
reconciled against the pre-existing per-channel `WheelLockShakeScale`/`WheelSlipShakeScale` multipliers
(no new overlapping control added) — both defaults raised 1.0→1.5 (150%), with UI labels re-expressed
to read as a percentage. The Layer 5 pulse's own, separate, 200 ms (5 Hz) gap floor on the Wheel
Lock/Slip tabs (`PulseSettings.MinGapMs`) is untouched — verified by dedicated tests and by mutation
evidence (b) below.

**Tests:** 536 total (533 pre-existing + 3 new), 0 failed, 0 skipped, full build (`Private/` present).
**Clean-clone tests:** 493 total (490 pre-existing + 3 new — all 3 new tests are public, none live in
`Private/QAdvanceFeedback.Tests/`), 0 failed, 0 skipped, `Private/` absent, verified in an isolated
scratch copy (not the working tree), re-verified a final time from a FRESH clean-clone snapshot after
all mutations were applied-and-reverted and all documentation/screenshot work was done.

**0-warning / single-DLL confirmation:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj
-t:Clean,Restore,Build -p:Configuration=Release -v:m` → `0 Warning(s), 0 Error(s)` for both the full
tree and the isolated clean-clone scratch copy (`lib/` copied alongside it, since the reference DLLs
live outside the project folder). `bin/Release/net48/` contains exactly `QAdvanceFeedback.dll` and
`QAdvanceFeedback.pdb` in both cases.

---

## 1. Shake frequency range — floor and default lowered

- `GForceShake.MinFrequencyHz`: **5.0 → 1.0**. `MaxFrequencyHz` unchanged at 20.0 (no driver feedback
  argued for a different ceiling, and 20 Hz already reads as a fast/punchy limit relative to the new,
  lower floor).
- `GForceSettings.ShakeFrequencyHz` and `GForceEngine.ShakeFrequencyHz` (the two places the floor was
  previously enforced in the setter, exactly as the old 5 Hz floor was): both setters clamp to
  `[GForceShake.MinFrequencyHz, GForceShake.MaxFrequencyHz]` unchanged in form — only the constant
  changed, so a hand-edited config file still cannot smuggle in a frequency below 1 Hz.
- **Default raised OFF the floor, not left sitting on it**: both classes' backing field is now an
  explicit `3.0` (previously it literally read `= GForceShake.MinFrequencyHz`, i.e. the default
  *was* the floor). Sitting the default exactly on a newly-lowered 1 Hz floor would have under-shot the
  driver's own feedback ("1-2 Hz reads better") for anyone who never opens the spinner — 3 Hz sits above
  that stated preferred range with headroom, while still being close enough to the floor to read as
  "obvious" per the same feedback that motivated lowering it in the first place.
- UI: `GForceShakeFrequency` spinner's `Minimum` changed from `5` to `1` (Maximum stays `20`); the
  `GForce.Shake.Frequency.Label` string changed from "Shake frequency (Hz, 5-20)" to "Shake frequency
  (Hz, 1-20)" in both English and Simplified Chinese.

**Pulse floor confirmed untouched.** `PulseSettings.MinGapMs` (200 ms / 5 Hz), the Layer 5 pulse's own
floor on the Wheel Lock/Wheel Slip tabs, was not touched by any of the above — it is a structurally
different property in a different class (`Core.Projection.PulseSettings`, not
`Core.GForce.GForceShake`/`Settings.GForceSettings`), governing a different feature (the pulse's
100↔min oscillation gap, not the G-force shake's sine frequency). Verified three ways:
1. `PulseSettingsTests` (pre-existing, unmodified) still asserts `MinGapMs == 200.0` and a gap of
   199.999 ms floors to 200 ms.
2. Two NEW pinning tests were added specifically to catch the two settings being confused with each
   other in a future change: `GForceEngineShakeTests.The_pulse_gap_floor_is_a_different_setting_and_is_unaffected_by_the_shake_floor_change`
   and `GForceSettingsTests.MUTATION_b_the_pulse_gap_floor_is_a_different_200ms_setting_unaffected_by_this_change`,
   both asserting `PulseSettings.MinGapMs == 200.0` from right next to the shake-frequency tests.
3. Mutation evidence (b) below: mutating `PulseSettings.MinGapMs` (the wrong setting) fails all three of
   the above tests, proving they would catch exactly this mistake.

---

## 2. "Shake intensity" — reconciled, not blindly added

### What actually exists (established before writing any code)

Grepped the whole codebase for "intensity"/"percentage"/"shaking percent" — **no such setting exists
anywhere**, in code, settings, or the string tables. The only pre-existing controls that scale the
shake's amplitude are the ones already documented in `docs/shake-and-toggle-report.md`:
`GForceSettings.WheelLockShakeScale` / `WheelSlipShakeScale`, bare non-negative multipliers (0-10 in the
UI spinner, `≥ 0` enforced in the setter), both defaulting to 1.0, feeding directly into
`GForceEngine.Compute`'s `contribution = scale × (wheelValue/100)` term, which in turn feeds
`GForceShake.Apply`'s `band = gForceValue × contribution`.

**What the driver's "50% shaking percentage" almost certainly refers to instead:** this project has a
genuinely different, pre-existing set of settings that ARE literally named and displayed as
percentages, defaulting to 50/25 — `GForceSettings.BrakeBottomRearSustainPercent` (default **50**),
`BrakeBackLowSustainPercent` (default 25), `AccelBottomRearSustainPercent` (default 25), and
`AccelBackLowSustainPercent` (default **50**) — the "Sustained feel" group on the G-Force tab (see the
screenshot), part of the STAGED TRAVEL pad-balance model from `docs/raw-gap-and-pad-balance-report.md`,
completely unrelated to the "Integrate Wheel Lock and Slip" shake feature. A 50%/25% pair, both shown
directly as "(%)" labels in the UI, is a much closer match to a driver's half-remembered "50%" than
anything in the shake feature itself, which has never had a percentage-labelled control before this
task. A second, weaker possibility (also flagged, per the task's own suggestion) is that the driver was
recalling the `wheelValue/100` term itself reading around 50 at a typical lock level — but that is a
live telemetry value, not a setting with a default, so it cannot be "bumped to 75%" as a configuration
change. **Recommendation for the owner:** confirm which (if either) of these was meant, since neither is
the same feature as the one this task's brief describes ("how obvious the shake is").

### Why a new, separate "Shake intensity" setting was NOT added

Adding a new multiplier on top of the existing per-channel scales would create exactly the two
overlapping, contradiction-prone controls the task warned against: `band = gForceValue × (wheelValue/100)
× WheelLockShakeScale × NewIntensity` has no single source of truth for "how obvious is the shake" — a
driver could set Lock scale to 2.0 and intensity to 50% and get an amplitude identical to Lock scale 1.0
and intensity 100%, with no way to tell from either control alone. The existing
`WheelLockShakeScale`/`WheelSlipShakeScale` already are the "how obvious" control, per-channel; the task
that introduced them explicitly designed them for exactly this purpose (`docs/shake-and-toggle-report.md`).

### What was implemented instead

1. **Both scales re-labelled in the UI to read as percentages**, per the recommended approach:
   `GForce.Shake.LockScale.Label` / `GForce.Shake.SlipScale.Label` changed from "Wheel Lock scale" /
   "Wheel Slip scale" to **"Wheel Lock scale (1.0 = 100%)"** / **"Wheel Slip scale (1.0 = 100%)"** (both
   languages), so the existing 1.0-centred multiplier reads intuitively without needing a second field.
2. **Deliberately did NOT convert the spinner itself to a live percent display** (e.g. showing "150"
   instead of "1.5" via a `NumericUpDown` percent format / ×100 conversion in code-behind). Reasoned
   deviation from the suggested approach, stated as instructed: this project's own settings UI has no
   interactive test harness — only unit tests (no WPF) and a static offline screenshot renderer with no
   `PluginManager` and no simulated keyboard input (see `docs/shake-and-toggle-report.md`'s own "no
   testable seam" precedent for `RefreshGForceShakeControls`). A percent-scaled `NumericUpDown` depends
   on MahApps' own parse-back behaviour for typed input, which cannot be exercised or verified in this
   environment; getting it wrong would silently corrupt the persisted scale on save. The label-only
   change delivers the same clarity ("1.0 = 100%") with zero behavioural/binding risk and zero change to
   the well-tested underlying model type.
3. **Both defaults raised 1.0 → 1.5** (`GForceSettings.WheelLockShakeScale`/`WheelSlipShakeScale` and the
   mirrored `GForceEngine` fields), delivering the driver's stated, unambiguous intent ("more obvious
   shake by default") as a genuine amplitude increase, not just a relabelling.
4. Persistence: no schema change — same two existing `double` fields, already round-tripped by
   `ConfigStore` (`ConfigStoreTests.Missing_file_yields_the_shake_settings_own_defaults` updated to pin
   1.5; `Save_then_Load_round_trips_the_shake_settings`, which uses explicit non-default values, is
   unaffected). Included in the global "Restore all default settings" automatically — `RestoreDefaults`
   derives from `QAdvanceFeedbackSettings.CreateDefault()`, i.e. `new GForceSettings()`, so the new field
   defaults flow through with no separate wiring needed (unchanged mechanism from prior passes).
5. Bounds: unchanged, still enforced in the setter (`value >= 0.0 ? value : 0.0`).

### Concrete amplitude effect (as required)

`band = gForceValue0100 × (wheelValue/100) × scale`. At a pad level (`gForceValue0100`) of 100 and a
wheel lock/slip value of 60:
- **Old default** (scale 1.0): `contribution = 0.6 × 1.0 = 0.6`, `band = 100 × 0.6 = 60`.
- **New default** (scale 1.5): `contribution = 0.6 × 1.5 = 0.9`, `band = 100 × 0.9 = 90`.

The oscillation band is **50% wider** (60 → 90, out of a possible 100) for the same wheel/G input —
a directly felt, more pronounced shake, matching the driver's stated intent. (Whether the band ends up
shifted, centred, or squashed per `GForceShake.Apply`'s existing clamp-by-shift rules is unaffected by
this change — only the band's own width scales with the new default.)

**Flag for the owner:** the "50%, maybe bump to 75%" figures the driver mentioned do not match any
existing shake-feature setting (see above) — please confirm whether they meant the Sustain
percentages (50%/25%, a different feature) or something else before assuming the new 1.5×/150% default
here is calibrated to the same number they had in mind; it was chosen as "meaningfully more pronounced"
rather than to hit a specific percentage the driver may not have been describing accurately.

---

## Documentation updated

- `README.md` / `README.zh-Hans.md`: the "Integrate Wheel Lock and Slip" paragraph on the G-Force tab
  rewritten to state the new 1-20 Hz range/3 Hz default, the new 1.5 (150%) scale defaults and their
  "1.0 = 100%" UI labelling, the concrete 60→90 band example, and a note distinguishing the shake's
  floor from the pulse's own unchanged 200 ms/5 Hz floor. Chinese edited via a Python script using
  `io.open(..., encoding='utf-8')` with explicit `\uXXXX`-escaped replacement text (verified to decode
  cleanly afterward via a second script) — no raw CJK went through a shell/heredoc.
- Both string tables (`StringTableEn.cs`, `StringTableZhHans.cs`): `GForce.Shake.Frequency.Label`,
  `GForce.Shake.LockScale.Label`, `GForce.Shake.SlipScale.Label` updated (English directly; Chinese via
  the same Python-script/`\uXXXX` approach). Key set unchanged (no keys added or removed), so
  `LocalizationTests`'s key-parity test needed no changes and still passes.
- `docs/shake-and-toggle-report.md`: a short superseding banner note added at the top (this project's
  own established convention — see that report's own path-note precedent) pointing at this report,
  since its "Settings" section documents the shake's now-superseded 5-20 Hz/1.0-scale defaults. Body
  left unchanged below the banner, as a historical record.
- Screenshots regenerated using the existing offline WPF harness (kept at
  `...\scratchpad\qpreview`, outside the repo), against a freshly rebuilt Release
  `QAdvanceFeedback.dll` AND a freshly rebuilt `Preview.exe` (its own `bin` was rebuilt via
  `-t:Rebuild`, not reused, per the prior pass's own documented caveat about stale copies). All four
  old PNGs were deleted by explicit path first (never a recursive delete) before regenerating:
  `settings-wheel-lock.png`, `settings-wheel-slip.png`, `settings-general.png` written directly under
  their expected names; the harness's own `settings-g-force.png` (named from the tab header text) was
  copied to `docs/images/settings-gforce.png`, matching the README's existing links. Visually confirmed
  the G-Force tab now shows "Shake frequency (Hz, 1-20)" defaulting to 3, and "Wheel Lock scale (1.0 =
  100%)" / "Wheel Slip scale (1.0 = 100%)" both defaulting to 1.5 (all three fields correctly greyed out
  while the toggle is Off, unchanged wiring). Wheel Lock/Wheel Slip/General tabs re-reviewed for
  regressions — visually unaffected by this task, as expected.

---

## MUTATION EVIDENCE

| # | Mutation | Result |
|---|---|---|
| (a) | `GForceSettings.ShakeFrequencyHz`'s setter floor removed (only the 20 Hz ceiling enforced; any value below 1 Hz stored as-is) | **2 tests fail**: `ShakeFrequencyHz_is_clamped_to_1_20_in_the_setter_itself`, `MUTATION_a_a_shake_frequency_below_1Hz_must_never_be_readable_back` |
| (b) | `PulseSettings.MinGapMs` changed from 200.0 to 50.0 (i.e. the pulse's own floor was moved instead of the shake's) | **3 tests fail**: `PulseSettingsTests.Gap_below_200ms_is_floored_to_200ms_by_the_model_itself` (pre-existing, unmodified), `GForceEngineShakeTests.The_pulse_gap_floor_is_a_different_setting_and_is_unaffected_by_the_shake_floor_change` (new), `GForceSettingsTests.MUTATION_b_the_pulse_gap_floor_is_a_different_200ms_setting_unaffected_by_this_change` (new) |
| (c) | `GForceSettings.WheelLockShakeScale`/`WheelSlipShakeScale` backing-field defaults reverted from 1.5 back to 1.0 | **2 tests fail**: `GForceSettingsTests.Shake_settings_default_to_off_3Hz_and_scale_1_5`, `ConfigStoreTests.Missing_file_yields_the_shake_settings_own_defaults` |

Each mutation was applied to a single file (backed up first to the scratchpad, outside the repo), the
relevant filtered test run observed to fail with the exact tests listed above, then restored from the
backup and `diff`-verified byte-identical to the pre-mutation original before moving on. The full
536-test suite and the 0-warning/single-DLL Release build were re-verified green after all three
reverts, and a final, fresh clean-clone snapshot (493/493) was taken and verified after all
documentation/screenshot work was also complete.

---

## Concerns / unverified

- **No live-game/SimHub/hardware verification** — the "more obvious" shake at 1.5×/3 Hz is a reasoned,
  concrete amplitude change (60→90 band width, worked above) but has not been felt on real rumble
  hardware, consistent with every prior report in this project family (no such rig is available in this
  environment).
- **The "50%, maybe bump to 75%" figures remain unresolved** — flagged above as most likely referring to
  the unrelated Sustain percentages (50%/25%) rather than the shake-intensity feature this task
  addresses; the owner should confirm before assuming the chosen 1.5×/150% default lines up with what
  they actually meant by "75%".
- **Deliberately did not convert the scale spinners to a live percent-formatted control** — see the
  reasoning in section 2 above (no interactive UI test harness to verify MahApps' `NumericUpDown`
  percent round-trip behaviour). If the owner specifically wants the spinner itself to show "150%"
  rather than "1.5" next to a "(1.0 = 100%)" label, that is a follow-up UI task that should be verified
  against a live, running SimHub settings dialog before shipping, not this offline harness.
- **`ShakeFrequencyHz`'s new 3 Hz default, like the original 5 Hz one, is a judgment call**, not
  independently rig-timed — it is justified against the driver's own stated "1-2 Hz reads better"
  feedback (sitting above that range with headroom) but has not itself been separately re-confirmed by
  seat time the way the pad-frequency recommendation in `docs/raw-gap-and-pad-balance-report.md` was.
