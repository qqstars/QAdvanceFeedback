# QAdvanceFeedback 1.0.6.0 Release Report

Status: build green, all tests green, version stamped `1.0.6.0`. This report documents what changed,
what was verified from earlier sessions' claimed work, and the acceptance-replay evidence for this
release.

## Final state

- **0 build warnings, 0 errors** (`dotnet build QAdvanceFeedback.sln -c Release`, full Clean/Restore/Build
  cycle run and confirmed).
- **994 tests, all green** (`dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -c Release`).
  Started at 989 tests / 6 failing; the 6 failures were fixed (see below, one of them renamed rather than
  weakened), and 5 new tests were added on top of the original 989 (2 proving the newly-found
  `OnlineDistributionLearner.MaxSamples` overflow gap is now closed, 3 for `NormalizePattern` defaults/
  JSON round-trip/restore-defaults) — 989 + 5 = 994.
- **Single-DLL distribution confirmed**: `QAdvanceFeedback\bin\Release\net48\` contains only
  `QAdvanceFeedback.dll` and `QAdvanceFeedback.pdb`.
- **Version**: `AssemblyVersion`/`FileVersion` both `1.0.6.0` in `QAdvanceFeedback.csproj`, confirmed by
  reading the built DLL's own `FileVersionInfo` (`1.0.6.0`), and displayed on the General settings tab.

## Step 1 — restoring green

The 6 failing `OutputProjectorTests` were failing because Lock's `CriticalOutput` default was
intentionally changed 80 → 60 (owner's explicit request, already implemented by an earlier session in
`ProjectorSettings.cs`). Each test was updated to assert the new, correct values rather than reverting
the default:

- `ApplyPreset_curve_slip_produces_slip_defaults_not_lock_defaults` — Lock's expected `CriticalOutput`
  changed 80 → 60.
- `Curve_default_passes_through_named_anchors` — `Project(78.0)` expected range changed to 59.5–60.5.
- `Curve_default_plateau_numbers_lock_channel_match_the_measured_report_table` — the Ideal plateau's own
  upper edge (`Project(64.0)`) moved 31.25 → 30.75 (confirmed algebraically: the monotone-cubic fit's
  right-hand neighbour for that edge is Critical's own output, which is now 60 not 80); the Max Grip
  plateau's own lower/anchor/upper-edge values also updated (78.75→59.25, 80→60, 80.3636→60.727).
- `Default_curve_preset_sends_expected_values_lock_channel` and
  `Preset_custom_with_curve_default_anchors_behaves_like_curve` — same `Project(78.0)` range fix.
- `Lock_curve_default_puts_near_the_limit_around_75_to_80_and_fully_locked_at_100` — **renamed** to
  `Lock_curve_default_reaches_its_own_max_grip_output_at_78_and_fully_locked_at_100`, since its own
  premise changed: Max Grip (input 78) no longer reads near the top of the 0–100 range (it now reads a
  moderate 60), so the test was re-expressed truthfully (checks the new ~60 value at 78, and that 100
  still unconditionally reads the true maximum) rather than forced to keep asserting "near the limit."

All exact replacement values were derived from the real `OutputProjector` output (a temporary diagnostic
test dumped the actual current values for every probe point, which were then hand-verified against the
monotone-cubic math before being written into the permanent assertions).

**`Part5OverflowAndNonStoppingLearningTests.RaiseHits_saturates_at_the_cap_while_SMax_keeps_learning`**:
this test was already passing on the very first run (989 tests, 6 failures — this was not one of them).
Reading its current code confirms the earlier session's diagnosis was correct and already fixed: the
production `SampleCountSaturationCap` is 1,000,000, but `RaiseCandidateHits` only advances on a call that
is NOT itself sitting in the post-raise cooldown window (`RaiseCooldownQualifyingSamples = 3`), so
roughly 1 in 4 calls actually increments the counter. The test now loops `4 * SampleCountSaturationCap +
20` times (with an explanatory comment) to genuinely reach the cap. **The bug was in the test's own loop
arithmetic, not the production code** — confirmed, not re-broken.

## Verification of "already done" work (Parts 1, 2 core, 3, 5)

Verified directly (by reading the code and running the relevant tests) and via three parallel read-only
research passes:

- **Part 1 (Slip reverted to 1.0.6.3's formula) — CONFIRMED.**
  `NormalizedWheelLockSlipEngine.cs`: `configuredForSeverity`/`fallbackForSeverity` are gated by
  `useFourRangeForSeverity` (`false` on the Slip call site), collapsing the published formula to
  `calibratedMean * (1 - fallbackWeight) + calibratedRawFallback * fallbackWeight` for Slip — bit-
  identical to 1.0.6.3. `ComputeDeltaGCollapseSeverity` is still called with its result discarded
  (`_ = ComputeDeltaGCollapseSeverity(...)`), keeping the diagnostic alive. `DeltaGCollapseBandMappingTests`
  (13 tests) all pass.
- **Part 3 (fallback ratios) — CONFIRMED.** `S90FallbackRatioOfSmax = 0.750 * 1.125 = 0.84375`,
  `S75FallbackRatioOfSmax = 0.40`, via `RatioOfSmaxFallback`/`TryBuildLockRangeCurveWithFallback`. Read-
  time only — no assignment back into `LockAnchorLearner`'s own persisted fields anywhere in the method.
  The determinism fix (a key no longer silently flips between the fallback curve and plain
  `calibratedMean` across repeat queries with no new evidence) is documented in the class's own remarks
  and covered by `S90FallbackRatioTests` (3 tests, all pass).
- **Part 2 core logic — CONFIRMED.** `enum NormalizePattern { MaxGripOnly, Mapping }`,
  `LockNormalizePattern` defaults to `Mapping`, threaded into `ComputeChannel` via
  `useFourRangeForSeverity: LockNormalizePattern == NormalizePattern.Mapping` on the Lock call site only.
  Curve-building and S75/S90/SMax learning run unconditionally regardless of the flag; only published
  severity and `LockFourRangeCurveActive` change. Exactly 4 tests in `NormalizePatternTests.cs`, all pass.
- **Part 5 overflow audit — 3 of 4 CONFIRMED, 1 gap found and fixed.**
  - `GripLearner.AdaptivePeakState.RaiseHits`/`LowerHits`: capped at `SampleCountSaturationCap = 1,000,000`.
  - `LockAnchorLearner`'s accepted/rejected/hit counters: same cap, same pattern.
  - `StreamingPercentileLearner._totalObservations`/per-bucket counts: same cap; once saturated,
    `_runningSum += value - _runningSum / cap` (the documented decaying-mean formula) takes over.
  - **`OnlineDistributionLearner.MaxSamples` (7000) — gap found, NOT pre-existing/confirmed as claimed.**
    The class's own doc comment claimed this was enforced "at the call site" (mirroring SimHub's own
    `CalibrationPointsAdded <= 7000` gate), but `MaxSamples` was referenced nowhere else in the codebase
    — `KeyedScaleLearner.ObserveAtPhysicalLimit`/`ObserveGeneral` fed the learner unconditionally, so its
    internal `_histogram` dictionary had no real ceiling for the life of a session. **I do NOT agree this
    should be left as-is** (it's a genuine, if slow-growing, unbounded-memory risk over a very long
    session, and — more importantly — it directly contradicts the class's own documented contract, which
    is worse than an undocumented gap). Fixed by wiring the gate into both call sites
    (`if (learner.Count < OnlineDistributionLearner.MaxSamples) learner.AddValue(...)`), with two new
    regression tests (`KeyedScaleLearner_physical_anchor_tier_stops_folding_in_new_samples_past_MaxSamples`
    / `..._general_tier_...`) proving both tiers now genuinely stop folding in new evidence past 7000
    samples, matching SimHub's own real convention rather than merely guarding against int overflow.

## G-Force readout (Part 4e) — root cause

**No further source-level difference exists versus 1.0.6.3.** A full diff of `Settings\SettingsControl.xaml`
and `Settings\GForceSettings.cs` against 1.0.6.3 found them byte-identical. `Settings\SettingsControl.xaml.cs`
has exactly one diff: the `DispatcherTimer` fix an earlier session already added (a `_gforceRefreshTimer`
field ticking once per second, calling `RefreshGForceLearnedText`) — the fix the owner reports did not
resolve the problem. The whole live read/write path was traced end-to-end
(`GForceSettings.SetCurrentGameAndCar` → `ObserveAccelG`/`ObserveDecelG` every valid frame →
`EffectiveAccelMaxG`/`EffectiveDecelMaxG` feeding the live severity path → `RefreshGForceLearnedText`
reading `TryGetCurrentAccelAutoDetected`/`DecelAutoDetected` off the identical settings instance) and
found internally consistent, with no stale-instance, dead-object, or mismatched-key bug anywhere in it.

Since the bug persists despite full source parity and an already-attempted timer fix, this is very
likely a real-host/runtime issue invisible to a static diff, not something a further guessed source
change should try to paper over. Recommended live diagnostics for the owner:

1. Add a temporary log line inside `_gforceRefreshTimer.Tick` (e.g. `SimHub.Logging.Current.Info` with
   `DateTime.UtcNow`) — confirms whether the timer genuinely ticks inside real SimHub's hosted
   `Dispatcher` (rules out "this UserControl's Dispatcher isn't pumping while its tab is inactive").
2. Log `_currentGameId`/`_currentCarId` inside `SetCurrentGameAndCar` for one real session — confirms the
   live per-frame path is reached and keys match what the UI later queries.
3. Compare the already-published `Diag.GForce.LearnedDecelMaxG`/`LearnedAccelMaxG` diagnostics against
   what the settings panel text shows, side by side, in the same live session — if `Diag.*` shows real
   learned numbers but the panel still says "no data yet," the divergence is specifically in the WPF
   binding/redraw (e.g. a `TextBlock.Text` write from a non-UI thread being silently dropped, or
   `SafeUiAction` swallowing an exception into `HealthRegistry` — check the Plugin Health panel after
   driving for a `SettingsUi` entry).

## Lock max-grip (SMax) learning vs 1.0.6.3 (Part G) — DIVERGES, not reverted

**The All-channel aggregation basis is identical**: `LockUsesAggregatedAllScale = true` in both 1.0.6.3
and this build (only the access modifier changed, `internal` → `public`) — 1.0.6.3's own doc comment
already states this was deliberately set to match "the owner's own 1.0.6.8 build," so this was never a
divergence to reconcile.

**The internal peak-tracking algorithm itself has diverged substantially** from 1.0.6.3's simple,
fixed-gain decaying maximum (`_learnedPeakG *= ForgetPerSample` every call, `+= RaiseAlpha * (magnitude -
level)` on any single higher reading) to the current `AdaptivePeakState`/`ApplyEvidenceWeightedObservation`
mechanism — an evidence-weighted, corroboration-gated estimator with a doubling confidence schedule,
asymmetric raise/lower cooldowns, a data-quality jerk gate, and stability-gated maturity.

**This was a deliberate decision, not left un-reconciled by oversight.** It is extensively documented
(`docs\adaptive-peak-learner-report.md`, `docs\stability-confidence-fix-report.md`,
`docs\speed-aware-grip-report.md`), was built to fix two specific, named defects in 1.0.6.3's simpler
mechanism (it could not distinguish "genuinely lower" from "didn't try this time," and a single outlier
spike moved the estimate a fixed 15% with zero corroboration), and is exactly the code this release's own
Part 5 overflow audit was asked to verify and preserve (`GripLearner.AdaptivePeakState.RaiseHits`/
`LowerHits` are named directly in the brief's own "ALREADY DONE" list as validated, audited code).
Reverting the estimator wholesale to 1.0.6.3's simpler decay would discard that validated work and
directly contradict the same brief's instruction to preserve it — a same-named-field coincidence
("SMax learning") should not be read as license to gut a different, more advanced, already-shipped
mechanism underneath it.

**Recommendation, not applied without sign-off: do NOT revert.** The aggregation basis — what the
brief's own parenthetical singles out — already matches. If the owner still wants 1.0.6.3's exact,
simpler decay after reading this, that is a deliberate downgrade decision for a future release, not a
same-day mechanical fix.

## Screenshots (Part I)

`tools\screenshot-harness` had two real bugs, both fixed:

1. It computed `docs\images` as the intended output directory but then wrote every PNG to a
   bin-directory-local `screenshot-out` folder instead — nothing ever copied those files into
   `docs\images`, so re-running the harness silently never updated the committed screenshots. Fixed to
   write directly to `docs\images`.
2. It named the G-Force capture `settings-g-force.png`, contradicting this repo's own documented
   capture rule (`docs\architecture.md`: `settings-gforce.png`, no hyphen) and the README's own links.
   Fixed to match.

All four screenshots regenerated (`ScreenshotHarness.exe`, from a fresh Release build) and hash-verified:

| File | Old SHA-256 (first 12 hex) | New SHA-256 (first 12 hex) | Changed? |
|---|---|---|---|
| settings-wheel-lock.png | `33b096edf46c` | `378f74905583` | Yes |
| settings-wheel-slip.png | `cd52dcee8327` | `5233fea04520` | Yes |
| settings-gforce.png | `2954d65bb3ec` | `2954d65bb3ec` | **No** (expected — G-Force tab untouched this release) |
| settings-general.png | `161b1bdfee54` | `97a245081e12` | Yes |

Opened and visually confirmed all four: full-height (no clipping — the harness measures/arranges at each
element's own natural `DesiredSize`, not a fixed preview-window height), no cropping, dark
MahApps/SimHub styling throughout. Wheel Lock shows the new "OUTPUT DATA AND SHAPING" header, the
"Normalize Pattern: Max-Grip/S90/S75 Mapping" dropdown with its description text, and the "Powerful
(30)"/"Perfect (60)"/"Max Grip (80)" labels with the corrected "78 to 60" anchor note. Wheel Slip shows
the same renamed section header but no Normalize Pattern selector, and its own fixed "Slightly
(30)"/"Ideal (60)"/"Max Grip (80)" labels (confirming the earlier mis-shared-string-key bug is fixed).
General shows "QAdvanceFeedback version: 1.0.6.0", read live from the built DLL.

## Acceptance-replay evidence

Replayed against the actual built DLLs (1.0.6.0's own and a freshly-rebuilt 1.0.6.3), not against
hardcoded/reimplemented formulas — reusing and extending the pre-existing scratchpad harnesses
(`v1063-compare-harness`, `v1068-compare-harness`, `s75-s90-output-accuracy-harness`), which already
reference real DLLs via `<HintPath>`.

1. **Slip output vs 1.0.6.3 — closely matches, with one flagged anomaly.** Per-log `Slip.All` mean/
   median, 1.0.6.3 → 1.0.6.0: most logs agree within <1 percentage point (e.g. `I_1_6_5_e_d/Raw`
   19.27/3.93 → 19.19/3.92; `Common_1_5_e_d` both Wet/Dry identical to 2 decimals). A few logs differ
   1–2pp. **One real, unexplained exception: `I_1_6_7_e_d/Raw`'s mean nearly doubled (7.87 → 12.34,
   median 1.26 → 2.20).** Since Slip's severity formula is proven algebraically identical to 1.0.6.3's
   (Part 1, above) and confirmed identical on every other log, this is very likely coming from some
   OTHER engine change in the 1.0.6.0 lineage (candidate: calibration-scale-learner behaviour) touching
   this one log's own source/car combination, not a Slip-formula regression — but it is NOT root-caused
   and should not be waved away. **Flagged as an open item, not silently resolved.**
2. **Lock in MaxGripOnly vs 1.0.6.3's Lock output — closely matches.** The 78–82 band's mean/median
   agree within ~0.2–0.5pp on nearly every log. `LockAllMax` (the session peak) differs by ~1pp on a
   couple of logs (e.g. `l_1_6_6_e_d/Raw` 89.45 → 88.43), consistent with — and fully explained by —
   Part G's own finding that the SMax peak-tracking *algorithm* has intentionally evolved beyond
   1.0.6.3's simple decay while the aggregation basis stayed identical; this is expected residual
   variance, not a defect.
3. **Lock in Mapping mode — achieved-G ratios (live 1.0.6.0 data, pooled n=555/860 frames):**
   - **Near-30** (target 75% of available grip): filtered achieved/event-peak ratio median **74.0%**
     — matches the brief's own pre-existing 74.4% baseline almost exactly.
   - **Near-60** (target 90%): filtered ratio median **82.7%** — a real improvement over the brief's
     cited 80.6% pre-1.125-upscaling baseline, but **still short of the 90% target**. The four-range
     curve was actively engaged for 99.5–99.7% of frames in both bands, so this is a genuine
     measurement of the shipped formula, not a cold-start artifact.
4. **Endpoints and full-lock maxima — confirmed, no regression.** On the curve-active path,
   `Evaluate(Smax) = 80.0000` and `Evaluate(100) = 100.0000` exactly, **0 violations across 31,486
   checks**. The separate fallback/`Rescale` path shows only pre-existing, fully-explained transient
   excursions above 80 while `Smax` is still learning mid-session (every log settles back below 80 by
   session end) — not a regression.
5. **Exclusion counts** (identical across both the 1.0.6.3 and 1.0.6.0 replays, confirming the exclusion
   logic itself behaves consistently): `I_1_6_8_e_d/Raw` restart-dropped **3612** frames;
   `Common_1_5_e_d/Dry/ShakeIt` restart-dropped **3196** frames (exact match to the brief's own cited
   figure); `old-logs/210223` and `old-logs/210421` load with `hasTelemetry=False` and contribute
   all-zero stats — correctly inert, correctly excludable on the stated "lacks `Diag.Telemetry.*`"
   grounds.

**Confidence**: items 3–5 are freshly, directly measured against the real finished 1.0.6.0 DLL (high
confidence). Items 1–2 are real dual-DLL replays, not algebra-only (high confidence on the aggregate
closeness claim) — but the `I_1_6_7_e_d/Raw` Slip anomaly in item 1 is a genuine open question.

## Checklist A–J

| Item | Status |
|---|---|
| A — Normalize Pattern UI (dropdown, live label/description switching, section rename, persistence, restore defaults, both string tables) | **Done** |
| B — Part 4(a)(b)(c) flatten ranges (3/2/2), configurable Start/End outputs, anchor inputs 30/62/78 | **Verified present**, persisted, in restore defaults |
| C — Part 4(c) Slip outputs Slightly 10 / Ideal 35 / Max Grip 75 | **Confirmed** (`ProjectorSettings.ApplyPreset(Curve, Slip)`) |
| D — Part 4(e) G-Force readout | **No code difference found beyond the already-attempted DispatcherTimer fix; live diagnostics specified above, no further guessed fix shipped** |
| E — Part 4(f) version label on General tab | **Done**, reads `FileVersionInfo` from the running assembly |
| F — Part 5 learning-guarantee persistence (SMax/S90/S75 in `QAdvanceFeedback.Parameters.json`, including under MaxGripOnly, all three keep learning past 200 samples) | **Verified** — `GripLearnerState.PeakG`/`LockAnchorState.S75`/`S90` are plain public fields (human-readable JSON), unconditionally exported regardless of `NormalizePattern`; existing tests already prove all three keep moving past 1,000+ samples |
| G — Lock max-grip learning parity with 1.0.6.3 | **Diverges (see above) — aggregation basis identical, peak-tracking algorithm intentionally more advanced; not reverted, flagged for owner decision** |
| H — Version stamp `1.0.6.0` | **Done** |
| I — Screenshots regenerated, hash-verified, visually confirmed | **Done** (two harness bugs found and fixed along the way) |
| J — READMEs and `docs\architecture.md` updated | **Done** (both READMEs' test count, curve/Normalize-Pattern description, General-tab bullet; `architecture.md` gained a dedicated "1.0.6.0 changes" section and had one stale "Max Grip output is 80" claim corrected; the parallel `architecture.zh-Hans.md`'s equivalent stale claim was also corrected, though a full mirror of the new section was out of this task's explicit scope) |

## Concerns

- **Near-60 achieved-G ratio (82.7%) still falls short of the 90% target**, though it is a real
  improvement over the pre-1.125-upscaling baseline (80.6%). This is a measured shortfall in the
  shipped Mapping-mode formula, not a bug per se — flagging it rather than either re-tuning the ratio
  constants without owner sign-off or silently reporting only the "improved" framing.
- **`I_1_6_7_e_d/Raw`'s Slip output nearly doubled (mean 7.87 → 12.34) between 1.0.6.3 and 1.0.6.0** on
  this one log specifically, despite the Slip severity formula being proven algebraically identical and
  matching closely on every other log. Not root-caused — flagged as an open item for follow-up, not
  smoothed over.
- **Part G (Lock max-grip learning) is a real, unresolved divergence from 1.0.6.3** — see above. I did
  not revert it without owner sign-off, since doing so would discard validated, tested, documented work
  this same brief asked me to verify and preserve elsewhere. This needs an explicit decision, not a
  same-day mechanical fix.
- **Part 4(e) G-Force readout remains genuinely broken** with no source-level explanation found. Further
  guessing at the fix without the live diagnostics above is likely to waste another cycle the same way
  the DispatcherTimer attempt did.
- `docs\architecture.zh-Hans.md` was only spot-patched (one stale claim fixed), not given a full mirror
  of the new English "1.0.6.0 changes" section — flagging this as an incomplete parity item since the
  task's explicit ask named only `architecture.md`.
