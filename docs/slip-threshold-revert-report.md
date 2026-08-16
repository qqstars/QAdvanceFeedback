# Slip brake-threshold default revert — QAdvanceFeedback

## STATUS

**Reverted.** `LegacyThresholds.Defaults.SlipBrakeThresholdPercent` is back to **100.0** (Wheel Slip
throttle-only by default), undoing a previous pass's change to 20.0. That previous change was technically
correct about SimHub (`WheelSlipEffect.GetRpmSpeedSlipLegacy` really has no `IsLock` branch — Lock and Slip
get the identical `Brake > 20` gate) but wrong to act on: it overrode the owner's own explicit, on-record
instruction — *"for Slip, percentage of Break Pedal Pressed (default 100%) OR throttle Pedal Pressed
(default 40%)... by default, set the break pedal presses as 100%, which means only throttle pedal pressed
will trigger wheelSlip."* The owner has since driven with throttle-only Slip and confirmed it "feels good,
reasonable — you can remain the current WheelSlip." Matching SimHub's own undifferentiated behaviour is not
worth overriding a deliberate, owner-tested product decision.

**Tests:** 568 total (full tree, `Private\` present), 0 failed, 0 skipped.
**Clean-clone tests:** 523 total (`Private\` absent, verified in an isolated scratch copy outside the
working tree), 0 failed, 0 skipped.
**Build:** `dotnet build QAdvanceFeedback/QAdvanceFeedback.csproj -t:Clean,Restore,Build
-p:Configuration=Release -v:m` → **0 Warning(s), 0 Error(s)** for both the full tree and the clean-clone
copy. `bin/Release/net48/` contains exactly `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb` in both cases
— single-DLL distribution intact.

---

## WHAT WAS CHANGED

1. **`QAdvanceFeedback\Core\LegacyThresholds.cs`** — `Defaults.SlipBrakeThresholdPercent`: 20.0 → **100.0**.
   Doc comments on the struct, on `SlipBrakeThresholdPercent` itself, and on `Defaults` rewritten to state
   plainly: the SimHub finding (no `IsLock` branch) is genuine and kept documented, but the owner's explicit
   100% instruction is what ships, and the setting remains lowerable to 20 by any driver who wants SimHub's
   real brake-responsive Slip.

2. **`QAdvanceFeedback\Settings\WheelChannelSettings.cs`** — `CreateDefaults` (Slip branch):
   `BrakeThresholdPercent`: 20.0 → **100.0**. Comment corrected to match.

3. **`Private\QAdvanceFeedback\LegacySlipAlgorithm.cs`** — the in-code comment above the Slip brake/throttle
   branch corrected (was asserting the 20-default was the "corrected" state); no algorithm logic touched,
   only prose. (This file is gitignored/withheld, but it compiles as part of the full 568-test tree, so its
   comments were fixed for consistency.)

4. **Localization** — `Sources.Threshold.Slip.Note` rewritten in both `StringTableEn.cs` and
   `StringTableZhHans.cs` to describe the 100%-default/throttle-only behaviour and explicitly mention
   lowering to 20% to match SimHub/Lock. The Chinese text was generated via a Python script computing
   `\uXXXX` escapes and round-trip-verified (decoded back to the intended Chinese and compared for exact
   equality) before being written into the source file — no raw CJK went through a shell/heredoc.
   `LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set` still passes (no key
   added/removed, only an existing value's text).

5. **`docs\raw-match-rootcause-report.md`** — an ADDENDUM section was added at the top (before its own
   STATUS section) stating plainly that its §3 "fix" was reverted, and reframing the surviving finding
   correctly: the SimHub/ShakeIt braking gap for Slip is a **deliberate, owner-chosen divergence**, not a
   defect. The rest of that report's §1/§2/§3 narrative is left in place as the historical record of the
   investigation (explicitly marked as "what this pass argued and did", not the current shipped state) —
   nothing was deleted, since the underlying decompilation/log analysis is genuine and useful.

## TESTS RESTORED / ADJUSTED (all in the withheld `Private\QAdvanceFeedback.Tests\LegacySlipAlgorithmTests.cs`
unless noted — this file holds the fidelity tests against the withheld `LegacySlipAlgorithm`)

- **`Braking_branch_uses_new_ground_speed_for_both_halves_of_the_delta`** — restored to assert
  `fl.Slip == 0.0` (brake=70 no longer exceeds Slip's own default-100 threshold, and throttle is 0) —
  this is the exact test the task's own MUTATION EVIDENCE example referenced.
- **`Slip_brake_threshold_at_its_default_20_now_fires_before_throttle_matching_SimHub_and_Lock`** —
  renamed to **`Slip_brake_threshold_at_its_default_100_disables_the_brake_path_so_only_throttle_triggers_slip`**
  and rewritten to assert the default (100) makes brake=100 fail its own threshold check, falling through
  to throttle (which fires and produces the result).
- **`Slip_brake_threshold_can_still_be_raised_back_toward_100_to_restore_the_old_throttle_only_behaviour`**
  (the test the previous pass added to prove configurability) — repurposed into
  **`Slip_brake_threshold_can_be_lowered_to_20_to_match_SimHub_and_Lock_when_a_driver_wants_it`**, which
  now proves the OTHER direction of configurability required by this task: a driver who explicitly sets
  `SlipBrakeThresholdPercent = 20.0` gets Slip responding to braking exactly like Lock/SimHub's real
  algorithm. Net test count in this file is unchanged (one renamed+rewritten, one repurposed) — no test
  added or removed.
- **`WheelChannelSettingsTests.cs` → `Lock_and_slip_defaults_ship_ShakeIt_mode_and_the_owners_revised_pedal_thresholds`**
  — assertion changed from `Assert.Equal(20.0, slipDefaults.BrakeThresholdPercent, 6)` back to
  `Assert.Equal(100.0, ...)`, comment corrected.
- Everything else that touches `LegacyThresholds`/`SlipBrakeThresholdPercent` elsewhere in the suite
  (`NormalizedWheelLockSlipEngineTests.cs`, `InertLegacyWheelLockSlipEngineTests.cs`,
  `LegacyWheelLockSlipEngineTests.cs`) already used explicit/custom threshold values rather than relying
  on the default, so none of them needed changes — checked directly, not assumed.

## CONFIGURABILITY VERIFIED

`Slip_brake_threshold_can_be_lowered_to_20_to_match_SimHub_and_Lock_when_a_driver_wants_it` (see above)
explicitly sets `SlipBrakeThresholdPercent = 20.0` and confirms the brake-driven term wins over throttle on
the same frame, on the same wiring the settings UI uses (`WheelChannelSettings.BrakeThresholdPercent` →
`LegacyThresholds.SlipBrakeThresholdPercent`, unchanged) — a driver who wants SimHub's real, brake-responsive
Slip can get it by lowering the spinner from 100 to 20 in the UI, no code change required.

## SIMHUB-DIVERGENCE FINDING — STILL DOCUMENTED

The genuine finding from the previous pass (SimHub's own decompiled `GetRpmSpeedSlipLegacy` has no
`IsLock` branch, so ShakeIt's real Slip output is non-zero under braking, accounting for the bulk of the
observed Slip-vs-ShakeIt gap during braking frames — 165/198 braking frames where this plugin's own Slip
read exactly 0 while ShakeIt averaged 64.9) is **kept, not deleted**, in:
- `QAdvanceFeedback\Core\LegacyThresholds.cs`'s own remarks (struct-level and `Defaults`-level).
- `Private\QAdvanceFeedback\LegacySlipAlgorithm.cs`'s in-code comment.
- `docs\raw-match-rootcause-report.md`'s new ADDENDUM section (plus its original §1/§3a/§3b, left intact).

All three now frame it correctly: **this is the plugin's deliberate, owner-chosen divergence from SimHub,
not a defect** — anyone comparing this plugin's Slip.Raw to ShakeIt's own published Slip during braking
should expect a large, intentional gap for exactly this reason.

## MUTATION EVIDENCE

Set `LegacyThresholds.Defaults.SlipBrakeThresholdPercent` back to 20.0 and re-ran the two directly-affected
tests:

```
Slip_brake_threshold_at_its_default_100_disables_the_brake_path_so_only_throttle_triggers_slip [FAIL]
Expected: 0.52287581699999996 (rounded from 0.52287581699346497)
Actual:   1.568627451 (rounded from 1.568627450980395)

Braking_branch_uses_new_ground_speed_for_both_halves_of_the_delta [FAIL]
Expected: 0 (rounded from 0)
Actual:   5.7142857139999998 (rounded from 5.7142857142857153)
```

Reverted to 100.0; full 568-test suite green again (confirmed above).

## CONCERNS

- **Screenshots not regenerated** — the Wheel Slip tab's Trigger Threshold spinner default reverts from
  20 back to 100; a screenshot taken with default settings would show this, but no SimHub/game/harness is
  available in this environment to regenerate it (same limitation the previous two passes noted).
- **`docs\raw-match-rootcause-report.md`'s historical §3 narrative still uses "FOUND AND FIXED"/"the fix"
  language** in its body (not just the STATUS line) — left as-is on purpose (historical record of that
  pass's own reasoning, now clearly superseded by the ADDENDUM at the top) rather than rewritten
  sentence-by-sentence, to avoid quietly erasing the record of what was argued and why it was wrong to act
  on. If a fully rewritten (not just addended) version of that report is wanted, that would be a follow-up.
- **No live-game/SimHub/hardware verification** — this revert is verified by build + full/clean-clone test
  suites + the mutation check above only, consistent with this environment's stated constraints.
