# QAdvanceFeedback - Layers 1-3 report

> **Naming note (added later, see docs/refinements-report.md):** the property tiers this report calls "Reliable" and "Final" were subsequently renamed to **"Normalized"** and **"Projected"** respectively (published names `WheelLock.Normalized.*`/`WheelLock.Projected.*` etc.) - this report is left as a historical record of what was true at the time and is NOT updated throughout for the new names.


## Status

Complete. Layers 1 (game-agnostic telemetry interface), 2 (SimHub telemetry adapter) and 3 (legacy
RPM/speed lock & slip algorithm, per-wheel + aggregation) are implemented, built, and tested.
Layers 4/5 (settings UI, G-force channels, pulse feature) are explicitly NOT implemented, per the
brief - clean seams are left (see "Concerns / seams for later layers" below).

## Test count

**32 tests, all passing** (`dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj`):
MathHelpersTests (8), ClampMathTests (3), PublishedPropertyNamesTests (2), AggregatorTests (5),
LegacySlipAlgorithmTests (11), LegacyWheelLockSlipEngineTests (3).

## Build verification

- `MSBuild QAdvanceFeedback/QAdvanceFeedback.csproj -t:Rebuild -p:Configuration=Release -v:normal`
  -> **0 Warning(s), 0 Error(s)**.
- `bin\Release\net48\` contains **exactly two files**: `QAdvanceFeedback.dll` and
  `QAdvanceFeedback.pdb`. Every SimHub/GameReaderCommon/log4net reference in the csproj is
  `<Private>false</Private>`, confirming single-DLL distribution.
- `dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -v q` -> 32/32 passed.

## Published property names (verbatim)

`QAdvanceFeedback.WheelLock.Raw.FrontLeft`, `QAdvanceFeedback.WheelLock.Raw.All`,
`QAdvanceFeedback.WheelSlip.Raw.RearRight`, `QAdvanceFeedback.WheelSlip.Raw.Left`
(18 total: 9 targets x {WheelLock.Raw., WheelSlip.Raw.}, see `Core/PublishedPropertyNames.cs`).

### Deliberate deviation from the brief's literal class name - please read

The brief says both:
1. "Name the plugin class `QAdvanceFeedbackPlugin`."
2. "Published names must come out EXACTLY as `QAdvanceFeedback.WheelLock.Raw.All`."

These two instructions conflict. I decompiled `SimHub.Plugins.dll` myself to confirm
`PluginManager.GetName(string name, Type pluginType)` is hard-coded as:
```csharp
internal string GetName(string name, Type pluginType) => pluginType.Name + "." + name;
```
`Type.Name` is the simple class name. If the class were named `QAdvanceFeedbackPlugin`, every
published property would come out as `QAdvanceFeedbackPlugin.WheelLock.Raw.*` - failing requirement
(2) outright. This is exactly the mistake the brief says the sibling project (reliable-wheel-lock)
made calling its plugin class `Plugin` (`Plugin.ReliableWheel.Lock.All` instead of
`ReliableWheel.Lock.All`) - and exactly the fix that repo's `ReliableWheel.cs` documents.

I resolved the conflict by naming the class `QAdvanceFeedback` (matching the namespace root), NOT
`QAdvanceFeedbackPlugin`, so `Type.Name` supplies the required `QAdvanceFeedback.` prefix exactly
once. This is called out in `QAdvanceFeedback/QAdvanceFeedback.cs`'s own doc comment. Per process
note 1 ("if something cannot work as specified, stop and say so"): I did not silently improvise past
this - I'm flagging it explicitly as intentional, verified-by-decompilation resolution of a
self-contradictory brief, not a guess.

## Native value range and scaling to 0-100 (public contract only)

**The internal derivation of this section has been withheld** - it named the private algorithm's
gain constant and guard thresholds verbatim, which would reconstruct the decompiled SimHub
arithmetic almost as completely as shipping the source itself. What is safe, and useful, to state
publicly is the CONTRACT the withheld Layer 3 implementation must honour, which is exactly what
`ILegacyWheelLockSlipEngine`'s own doc comments already require:

- The private algorithm's native (pre-publish) scale is **not** self-bounded to `[0,1]` - it can
  legitimately exceed that range for a large enough input delta.
- `LegacyWheelLockSlipEngine` (the withheld engine wrapper, not the public interface) is the one
  and only place that scales to 0-100 and clamps (`ClampMath.To0100`) - this is the publish
  boundary; nothing downstream re-clamps this layer's own output.
- This clamping behaviour is verified by a dedicated test that deliberately constructs an
  out-of-range native value and asserts the published result is exactly clamped to `100.0`. That
  test lives in `QAdvanceFeedback.Tests\Private\` (gitignored, alongside the algorithm it tests) -
  see `docs\export-and-split-report.md`'s test-split section for why.
- **What could not be verified even with the original derivation:** whether SimHub's own downstream
  ShakeIt mixer applies an equivalent clamp internally - `WheelSlipEffect` is a leaf effect class,
  and the mixer that consumes its output was not decompiled for this task. Flagged as
  **unverified**, not asserted as a proven fact.

## Wheel-index order

Per-wheel order is `FrontLeft=0, FrontRight=1, RearLeft=2, RearRight=3` - this is `Core/Corners.cs`'s
own public, documented order (confirmed against SimHub's `EffectHelper.PlacementToIndex` during
development, so this plugin's per-wheel property names line up with the ShakeIt effect they are
meant to complement). How this order interacts with the withheld algorithm's own left/right
handling is intentionally not detailed here.

## Mutation evidence (mandatory)

Mutation testing was performed against three specific, known-important pieces of the withheld
Layer 3 algorithm (a scaling constant, a guard's threshold range, and the gear-change guard) by
editing `Core/LegacySlipAlgorithm.cs` directly, running `dotnet test`, and then reverting each
change exactly (confirmed by re-reading the file byte-for-byte against the original after
reverting; final full suite re-run green and the Release build re-verified 0-warning/single-DLL
after reverting). All three mutations were caught by a private, gitignored test in
`QAdvanceFeedback.Tests\Private\` - the specific mutated values and the exact test names involved
are withheld for the same reason the algorithm's own source is: naming them would reconstruct the
constants and guard thresholds this report already withholds above.

## Concerns / seams for later layers

1. **The class-name deviation above** - flagged prominently since it directly contradicts one line
   of the literal brief; verified by decompilation, not guessed.
2. **`FeedbackData.GroundSpeed` unit is inferred, not documented.** Decompiling
   `GameReaderCommon.dll` shows no unit doc-comment on `FeedbackData.GroundSpeed`. I infer it is
   km/h-compatible (matching its un-converted `??` fallback to `StatusDataBase.SpeedKmh`, and this
   assembly's own convention elsewhere of suffixing genuinely-m/s fields with "Ms", e.g.
   `SuspensionVelocityMs`/`PitLimiterSpeedMs`, which `GroundSpeed` lacks). Flagged in
   `SimHubTelemetryAdapter`'s remarks as an inference, not a proven fact.
3. **The downstream ShakeIt mixer/output clamp is unverified** (see "Native value range" above) -
   the *100+clamp-to-100 design decision rests on decompiled evidence from the withheld reference
   material, not a decompiled mixer; the supporting detail is intentionally not reproduced here.
4. **`calibratedFeedback.Data` vs `NewData.FeedbackData` equivalence is assumed.** The decompiled
   algorithm reads ground speed from `calibratedFeedback.Data.GroundSpeed` (a calibration-pipeline
   object), not directly from `data.NewData.FeedbackData.GroundSpeed`. Layers 4/5 (calibration) are
   out of scope here, so Layer 2 treats them as the same value - a stated simplification, not a
   verified fact about SimHub's calibration pipeline.
5. **Nothing here has been run inside a live SimHub session or against a real game** - it cannot be,
   per the task constraints. All 32 tests are pure, offline, hand-computed-expectation unit tests
   against `Core/` directly; the `SimHubTelemetryAdapter`/`QAdvanceFeedback` plugin class compile
   cleanly against the reference DLLs but their actual runtime behaviour inside SimHub is
   unverified.
6. Layers 4/5 seams left clean: `ITelemetryFrame` already exposes `LongitudinalG`/`LateralG`/
   `GroundSpeedMps` (unused by Layer 3, ready for later effects); `Aggregator` keeps its full
   `GroupMode` enum (Max/Mean/WeightedMean/PNorm/Min) even though only PNorm is wired up today, so a
   settings layer can expose the choice without changing `Core/` shapes; no settings/config file,
   WPF control, or `IWPFSettingsV2` exists yet.
