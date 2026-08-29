# v1.0.7 — Tiered Cold-Start Reference System

## Version stamp

`AssemblyVersion`/`FileVersion` bumped from `1.0.6.0` to **`1.0.7.0`** (four-part field, matches the
project's own existing convention exactly — `QAdvanceFeedback\QAdvanceFeedback.csproj`).

## Interpretation of "the tier percentage" (Projected-side floor)

Confirmed as specified: the tier percentages (50/60/70/80%) are implemented as the starting **FLOOR of
the existing Layer-5 `ColdStartScale` Stage-1 ramp** (`Core\Projection\ColdStartScale.cs`), not as a
percentage of the Normalized value. `ColdStartScale.Compute(confidence, floor)` now takes a `floor`
parameter (default `MinSafeFloor` = 0.5, so every pre-existing caller is untouched); the owner's own
seven anchor points (fitted at floor 0.5) are reproduced exactly and then affinely re-mapped so
`0 → floor` and `1 → 1.0`, preserving the monotone-Hermite curve's own monotonicity/smoothness at any
floor ≥ 0.5. `ColdStartTierFloors.FloorFor(tier)` supplies 0.50/0.60/0.70/0.80 for Tier 1–4. The owner's
own worked Tier-4 example (dry 75, wet-cold 68, blended 73) is a **Normalized-layer** number, produced
separately by the tiered reference blend inside `KeyedScaleLearner` (see below) — the two mechanisms
compose: the reference blend changes what value 73 *is*, and the tier floor changes how strongly Layer 5
damps *that* value while the channel is still cold. Please correct me if a different reading was intended.

## Reconciliation with the existing cross-car seed

`KeyedScaleLearner`'s old `_crossCarSeed` mechanism (keyed by `(gameId, sourceIdentity)`, ignoring car)
covered exactly Tier 3's scope and nothing else, and was capped to never amplify. It is now **vestigial**:
`ObserveAtPhysicalLimit` no longer writes to it, `ExportCrossCarSeeds`/`ImportCrossCarSeeds` are kept
(for API/persisted-file compatibility — `RuntimeDocument.Version` 8→9) but are no-ops. A new resolver,
`KeyedScaleLearner.ResolveReference`, generalises the same idea across all four tiers by scanning the
**same substrate the old mechanism itself drew from** (`_physicalAnchor`, i.e. exactly what
`ExportAll`/`ImportAll` already persist) rather than maintaining a second seed dictionary — one mechanism,
not two layered together.

The reconciliation of the two mechanisms' conflicting intents (old: never amplify; new: tiers 2–4 should
raise a cold key's start) is **tier-dependent**: Tier 2 (a different game — the highest cross-context risk,
arguably worse than the old cross-car case since even native units may differ) **keeps** the old
never-amplify cap. Tier 3 (same game, different car) and Tier 4 (same game+car, different surface)
**deliberately drop it**, since the brief is explicit that a same-game/same-car reference should be able to
raise output. This is a stated interpretation call — `ColdStartAndCrossCarSeedTests.cs` documents it
alongside updated tests (the old "never amplify, even same-game" tests are renamed/rewritten to assert the
new behaviour; a new `MutationGuard_a_different_game_still_never_amplifies_a_cold_reading` test confirms the
old gate's safety intent **is** preserved for Tier 2).

**Whether the old gate's safety intent is preserved:** partially, and by design, not by accident. It is
fully preserved for Tier 2 (the case it was built for). For Tier 3/4 it is intentionally relaxed per the
brief; the residual risk (a same-game car with a wildly different native scale legitimately amplifying a
cold reading) is the one place I'd ask the owner to confirm the trade-off — see Concerns.

## Tier-resolution rules (including tie-break)

- **Same input source only**, always — the resolver filters candidates on the source segment of the
  composite key before considering anything else; a Raw store is never offered to a ShakeIt key.
- Preference order (most specific wins): **Tier 4** (same game+car, different surface) > **Tier 3** (same
  game, different car) > **Tier 2** (different game) > **Tier 1** (nothing).
- Within a tier, the candidate with the **most samples** (its primary-tier `Count`) wins.
- **Tie-break**: the lexicographically smallest composite key (game|#|car|#|source|#|surface), ordinal
  comparison — fixed, deterministic, not itself semantically meaningful. Stated explicitly per the brief's
  own request; covered by `A_tie_on_sample_count_is_broken_by_the_lexicographically_smallest_composite_key`.
- Tier 4 candidates are only ever considered when the caller supplies a **real, non-empty surface hint**
  (the internal engine call always does — `Sealed`/`Loose`, mirroring `LockCurrentSurfaceBucket`); a
  hint-less caller (nearly every pre-existing direct `KeyedScaleLearner` test) never risks Tier-4
  self-contamination from the additive surface-tagged bookkeeping described next.
- A cold key's own resolved tier is **cached once it has its own first primary-tier observation** (never
  re-scanned afterwards, so an unrelated car maturing later cannot retroactively perturb an
  already-settling key — a real regression caught and fixed during implementation, see Concerns). While a
  key has genuinely zero evidence of its own, resolution is re-tried on every query (cheap — the candidate
  pool is the number of distinct keys ever seen, not per-frame telemetry volume).
- Surface identity (constraint #5): confirmed by test — if the *current* surface already has its own
  primary-tier evidence, Tier 4 does not apply, regardless of how much evidence a *different* surface has
  (`Tier4_does_not_apply_when_the_current_surface_already_has_its_own_evidence`).

Persisted state: no new dictionary or field was needed — the resolver reads directly from
`LockScaleLearners`/`SlipScaleLearners`' own already-persisted per-key entries (richer keys, same shape).
`RuntimeDocument.Version` was still bumped 8→9 to document `LockScaleCrossCarSeed`/`SlipScaleCrossCarSeed`
becoming vestigial (an old file's seed section is now silently dropped, never acted on).

## Unit-test results per tier

All in `QAdvanceFeedback.Tests\TieredColdStartReferenceTests.cs` (24 new tests), plus updates to
`ColdStartAndCrossCarSeedTests.cs` and `RegressionFixTests.cs` — full list below, all green:

| Check | Result |
|---|---|
| Tier 1 resolves when no same-source reference exists anywhere | pass |
| Tier 1 is bit-identical to pre-1.0.7 for a genuinely cold, reference-less key (exact, 9dp) | pass |
| Tier 2 resolves for same source, different game | pass |
| Tier 3 resolves for same source+game, different car | pass |
| Tier 4 resolves for same source+game+car, different surface (explicit hint) | pass |
| Tier 4 does not apply when the current surface already has its own evidence | pass |
| Tier 4 requires an explicit surface hint; never self-contaminates a hint-less query | pass |
| A Raw reference is never borrowed for a ShakeIt key | pass |
| Most-samples candidate wins within a tier | pass |
| Tie on sample count broken by lexicographically smallest key | pass |
| A warm key (250 samples) is unaffected by a cross-context reference appearing later | pass |
| Mutation evidence: forcing Tier 1 removes the Tier-3 improvement (unit-level) | pass |
| Each tier's Projected floor = 50/60/70/80% exactly | pass |
| Ramp still reaches exactly 100% at confidence 1, for every tier's floor | pass |
| Tier 1 floor bit-identical to the pre-1.0.7 default overload | pass |
| A floor below 0.5 is clamped up, never down | pass |
| A higher floor is permitted and produces a higher starting scale (monotone across tiers) | pass |
| `ProjectedWheelLockSlipEngine` floor defaults preserve pre-1.0.7 behaviour | pass |

## Real-log results

Harness: `QAdvanceFeedback.Tests\RealLogTieredColdStartReportTests.cs` — replays the real
`NormalizedWheelLockSlipEngine`/`ProjectedWheelLockSlipEngine` pipeline against each log's own captured
`Diag.Source.Lock/Slip.*` (configured-source input) and `WheelLock/Slip.Raw.*` (Layer-3 fallback) columns,
causally, frame order. Each log is replayed **twice** from the same test run: once with
`KeyedScaleLearner.ForceTier1ForTesting = true` ("before", reproduces pre-1.0.7 exactly) and once normally
("after"). 14/14 usable logs found and replayed (counts below match the brief's own "14 usable logs" tally:
8 from `1.0.6_logs`, 4 from `Common_1_5_e_d`, 2 files from `c_1_5_3_e_d`, each containing two car segments).

**Exclusions applied, with dropped counts** (found by scanning for `GroundSpeedKmh` teleport
discontinuities, since these particular logs have no other restart marker):
- `old-logs\...210223` (raw) and `...210421` (shakeit): excluded per brief, not replayed at all.
- `old-logs\...230140`: not part of the "14 usable" set (used elsewhere, `BranchDispatchReplayTests`) —
  excluded from this analysis.
- `I_1_6_8_e_d` Raw: a teleport (`GroundSpeedKmh` 3.0→329 km/h in one frame) at row 3613, elapsed ≈ 60.9s
  from the first frame — matches "restart at t≈61s". Truncated to rows 1–3612; **6887 rows dropped**.
- `Common_1_5_e_d\...WithShakeItDry`: a teleport (0.08→238.7 km/h) at row 3197 — matches "the 3196-frame
  restart" (pre-restart segment is exactly 3196 rows). Truncated to rows 1–3196; **3756 rows dropped**.

**Which tier each replay resolved to:** every `1.0.6_logs` and `Common_1_5_e_d` file is a single
(game,car,source[,surface]) combination replayed in its own fresh engine with nothing else to borrow from
— all 12 of those resolve **Tier 1**, and their before/after numbers are **bit-identical** (confirmed
directly in the harness output — e.g. I_1_6_5 ShakeIt: peak 98.0/98.0, >90 fraction 0.6%/0.6%, both
before and after, every log). This is the required "Tier 1 unchanged" confirmation on real data.

`c_1_5_3_e_d` (both ShakeIt and Raw files) is the Tier 3 exercise the brief points to: Sauber (driven
first, WET/Silverstone) matures a real same-game/same-source reference; F1 Generic (driven second,
DRY/Abu Dhabi — a genuinely different car and surface) then resolves **Tier 3** in both files (Tier 4 did
not engage here because these two cars are logged as literally different cars, not the same car on two
surfaces, so Tier 3 — same game, different car — is the correct, most-specific match; a genuine same-car
different-surface case is exercised directly by the unit tests above since none of the 14 real logs happen
to replay the identical car on two surfaces within one file).

F1 Generic, first ~60s of its own segment vs the rest, before (Tier-1-forced) vs after (Tier 3 engaged):

| File | Metric | Before | After |
|---|---|---|---|
| ShakeIt | early LOCK peak | 92.4 | **95.5** |
| ShakeIt | early LOCK >90 frac | 0.1% | **0.8%** |
| ShakeIt | early SLIP peak | 47.7 | **72.1** |
| ShakeIt | rest LOCK peak | 92.8 | 97.9 |
| ShakeIt | rest SLIP peak | 9.9 | 35.5 |
| Raw | early LOCK peak | 97.3 | **98.7** |
| Raw | early LOCK >90 frac | 0.7% | **1.7%** |
| Raw | early SLIP >90 frac | 1.3% | 1.8% |

Early-lap output **rises** exactly as expected for a Tier-3 engagement, in both files, on both channels.
The "rest of lap" figures also rise somewhat (Slip markedly, in the ShakeIt file) — this is an honest,
expected consequence of Tier 3 legitimately raising this key's calibration throughout its life, not a
destabilisation: F1 Generic's own evidence continues to accumulate and dominate the blend as its own
`weight` grows (see `LearnedCeilingForKey`), and no channel-invariant (aggregation weights, curve anchors,
`CanonicalAtLimitAnchor`, endpoints) changed. Sauber (the reference car) shows **zero** change before vs
after in both files, confirming the reference car itself is unaffected by being borrowed from.

**Mutation evidence:** the "before" column above **is** the mutation-evidence run — forcing tier
resolution to Tier 1 for the entire replay removes the early-lap improvement exactly where Tier 3 would
otherwise have engaged (F1 Generic), while leaving every other log's numbers untouched (they were already
Tier 1). This is produced from the same harness/build, not a separately checked-out snapshot.

## Endpoint and no-regression confirmation

- `source == SMax → 80` and `source == 100 → 100`: unchanged — governed by `KeyedScaleLearner.Rescale`'s
  own pre-existing formula and `CanonicalAtLimitAnchor = 80`, neither of which this task modified; the full
  pre-existing regression suite (endpoints, clamp-chain fix, aggregation weights, curve anchors, Normalize
  Pattern selector, `LockAnchorLearner`, counter saturation caps) is green — 1020/1020.
- Speed-aware reference (pooled ratio >1.0 near 17.8%) and the clamp-chain fix: untouched files
  (`GripLearner.cs` speed-model, `NormalizedWheelLockSlipEngine`'s clamp-then-scale block) — their own
  pinned tests pass unchanged.
- No hard sample gate anywhere: the tier resolver adds no new absolute-count gate; it reuses the existing
  continuous `ConcaveHotWeight` ramp for the blend weight and a simple "has ≥1 own observation" latch only
  for *which* answer to cache, not for whether learning continues.

## Mutation evidence (summary)

1. Unit-level: `MutationEvidence_forcing_tier1_removes_the_tier3_improvement` — 80.0 (Tier 3) vs 90.0
   (forced Tier 1) for an identical synthetic scenario.
2. Real-log: the before/after table above, produced by the same `ForceTier1ForTesting` switch across the
   full real-log harness.

## Test count, warnings, single DLL

- **1020/1020 tests pass** (994 baseline + 26 new/changed: 24 new tier tests, 1 new real-log replay test,
  and net-1 from renaming/rewriting 3 superseded `ColdStartAndCrossCarSeedTests` and updating 1
  `RegressionFixTests` threshold).
- `dotnet build -c Release` (after a full Clean+Restore): **0 warnings, 0 errors**.
- `bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb` — single-DLL
  distribution preserved.

## Concerns

1. **Tier 3/4 amplification risk is real, not just theoretical.** Removing the "never amplify" cap for
   same-game (and same-car) references means a wildly-different-native-scale car within the same game/game
   engine (e.g. a mod, or a title where per-car telemetry scaling genuinely varies) could still produce a
   larger-than-warranted cold-start jump. The Layer-5 floor is *higher*, not lower, for these tiers, so it
   damps *less*, not more, at exactly the moment this risk is highest. I judged same-game/same-car
   reference scale to be "usually comparable" per the brief's own explicit instruction, but this is the one
   place I'd most want the owner's sign-off.
2. **Tier resolution locks in at a key's first own observation and never re-scans afterwards.** This fixed
   a real regression (a car's own calibration must not retroactively change because an unrelated car
   matured later — `Switching_away_and_back_within_one_session_reproduces_the_first_visits_mapping_exactly`)
   but means a key that happens to observe its own first physical-limit moment *before* a good reference
   exists is stuck at whatever tier was available then for the rest of the session (even if a much better
   reference appears moments later). Acceptable trade-off, stated explicitly rather than hidden.
3. **Tier 4 could not be exercised on real logs** — none of the 14 usable logs replay the identical car on
   two different surfaces within one file; it is unit-tested (with a synthetic store) but not real-log
   confirmed. `c_1_5_3_e_d`'s two cars are also two different surfaces/tracks, which is why they resolve
   Tier 3 (the more specific tier, different car, applies) rather than Tier 4.
4. **Diag.Lock/Slip.ColdStartTier is a best-effort readout**, computed once per frame outside
   `ComputeChannel` from the same smoothed loose-fraction/`SurfaceLearningPurityThreshold` classification
   `LockCurrentSurfaceBucket` already exposes, to avoid adding an `out` parameter to every one of
   `ComputeChannel`'s several early-return paths in an already very large (2000-line) method. It should
   match the tier the live blend actually used in the overwhelming majority of frames, but was not proven
   frame-for-frame identical to an internal-only readout.
