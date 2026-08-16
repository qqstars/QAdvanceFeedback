# Motion feel copy pass - G-Force tab

## STATUS

Done. UI-copy-only change (labels + help text) plus README sync and screenshot regen. No default, bound, or computation touched.

## Tests / build

- 567/567 tests green (full run), 0 build warnings, single `QAdvanceFeedback.dll` produced (net48) alongside the separate test assembly (net8.0) - no change to that shape.
- Key-parity test (`LocalizationTests.English_and_Simplified_Chinese_have_exactly_the_same_key_set`) stays green - no keys added/removed/renamed, only values changed.

## Important finding before making changes

The task brief assumed the *live* labels were still "Kick strength" / "Kick smoothing time" / "Level response time". That was **not** the state of the code: `StringTableEn.cs` already had `TransientGain` labelled **"Sweep speed gain"** and `TransientTau` labelled **"Sweep rate decay time (s)"** - only `SustainTau` was still "Level response time (s)". So a prior pass had already partly fixed the UI labels; the stale "Kick strength/Kick smoothing time" copy survived only in **README.md** and **README.zh-Hans.md** (both still described "Motion feel (the washout model)" using the old washout terminology and old labels), not in the shipped settings screen. I verified this against the actual source (`grep` across `.cs`/`.md`) before deciding what to rename, per the instruction to check before trusting the premise.

## Verification against GForceEngine

`TransientGain` is read in exactly one place, `AdvanceStageProgress`:
```
double advancePerSecond = Math.Min(Math.Max(travelRate * TransientGain, MinStageProgressPerSecond), MaxStageProgressPerSecond);
stageProgress = ClampMath.To01(stageProgress + advancePerSecond * dtSeconds);
```
It scales the observed/latched delta-rate into **stage-progress-per-second** only - it never touches `AdvanceSustainLevel` (the amplitude/level track), which is driven solely by `SustainTimeConstantSeconds`. So "speed, not amplitude" is confirmed - no misleading-label risk from a hidden amplitude effect.

`TransientTimeConstantSeconds` is the decay time constant of the *latched* travel rate (`ExponentialDecayToZero(travelRate, dtSeconds, TransientTimeConstantSeconds)`), i.e. it smooths/softens the sweep's onset rather than letting one noisy frame snap it forward - "smoothing" is an accurate plain-English description.

**Stage-progress cap confirmed:** `GForceEngine.MaxStageProgressPerSecond = 5.0` (private const) - a full 0→1 sweep can never complete faster than 0.2s regardless of how large the driving delta is. Raising Sweep speed past the point where `travelRate * TransientGain >= 5.0` has no further effect, since the `Math.Min(..., MaxStageProgressPerSecond)` clamp saturates. This is called out explicitly in the help text so a driver who cranks the slider and stops feeling any difference isn't left confused.

## Final labels and help text shipped (English)

| Key | Old | New |
|---|---|---|
| `GForce.SustainTau.Label` | Level response time (s) | *unchanged* - already accurate |
| `GForce.TransientTau.Label` | Sweep rate decay time (s) | **Sweep smoothing time (s)** |
| `GForce.TransientGain.Label` | Sweep speed gain | **Sweep speed** |

`GForce.Motion.Note` (help text under the "Motion feel" section header):

> Braking/acceleration feedback travels through three stages (far pad, then middle pad, then the pad closest to the direction of force) before settling - a hard stamp on the pedal sweeps through quickly and strongly, a gentle change sweeps slowly and gently. The STEADY level you're holding (how far each pad settles) is separate from this travel. These three numbers control the feel - a higher level response time feels slower/smoother once settled, a higher sweep speed makes a sudden change travel faster. To make the sweep feel slower, lower Sweep speed (it travels more gradually across the pads) and raise Sweep smoothing time (it eases in rather than snaps). Sweep speed is capped internally, so past a point turning it up further won't make the sweep any faster.

The added guidance is the last two sentences - directly answers "what do I adjust to make it slower/faster" and flags the internal cap.

Simplified Chinese (`StringTableZhHans.cs`) was updated in parallel with the same three keys, edited via a Python script (`io.open(..., encoding='utf-8')`, explicit `\uXXXX` escapes matching the file's existing style) - no raw CJK typed through a shell heredoc.

## READMEs

- `README.md`: rewrote the "Motion feel (the washout model)" bullet section (now "Motion feel (the staged sweep)") to match the current staged-sweep model and the new labels/guidance, including the 5 sweeps/second cap.
- `README.zh-Hans.md`: mirrored the same rewrite in Simplified Chinese, edited directly (real UTF-8 file, not a shell heredoc).
- Left untouched (out of scope, flagged below): the "G-force channels" intro line (`README.md` line ~41, "modelled on classical washout/motion-cueing rigs") and the "References and copyright" section's washout/motion-cueing attribution paragraphs - these describe the model's design lineage/attribution, not the specific settings, and changing them would have been scope creep beyond "the section's help text".

## Screenshot

Regenerated via the harness at the scratchpad `qpreview` folder (built against the freshly-rebuilt `QAdvanceFeedback.dll`, Release config) after all copy changes. The harness renders all four tabs each run and writes `settings-g-force.png` (its own naming); only that file was copied into the repo, renamed to `docs\images\settings-gforce.png` to match the README's `<img>`/markdown link. The other three PNGs it also wrote (`settings-general.png`, `settings-wheel-lock.png`, `settings-wheel-slip.png`) were left in the scratchpad and NOT copied over the existing repo copies - confirmed via file timestamps that `docs\images`'s other three PNGs are untouched. The harness itself lives only under the scratchpad path and was never copied into the repo.

Visually confirmed in the rendered screenshot: labels read "Level response time (s)" = 0.15, "Sweep smoothing time (s)" = 0.08, "Sweep speed" = 1.5 (all defaults unchanged), and the new help text renders correctly under "MOTION FEEL".

## Concerns / judgment calls

1. The task brief's assumed starting labels ("Kick strength", "Kick smoothing time") didn't match the live code - only the READMEs still used that language. Fixed the READMEs to match the (already-decent) current UI terminology instead of reintroducing the older phrasing.
2. Renamed `TransientGain`'s label from "Sweep speed gain" to "Sweep speed" (dropped "gain" as unnecessary jargon) and `TransientTau`'s label from "Sweep rate decay time (s)" to "Sweep smoothing time (s)" (plainer wording, pairs naturally with "Level response time (s)"). Labels only - `TransientGain`/`TransientTimeConstantSeconds` property names and persisted settings keys are untouched, so no config-migration risk.
3. Did not touch the broader "washout"/motion-cueing attribution prose elsewhere in either README (intro property list and "References and copyright") - flagging this as a possible follow-up if the owner wants the whole document's terminology fully purged of "washout", but it's design-history/copyright attribution rather than settings guidance, so left alone to avoid scope creep.
