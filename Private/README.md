# Private\

This folder is intentionally **not** part of the public repository (see the sibling
`.gitignore` in this folder, which ignores every `*.cs` file anywhere under here but
deliberately keeps this `README.md` and the `.gitignore` itself). It holds every withheld
source file for both projects in this solution, consolidated into one top-level location
(mirroring the two project names) rather than one scattered `Private\` folder per project:

- **`Private\QAdvanceFeedback\`** - the plugin's withheld Layer 2/3 implementations.
- **`Private\QAdvanceFeedback.Tests\`** - the test project's withheld fidelity tests for those
  same two layers.

## `Private\QAdvanceFeedback\` - the withheld plugin implementation

Holds the two pieces of this plugin that were built by decompiling and reproducing SimHub's
own `WheelSlipEffect` arithmetic, which the project owner has chosen to withhold from the
open-source release:

- **Layer 2 - the telemetry adapter** (`SimHubTelemetryAdapter.cs`): maps SimHub's own
  `GameData`/`StatusDataBase`/`FeedbackData`/`LocalVelocity` shape onto this plugin's public,
  game-agnostic `QAdvanceFeedback.Core.ITelemetryFrame` / `TelemetrySample` types.
- **Layer 3 - the legacy algorithm** (`LegacySlipAlgorithm.cs` + `LegacyWheelLockSlipEngine.cs`):
  the actual per-wheel lock/slip arithmetic, faithfully reproducing SimHub's decompiled
  `WheelSlipEffect.GetRpmSpeedSlipLegacy` (the branch used when `UseLegacyIracingAlgorythm` is
  enabled), plus the aggregation/scaling that turns its native scale into the plugin's
  published 0-100 `WheelLock.Raw.*` / `WheelSlip.Raw.*` properties.

### Why the rest of the plugin still builds and runs without this folder

Everything **outside** `Private\` - Layers 1, 4, 5, G-force, the settings UI, persistence, and
this plugin's own composition root (`QAdvanceFeedback.cs`) - references only two PUBLIC
interfaces, never the concrete classes above by name:

- `QAdvanceFeedback.Core.ILegacyWheelLockSlipEngine` (see that file for the full documented
  contract: inputs, the required 0-100 output range, per-wheel semantics, guards).
- `QAdvanceFeedback.ITelemetryAdapter` (see that file for its contract).

`QAdvanceFeedback.cs` resolves both of these at RUNTIME through `AlgorithmFactory`, which looks
for `QAdvanceFeedback.Core.LegacyWheelLockSlipEngine` and `QAdvanceFeedback.SimHubTelemetryAdapter`
by NAME, via reflection, in its own assembly (`QAdvanceFeedback.Core.PrivateTypeResolver`) - never
a compile-time reference. When this folder (and therefore those two type names) is absent, the
factory silently falls back to two public, clearly-inert stubs
(`QAdvanceFeedback.InertTelemetryAdapter` / `QAdvanceFeedback.Core.InertLegacyWheelLockSlipEngine`),
logging once to the SimHub log that no algorithm implementation is present. The plugin still
builds, loads into SimHub, and runs every other channel (Layer 4/5 projection, G-force, the
settings UI, CSV export, persistence) exactly as normal - only the Wheel Lock/Slip **Raw**
numbers read as a flat 0 (never a crash, never an uninitialised/garbage value) until a real
implementation is supplied here.

**Since this folder now lives OUTSIDE both project directories** (it is a top-level sibling of
`QAdvanceFeedback\` and `QAdvanceFeedback.Tests\`, next to the `.sln`), the SDK-style projects'
default `**\*.cs` glob - which only scans *under* each project's own directory - no longer picks
these files up automatically. Both `.csproj` files have an explicit `<Compile Include>` for their
half of this folder instead (see each csproj's own comment). The `Include` is a wildcard, which
MSBuild simply evaluates to an empty item list when the folder is absent - not a build error - so
both projects still build, and every test outside `Private\` still passes, whether or not this
folder exists on disk.

### What a third party must implement to restore real Wheel Lock/Slip output

Drop two files into `Private\QAdvanceFeedback\`:

1. **`SimHubTelemetryAdapter.cs`** - a class named EXACTLY `QAdvanceFeedback.SimHubTelemetryAdapter`
   (namespace `QAdvanceFeedback`), implementing `QAdvanceFeedback.ITelemetryAdapter`, with a public
   parameterless constructor. See that interface's XML doc comments for the full contract
   (never return null from `Read`, never throw, what `Reset` is for).
2. **`LegacyWheelLockSlipEngine.cs`** - a class named EXACTLY
   `QAdvanceFeedback.Core.LegacyWheelLockSlipEngine` (namespace `QAdvanceFeedback.Core`),
   implementing `QAdvanceFeedback.Core.ILegacyWheelLockSlipEngine`, with a public parameterless
   constructor. See that interface's XML doc comments for the full contract: it must consume one
   `ITelemetrySample` (the current + previous game-agnostic telemetry frame) and return one
   `LegacyWheelLockSlipResult` - four per-wheel values (`Corners`: FrontLeft/FrontRight/RearLeft/
   RearRight, in that fixed index order) plus the Front/Rear/Left/Right/All aggregates, for BOTH
   the Lock and the Slip channel, every one of those 18 numbers already scaled to 0-100 and
   clamped (`QAdvanceFeedback.Core.ClampMath.To0100`) - this is the publish boundary; nothing
   downstream re-clamps this layer's own output. A missing telemetry reading (`null` on
   `ITelemetryFrame`) must be treated as "cannot tell", never silently substituted with 0 - see
   `ITelemetryFrame`'s own remarks.

The exact type names matter: `AlgorithmFactory` looks them up as plain strings
(`"QAdvanceFeedback.SimHubTelemetryAdapter"` / `"QAdvanceFeedback.Core.LegacyWheelLockSlipEngine"`)
- a different name, namespace, or a missing public parameterless constructor all fall back to the
inert stub exactly as if the file were absent (this is deliberate: a half-broken drop-in must
degrade safely, never crash the plugin - see `PrivateTypeResolver`'s own remarks).

You do not need to touch `AlgorithmFactory.cs`, `ITelemetryAdapter.cs`,
`ILegacyWheelLockSlipEngine.cs`, or anything else outside this folder - dropping these two files
into `Private\QAdvanceFeedback\` is sufficient; the plugin csproj's explicit `<Compile Include>`
(described above) picks them up the next time the project is built, no wildcard-under-project-dir
magic needed since it is no longer relying on that.

## `Private\QAdvanceFeedback.Tests\` - the withheld fidelity tests

Withholding the plugin implementation folder above is not enough on its own: a public test suite
with hand-computed expected values, guard-condition constants, and a test named after a
reproduced SimHub quirk would reconstruct the algorithm almost as completely as the source
itself. So the test project has the exact same split, consolidated into this sibling folder:

- **`LegacySlipAlgorithmTests.cs`** and **`LegacyWheelLockSlipEngineTests.cs`** - fidelity tests
  for `QAdvanceFeedback.Core.LegacySlipAlgorithm` / `LegacyWheelLockSlipEngine`, the withheld
  Layer 3 implementation. They are withheld for two independent reasons, either one of which
  would be sufficient on its own:
  1. They reference the withheld concrete types directly (`LegacySlipAlgorithm.Compute(...)`,
     `new LegacyWheelLockSlipEngine()`), so they cannot even compile in a clone that has not been
     given a `Private\QAdvanceFeedback\` implementation.
  2. Even if they could compile, their hand-computed expected values, guard-condition constants,
     and a test named after a reproduced SimHub quirk
     (`Braking_branch_uses_new_ground_speed_for_both_halves_of_the_delta`) reconstruct SimHub's own
     decompiled arithmetic almost completely - publishing them would defeat the entire point of
     withholding the implementation itself.

### What does NOT live here (and stays public, on purpose)

Tests for the **mechanism** around Layer 3 - as opposed to the withheld algorithm's own
arithmetic - stay in the main `QAdvanceFeedback.Tests\` folder, because they only exercise this
plugin's own public contracts and never encode a SimHub-derived constant or expected value:

- `InertLegacyWheelLockSlipEngineTests.cs` - the public, always-present stub is genuinely inert
  (every published number is exactly zero, never throws).
- `PrivateTypeResolverTests.cs` - the reflection factory/resolver mechanism itself (including its
  end-to-end case against the real type name, which uses that name only as a plain string via
  `Assembly.GetType`, never a compile-time reference, and self-adapts its assertion to whichever
  state - `Private` present or absent - the current build is actually in).
- `AggregatorTests.cs` - generic p-norm aggregation math (`Aggregator`/`Corners`), independent of
  any specific algorithm's output shape.
- `LongitudinalDirectionResolverTests.cs` - the Layer 4/5 `LongitudinalDirectionResolver`, an
  original component, not derived from decompiling SimHub, so its own thresholds are ours to
  publish freely.

### How the build degrades gracefully

`QAdvanceFeedback.Tests.csproj` has an explicit `<Compile Include>` wildcard for this folder (see
that csproj's own comment) - a plain SDK glob would no longer reach it now that it lives outside
the test project's own directory. That Include:

- **This folder present** (this working copy, right now): both files above compile in
  automatically, linked into a virtual `Private\` folder in Solution Explorer, and (given
  `Private\QAdvanceFeedback\LegacySlipAlgorithm.cs` / `LegacyWheelLockSlipEngine.cs` are ALSO
  present, separately link-compiled into this same test assembly via the csproj's own explicit
  `<Compile Include>` for that folder) they run and pass exactly as before the relocation.
- **This folder absent** (a clean clone with neither `Private\` subfolder supplied yet): the
  wildcard simply evaluates to nothing - no missing-file compile error, no special-cased filename
  `Remove` to keep in sync. The remaining public tests build and run unaffected.

### Restoring full-fidelity coverage

A third party who has supplied their own `Private\QAdvanceFeedback\LegacySlipAlgorithm.cs` /
`LegacyWheelLockSlipEngine.cs` and wants the same depth of regression coverage this repository's
own author has, should write their own tests in `Private\QAdvanceFeedback.Tests\` - being
gitignored, doing so never risks committing SimHub-derived detail back into the repository by
accident.

## Honest limits of this scheme (read this before relying on it for anything)

This is **source-code omission, not protection**. The compiled `QAdvanceFeedback.dll` released
in `bin\Release\net48\` still contains whichever concrete implementation was present at build
time (the project owner's own, or a third party's), compiled into ordinary MSIL - it can be
decompiled with exactly the same tools and techniques this project itself used to decompile
SimHub's `SimHub.Plugins.dll` (see `docs\reference\*.decompiled.cs` and the various `*-report.md`
files in `docs\`). Withholding the `.cs` source from the git history keeps a casual reader of the
GitHub repository from seeing the algorithm, and keeps a plain `git clone` from reproducing it -
it does not, and cannot, keep it out of the shipped binary.

## History

This folder previously existed as two separate, project-scoped folders
(`QAdvanceFeedback\Private\` and `QAdvanceFeedback.Tests\Private\`), each with its own
`.gitignore`/`README.md`. They were consolidated into this single top-level folder - see
`docs\private-relocation-report.md` for the full rationale and the exact csproj changes this
required.
