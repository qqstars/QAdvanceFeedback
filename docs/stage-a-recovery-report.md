# Stage A recovery (from a crashed mid-revert agent) - report

## STATUS: RECOVERED

A prior agent implemented an experimental "grip utilization" band-mapping on top of the shipped
"Stage A" speed-aware grip model (`docs\speed-aware-grip-report.md`), built it successfully, then
crashed (API 500) while reverting it back out. The revert over-corrected: it restored
`GripLearner.cs`, `KeyedGripLearner.cs`, and `NormalizedWheelLockSlipEngine.cs` from the owner's
PRE-Stage-A backup (`QAdvanceFeedback_1.0.6.5_MaxTo80`), deleting Stage A itself, while Stage A's
persistence plumbing (`RuntimeDocument.cs`/`RuntimeCache.cs`, `Version` bump to 5) and its two test
files were left in place - leaving the repository non-compiling.

Recovery was done by decompiling the last DLL built before the crash (`stageA-recovery\QAdvanceFeedback.dll`,
built 2026-08-18 15:54:47, containing Stage A **and** the experiment entangled together) as the
authoritative semantic reference, then hand-reconstructing idiomatic Stage-A-only source (full XML
doc comments, house-style naming, no ILSpy artifacts) back into the real repository - deliberately
excluding every piece of the experimental utilization band-mapping.

## RESULT

- `dotnet build QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s).**
- `dotnet test QAdvanceFeedback.sln -c Release --no-build`: **868/868 passed, 0 failed, 0 skipped**,
  none modified to make them pass (the two pre-existing Stage A test files,
  `GripLearnerSpeedModelTests.cs` x10 and `NormalizedEngineSpeedModelTests.cs` x2, compile and pass
  unmodified, byte-for-byte as they were left by the crashed agent).
- `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `.pdb` -
  **single-DLL confirmed** (verified after a full Clean/Restore/Build).
- No git operation of any kind was run (the repo is not under git). No recursive deletes. No raw
  CJK was written (none needed).

## FILES CHANGED

- `QAdvanceFeedback\Core\Normalized\GripLearner.cs` - reconstructed Stage A: speed-bucketed
  `SpeedBucket` state, `GMech`/`K`/`PublishedGMech`/`PublishedK`/`PersistedGMech`/`PersistedK`,
  `SpeedAwarePeakG(speedKmh)`, `FitSpeedModel` (the guarded weighted least-squares fit), `BucketIndex`,
  `Observe(magnitudeG, speedKmh = null)`, `Ratio(magnitudeG, applyColdStartCeiling = true, speedKmh = null)`,
  `Load(learnedPeakG, samples, gMech, k)` overload.
- `QAdvanceFeedback\Core\Normalized\KeyedGripLearner.cs` - `GripLearnerState.GMech`/`.K` fields;
  `Ratio`/`Observe` gained a `speedKmh` parameter threaded to `GripLearner`; `ExportAll`/`ImportAll`
  carry `GMech`/`K` through persistence.
- `QAdvanceFeedback\Core\Normalized\NormalizedWheelLockSlipEngine.cs` - minimal, surgical diff: added
  one `speedKmh` extraction (`frame?.GroundSpeedKmh ?? frame?.SpeedKmh`, the same convention every
  other speed-consuming class in this layer already uses) and threaded it through the two
  `Observe` calls and the two `physicalReference.Ratio` calls that feed the physical-limit detector.
  Nothing else in this 950-line file changed - confirmed by diffing against the pre-Stage-A backup
  (25 diff lines total, all additions of the `speedKmh` argument/extraction).

## WHAT DIFFERS FROM THE DECOMPILED REFERENCE, AND WHY

Compared public-member-signature sets between the restored files and the decompiled reference
(`stageA-recovery\src\QAdvanceFeedback.Core.Normalized\`):

- **`GripLearner.cs`: identical public API surface** to the decompiled reference (every method
  signature matches exactly) - all Stage A members (`GMech`, `K`, `PublishedGMech`, `PublishedK`,
  `PersistedGMech`, `PersistedK`, `SpeedAwarePeakG`, the `Observe`/`Ratio`/`Load` overloads) present
  and semantically identical (same guard order, same constants: `MinPopulatedBucketsForFit=2`,
  `MinAeroFitSpeedSpreadKmh=80`, `MaxGMechOvershootOfLearnedPeak=1.3`, `FallingTailRejectFraction=0.85`,
  `MaxPlausibleSpeedKmh=500`, same weighted-least-squares algebra). Literals were re-expressed in
  clean form (e.g. the ramp width as `0.1`, not ILSpy's `0.10000000000000009`) and every member got a
  full house-style XML doc comment reconstructed from `docs\speed-aware-grip-report.md`'s own
  rationale - the decompiled output had neither.
- **`KeyedGripLearner.cs`: one deliberate omission - `HotEvidenceWeight`.** The decompiled DLL has a
  `HotEvidenceWeight` property on both `GripLearner` and `KeyedGripLearner`. It was NOT restored,
  because: (1) it is not referenced by either of the two authoritative Stage A test files: it is not
  part of the API contract those tests establish; (2) it is not present in the pre-Stage-A backup
  either, so its origin is ambiguous - it may have been added specifically to support the
  experimental utilization blend's own confidence weighting, or may be an unrelated, never-shipped
  diagnostic the same agent added opportunistically; (3) `NormalizedWheelLockSlipEngine`'s own
  experimental severity blend doesn't even call it (it uses `physicalReference.Confidence` instead).
  A stray `<see cref="GripLearner.HotEvidenceWeight">` doc-comment reference already exists in the
  pre-existing `RegressionFixTests.cs` (predates this whole incident) but is harmless: this project
  does not set `<GenerateDocumentationFile>`, so an unresolved `cref` is not compiler-checked and
  causes no warning. Per the brief's own instruction to STOP and report rather than guess: this one
  member is flagged here rather than silently added on the theory that it "might be needed" -
  restoring it would risk re-introducing part of the unevaluated experiment's own plumbing. It is
  preserved for reference in `docs\experiments\grip-utilization-decompiled\`.
- **`NormalizedWheelLockSlipEngine.cs`: no omissions relative to Stage A** - the ONLY differences from
  the pre-Stage-A backup are the `speedKmh` threading described above; every one of the decompiled
  file's utilization-specific members (`LockUtilizationPowerAnchor`, `SlipUtilizationPowerAnchor`,
  `UtilizationIdealAnchor`, `UtilizationAtLimitAnchor`, `UtilizationBlendRampLow/High`,
  `SourceSeverityBlendRampLow/High`, `SourceReleaseBlendRampHigh`, `ComputeUtilizationBlendWeight`,
  `ComputeSourceSeverityBlendWeight`, `ComputeSourceReleaseBlendWeight`, the `utilizationPowerAnchor`
  parameter on `ComputeChannel`, and the `GripUtilizationSubLimitMap.Evaluate` call that replaced
  `severity = calibratedMean` with a blended utilization curve) was deliberately excluded - none of
  it is part of Stage A, and the pre-existing legitimate `effectiveGripUtilization`-style references
  (6 case-insensitive matches in the backup) were left completely untouched, exactly as instructed.

## THE UTILIZATION EXPERIMENT: NOT IN THE BUILD, PRESERVED FOR LATER

`docs\experiments\grip-utilization-decompiled\` now contains the decompiled reference output (with a
README explaining status/provenance) for:
- `NormalizedWheelLockSlipEngine.decompiled.cs` (the engine with the experimental severity blend)
- `GripUtilizationSubLimitMap.decompiled.cs` (the new PCHIP curve class the experiment introduced)
- `GripLearner.decompiled.cs` / `KeyedGripLearner.decompiled.cs` (same-build state, for completeness,
  including the unexplained `HotEvidenceWeight` member above)

None of these four files are referenced by `QAdvanceFeedback.csproj` or `QAdvanceFeedback.sln` (they
live under `docs\`, outside the project's own directory, so the SDK-style project's default globbing
never picks them up regardless - confirmed: adding them did not change the build's file count or
introduce any new compiler input). The experiment was never evaluated against real logs and never
produced its own report; the README says so explicitly and points at
`docs\speed-aware-grip-report.md` Part 2's methodology as the bar it would need to clear before ever
being revived.

## STAGE A VERIFIED WORKING, NOT MERELY COMPILING

All 12 Stage A tests pass explicitly (`--filter "FullyQualifiedName~GripLearnerSpeedModelTests|FullyQualifiedName~NormalizedEngineSpeedModelTests"`, 12/12 passed), including the three the brief specifically asked to confirm:

- **Lock fits a positive k**: `Wide_speed_spread_with_a_genuine_aero_shape_fits_a_positive_k_and_fixes_the_low_speed_dead_zone` - PASSED. A synthetic aero shape (`peak_decel(v) = 1.5 + 0.0006*v_mps^2`) across 6 speed bands fits `K > 0`, and the same 1.45g reading reads as near-the-limit at 20 km/h but comfortably under the limit at 280 km/h.
- **Slip fits k = 0**: `A_falling_with_speed_shape_never_fits_a_negative_k` - PASSED. A falling-with-speed shape (traction-limited launch, power-limited at speed) rejects the rising-fit guard (falling-tail signature) and settles `K == 0.0` with `GMech` collapsing to the exact same value as the fully-degenerate flat path.
- **k = 0 is bit-identical to the flat scalar model**: `Speed_unaware_callers_are_bit_identical_to_before_this_feature` - PASSED. `K` stays exactly 0, `SpeedAwarePeakG(speedKmh) == PublishedPeakG` for every speed tested (0/60/150/300 km/h), and `Ratio(magnitude)` equals `Ratio(magnitude, speedKmh: v)` exactly, at 12-decimal precision.

## OWNER'S TESTED DEFAULTS - CONFIRMED INTACT

Verified directly in source (none of these files were touched by the crash or by this recovery):
- `KeyedScaleLearner.CanonicalAtLimitAnchor = 80.0`
- `AggregationWeights.LockDefaults = (0.75, 0.25, 0.90, 0.10, floor 0.0)`,
  `AggregationWeights.SlipDefaults = (0.85, 0.15, 0.45, 0.55, floor 0.70)`
- `ProjectorSettings` curve inputs `30.0/60.0/80.0`; Lock outputs `10.0/30.0/80.0`; Slip outputs
  `10.0/35.0/75.0`

## THE STRAY `scratchpad\` DIRECTORY - NOT REMOVED

`QAdvanceFeedback\scratchpad\` (repo root) contains **two** subdirectories, not one:
- `griputil-harness\` - the crashed agent's own throwaway harness (`Harness.csproj` + `Program.cs`
  referencing the built `QAdvanceFeedback.dll`, plus `bin`/`obj`/`out` build output) for the grip-
  utilization experiment.
- `narrative-validation-harness\` - a **separate, unrelated, pre-existing** real harness (its own
  `Harness.csproj` + a genuine 48KB `Program.cs` that replays CSV telemetry logs through
  `ITelemetryFrame`/the real engine, not just build output) - this is NOT throwaway output from the
  crashed agent; it belongs to an earlier, different validation task.

Per the brief's explicit instruction ("delete ONLY if it contains nothing but its own throwaway
harness build output... if it holds anything else, leave it and say so"), **`scratchpad\` was left
in place, untouched**, because it holds more than just the one throwaway harness.

## PARTS OF STAGE A THAT COULD NOT BE FAITHFULLY CONFIRMED

Nothing required by the two Stage A test files or by `docs\speed-aware-grip-report.md`'s own
documented design was left un-restored. The one open item is the `HotEvidenceWeight` member
discussed above - not part of Stage A's documented contract, not needed for 868/868 to pass, and
deliberately left out rather than guessed back in. If the owner confirms it was a genuine,
independently-shipped feature (rather than experiment-only scaffolding), it can be added back
straightforwardly by mirroring the decompiled reference now preserved under
`docs\experiments\grip-utilization-decompiled\`.
