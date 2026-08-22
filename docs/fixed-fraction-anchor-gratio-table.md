# Fixed-fraction anchor -> achieved-G-ratio table (1.0.6.0 speed-aware-fix build)

MEASUREMENT ONLY. No product source file was modified in this or any other folder. The
already-built DLL was used exactly as it stood before this task began:

- **Build under measurement**: `C:\Development\Repos\Samples\simhub\QAdvanceFeedback_1.0.6.0_release_speed_aware_fix\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll`
  (v1.0.6.0, the "speed-aware reference fix" build - 994 tests, 0 warnings, single DLL:
  `QAdvanceFeedback.dll` (329,216 bytes) + `QAdvanceFeedback.pdb` only, confirmed present with no
  other binary in that folder). **Not rebuilt for this task.**
- Two comparison builds, read-only, for the side-by-side digest only (Section "Three-build
  side-by-side"): the untouched `QAdvanceFeedback\` release (v1.0.6.0) and
  `QAdvanceFeedback_1.0.6.0_release_no_speed_aware\` (the no-speed-aware control).

No git operations were performed. No recursive deletes. No folder other than
`QAdvanceFeedback_1.0.6.0_release_speed_aware_fix` was written to (its own
`docs\fixed-fraction-anchor-gratio-table.md`, this file, is the only new/changed file).

## Harness

**Reused as-is, unmodified**: `QAdvanceFeedback_1.0.6.0_release_speed_aware_fix\scratchpad\fixed-fraction-anchor-gratio-harness\`
(`Shared\Program.cs`, `BuildOld\`/`BuildNew\` csproj + outputs). This harness already emits
`summary.csv` in exactly the column layout this table needs (`Build,RowLabel,CarLabel,WetDry,
SourceUsed,SMax,S75_Inst_n,S75_Inst_P90,...,SMax_Delay_Max,SMax_TruncatedWindows`) - it is a
direct descendant of the `QAdvanceFeedback_1.0.6.9_anchor_sampling` harness this table's
methodology is required to match, extended additively (not rewritten) for the
`speed-aware-reference-fix-report.md` task. **No adaptation was needed or made** - the table
below is produced by converting that CSV's ratio columns (0.xxx) to percentages, nothing else.

- `BuildOld\HarnessOld.csproj` -> `HintPath=C:\...\QAdvanceFeedback\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll`
  (the untouched 1.0.6.0 release DLL) -> output `BuildOld\out060\`.
- `BuildNew\HarnessNew.csproj` -> `HintPath=C:\...\QAdvanceFeedback_1.0.6.0_release_speed_aware_fix\QAdvanceFeedback\bin\Release\net48\QAdvanceFeedback.dll`
  (**this build**, the one this report measures) -> output `BuildNew\outNew\`.
- **The data was already on disk** (both `out060\` and `outNew\` were fully populated,
  timestamped before this task started) - confirmed by cross-checking `outNew\ratio_over_one.csv`'s
  POOLED row (17.80%, 700/3932) and `out060\ratio_over_one.csv`'s POOLED row (51.45%, 2023/3932)
  against the exact figures `docs\speed-aware-reference-fix-report.md` Section 3 already reports -
  **byte-for-byte match**, confirming this file's harness run is the same one that report used, not
  a re-run that could have drifted. No recomputation was performed; this report only reformats and
  re-derives the required table/digest from the already-computed CSVs.

## Reference formula and S90 constant (this build's own shipped value)

Confirmed by reading (read-only) `Core\Normalized\NormalizedWheelLockSlipEngine.cs`:
```
private const double S90FallbackRatioOfSmax = 0.750 * 1.125; // = 0.84375
private const double S75FallbackRatioOfSmax = 0.40;
```
Matches the harness's own `Thresholds` array (`S75=0.40, S90=0.84375, SMax=1.00`) and the brief's
stated fraction. The G-ratio itself (`USpeedAware`) is the **corrected** reference this task's own
prior work (`speed-aware-reference-fix-report.md`) produced: `GripLearner`'s per-speed-bucket
`RobustBandEstimator` (replacing `AdaptivePeakState`) with at-limit-gated bucket admission, blended
sealed/loose exactly as before - i.e. the same formula shape as the two existing tables' own
"speed-aware available max-grip G" reference, but with the two diagnosed defects (decay erosion,
unreached limits) fixed. SMax itself is **not** touched by this fix (taught from the flat,
non-speed-aware scalar) - confirmed unchanged from the release for every log/car except
`Common/Raw/Dry` (58.432 -> 58.322, 0.19%, immaterial).

## Artefact handling (identical corpus, identical drops - unchanged from the two existing tables)

| log | total data rows | restart rows dropped | frozen-tail rows dropped | fed rows |
|---|---|---|---|---|
| I_1_6_5/Raw | 6732 | 0 | 30 | 6702 |
| I_1_6_5/ShakeIt | 6855 | 6 | 30 | 6819 |
| l_1_6_6/Raw | 6763 | 0 | 30 | 6733 |
| l_1_6_6/ShakeIt | 6782 | 0 | 30 | 6752 |
| I_1_6_7/Raw | 6924 | 0 | 30 | 6894 |
| I_1_6_7/ShakeIt | 6765 | 0 | 31 | 6734 |
| I_1_6_8/Raw | 10499 | **3612** | 30 | 6857 |
| I_1_6_8/ShakeIt | 6872 | 0 | 30 | 6842 |
| c_1_5_3/ShakeIt | 16409 | 0 | 30 | 16379 |
| c_1_5_3/Raw | 20353 | 0 | 30 | 20323 |
| Common/Raw/Wet | 3522 | 0 | 58 | 3464 |
| Common/Raw/Dry | 5897 | 0 | 29 | 5868 |
| Common/ShakeIt/Wet | 3722 | 0 | 58 | 3664 |
| Common/ShakeIt/Dry | 6952 | **3196** | 30 | 3726 |

Identical, row-for-row, to the artefact tables in both pre-existing reports (`I_1_6_8/Raw`'s
~3612-row mid-recording restart, `Common/ShakeIt/Dry`'s ~3196-row restart, 29-58-row frozen tails
everywhere, plus the pre-existing 6-row `I_1_6_5/ShakeIt` restart). `old-logs\...210223` /
`...210421` are excluded by construction (never in the harness's log list).

## Threshold-crossing and delayWindow definitions (stated exactly, matching the two existing tables)

**Braking frame** = `Diag.Direction=="Slowing"` (recomputed live via `engine.CurrentDirection`,
never read from the log) **AND** `BrakePercent>=20` **AND** `|LateralG|<=0.5`.

**Braking event** = a maximal run of consecutive-RowIndex braking frames.

**Crossing** = first-touch, rising, once per event: within each braking event, the crossing frame
for a threshold is the first frame (lowest RowIndex) whose Source value (`Diag.Source.Lock.*`
aggregate for ShakeIt/Configured rows, `WheelLock.Raw.*` aggregate for the `_raw` fallback rows) is
`>=` that threshold. A later re-crossing of the same threshold within the same event is not counted
again. An event that never reaches the threshold contributes zero samples.

**delayWindow** = `MAX(ratio)` over `[crossingRowIndex, crossingRowIndex + N]`, N = **9** frames for
S75, **11** for S90, **10** for SMax (the per-anchor median source-to-G lag, rounded up - matching
both existing tables exactly). Not capped at the braking event's own end (the lagged G is still the
physical consequence of the crossing even if the brake releases a frame or two later). All
`*_TruncatedWindows` counts in the raw CSV are 0 for this corpus (no window ran off the end of a
file).

Cell format: **`n=<count>: P90/P95/Mean/Min/Max%`** - all five figures are the SAME statistic the
owner listed twice under two names ("Average" and "Mean"); reported once, here as Mean.
Percentages are `achieved G / speed-aware available peak G` (the corrected reference) at the
crossing frame (Inst) or the max of that ratio over the delay window (delayWindow).

## THE TABLE - 1.0.6.0 speed-aware-fix build (this build)

| log (CarLabel, SMax) | S75 Inst | S75 delayWindow | S90 Inst | S90 delayWindow | SMax Inst | SMax delayWindow |
|---|---|---|---|---|---|---|
| I_1_6_5/Raw (RedBull, SMax=72.9) | n=1: 305/305/305/305/305% | n=1: 374/374/374/374/374% | n=1: 305/305/305/305/305% | n=1: 374/374/374/374/374% | n=1: 305/305/305/305/305% | n=1: 374/374/374/374/374% |
| I_1_6_5/ShakeIt_ShakeIt (RedBull, SMax=41.8) | n=6: 209/260/117/37/310% | n=6: 216/269/130/65/323% | n=4: 243/277/123/37/310% | n=4: 253/288/141/65/323% | n=4: 243/277/123/37/310% | n=4: 253/288/141/65/323% |
| I_1_6_5/ShakeIt_raw (RedBull, SMax=39.3) | n=4: 243/277/123/37/310% | n=4: 253/288/141/65/323% | n=3: 260/285/135/37/310% | n=3: 276/299/159/65/323% | n=3: 260/285/135/37/310% | n=3: 276/299/159/65/323% |
| l_1_6_6/Raw (RedBull, SMax=73.7) | n=1: 71/71/71/71/71% | n=1: 72/72/72/72/72% | n=1: 71/71/71/71/71% | n=1: 72/72/72/72/72% | n=1: 71/71/71/71/71% | n=1: 72/72/72/72/72% |
| l_1_6_6/ShakeIt_ShakeIt (RedBull, SMax=33.5) | n=4: 260/271/171/97/283% | n=4: 328/354/196/98/380% | n=3: 323/338/212/78/353% | n=3: 346/363/222/78/380% | n=3: 323/338/209/68/353% | n=3: 346/363/219/68/380% |
| l_1_6_6/ShakeIt_raw (RedBull, SMax=45.6) | n=3: 198/202/147/68/206% | n=3: 346/363/219/68/380% | n=2: 275/279/244/206/283% | n=2: 363/371/294/208/380% | n=2: 275/279/244/206/283% | n=2: 363/371/294/208/380% |
| I_1_6_7/Raw (RedBull, SMax=57.8) | n=2: 309/322/203/71/335% | n=2: 360/375/237/83/391% | n=2: 309/322/203/71/335% | n=2: 360/375/237/83/391% | n=2: 309/322/203/71/335% | n=2: 360/375/237/83/391% |
| I_1_6_7/ShakeIt_ShakeIt (RedBull, SMax=38.8) | n=3: 227/244/140/66/260% | n=3: 288/312/168/73/336% | n=2: 241/251/163/66/260% | n=2: 310/323/205/73/336% | n=2: 241/251/163/66/260% | n=2: 310/323/205/73/336% |
| I_1_6_7/ShakeIt_raw (RedBull, SMax=74.8) | n=2: 241/251/163/66/260% | n=2: 310/323/205/73/336% | n=2: 241/251/163/66/260% | n=2: 310/323/205/73/336% | n=2: 241/251/163/66/260% | n=2: 310/323/205/73/336% |
| I_1_6_8/Raw (RedBull, SMax=72.0) | n=1: 73/73/73/73/73% | n=1: 77/77/77/77/77% | n=1: 73/73/73/73/73% | n=1: 79/79/79/79/79% | n=1: 73/73/73/73/73% | n=1: 78/78/78/78/78% |
| I_1_6_8/ShakeIt_ShakeIt (RedBull, SMax=43.9) | n=2: 325/339/214/76/352% | n=2: 365/380/240/85/396% | n=2: 364/380/236/76/396% | n=2: 365/380/240/85/396% | n=2: 364/380/236/76/396% | n=2: 365/380/240/85/396% |
| I_1_6_8/ShakeIt_raw (RedBull, SMax=73.9) | n=2: 226/234/159/76/243% | n=2: 365/380/240/85/396% | n=2: 325/339/214/76/352% | n=2: 365/380/240/85/396% | n=2: 208/216/149/76/223% | n=2: 214/221/156/85/228% |
| c_1_5_3/ShakeIt/Sauber_ShakeIt (Sauber, SMax=59.5) | n=14: 103/154/97/71/246% | n=14: 114/199/111/72/349% | n=11: 105/207/109/71/310% | n=11: 118/233/116/72/349% | n=10: 135/222/111/71/310% | n=10: 141/245/117/72/349% |
| c_1_5_3/ShakeIt/Sauber_raw (Sauber, SMax=75.8) | n=10: 100/103/86/71/105% | n=10: 141/245/118/72/349% | n=7: 105/110/88/71/116% | n=7: 210/280/127/72/349% | n=7: 102/110/87/71/118% | n=7: 210/280/127/72/349% |
| c_1_5_3/ShakeIt/F1Generic_ShakeIt (F1Generic, SMax=65.8) | n=4: 70/71/67/63/71% | n=4: 76/78/70/63/80% | n=0 | n=0 | n=0 | n=0 |
| c_1_5_3/ShakeIt/F1Generic_raw (F1Generic, SMax=46.8) | n=10: 271/284/120/68/296% | n=10: 272/284/123/68/297% | n=8: 140/203/103/68/265% | n=8: 141/203/104/68/265% | n=7: 83/85/77/68/87% | n=7: 84/86/79/68/88% |
| c_1_5_3/Raw/Sauber (Sauber, SMax=52.6) | n=11: 143/221/106/52/300% | n=11: 165/232/122/67/300% | n=9: 174/237/113/52/300% | n=9: 192/246/126/67/300% | n=9: 174/237/113/52/300% | n=9: 192/246/126/67/300% |
| c_1_5_3/Raw/F1Generic (F1Generic, SMax=37.3) | n=10: 98/147/87/58/196% | n=10: 102/149/91/63/196% | n=10: 93/93/80/58/94% | n=10: 93/93/81/63/94% | n=9: 91/91/78/58/91% | n=9: 91/91/79/63/91% |
| Common/Raw/Wet (Sauber(inferred;Wet), SMax=77.0) | n=6: 136/138/98/58/140% | n=6: 234/281/145/83/328% | n=4: 117/124/89/58/131% | n=4: 272/300/160/83/328% | n=4: 117/124/89/58/131% | n=4: 272/300/160/83/328% |
| Common/Raw/Dry (RedBull(inferred;Dry), SMax=58.3) | n=7: 242/297/137/64/352% | n=7: 242/297/140/79/352% | n=6: 219/260/134/81/300% | n=6: 219/260/135/81/300% | n=6: 212/255/132/80/297% | n=6: 212/255/132/80/297% |
| Common/ShakeIt/Wet_ShakeIt (Sauber(inferred;Wet), SMax=55.2) | n=5: 345/355/234/84/366% | n=5: 386/395/256/90/403% | n=3: 312/339/184/90/366% | n=3: 342/373/197/90/403% | n=3: 312/339/183/90/366% | n=3: 341/372/196/90/403% |
| Common/ShakeIt/Wet_raw (Sauber(inferred;Wet), SMax=49.6) | n=5: 338/346/185/79/354% | n=5: 386/395/256/90/403% | n=3: 95/96/91/87/97% | n=3: 342/373/197/90/403% | n=3: 93/93/90/87/94% | n=3: 342/372/196/90/403% |
| Common/ShakeIt/Dry_ShakeIt (F1Generic(inferred;Dry), SMax=70.0) | n=7: 143/184/100/51/225% | n=7: 143/184/102/63/225% | n=2: 78/78/77/75/79% | n=2: 78/78/77/76/79% | n=0 | n=0 |
| Common/ShakeIt/Dry_raw (F1Generic(inferred;Dry), SMax=73.6) | n=7: 149/187/101/46/225% | n=7: 149/187/104/63/225% | n=6: 156/190/106/77/225% | n=6: 156/190/106/78/225% | n=3: 81/81/80/77/82% | n=3: 81/81/80/78/82% |

Full unrounded CSV: `scratchpad\fixed-fraction-anchor-gratio-harness\BuildNew\outNew\summary.csv`.

## Quality flags - cells with n<10

**8 of 72 cells reach n>=10** (the same 8, at the same n, as both pre-existing tables - the
crossing population is essentially unchanged by this fix since SMax itself barely moves):

- `c_1_5_3/ShakeIt/Sauber_ShakeIt`: S75 (n=14), S90 (n=11), SMax (n=10)
- `c_1_5_3/ShakeIt/Sauber_raw`: S75 (n=10)
- `c_1_5_3/ShakeIt/F1Generic_raw`: S75 (n=10)
- `c_1_5_3/Raw/Sauber`: S75 (n=11)
- `c_1_5_3/Raw/F1Generic`: S75 (n=10), S90 (n=10)

**Every other cell (64 of 72) is n<10**, many n=1-4 (several n=0 where no event ever reached the
threshold: `c_1_5_3/ShakeIt/F1Generic_ShakeIt` S90/SMax, `Common/ShakeIt/Dry_ShakeIt` SMax) - read
as illustrative/directional only, per the brief's own instruction. Single-event (n=1) rows are a
single observed value, not a distribution.

## Also reported (1): fraction of braking frames with ratio > 1.0

| log | n (braking) | Release ratio>1.0 | speed-aware-fix ratio>1.0 (this build) |
|---|---|---|---|
| I_1_6_5/Raw | 131 | 67.2% | 22.9% |
| I_1_6_5/ShakeIt | 187 | 37.4% | 16.0% |
| l_1_6_6/Raw | 85 | 43.5% | 23.5% |
| l_1_6_6/ShakeIt | 162 | 59.3% | 13.0% |
| I_1_6_7/Raw | 103 | 34.9% | 23.3% |
| I_1_6_7/ShakeIt | 93 | 41.9% | 26.9% |
| I_1_6_8/Raw | 88 | 59.1% | 33.0% |
| I_1_6_8/ShakeIt | 119 | 61.3% | 21.9% |
| c_1_5_3/ShakeIt/Sauber | 182 | 34.6% | 9.9% |
| c_1_5_3/ShakeIt/F1Generic | 306 | 55.9% | 8.5% |
| c_1_5_3/Raw/Sauber | 222 | 53.6% | 9.9% |
| c_1_5_3/Raw/F1Generic | 295 | 20.3% | 7.1% |
| Common/Raw/Wet | 176 | 73.3% | 20.4% |
| Common/Raw/Dry | 160 | 68.8% | 40.0% |
| Common/ShakeIt/Wet | 128 | 73.4% | 47.7% |
| Common/ShakeIt/Dry | 159 | 56.6% | 12.6% |
| **POOLED** | **3932** | **51.45%** (2023/3932) | **17.80%** (700/3932) |

**Confirmed**: pooled 17.80% for this build vs 51.45% for the untouched release - matches the
brief's cited figures exactly (cross-checked byte-for-byte against
`docs\speed-aware-reference-fix-report.md` Section 3, which used the identical harness run). Every
single row drops from release to this build; the reduction ranges from modest (Common/ShakeIt/Wet,
73.4%->47.7%) to large (c_1_5_3/ShakeIt/F1Generic, 55.9%->8.5%).

## Also reported (2): three-build side-by-side, n>=10 cells only

Release = untouched `QAdvanceFeedback\` v1.0.6.0. no_speed_aware control =
`QAdvanceFeedback_1.0.6.0_release_no_speed_aware`. speed-aware-fix = this build. All three read
from their own already-computed `summary.csv` (no recomputation).

| Row | Threshold | Variant | n | Release (1.0.6.0) | no_speed_aware control | speed-aware-fix (this build) |
|---|---|---|---|---|---|---|
| c_1_5_3/ShakeIt/Sauber_ShakeIt | S75 | Inst | 14 | 142/184/109/61/246% | 142/184/103/56/246% | 103/154/97/71/246% |
| c_1_5_3/ShakeIt/Sauber_ShakeIt | S75 | delayWindow | 14 | 156/225/124/62/349% | 156/225/117/57/349% | 114/199/111/72/349% |
| c_1_5_3/ShakeIt/Sauber_ShakeIt | S90 | Inst | 11 | 150/230/120/61/310% | 150/230/113/56/310% | 105/207/109/71/310% |
| c_1_5_3/ShakeIt/Sauber_ShakeIt | S90 | delayWindow | 11 | 158/254/128/62/349% | 158/254/120/57/349% | 118/233/116/72/349% |
| c_1_5_3/ShakeIt/Sauber_ShakeIt | SMax | Inst | 10 | 166/238/121/61/310% | 166/238/113/56/310% | 135/222/111/71/310% |
| c_1_5_3/ShakeIt/Sauber_ShakeIt | SMax | delayWindow | 10 | 177/263/127/62/349% | 177/263/120/57/349% | 141/245/117/72/349% |
| c_1_5_3/ShakeIt/Sauber_raw | S75 | Inst | 10 | 125/138/96/61/150% | 125/138/91/56/150% | 100/103/86/71/105% |
| c_1_5_3/ShakeIt/Sauber_raw | S75 | delayWindow | 10 | 177/263/129/62/349% | 177/263/123/57/349% | 141/245/118/72/349% |
| c_1_5_3/ShakeIt/F1Generic_raw | S75 | Inst | 10 | 271/284/139/74/296% | 271/284/136/74/296% | 271/284/120/68/296% |
| c_1_5_3/ShakeIt/F1Generic_raw | S75 | delayWindow | 10 | 304/335/168/78/365% | 272/284/139/76/297% | 272/284/123/68/297% |
| c_1_5_3/Raw/Sauber | S75 | Inst | 11 | 157/228/126/64/300% | 143/221/105/47/300% | 143/221/106/52/300% |
| c_1_5_3/Raw/Sauber | S75 | delayWindow | 11 | 204/252/146/88/300% | 155/227/120/47/300% | 165/232/122/67/300% |
| c_1_5_3/Raw/F1Generic | S75 | Inst | 10 | 105/150/96/65/196% | 113/155/88/57/196% | 98/147/87/58/196% |
| c_1_5_3/Raw/F1Generic | S75 | delayWindow | 10 | 114/155/101/71/196% | 115/155/91/58/196% | 102/149/91/63/196% |
| c_1_5_3/Raw/F1Generic | S90 | Inst | 10 | 102/105/89/65/109% | 93/93/77/57/93% | 93/93/80/58/94% |
| c_1_5_3/Raw/F1Generic | S90 | delayWindow | 10 | 102/105/91/71/109% | 93/93/78/58/93% | 93/93/81/63/94% |

**Reading the Mean% column across the three builds** (the statistic the owner should weight most,
per the two existing reports' own guidance that Max/P95 are pulled up hard by under-converged
speed buckets):

- `Sauber_ShakeIt` S75/S90/SMax Inst: **109/120/121% (release) -> 103/113/113% (no_speed_aware) ->
  97/109/111% (speed-aware-fix)** - a smooth, monotonic walk toward target across all three builds,
  with this build the closest of the three at S75/S90, and no_speed_aware slightly closer at SMax.
- `Sauber_raw` S75 Inst: **96% -> 91% -> 86%** - same monotonic pattern, moving further past 75%
  toward undershoot territory (86% is already below the shipped fraction's own naive expectation
  of "at 0.40*SMax you should be at 75%, not below it").
- `F1Generic_raw` S75 Inst: **139% -> 136% -> 120%** - this build makes the biggest single move of
  the three toward target, but still far over 75%.
- `Raw/Sauber` S75 Inst: **126% -> 105% -> 106%** - release to no_speed_aware makes the big jump;
  this build lands at essentially the same point as no_speed_aware (not a further improvement).
- `Raw/F1Generic` S75 Inst: **96% -> 88% -> 87%** - same pattern as Raw/Sauber, converges with
  no_speed_aware rather than beating it.
- `Raw/F1Generic` **S90** Inst: **89% (release, already close to 90%) -> 77% (no_speed_aware) ->
  80% (speed-aware-fix)** - the ONE case where the "fix" (either the no-speed-aware control or the
  speed-aware reference fix) moves a cell AWAY from target, because release was already almost
  exactly on target here and both corrections pull it down into undershoot.

## Also reported (3): the owner's question, answered plainly

**With the corrected reference, do 0.40*SMax and 0.84375*SMax now deliver close to 75% and 90% of
available deceleration?**

**Judged on the 8 n>=10 cells (the only rows that can support a conclusion) - closer, but not
"close" in any strict sense, and unevenly so:**

- **S75-Inst** (5 n>=10 rows): Mean% = 97, 86, 120, 106, 87 - i.e. **86%-120%**, every single row
  still reads ABOVE the 75% target, by 11 to 45 points. The two closest rows (`Sauber_raw` 86%,
  `Raw/F1Generic` 87%) are within about +11-12 points of 75%; the worst (`F1Generic_raw`, 120%) is
  still 45 points over.
- **S90-Inst** (2 n>=10 rows): Mean% = 109% and 80% - one over target by 19 points, one under by 10.
  No consistent direction.
- **SMax-Inst** (1 n>=10 row): Mean% = 111%, i.e. 11 points over 100% - the single closest-to-target
  reading in the whole n>=10 set.

**So: the corrected reference measurably tightens the well-populated rows** (every one of the 6
distinct n>=10 row/threshold combinations that changed at all moved toward, not away from, its
target when going release -> this build, with the single exception of `Raw/F1Generic` S90, which
was already essentially on target under release and both correction attempts pull it slightly
under) - **but it does not make 0.40/0.84375*SMax read as "close to 75%/90%"** in the sense of
landing within a few points; the well-populated rows still range 80-120% against their own targets,
a residual gap of roughly 10-45 points depending on the row.

**Does it shrink the absurd tail (200-400% cells, previously all n=1-4)?** **No, not
meaningfully.** Spot-checking the same thin cells across builds (e.g. `I_1_6_5/Raw` S75 Inst,
n=1, 305% in both the release and this build; `I_1_6_5/ShakeIt_ShakeIt` S75 Delay Max, n=6, 310%
unchanged in both), the extreme Max/P95 values in the n<10 rows are driven by individual
under-converged-speed-bucket frames whose peak reading did not move much even after the
speed-bucket estimator fix - the fix's own report (`speed-aware-reference-fix-report.md` Section 3)
independently diagnoses this same residual: a 2-parameter regression can still under-predict at an
intermediate speed inside one braking zone, leaving isolated frames reading far over 100%. **Net:
the fix tightens the well-populated rows' central tendency (Mean/P90) toward target, but the thin
rows' extreme tail (Max, and to a lesser extent P95) is essentially as wide as before** - this
matches, rather than contradicts, what the two existing reports already found for the
1.0.6.9/no-speed-aware comparisons (their own "well-populated rows read 75-105%, absurd 200-400%
cells all n=1-4" pattern holds here too, just with the well-populated rows shifted a further ~10-15
points closer to target on average).

## Concerns

1. Only 8 of 72 cells reach n>=10; every conclusion above about "does the fix help" rests on those
   8 cells (6 distinct row/threshold pairs after collapsing Inst/Delay). The other 64 cells,
   several with n=1-2, are illustrative only.
2. `c_1_5_3/Raw/F1Generic` S90 is the one case where BOTH correction attempts (no_speed_aware and
   this build's speed-aware-fix) move a cell the WRONG way relative to target (89%->77%/80% against
   a 90% target) - worth the owner's attention specifically, since it shows the corrected reference
   is not uniformly an improvement.
3. `Common_1_5_e_d`'s car identity is inferred (no `Diag.CarId`), same caveat as both existing
   tables.
4. The extreme Max/P95 tail in thin cells is frequently unchanged frame-for-frame across builds
   (same peak crossing frame, similar reference reading) - readers should not expect this fix to
   have "fixed" the P95/Max columns even where it visibly tightened Mean/P90.
5. This report's three-build digest reads each build's own already-computed `summary.csv` files
   (all under other folders' `scratchpad\...\out*\`) rather than recomputing - consistent with the
   brief's instruction to check for already-run data first, but it means this report inherits
   whatever those other builds' own harness runs already established (their own SMax/crossing
   population), not an independently re-verified cross-build population match. Row-for-row `SMax`
   values were spot-checked as effectively identical across the three builds for every n>=10 row
   (confirming the threshold locations, and hence the crossing/`n` populations, are comparable).

## Confirmation

No product source file was read for writing, no file under any `QAdvanceFeedback*` folder besides
this one was created or modified, no DLL was rebuilt, and no git or recursive-delete operation was
performed. The only new/changed file is this report:
`QAdvanceFeedback_1.0.6.0_release_speed_aware_fix\docs\fixed-fraction-anchor-gratio-table.md`. The
harness under `QAdvanceFeedback_1.0.6.0_release_speed_aware_fix\scratchpad\fixed-fraction-anchor-gratio-harness\`
was read, not edited (its output already matched the required format).
