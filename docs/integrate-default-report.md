# "Integrate Wheel Lock and Slip" default flip — completion report

## STATUS: Complete

The partially-applied change (`GForceSettings.IntegrateWheelLockAndSlip` default `false` -> `true`, the
one line already edited) has been finished end-to-end: the settings-layer default, its tests, the
"Restore all default settings" path, the in-app UI copy, both READMEs, and the G-Force settings
screenshot are all now consistent with the new default. Full suite green, clean-clone green, 0 warnings,
single DLL.

## Test counts

- **Full tree: 567/567 passing** (unchanged count from the 567-test baseline — no tests were added or
  removed, several were rewritten in place or explicitly disabled where they needed the feature OFF).
- **Clean-clone (`Private\` folder absent, simulating a fresh clone): 523/523 passing** (matches the
  523-test clean-clone baseline exactly). Verified in an isolated scratch copy (robocopy of the full tree
  excluding `.vs`, `bin`, `obj`, `.simhub-refs-work`, and `Private`), built and tested independently of the
  working tree.

## Build

- Working tree, Debug: **0 Warning(s), 0 Error(s)**, single `QAdvanceFeedback.dll` (net48) + separate test
  assembly (net8.0, `QAdvanceFeedback.Tests.dll`) — same shape as before.
- Working tree, Release: **0 Warning(s), 0 Error(s)** (built only to produce the DLL the screenshot
  harness references).
- Clean-clone scratch copy, Debug: **0 Warning(s), 0 Error(s)**, same single-DLL shape.

## The two defaults — decision and rationale

There are two independent `IntegrateWheelLockAndSlip` properties:

- **`Settings.GForceSettings.IntegrateWheelLockAndSlip`** — the SETTINGS-layer default, i.e. what a
  fresh install / "Restore all default settings" actually gives a driver. **Now `true`.**
- **`Core.GForce.GForceEngine.IntegrateWheelLockAndSlip`** — the bare-constructor default on the raw
  engine class. **Left `false`**, unchanged.

**Decision: keep them disagreeing, deliberately, and document why.** This is not drift — it's a
library-vs-application split:

- `GForceEngine` is a low-level component anyone can construct directly (every unit test in
  `GForceEngineShakeTests` does exactly this). Its own bare default staying OFF is a conservative
  "inert unless configured" baseline for any such direct caller, and it is precisely what the
  "checkbox off is byte-identical to before this feature existed" guarantee
  (`Checkbox_off_produces_byte_identical_output_regardless_of_wheel_values_passed_in`) is built on.
  Flipping it too would have forced explicit `IntegrateWheelLockAndSlip = false` onto roughly a dozen
  existing "disabled"/"baseline" fixtures for no behavioural benefit to any real user — pure churn.
- `GForceSettings` is the product-facing configuration object. `GForceSettings.ApplyTo(engine)` — called
  once at `Init` and again on every settings Apply — pushes the settings value onto the engine every
  time. So for every real, fully-wired install, **the settings default is the only default that matters**;
  the engine's own bare default is only ever visible to code that bypasses settings entirely (tests, or a
  hypothetical future embedding).
- This mirrors an existing precedent in the same plugin family:
  `Core.Projection.PulseSettings.Enabled` (the other "changes the existing feel" toggle) also ships OFF
  at the settings layer — that decision is untouched, and is not contradicted by this one; each toggle's
  default was decided independently, on its own merits, by the owner.

Both properties' doc comments (`GForceSettings.cs` line ~135, `GForceEngine.cs` line ~144) and the
corresponding test (`GForceEngineShakeTests.IntegrateWheelLockAndSlip_defaults_to_off_on_the_bare_engine_itself`,
renamed and re-commented from `..._defaults_to_off`) now spell this relationship out explicitly, specifically
to prevent the "silently disagreeing defaults" trap the task called out.

## Tests rewritten (legitimate default changes, not weakened assertions)

- **`GForceSettingsTests.cs`** — `Shake_settings_default_to_off_3Hz_and_scale_1_5` renamed to
  `Shake_settings_now_default_to_on_3Hz_and_scale_1_5`; `Assert.False` -> `Assert.True` on
  `settings.IntegrateWheelLockAndSlip`, comment updated to explain the new default and its inertness
  guarantee.
- **`ConfigStoreTests.cs`** — `Missing_file_yields_the_shake_settings_own_defaults`:
  `Assert.False` -> `Assert.True`. The sibling round-trip test
  (`Save_then_Load_round_trips_the_shake_settings`, renamed
  `..._when_explicitly_disabled`) now explicitly sets `IntegrateWheelLockAndSlip = false` before saving
  — it needs a non-default value to prove the round trip actually carries state, which `true` no longer
  is.
- **`QAdvanceFeedbackSettingsTests.cs`** —
  `RestoreDefaults_resets_every_customised_field_across_all_four_tabs` was touching this field to `true`
  before calling `RestoreDefaults()`, which is now a no-op for this field (true -> true proves nothing).
  Changed the touch value to `false` so the test genuinely exercises the restore-to-`true` behaviour.
- **`GForceEngineShakeTests.cs`**:
  - `IntegrateWheelLockAndSlip_defaults_to_off` renamed to
    `IntegrateWheelLockAndSlip_defaults_to_off_on_the_bare_engine_itself`, with a doc comment explaining
    this is the ENGINE's own baseline, not the settings default — assertion unchanged (still `False`,
    correctly, since the engine default did not move).
  - Every "disabled"/"baseline" `GForceEngine` fixture that relied on the (unmoved, but now
    easily-confused-with-the-settings-default) bare-constructor default was made to set
    `IntegrateWheelLockAndSlip = false` explicitly instead of relying on the implicit default — four call
    sites (`Checkbox_off_produces_byte_identical_output...`'s two engines,
    `Wheel_value_of_zero_produces_no_shake_even_when_enabled`,
    `Scale_of_zero_produces_no_shake_even_with_a_full_wheel_value`,
    `Slip_alone_can_also_drive_the_shake`). This removes any future reliance on an implicit default that
    could plausibly move again.
- **`GForceShakeStabilityTests.cs`** — already set `IntegrateWheelLockAndSlip = true` explicitly in its
  `NewShakingEngine()` helper; no change needed, confirmed it does not rely on either default.
- Confirmed via `grep` that no other test file references `IntegrateWheelLockAndSlip`.

## Mutation evidence

Reverted `GForceSettings.IntegrateWheelLockAndSlip`'s default back to `false`, rebuilt, and reran the full
suite: exactly 2 failures, both expected —
`GForceSettingsTests.Shake_settings_now_default_to_on_3Hz_and_scale_1_5` and
`ConfigStoreTests.Missing_file_yields_the_shake_settings_own_defaults` (565/567 passing). Reverted back to
`true`, rebuilt, reran: 567/567 green again.

## "Restore all default settings"

Verified by inspection and by the existing `RestoreDefaults_resets_every_customised_field_across_all_four_tabs`
test (now touching the field to `false` first, per above): `QAdvanceFeedbackSettings.RestoreDefaults()` and
`CreateDefault()` both simply construct a fresh `GForceSettings()`, so the restore path picks up the new
`true` default automatically — no separate logic to update.

## Pulse-vs-shake interaction (`ProjectedWithoutPulse`)

`GForceShakeStabilityTests.cs`'s `NewShakingEngine()` helper already sets
`IntegrateWheelLockAndSlip = true` explicitly (it always has — this suite predates the default flip and
was written to test the shake with the feature deliberately ON). It does not depend on either default, so
this suite is unaffected by the flip and continues to pass, confirming the shake still reads
`ProjectedWithoutPulse` stably (no pulse-driven wobble) with this feature now live for every fresh install.

## Inertness with no lock/slip signal — confirmed

The shake amplitude is computed as `band = gForceValue * (wheelValue/100) * scale` (see
`GForceEngine.Compute`); `wheelLockAll0100`/`wheelSlipAll0100` default to `0.0` when a caller (or a driver
who has never wired up the Wheel Lock/Wheel Slip channels) doesn't supply them, which collapses `band` to
`0` regardless of `gForceValue`, `scale`, or frequency. This is exercised directly by
`GForceEngineShakeTests.Wheel_value_of_zero_produces_no_shake_even_when_enabled`, which asserts
byte-for-byte-equal output between a disabled engine and an enabled-but-zero-wheel-value engine (9-decimal
tolerance) across 50 frames of varying G. Net effect: a driver who installs fresh and never touches the
Wheel Lock/Wheel Slip tabs sees **no behavioural change whatsoever** — the new default only becomes visible
once real lock/slip telemetry is flowing.

## Docs and screenshot

- **`README.md`** — "Integrate Wheel Lock and Slip" paragraph rewritten: "Off by default..." ->
  "On by default...", with the inertness guarantee and an escape hatch ("uncheck to fall back to the
  plain... feel") called out explicitly.
- **`README.zh-Hans.md`** — mirrored translation of the same paragraph.
- **In-app UI text** — `StringTableEn.cs`/`StringTableZhHans.cs`'s `GForce.Shake.Note` (the italic note
  shown directly under the "Integrate Wheel Lock and Slip" toggle on the actual settings screen) also said
  "off by default" and would have shown up wrong in the regenerated screenshot if left alone — updated in
  both languages to match.
- **Screenshot** — regenerated via the throwaway WPF harness at
  `...\scratchpad\qpreview\Preview.csproj` (kept outside the repo, references the freshly-built Release
  `QAdvanceFeedback.dll`). The harness renders all four tabs and names the G-Force one
  `settings-g-force.png` (from the tab header "G-Force"); copied that one file only, renamed, to
  `docs\images\settings-gforce.png`. Visually confirmed: toggle reads "On", note text reads the new
  wording. The other three PNGs in `docs\images\` (`settings-wheel-lock.png`, `settings-wheel-slip.png`,
  `settings-general.png`) were not touched — confirmed by unchanged file timestamps/sizes after the
  harness run (which regenerates all four into the scratch dir, but only the G-Force one was copied back).

## Concerns

- None blocking. The only judgment call is the "keep the two defaults disagreeing" decision above; I
  believe it's the coherent, low-blast-radius choice and have documented it in both source files and the
  test that exercises it, so it can't silently regress into an unexplained inconsistency later.
- Not independently re-verified: actual driver-felt behaviour in a live SimHub session (as already
  disclosed elsewhere in this project — nothing in this codebase has been run inside SimHub against a real
  game or real hardware; all evidence here is unit-test-level and one offline rendered screenshot).
