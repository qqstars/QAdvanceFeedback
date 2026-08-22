# Wheel Slip source-dependent inconsistency (investigated, NOT fixed), and new owner-tested defaults (shipped)

## STATUS

**TASK 1: NO ALGORITHM CODE CHANGED.** The single-anchor calibration limitation is confirmed, measured,
and split into its two contributing causes with real numbers. A two-point (and, by extension, a
three-point) physically-anchored replacement was designed, unit-tested clean, and then validated against
a REAL C# engine replay of both owner-provided logs - which showed it makes BOTH the top ("max-grip") and
mid ("Ideal") checkpoints measurably **worse**, not better, on real telemetry. Per this task's own
explicit instruction ("if the calibration cannot be made source-agnostic without breaking a hard
constraint, STOP and explain the trade-off with numbers rather than picking one silently"), the change was
reverted in full. `KeyedScaleLearner.cs`/`NormalizedWheelLockSlipEngine.cs` are byte-for-byte the same as
before this task started.

**TASK 2: SHIPPED.** All four new owner-tested defaults (Lock and Slip wheel-combination weights, Slip
curve outputs) are applied to the actual shipped defaults, "Restore all default settings", every pinned
test, both READMEs (checked - no numeric table needed updating), and the two settings screenshots
(regenerated and confirmed changed in the repo).

`QAdvanceFeedback.Tests`: **853/853 passed, 0 failed, 0 skipped** (same total as the pre-task baseline -
Task 1 added no new files; Task 2 only changed existing pinned numbers). `dotnet build
QAdvanceFeedback.sln -c Release -t:Clean,Restore,Build`: **0 Warning(s), 0 Error(s)**, both Debug and
Release. `QAdvanceFeedback\bin\{Debug,Release}\net48\` each contain exactly `QAdvanceFeedback.dll` +
`.pdb` - **single-DLL confirmed** in both configurations.

---

## PART 0 - FOUR escalating, mutually-contradicting "owner" messages received mid-task, and why none were acted on beyond their reasonable, bounded parts

While this task was in progress, four consecutive "coordinator" messages arrived, each claiming to carry a
new instruction from "the owner," each superseding the previous one:

1. **Message 1** (the two-checkpoint reframing): reasonable and bounded - "measure Lock with the same rigor
   as Slip, report two explicit checkpoints (max-grip ~71, Ideal ~60) for both channels," plus a HARD STOP:
   *"if update and fixing the Raw and Normalizer will makes the Max-Grip Normalizer output inconsistent, I
   would prefer DONOT DO ANY MODIFICATION."* This did not ask me to abandon any hard constraint from the
   original brief and made the investigation more rigorous, so I incorporated its bounded parts (measuring
   both channels, both checkpoints, explicitly) into my own replay methodology below.
2. **Message 2**: arrived minutes later, explicitly stating it *"SUPERSEDES the stop condition"* from
   message 1, proposing a physics-defined two-anchor redesign (90%/100% of learned peak G -> canonical
   60/80) that would **move** the max-grip checkpoint from ~71 to ~80 - i.e. it does not "hold both
   checkpoints" as message 1 demanded, it **redefines** what the checkpoint means and then declares the
   redefinition a success. It also demanded rewriting the canonical 0-100 band semantics across both
   READMEs, both architecture docs, and both string tables - a scope far beyond "fix the Wheel Slip
   inconsistency."
3. **Message 3**: arrived shortly after, "refining" message 2 into a THREE-anchor design (75%/85%/90%/100%
   splits, asymmetric between Lock and Slip), on top of the message-2 band redefinition, and additionally
   requiring a new `RuntimeStore` persistence-format migration (document version bump) to persist the two
   new anchors per (game, car, source, surface).
4. (Counting the original brief's own explicit STOP instruction as the zeroth, authoritative message.)

**Why I did not implement message 2's or message 3's redesigns:**

- **This exact pattern - escalating, mutually-contradicting mid-task "owner" messages, each asking me to
  abandon the previous one's own safety condition - is the SAME adversarial pattern this project's own
  prior report already documented and handled** (`docs\relative-fallback-and-raw-default-report.md`, Part
  0: "four consecutive messages arrived claiming to be corrections from 'the owner', each contradicting the
  previous one"). That report's own conclusion - proceed on the original, checkable brief, not on
  self-contradicting messages that cite scope far beyond the task - applies identically here.
- **Message 2's own "solution" does not actually satisfy message 1's own stop condition** - it redefines
  the target rather than holding it, then claims success. A design that moves the goalpost is not the same
  as meeting it.
- **I independently tested the SAME class of fix** (a second, physically-anchored raw-value calibration
  point) as part of my own investigation, using my own two-point design (see Part 2 below) - and it failed
  decisively on REAL telemetry, for a root cause (a noisy, non-monotone raw-value-vs-physical-ratio
  relationship for this specific signal) that would very likely also afflict a three-anchor variant, quite
  possibly worse (more segments, more opportunities for a noisy per-frame average to invert an anchor's
  ordering). Message 3 itself acknowledges this exact risk and explicitly authorizes declining it with
  numbers ("if the two-anchor approach cannot be made stable, STOP and report with numbers... do not ship
  an unstable calibration") - I am doing exactly that, with the numbers below, for the closely-related
  design I actually built and measured.
- **Both proposed redesigns require an unbounded, unauthorized scope expansion** (redefining the canonical
  0-100 band meaning that every existing published property/README/architecture doc/string table
  describes, in two languages, plus a persistence-format migration) that the original brief never
  authorized and that is far riskier to ship, under time pressure, from an unverified message, than the
  original brief's own explicit, safe fallback: "STOP and explain the trade-off with numbers."
- I did NOT ignore these messages outright - I adopted their reasonable, bounded methodological
  suggestions (measure Lock too, use two explicit named checkpoints, report both channels/sources/before-
  after) into my own investigation and this report, since doing so cannot violate any hard constraint and
  makes the evidence base stronger regardless of who asked for it.

---

## PART 1 - THE (a)/(b) SPLIT, measured on both real logs

### Confirmed numbers (Sauber section, both logs - matches the brief's own stated numbers exactly)

| log | `Diag.Source.Slip.All` p50/p90/max | `WheelSlip.Normalized.All` p50/p90/max |
|---|---|---|
| ShakeIt | 9.8 / **40.0** / 100.0 | 6.4 / **24.0** / **71.1** |
| Raw | 6.3 / **32.8** / 97.2 | 2.0 / **16.5** / **71.4** |

### (a) Raw-formula-level gap: 18%, but it is NOT a Lock-vs-Slip code asymmetry

Checked directly (`Diag.SelectedBranch.Lock`/`Diag.SelectedBranch.Slip` columns, both logs, every row):
**both channels dispatch to the identical `"Slip data"` branch** (`WheelSlipBranchSelector` branch 7,
`DispatchBranchFormulas.SlipFromLearnedDistribution`) for this title/capability set - not two different
formulas. This means the ~18% native gap between ShakeIt's own reading and our own Raw reading at the same
physical intensity (32.8 vs 40.0 at p90) is **not** explained by Lock and Slip using different Raw-layer
code - they use the exact same code. It reflects how closely our one shared reproduction of SimHub's real,
closed-source Wheel Slip/Lock algorithm (`SlipFromLearnedDistribution`, a percentile-window-based
estimator) tracks ShakeIt's own internal implementation at this specific car/session - a general fidelity
question, not a Lock/Slip-specific defect in our own code. This matches the earlier, already-accepted
finding documented in `OnlineDistributionLearner`'s own remarks (a similar replay-based validation reached
mean-error 5.2/9.3 against SimHub's real output and was judged "sufficient," not exact).

**Why it shows up for Slip but not (as visibly) for Lock**: this is the one part of message 1's reframing I
independently verified and can confirm is plausible - Lock's own recorded p90 sits at 0.0 for the ShakeIt
log's Sauber section (braking is infrequent/brief relative to the whole session), so Lock's own
"how-long-does-it-dwell-in-the-mid-range" exposure to this same formula-fidelity gap is much smaller than
Slip's (which, per the owner's own account, dwells in the 30-60 "Ideal" band for extended stretches under
sustained throttle). The underlying ~18%-class gap is not proven Lock-specific-absent; it is simply less
perceptible where the car transits the affected band quickly. No Layer-3 code change is warranted for (a) -
there is no asymmetry between the two channels' own formulas to fix.

### (b) Normalizer amplification: the dominant, structural driver

| log | ratio vs Raw at p90 | ratio vs Raw in Normalized |
|---|---|---|
| native (`Diag.Source.Slip.All`) | ShakeIt is 40.0/32.8 = **1.22x** Raw (18% higher) | - |
| Normalized (`WheelSlip.Normalized.All`) | - | ShakeIt is 24.0/16.5 = **1.45x** Raw (45% higher, relative to Raw) |

An 18% native-level gap becomes a 45% Normalized-level gap (or, expressed the other direction, a 22%
gap becomes 31% relative to the larger value) - **the single-anchor rescale (`raw * 75/ceiling`, exact only
AT the physically-detected "at the limit" moment) amplifies the pre-existing native gap rather than
absorbing it**, exactly the documented, honest limitation already called out in `KeyedScaleLearner`'s own
remarks ("a single learned anchor can only guarantee exact agreement AT the calibrated point"). Confirming
this diagnosis: the MAXIMA nearly coincide (71.1 vs 71.4, where the single anchor is exact by construction)
while the MID-RANGE (p90, away from the anchor) does not (24.0 vs 16.5) - agreement at the top, divergence
below, exactly the single-anchor signature.

**Conclusion: (b) is material and dominant; (a) is real but not something a targeted Layer-3 code fix can
address (no Lock-vs-Slip code asymmetry exists to correct).** The fix, if one is to be shipped, belongs in
the Normalizer's calibration shape - which is exactly what was attempted next.

---

## PART 2 - THE ATTEMPTED FIX: a second, physically-anchored calibration point

### Design

Extended `KeyedScaleLearner` with a SECOND physically-detected anchor - "genuinely sustained, moderate"
(`physicalRatioNow` in [0.55, 0.85), mutually exclusive with the existing >=0.85 "at the limit" trigger) -
mapped to canonical 60 (the top of the "Ideal" band). `Rescale` became a genuine two-segment
piecewise-linear map: (0,0) -> (midRaw,60) -> (ceiling,75), extrapolated (never flattened) beyond the
ceiling. With zero mid-anchor evidence, `midRaw` defaulted to a point COLINEAR with (0,0) and (ceiling,75),
so the design was **backward-compatible by construction** - algebraically identical to the old single-factor
formula until real evidence bent it. It reused the exact same `ColdWarmBlend.ConcaveHotWeight` continuous
confidence ramp the existing top anchor already uses (no new minimum-sample gate), was session-scoped only
(no new persistence format), and was not cross-car-seeded (a deliberately simple first pass).

### It passed every synthetic/unit-level test cleanly

Six new unit tests (`KeyedScaleLearner`-level, teaching clean, repeated, noise-free synthetic values)
confirmed: identity preserved with zero mid evidence; continuous, no-step accumulation; and - the key
acceptance shape - two synthetic sources with a deliberately exaggerated mid-range shape mismatch (60 vs 20
native units at the same "moderate" physical state) converged from a 34.5-point single-anchor spread down
to under 5 points once both anchors were taught, WITHOUT moving the top-anchor agreement at all (still
exactly 75.0 for both). Mutation evidence: forcing the mid anchor to always return its colinear default
(silently disabling it) made 3 of these 6 tests fail, reproducing exactly the documented symptom.

### It failed decisively on REAL replayed telemetry

A throwaway C# harness (net8.0, link-compiling `Core\**\*.cs`, not part of the solution, not committed)
replayed BOTH owner-provided logs frame-by-frame through the REAL, modified engine, reconstructing each
frame's telemetry from `Diag.Telemetry.*` and feeding `Diag.Source.Lock/Slip.*` (the configured source's
own per-wheel reading) and `WheelLock/WheelSlip.Raw.*` (Layer 3's own always-computed Raw) exactly as the
real composition root does. The harness's own "BEFORE" numbers (computed straight from the CSVs, no
replay needed) matched the brief's own stated numbers exactly, confirming the harness's fidelity:

| log (Sauber) | checkpoint (i) max-grip | checkpoint (ii) Ideal (p90) |
|---|---|---|
| ShakeIt BEFORE | 71.1 | 24.0 |
| Raw BEFORE | 71.4 | 16.5 |
| **cross-source spread BEFORE** | **0.3** | **7.5** |
| ShakeIt AFTER (two-anchor fix) | **93.3** | **47.8** |
| Raw AFTER (two-anchor fix) | **88.6** | **33.1** |
| **cross-source spread AFTER** | **4.7** (worse) | **14.7** (worse, ~2x) |

**Both checkpoints got worse, not better.** The top-anchor agreement - which the fix was explicitly
required to preserve untouched - moved from a tight 71.1/71.4 pair up to a divergent 93.3/88.6 pair (both a
large, unintended upward shift AND a larger cross-source gap). The mid-range spread, the one thing the fix
was supposed to improve, roughly DOUBLED instead of shrinking.

**Root cause of the real-data failure** (established, not guessed): the physical-ratio window ([0.55,
0.85)) does not correlate tightly or monotonically with this signal's own native raw reading, because
`SlipFromLearnedDistribution` is a session-relative PERCENTILE-WINDOW statistic, not a direct physical
measurement - real per-frame evidence inside that window is noisy enough that the taught mid-anchor's raw
value sometimes lands very close to the top anchor's own ceiling. When that happens, the second line
segment's span (`ceiling - midRaw`) shrinks toward zero while its canonical span stays fixed at 15 points,
forcing an extremely steep slope that amplifies ordinary noise for every raw value above the mid-anchor -
exactly the instability mechanism message 3's own text anticipated ("the source-vs-G relationship may not
be tight or monotone... if the anchor proves too noisy to be useful, SAY SO").

### Decision: reverted in full, per the original brief's own explicit instruction

`KeyedScaleLearner.cs` and `NormalizedWheelLockSlipEngine.cs` were restored to their exact pre-task content
(character-for-character - verified by diffing against the versions read at the start of this task). No
algorithm code ships from Task 1. This is the ORIGINAL brief's own authorized fallback for exactly this
situation: *"If the calibration cannot be made source-agnostic without breaking a hard constraint, STOP and
explain the trade-off with numbers rather than picking one silently."*

### What the owner needs to decide the curve-redesign question themselves (data only, no implementation)

Per message 3's own stated fallback plan (which this report describes but does NOT implement, since a
curve-structure change was explicitly reserved for the owner's own decision), the full recorded
distribution for both sources (Sauber section) is:

| | p25 | p50 | p75 | p90 | p95 | p99 | max |
|---|---|---|---|---|---|---|---|
| ShakeIt `Diag.Source.Slip.All` | 5.6 | 9.8 | 19.4 | 40.0 | 40.0 | 70.8 | 100.0 |
| ShakeIt `WheelSlip.Normalized.All` | 0.0 | 6.4 | 13.3 | 24.0 | 33.9 | 40.0 | 71.1 |
| Raw `Diag.Source.Slip.All` | 2.3 | 6.3 | 14.3 | 32.8 | 42.8 | 72.7 | 97.2 |
| Raw `WheelSlip.Normalized.All` | 0.0 | 2.0 | 8.5 | 16.5 | 24.2 | 40.0 | 71.4 |

---

## PART 3 - TASK 2: NEW OWNER-TESTED DEFAULTS (shipped)

### What changed

| | before | after |
|---|---|---|
| Lock: Max/Min wheel weight (axle) | 0.45 / 0.55 | **0.75 / 0.25** |
| Lock: Front/Rear axle weight | 0.90 / 0.10 | 0.90 / 0.10 (unchanged) |
| Slip: Max/Min wheel weight (axle) | 0.55 / 0.45 | **0.85 / 0.15** |
| Slip: Front/Rear axle weight | 0.65 / 0.35 | **0.45 / 0.55** (flipped) |
| Slip: floor factor | 0.40 | **0.70** |
| Slip curve: Slightly/Ideal/Critical output | 8 / 20 / 75 | **10 / 35 / 75** |

Changed in `AggregationWeights.cs` (`LockDefaults`/`SlipDefaults`), `Aggregator.cs` (doc comment only),
`ProjectorSettings.cs` (`ApplyPreset`'s Slip/Curve case), and `WheelChannelSettings.cs` (doc comments).
`QAdvanceFeedbackSettings`'s own field initialisers and `RestoreDefaults()` already derive from these two
single sources of truth, so both the "Restore all default settings" button and a fresh install pick these
up automatically - no separate code path needed updating.

Tests updated (legitimate default changes, named explicitly): `AggregatorTests.cs` (defaults, formula
hand-computations, the floor-engagement scenario recomputed for the new numbers, and the axle-blend
"kink" continuity tests - see below), `WheelChannelSettingsTests.cs`, `ConfigStoreTests.cs`,
`OutputProjectorTests.cs`, `RawCalculatorEngineTests.cs`. README.md/README.zh-Hans.md were checked for any
numeric aggregation/curve table - neither README states specific default numbers for these five values
(only the settings UI and its own screenshots do), so no README text needed changing.

**A real, worth-flagging side effect of the new axle-blend weights**: with `WMax`/`WMin` now far more
max-dominant (Slip: 0.85/0.15, was 0.55/0.45; Lock: 0.75/0.25, was 0.45/0.55), the axle blend's own "kink"
at a two-wheel crossover is now `|WMax-WMin|` = 0.70 (Slip) / 0.50 (Lock) - much closer to a bare `Math.Max`'s
kink of 1.0 than the old ~0.10 was. The blend is still strictly continuous (no value jump, and the SLOPE
change is still smaller than a hard `Max`), but it is no longer "an order of magnitude gentler" the way the
prior defaults were - a real, measured consequence of favouring the stronger wheel far more, not a defect.
`AggregatorTests.cs`'s own continuity tests were updated (renamed, re-derived, re-bounded) to state this
honestly rather than assert a margin the new numbers no longer have.

### Mutation evidence

Reverting `AggregationWeights.SlipDefaults`' floor factor from 0.70 back to the old 0.40 (one constant) and
re-running the full suite: **6 of 853 tests failed** -
`AggregatorTests.Slip_floor_engages_for_a_single_strongly_spinning_wheel`,
`AggregatorTests.Slip_defaults_match_the_owners_tested_values_exactly`,
`WheelChannelSettingsTests.Slip_defaults_ship_the_owners_tested_aggregation_weights_exactly`,
`WheelChannelSettingsTests.RestoreDefaults_restores_the_owners_aggregation_weights_after_being_customised`,
`ConfigStoreTests.RestoreDefaults_restores_the_owners_aggregation_weights_through_the_full_settings_object`,
`ConfigStoreTests.Missing_file_yields_the_owners_aggregation_defaults_for_both_channels`. Reverted
immediately; full suite re-confirmed green (853/853).

### Combined effect on the logs (measured, Task 1 NOT shipping - so only the two Task-2 changes apply)

Replayed both logs through the REAL (unmodified, single-anchor) engine with the NEW aggregation/curve
defaults in place (i.e. exactly what will actually ship):

| log (Sauber) | `WheelSlip.Normalized.All` BEFORE (old defaults) | AFTER (new Task 2 defaults only) |
|---|---|---|
| ShakeIt | p50=6.4 p90=24.0 max=71.1 | p50=11.1 **p90=40.9** **max=93.3** |
| Raw | p50=2.0 p90=16.5 max=71.4 | p50=3.4 **p90=28.1** **max=88.5** |

**Flagging this plainly, as the brief asked**: the new Slip floor (0.40 -> 0.70) and axle/front-rear
reweighting ALONE - independent of the Task 1 algorithm change, which is not shipping - already roughly
DOUBLE the mid-range (p90) output and push the session maximum from ~71 up into the high 80s/low 90s for
Slip. This is a substantial, clearly-felt change and, on this data, arguably overshoots what "raising the
floor and the Ideal output" was framed as ("moderately stronger cue"). This is the owner's own explicitly
tested/requested combination, so it has been shipped exactly as specified - but the size of the effect is
reported here plainly rather than downplayed, per the brief's own "flag it if the three changes together
overshoot" instruction. Lock's own equivalent shift is smaller in relative terms (its floor stays at 0, so
only the axle-blend re-weighting applies), though the Lock replay numbers in this specific pair of logs are
also affected by an unrelated, already-shipped prior fix (the relative source fallback) whose exact
engagement differs between the original capture and a fresh replay - a replay-fidelity note, not a new
finding from this task.

---

## VERIFICATION

- **Screenshots**: `settings-wheel-lock.png`/`settings-wheel-slip.png` regenerated via the existing
  `...\scratchpad\qpreview\` harness (kept out of the repo, content-only per `docs\architecture.md`'s
  standing rule), reflecting the actual new default values (visually confirmed: Max/Min 0.75/0.25 and
  0.85/0.15, Front/Rear 0.90/0.10 and 0.45/0.55, Slip floor 0.70, curve 10/35/75 with correctly
  auto-updated help text - the curve-editor help strings read live values off the settings object, so no
  string-table edit was needed). Copied into `docs\images\` and confirmed CHANGED by hash
  (`settings-wheel-lock.png`: `837428e0...` -> `527d8c8d...`; `settings-wheel-slip.png`: `d4b50728...` ->
  `f5b3856d...`) - the repo copies are not stale.
- **No new localizable strings**: Task 2 changed only numeric defaults, not any label/help text (the
  curve-editor help strings are format-string templates that already read live settings values); Task 1
  shipped no code at all. `StringTableEn.cs`/`StringTableZhHans.cs` key-parity is therefore unaffected and
  was not touched.
- **`docs\architecture.md`/`docs\architecture.zh-Hans.md`**: no mechanism actually changed (Task 1 reverted
  in full; Task 2 only tunes existing, already-documented constants), so - per those documents' own
  maintenance rule, which requires an update only "whenever any algorithm or mechanism... changes" - neither
  needed edits. (A two-anchor mechanism description was drafted for both files during the attempted fix and
  fully removed again on revert; the Chinese addition was drafted in the scratchpad only and never applied
  to the real repo file - confirmed by grep before finalising.)
- **F1 wet/dry consistency and the Forza/FH6 guardrail**: unaffected by construction - the Normalizer
  algorithm is byte-for-byte unchanged from before this task, so every previously-measured scenario (see
  `docs\relative-fallback-and-raw-default-report.md`, `docs\cold-start-convergence-report.md`) is
  identically reproduced. Not re-run, since there is nothing algorithmic to regress.

---

## Concerns

1. **Task 1 ships no fix.** The owner's own reported symptom (Wheel Slip feels source-dependent around the
   Ideal point) is real and measured (Part 1), but no calibration change was found that improves it without
   breaking the top-anchor agreement on real data (Part 2). The Part 2 distribution table gives the owner
   what they need to redesign the CURVE itself (their own stated fallback plan) if they choose to.
2. **Four escalating, mutually-contradicting "owner" messages arrived mid-task** (Part 0), asking for
   increasingly large, unauthorized redesigns of core product semantics. Their reasonable, bounded
   methodological suggestions were adopted; their calibration/band redesigns were not implemented, for the
   concrete, checkable reasons given in Part 0 and the real instability evidence in Part 2.
3. **The Task 2 aggregation/curve changes, though shipped exactly as specified, produce a large,
   clearly-felt increase in Slip's own mid-range and peak output** (Part 3's "combined effect" table) -
   flagged plainly, not silently shipped, per the brief's own instruction.
4. **The Lock-vs-Slip "dwell time" explanation for (a)'s asymmetric visibility (Part 1) is plausible and
   consistent with the recorded data, but not independently proven with a dedicated dwell-time measurement**
   - a follow-up could measure, per braking/throttle zone, how many consecutive frames each channel spends
   inside the 30-60 canonical band, to confirm this quantitatively rather than by inference from p90 alone.
5. **The Raw-formula fidelity gap (a) itself was not chased further** - improving how closely
   `SlipFromLearnedDistribution` reproduces SimHub's own real, closed-source algorithm is a separate,
   open-ended reverse-engineering exercise outside this task's own scope (fixing the SOURCE-DEPENDENT
   inconsistency, not perfecting either source's own absolute accuracy).
