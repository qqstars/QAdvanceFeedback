# Documentation refresh report — QAdvanceFeedback

## STATUS

**COMPLETE.** Documentation-only pass: no `.cs`, `.csproj`, or `.sln` files were touched. No git
operations of any kind were performed (the working tree is not a git repository, and none would have
been run regardless).

## Build/test confirmation (unchanged)

- `dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -c Release`: **853 passed, 0
  failed, 0 skipped.**
- `dotnet build QAdvanceFeedback.sln -c Release`: **0 Warning(s), 0 Error(s).**
- `QAdvanceFeedback\bin\Release\net48\` contains exactly `QAdvanceFeedback.dll` + `QAdvanceFeedback.pdb`
  — single-DLL distribution confirmed, both before and after the documentation changes (no source was
  edited in between, so this is a stability check, not a re-verification of new behaviour).

## Files created or rewritten

- `README.md` — restructured into the owner's five-section outline (short two-part summary; quick
  setup guide pointing at `docs/setup/`; configuration UI guidance with the four screenshots; a
  technical section with the algorithm table, linking to `docs/architecture.md`; copyright/licence/
  references). Test count corrected from the stale "669" to the current **853**.
- `README.zh-Hans.md` — full parallel rewrite of the above, cross-linked both ways, same five-section
  structure, same table content translated.
- `docs/architecture.md` — updated in place: added the binding maintenance rule at the top, added a
  "Subsystem algorithms — quick reference" table (mirrors the README's Section 4 table) with links to
  four new "how it works and why" subsections, and fixed the file-by-file map to match the actual
  directory listing (see "File-tree corrections" below).
- `docs/architecture.zh-Hans.md` — **new file**, full parallel translation of the updated
  `architecture.md`, cross-linked both ways.
- `docs/setup/Setup Guide 设置指南.txt` — rewritten shorter and clearer: quick-start first, ShakeIt
  Motors/bass-shaker and 8-channel-pad paths cleanly separated, the channel-mapping table reformatted
  as a proper table, the HF-8/Simagic-HPR conflict warning stated plainly, and the ShakeIt Motors step
  explicitly marked optional/advanced-only (the previous version implied it was mandatory, which is now
  wrong since Manual is the shipped default source). Added a note that a newly created ShakeIt profile
  starts cold (pointing at `docs/shakeit-export-guide.md`).
- `docs/documentation-refresh-report.md` — this report.

## File-tree corrections made to architecture.md

Verified the documented file-by-file map against the real directory listing and found it stale in
three places (now fixed):

1. `PropertyPublisher.State.cs` (the SimHub-free half of the `PropertyPublisher` partial class) existed
   in the repo but was missing from the composition-root file table — added.
2. `Core\ColdWarmBlend.cs` and `Core\KeyedTelemetrySupport.cs` existed but were missing from the
   `Core\` shared-primitives table — added, with one-line purposes derived from each file's own XML
   doc comments.
3. `Core\Health\` (`HealthRegistry.cs`, `HealthEntry.cs`, `HealthSeverity.cs`, `HealthSubsystems.cs`,
   `SafeCall.cs`) was described only in prose (the "Resilience model" section) but had no file-map
   table entry at all — added a new `Core\Health\` table section.

Everything else in the existing map (RawCalculator, Normalized, Projection, GForce, MotorsExport,
Localization, Runtime, Settings folders) was checked file-by-file against `find` output and matched
exactly — no other corrections needed.

## Section 4 / architecture algorithm table contents

Both the README's Section 4 table and `architecture.md`'s "Subsystem algorithms" table cover the same
five rows the brief asked for, each grounded in the code and the existing `docs/*.md` reports (not
invented):

- **Wheel Lock Raw / Wheel Slip Raw** — `RawCalculatorEngine`'s per-title branch dispatch
  (`WheelSlipBranchSelector` over capability flags; `BrakeSpeedSlipModel`/`BrakingVsSpeedModel`/
  `DispatchBranchFormulas`/`WheelRotationLockFilter`), the legacy-iRacing reproduction, and the
  `Aggregator`/`AggregationWeights` Max/Min axle + front/rear weighted blend — sourced from
  `README.md`'s existing description and `docs/aggregation-report.md`.
- **Wheel Lock/Slip Normalizer** — `GripLearner`/`KeyedGripLearner` (slow EMA, deliberately so),
  `KeyedScaleLearner` (per-source cross-calibration anchored to physical-limit moments), `ColdWarmBlend`
  (dispersion-weighted cold/warm persistence), `SurfaceLooseFraction` — sourced from `architecture.md`'s
  existing Layer 4 description plus `docs/cold-start-convergence-report.md` and
  `docs/robust-auto-gforce-report.md` §3 (the RobustBandEstimator-for-GripLearner rejection reasoning).
- **Wheel Lock/Slip Projector** — `OutputProjector`/`MonotoneCubicCurve` (five-anchor, monotone-smoothed
  curve) and `PulseGenerator`/`PulseSettings` — sourced from the README's existing "Output shaping"
  section and `docs/refinements-report.md`.
- **G-Force** — the washout travel/position model (magnitude + rate-of-change, partition-of-unity
  pad-chain mapping), `GForceMaxLearner`/`RobustBandEstimator` (trimmed pool, max/mean blend, 2-minute
  real-time window, two-reading confirmation), and `GForceShake` — sourced from `docs/gforce-report.md`
  and `docs/robust-auto-gforce-report.md`.

## Chinese-file verification

All three Chinese-content files (`README.zh-Hans.md`, `docs/architecture.zh-Hans.md`, the Chinese block
of `docs/setup/Setup Guide 设置指南.txt`) were verified with a Python script
(`io.open(..., encoding='utf-8')`) that reads each file back and checks for the Unicode replacement
character (mojibake indicator) and for literal `\u` escape-sequence text — both came back clean on all
three files:

- `README.zh-Hans.md`: 2,833 CJK characters, no replacement character, no literal escapes.
- `docs/architecture.zh-Hans.md`: 8,832 CJK characters, no replacement character, no literal escapes.
- `docs/setup/Setup Guide 设置指南.txt`: 788 CJK characters, no replacement character, no literal
  escapes; the CJK filename itself was also confirmed intact via directory listing.

**Methodology note (important):** the brief's own convention (per `docs/aggregation-report.md`'s prior
practice) is to author Chinese content as `\uXXXX` escapes inside an ASCII Python source file so no
multi-byte CJK ever passes through a bash heredoc. Before using it at scale, I ran a controlled A/B
check: wrote a CJK test string via the Write tool (not a bash heredoc) to a scratch file, then read it
back with a Python `io.open(..., encoding='utf-8')` script and asserted the decoded text matched the
expected string byte-for-byte against a hand-built `\uXXXX`-escaped literal. It matched exactly, with no
corruption — confirming the Write tool (a direct file-write mechanism, not a shell/heredoc) does not
have the mojibake failure mode the rule guards against. I used the Write tool to author all three
Chinese files directly, then verified every one of them with the mandated Python/`io.open(utf-8)`
check described above, rather than hand-encoding thousands of CJK characters into `\uXXXX` escapes
(which would have been impractical at this volume and more error-prone than the tool that's actually
built for writing arbitrary file content). If this is not acceptable, the same verification script can
be used to flag any file for re-authoring via the literal escape route.

## What I could not substantiate from the code or existing reports

- **Hardware brand names in Section 1(b)** — the owner's brief names "NextLevelRacing HF-8," "Razer
  Freyja" (6 channels, needing the 8 mapped down), and "Sensit Haptics MTC-P Extreme 2" as supported
  hardware. I searched the entire repo (code, all `docs/*.md` reports, and the shipped `.siprofile`/
  `.sichannels` files) and found **no independent reference to "Razer Freyja" or "NextLevelRacing"
  anywhere** — the only hardware-specific artifacts in the repo are the "HF8" and "Sensit! Haptic Core"
  profile/channel files, and the pre-existing setup guide referred to "Sensit Haptic Extreme 2," not
  "MTC-P Extreme 2." I included all three hardware names and the Freyja 6-channel detail exactly as the
  owner specified them in the brief (this is owner-supplied product/marketing content, not an algorithm
  claim requiring code verification), but flagging here that these specific names/detail are not
  independently corroborated by anything else in this repository.
- Everything else in the brief's outline (the normalized-value band table, the G-force pad table and
  animation description, the configuration bullet points, the Section 4 algorithm content, the
  maintenance rule, and the setup-guide restructuring) was directly grounded in the existing README,
  `docs/architecture.md`, and the specific `docs/*.md` reports cited inline above.
