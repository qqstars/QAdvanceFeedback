# QAdvanceFeedback - ShakeIt export + Layer 2/3 open-source split report

> **Naming note (added later, see docs/refinements-report.md):** the property tiers this report calls "Reliable" and "Final" were subsequently renamed to **"Normalized"** and **"Projected"** respectively (published names `WheelLock.Normalized.*`/`WheelLock.Projected.*` etc.) - this report is left as a historical record of what was true at the time and is NOT updated throughout for the new names.


## Status

Both tasks complete. **348/348 tests passing**, Release build **0 Warning(s), 0 Error(s)**,
`bin\Release\net48\` contains exactly two files (`QAdvanceFeedback.dll`, `QAdvanceFeedback.pdb`).
Verified with a real, independent build of the plugin project WITHOUT `Private\*.cs` present (see
"Task B verification" below) - also 0 warnings, 0 errors, single-DLL.

## Test count

**348 tests, all passing** (`dotnet test QAdvanceFeedback.Tests/QAdvanceFeedback.Tests.csproj -v q`).
Baseline before this task was 330 (per the prior `docs\*-report.md` history plus the packaging
agent's concurrent work); this task added **18 new tests**:
`ClampMathTests` (+6, `CoerceOrFallback`), `PrivateTypeResolverTests` (+7), and
`InertLegacyWheelLockSlipEngineTests` (+5).

## Build / single-DLL confirmation

- `MSBuild QAdvanceFeedback\QAdvanceFeedback.csproj -t:Rebuild -p:Configuration=Release -v:normal`
  -> **0 Warning(s), 0 Error(s)**.
- `bin\Release\net48\` contains **exactly two files**: `QAdvanceFeedback.dll` (153,088 bytes) and
  `QAdvanceFeedback.pdb`.
- Confirmed a second time on an independent scratch copy of the project with `Private\*.cs` removed
  (see "Task B verification") - also 0/0, also single-DLL.

---

## TASK A - SimHub ShakeIt "export output value as a property" (decompilation-verified)

All of the following was confirmed by decompiling `SimHub.Plugins.dll` (`ilspycmd`, against the copy
in `QAdvanceFeedback\lib\`) - nothing here was guessed or taken from memory. Full detail, including
every decompiled member and the reasoning, is in `docs\shakeit-export-guide.md`.

### The confirmed property name/prefix

`ShakeITV3PluginBase.ExportProperties` builds each exported name as:

```csharp
"Export." + i.Output.PropertyName + "." + j.Placement
```

then calls `pluginManager.AttachDelegate(name, pluginType, ...)`, and
`PluginManager.GetName(name, pluginType)` is hard-coded as `pluginType.Name + "." + name`. So the
**full registered name** is:

```
<ShakeITMotorsV3Plugin | ShakeITBSV3Plugin>.Export.<user-typed property name>.<Placement>
```

- `pluginType.Name` is `ShakeITMotorsV3Plugin` if the user enabled the **"ShakeIt Motors"** plugin,
  or `ShakeITBSV3Plugin` if they enabled **"ShakeIt Bass Shakers"** (both confirmed
  `[PluginName(...)]` attribute values).
- The owner's guess (`ShakeItV3.WheelLock.IRacing.FrontLeft`) was **not correct** - there is no
  `ShakeItV3.` prefix, no `.IRacing.` segment, and the plugin-name segment is one of the two class
  names above, not a fixed `ShakeItV3`.
- `Placement` is the `FFBPlacement` enum value: `FrontLeft`/`FrontRight`/`RearLeft`/`RearRight` for
  the two effects that matter here (confirmed each container constructs exactly these four
  `WheelSlipEffect` instances with those placements).

### Confirmed UI label strings (SimHub's own wording/spelling, quoted exactly)

| Step | Confirmed SimHub label / name |
|---|---|
| Enabling the plugin | `[PluginName("ShakeIt Motors")]` / `[PluginName("ShakeIt Bass Shakers")]` |
| Adding the effect | Container display names `"Wheels lock"` / `"Wheels slip"`, group `"Slip and lock"` (there is also a simpler, non-per-wheel `"Wheels spin and lock"` - not what you want) |
| Channel/placement selector | No such selector on these two effects - they are ALWAYS exactly 4 sub-effects (Front left/Front right/Rear left/Rear right). The "mono/L-R/front-rear/4 wheels" pattern the owner recalled is a DIFFERENT, generic audio-channel-mapping selector (`ChannelsMode`: `Mono`, `"Left / Right"`, `"Front / Rear"`, `"Front Left / Right"`, `"Rear Left / Right"`, `"Corners"` (4 wheels), `"Custom channel map (up to 8 channels)"|) used when routing a Bass Shakers effect to physical sound channels - unrelated to which/how-many properties get exported |
| Legacy algorithm checkbox | **"Use legacy IRacing algorythm"** (SimHub's own spelling: capital R in "IRacing", "algorythm" misspelled) - backing member `UseLegacyIracingAlgorythm` on both containers |
| Export checkbox | **"Export output value as a property"** (resource KEY is `..._ExportOutputToPropery_...` - typo'd "Propery" only in the internal key, the on-screen text is spelled correctly) |
| Property name field | **"Property name"** (same text for label and watermark) |
| "Cannot be used" / mute option | **"Disable output"** (`OutputBase.DisableOutput`) - confirmed by decompilation that this is safe to combine with export: the export path (`ExportProperties`) checks only `GetEffectiveIsEnabled()`, `ExportProperty`, and non-empty `PropertyName`, never `DisableOutput` - so ticking "Disable output" stops it driving hardware while it keeps computing/exporting |

### Per-wheel / range

Confirmed per-wheel (four independent properties, not one combined value) and confirmed **0-100**:
`EffectsContainerBase`'s per-frame update does
`effect.LastOutput = Clamp(computedValue, 0.0, 100.0)` immediately before that same value is what
gets exported.

### Deliverables

- **`docs\shakeit-export-guide.md`** - the full numbered walkthrough with all of the above, plus
  exact worked example property names for both plugin variants.
- **Settings UI note** - a short, bilingual, italic note added to both the Wheel Lock and Wheel Slip
  tabs, directly under the existing Sources notes (next to the four per-wheel source fields):
  English key `Sources.ShakeItNote` (`StringTableEn.cs`) and its Simplified Chinese counterpart in
  `StringTableZhHans.cs`, inserted via a pure-ASCII Python script
  (`io.open(..., encoding='utf-8')` + `\uXXXX` escapes in the script source - no raw CJK in any
  heredoc), matching this project's own stated convention for editing that file. Key-parity test
  (`LocalizationTests`) stays green - verified.

### Layer 4 default source: **NOT changed - `Raw.*` stays the shipped default**

The standing instruction was conditional ("**if** the exported properties are per-wheel and
usable... change the default"). The export IS per-wheel and on the right scale, but I judged it
**not usable as an automatic default**, for a concrete, decompiled reason: the full property name
is not a fixed string SimHub assigns - it depends on (a) which of the two ShakeIt plugin variants
the user enabled (different `pluginType.Name` prefix) and (b) whatever free text they typed into
"Property name" (there is no default name). Hard-coding a guess (e.g. always assuming
`ShakeITMotorsV3Plugin.Export.WheelLock.FrontLeft`) would silently produce nothing for anyone who
used Bass Shakers, a different property name, or hasn't set up the export at all - exactly the
kind of confident-but-wrong default this project avoids elsewhere, and exactly the case the task's
own escape hatch anticipated. `DefaultWheelSources.cs`/`WheelChannelSettings.cs` are unchanged in
behaviour; I added a doc comment there recording this reasoning for future maintainers, and the
guide documents how to point a Source field at a ShakeIt export **manually** (step 7).

### Fallback tested

`WheelSourceResolver.Resolve`'s Plain-source path (`PluginManager.GetPropertyValue` -> convert ->
fallback on any failure) was refactored to funnel through a new, pure, SimHub-free core:
`ClampMath.CoerceOrFallback(object raw, double fallback)`. This is the exact mechanism a Layer 4
Source field pointed at a ShakeIt-exported property (or anything else) relies on. Tested directly
(`ClampMathTests`, 6 new cases): `null` (property not yet exported) -> fallback, not zero; a present
boxed `double`/`int`/convertible `string` -> used verbatim; `NaN`/`Infinity` -> fallback; an
unconvertible object -> fallback, no throw. `WheelSourceResolver`/`PluginManager` themselves cannot
be unit-tested outside a live SimHub process (same limitation this project has stated elsewhere) -
flagged as **unverified**, not silently assumed to work.

---

## TASK B - Open-sourcing without the SimHub-derived Layers 2/3

### How the code split works

Two PUBLIC contracts now live outside `Private\` and ship in the repo, each fully documented (input
shape, the required 0-100 output range, per-wheel semantics, guards, never-throw expectation):

- **`QAdvanceFeedback.Core.ILegacyWheelLockSlipEngine`** (Layer 3) - `Compute(ITelemetrySample)` ->
  `LegacyWheelLockSlipResult`.
- **`QAdvanceFeedback.ITelemetryAdapter`** (Layer 2) - `Reset()` / `Read(GameData)` ->
  `TelemetrySample`.

The real implementations (`LegacyWheelLockSlipEngine`+`LegacySlipAlgorithm`,
`SimHubTelemetryAdapter`) moved, verbatim except for two small mechanical changes (see below), into
`QAdvanceFeedback\Private\`, marked `internal`. **Nothing outside `Private\` references these
concrete class names anywhere** (verified by grep before and after the move - the only other
mentions in the whole codebase were doc-comment cross-references, which still compile fine as plain
text).

`QAdvanceFeedback.cs` now resolves both through a new `internal static class AlgorithmFactory`,
which calls a new, pure, SimHub-free `QAdvanceFeedback.Core.PrivateTypeResolver.CreateOrFallback<T>`
- this looks up a type BY NAME STRING (`"QAdvanceFeedback.Core.LegacyWheelLockSlipEngine"` /
`"QAdvanceFeedback.SimHubTelemetryAdapter"`) via `Assembly.GetType`, and only constructs it if it
exists, is concrete, and is assignable to the requested interface; any failure (absent, wrong shape,
no parameterless constructor, throws) falls through to a public, deliberately inert stub
(`InertLegacyWheelLockSlipEngine` / `InertTelemetryAdapter`), logging once via `SimHub.Logging` that
no algorithm implementation is present. Because the lookup is a runtime string, not a compile-time
type reference, `QAdvanceFeedback.cs` and everything else compiles identically whether or not
`Private\` exists on disk - this is the entire mechanism that makes the split work with SDK-style
`**/*.cs` globbing (this csproj has no explicit `<Compile>` items at all).

Two mechanical fixes were needed to make this work correctly, both flagged here rather than left
silent:
1. `LegacyWheelLockSlipEngine`'s constructor had a single **optional** parameter
   (`Aggregator aggregator = null`) - `Activator.CreateInstance(Type)` only recognises a TRUE
   zero-parameter constructor, so this silently made the type unresolvable (falling back to the
   stub even with `Private\` present) until I split it into a genuine parameterless constructor
   plus a required-parameter overload. Caught by `PrivateTypeResolverTests`' own end-to-end case
   against the real type name, not assumed.
2. Everything else (interfaces, stubs, factory, `.gitignore`, `README.md`) is new, additive code.

`QAdvanceFeedback\Private\README.md` (kept, NOT gitignored) documents the exact contract, the two
required type names/namespaces, and that a public parameterless constructor is mandatory.

### Gitignore

Per instructions, the root `.gitignore` (owned by the concurrent packaging task) was **not
touched**. Instead:
- Added a scoped `QAdvanceFeedback\Private\.gitignore` (`*.cs`, `!.gitignore`, `!README.md`) - this
  is self-contained and takes effect regardless of what the root file does.
- **Exact line for the root `.gitignore`, if preferred there instead**:
  ```
  QAdvanceFeedback/Private/*.cs
  !QAdvanceFeedback/Private/README.md
  ```

### Tests

- **With the Private implementation present (this working copy)**: all 348 tests pass, including
  the pre-existing `LegacySlipAlgorithmTests`/`LegacyWheelLockSlipEngineTests`, which now
  link-compile the real algorithm from its new `Private\` location via two explicit,
  `Exists()`-guarded `<Compile Include>` lines in `QAdvanceFeedback.Tests.csproj` (not a wildcard,
  so a Private-less clone doesn't get a missing-file build error) - behaviour is provably unchanged.
- **Stub inertness** (`InertLegacyWheelLockSlipEngineTests`, 5 new tests): every one of the 18
  published Lock/Slip numbers is exactly 0 regardless of input (including a null sample), never
  throws, and is stable across repeated calls.
- **Resolution mechanism** (`PrivateTypeResolverTests`, 7 new tests): existing-and-assignable type ->
  constructed; nonexistent name, wrong type, no parameterless constructor, or a null assembly -> all
  fall back safely, never throw; plus one test that runs the exact real type name
  (`"QAdvanceFeedback.Core.LegacyWheelLockSlipEngine"`) and self-adapts its assertion to whichever
  state (`Private` present or absent) the CURRENT build is actually in, so it stays meaningful and
  green either way.
- The Tests project's own csproj now degrades gracefully too: if `Private\LegacySlipAlgorithm.cs` is
  absent, it automatically excludes the two test files that reference the concrete types directly,
  so `dotnet test` still runs cleanly on a Private-less clone (a bonus beyond what was strictly
  asked, kept because it was nearly free).

### Task B verification: build WITHOUT the Private files

Done for real, not claimed blind. Copied the `QAdvanceFeedback\` project folder (source files only,
no `bin`/`obj`) to a scratch directory, deleted the three `Private\*.cs` files there, then built that
copy directly (`MSBuild ...\QAdvanceFeedback.csproj -t:Restore,Build -p:Configuration=Release
-p:SimHubRefPath=<original lib path>`, overriding the ref-assembly path since only the project
folder was copied, not `lib\`):

- Build result: **0 Warning(s), 0 Error(s)**.
- Compile file list (from `-v:normal`) confirmed the three `Private\*.cs` files were genuinely
  absent from the invocation - not silently still compiled from a cached path.
- `bin\Release\net48\` in the scratch copy: exactly `QAdvanceFeedback.dll` + `.pdb`, same as the
  real build.
- Decompiled the resulting scratch DLL's type list (`ilspycmd -l c`): contains
  `QAdvanceFeedback.InertTelemetryAdapter` and `QAdvanceFeedback.Core.InertLegacyWheelLockSlipEngine`,
  and does **NOT** contain `SimHubTelemetryAdapter` or `LegacyWheelLockSlipEngine` (only the public
  `LegacyWheelLockSlipResult` DTO, as expected) - direct proof the stub path is what a Private-less
  build actually ships, not an assumption.
- The scratch copy (every file, by explicit full path, plus its now-empty directories) was then
  removed from the scratchpad - nothing was left behind, and nothing outside the isolated scratch
  temp directory was touched.

This was NOT run through SimHub itself (can't - listed as unverified below), only compiled and
inspected statically.

### Honesty about the limits of this scheme (stated plainly, as instructed)

**This is source-code omission, not protection.** The compiled `QAdvanceFeedback.dll` shipped from
`bin\Release\net48\` still contains whichever concrete Layer 2/3 implementation was present at
build time, as ordinary MSIL - it can be decompiled with exactly the same `ilspycmd` technique this
project itself used against `SimHub.Plugins.dll`. Withholding the `.cs` source from git keeps a
casual GitHub reader, and a plain `git clone`, from seeing or reproducing the algorithm; it does
**not**, and structurally cannot, keep it out of the binary anyone downloads and runs. The owner
should treat this as "not on GitHub", not as "can't be extracted".

### Unverified (cannot run SimHub in this environment)

- Whether SimHub actually loads and runs the plugin correctly with `AlgorithmFactory` in place (the
  reflection call itself, `Activator.CreateInstance`, `SimHub.Logging.Current.Warn` at real runtime).
- Whether the ShakeIt export walkthrough in `docs\shakeit-export-guide.md`, followed literally in a
  running SimHub install, produces exactly the property names described - it is decompilation-
  verified against the code path that builds those names, but never exercised live.
- The stub `InertTelemetryAdapter`'s behaviour against a REAL `GameData` instance (untestable
  outside SimHub - its own inertness was verified by code inspection: it never dereferences its
  argument at all, only `InertLegacyWheelLockSlipEngine`'s inertness has an automated test, for the
  reasons stated in the Tests section above).

## Concerns

1. The ShakeIt "channel/placement selector" the owner recalled (mono/L-R/front-rear/4-wheels) turned
   out to be a real SimHub feature, but a DIFFERENT one (audio channel mapping) than the effect
   structure it was originally attributed to (which is simply always 4 fixed wheels) - flagged
   clearly in the guide so it isn't mistaken for a per-wheel-export configuration knob.
2. The recommended `WheelLock`/`WheelSlip` property-naming convention in the guide (step 5) is a
   convention this report/guide invented for predictability - SimHub itself has no default or
   suggested name.
3. Everything under "Unverified" above.

---

## TASK C - Fixing the test-suite split (two defects in the Task B scheme)

### Status

Both defects fixed and independently verified with a real, from-scratch build in a scratch copy -
see "Clean-clone verification" below. **348/348 tests still pass with both `Private\` folders
present** (this working copy); **334/334 tests pass in a copy with BOTH `QAdvanceFeedback\Private\`
and `QAdvanceFeedback.Tests\Private\` reduced to only their `.gitignore`/`README.md`** - a genuine
clean-clone simulation, not merely the plugin project. Solution build: **0 Warning(s), 0 Error(s)**
in both states; Release build of the plugin project alone: **0 Warning(s), 0 Error(s)**,
`bin\Release\net48\` still exactly `QAdvanceFeedback.dll` + `.pdb`.

### Defect 1 - re-investigated: the real problem was "never verified", not "definitely broken"

The brief for this task asserted the test project would not compile at all on a clean clone, and
named six suspect files (`AggregatorTests.cs`, `InertLegacyWheelLockSlipEngineTests.cs`,
`LegacySlipAlgorithmTests.cs`, `LegacyWheelLockSlipEngineTests.cs`,
`LongitudinalDirectionResolverTests.cs`, `PrivateTypeResolverTests.cs`), while explicitly asking me
to verify the exact list myself rather than trust it. I did, by grepping every test file for actual
compile-time references (not comments/doc-cref/string literals) to the three withheld concrete type
names (`LegacySlipAlgorithm`, `LegacyWheelLockSlipEngine`, `SimHubTelemetryAdapter`), then confirmed
by actually building:

- Only **two** files genuinely reference a withheld type as a compile-time symbol:
  `LegacySlipAlgorithmTests.cs` (`LegacySlipAlgorithm.Compute(...)`) and
  `LegacyWheelLockSlipEngineTests.cs` (`new LegacyWheelLockSlipEngine()`).
- `AggregatorTests.cs` and `LongitudinalDirectionResolverTests.cs` never construct a withheld type
  at all - they use only public `Core`/`Core.Reliable` types. `InertLegacyWheelLockSlipEngineTests.cs`
  exercises only the public stub. `PrivateTypeResolverTests.cs` refers to
  `"QAdvanceFeedback.Core.LegacyWheelLockSlipEngine"` only as a **string literal** passed to
  `Assembly.GetType` (reflection), never as a compile-time type reference - it was written this way
  on purpose, precisely so it keeps working whether or not `Private\` exists.
- The existing `QAdvanceFeedback.Tests.csproj` already had a `Condition="!Exists(...)"` `<Compile
  Remove>` block for exactly the two real offenders. I built a genuinely from-scratch scratch copy
  (no `bin`/`obj` carried over, so no stale/cached compile could mask a real failure), deleted the
  three plugin `Private\*.cs` files, and both `dotnet build QAdvanceFeedback.sln` and `dotnet test`
  succeeded (334/334) **before I changed anything** - so, as tested, "the test project will not
  compile" did not currently reproduce. What genuinely WAS true, per the brief's own framing: this
  had only ever been asserted by code inspection in the prior report, never verified with a real
  clean-clone build - I have now done that verification for real (see below), and it passes.
- I made the fix anyway (see Defect 2), because physically moving the two real offenders into a
  gitignored `Private\` test folder is strictly more robust than the filename-based `<Compile
  Remove>` it replaces: a filename list silently rots if a test is renamed or a new SimHub-derived
  test is added and someone forgets to add it to the `Remove` list, whereas physical absence needs
  nothing to be kept in sync.

### Defect 2 - the test suite itself disclosed the algorithm

Fixed by giving the test project the same `Private\` split as the plugin project:

- **Moved** `LegacySlipAlgorithmTests.cs` and `LegacyWheelLockSlipEngineTests.cs`, unchanged, into
  new `QAdvanceFeedback.Tests\Private\`. These are the only two files that both (a) reference a
  withheld concrete type directly and (b) encode hand-computed SimHub-derived constants, guard
  thresholds, and a test named after a reproduced SimHub quirk
  (`Braking_branch_uses_new_ground_speed_for_both_halves_of_the_delta`) - exactly the disclosure
  risk described in the brief.
- **Added** `QAdvanceFeedback.Tests\Private\.gitignore` (`*.cs` / `!.gitignore` / `!README.md`,
  identical shape to `QAdvanceFeedback\Private\.gitignore`) and
  `QAdvanceFeedback.Tests\Private\README.md` (what lives there and why, what deliberately stays
  public and why, and how the build degrades gracefully).
- **Did NOT move** `AggregatorTests.cs` or `LongitudinalDirectionResolverTests.cs` - per the
  investigation above, neither constructs a withheld type or depends on SimHub-derived output
  shape (`LongitudinalDirectionResolver` is an original Layer 4/5 component, not decompiled from
  SimHub). I did, however, fix one genuinely disclosing **comment** in the file that stayed public:
  `AggregatorTests.Quad_of_two_equal_pairs_matches_pair_of_one_pair` had a comment asserting
  "FL=RL, FR=RR (**the legacy algorithm's actual shape**...)" - stating a real behavioural fact
  about the withheld algorithm, even though the test itself is generic p-norm math. Reworded to
  describe the general aggregator property being tested, with no claim about any specific
  algorithm's shape.
- **Left unchanged** `InertLegacyWheelLockSlipEngineTests.cs` and `PrivateTypeResolverTests.cs` -
  both are exactly the "mechanism" tests the brief says must stay public (inert-stub path,
  reflection factory/resolver behaviour), and neither encodes anything SimHub-derived.
- **`QAdvanceFeedback.Tests.csproj`** updated: removed the now-unnecessary `Condition="!Exists(...)"`
  `<Compile Remove="LegacySlipAlgorithmTests.cs"/-.../>` block (the files it targeted no longer
  exist at that path - it would silently no-op, so it was deleted rather than left as dead
  configuration) and rewrote the comment above the still-necessary guarded `<Compile Include>` of
  the plugin's own `Private\LegacySlipAlgorithm.cs`/`LegacyWheelLockSlipEngine.cs` to describe the
  new mechanism. No new `<Compile>` item was needed for the test project's own `Private\` folder -
  it has no explicit items at all and relies entirely on the SDK's default `**/*.cs` glob, so the
  two moved files compile in automatically when present and are simply absent (not a compile error)
  when not.
- **`QAdvanceFeedback\Private\README.md`** updated with a new "The test suite has the same split,
  for the same reason" section pointing at the test-side folder.

### Docs scan for the same disclosure problem

Scanned every public `docs\*.md` (excluding the ones explicitly out of scope: `README.md`,
`README.zh-Hans.md`, `docs\shakeit-export-guide.md`, `docs\images\`) for SimHub-derived constants,
expected values, or reproduced-bug behaviour, by grepping for the withheld algorithm's known
markers (its gain constant, its `Offset(...)` guard ranges, `GetRpmSpeedSlipLegacy`, decompiled
class names, mutated-constant values, wheel-parity/halving language).

- **`docs\layer123-report.md`** was the only offender, exactly as suspected, and it was thorough
  about it: a "Native value range and scaling" section spelling out the algorithm's internal gain
  constant and both guards' exact threshold ranges (derived from the decompiled source), a
  "Mutation evidence" table naming the exact mutated constant values AND the exact private test
  names/behaviour each mutation broke (which would let a reader reconstruct both the constants and
  what each guard does), and a "Wheel-index order" section explicitly tying the public per-wheel
  index order to the withheld algorithm's internal left/right halving logic. **Trimmed in place**
  (not moved to a gitignored file, since removing the specific numeric/behavioural detail was
  sufficient and simpler): each section now states only this project's own PUBLIC contract (native
  output isn't self-bounded to `[0,1]`; the engine wrapper is the sole `*100`+clamp boundary,
  verified by a dedicated - now private - test) and explicitly notes that the specific constants,
  guard thresholds, mutated values, and test names have been withheld, with a pointer to where the
  now-private test that covers them lives. The per-wheel index order itself (`FrontLeft=0...`) was
  kept, since that is `Core/Corners.cs`'s own public, already-shipped ordering, not a secret - only
  its explicit tie to the withheld algorithm's halving behaviour was removed.
- All other reports (`docs\gforce-report.md`, `docs\layer45-report.md`, `docs\packaging-report.md`,
  `docs\wiring-ui-report.md`, this file) were grepped for the same markers and found clean - the
  only other hits were mentions of the `UseLegacyIracingAlgorythm` **checkbox name** (a SimHub UI
  label a user configures, not derived algorithm arithmetic) and generic key-parity wording, neither
  of which discloses anything about the withheld algorithm's internals.

### `docs\reference\README.md` (new)

Added, explaining that `docs/reference/*.cs` (already gitignored by the root `.gitignore` -
confirmed) holds decompiled SimHub source kept as local reference material, and how to regenerate
it with `ilspycmd` against this project's own `lib/` (fetched by `tools/fetch-simhub-refs.sh`).
While writing it I actually ran the regeneration commands to confirm they work:

- `ilspycmd -t "SimHub.Plugins.DataPlugins.ShakeItV3.Effects.WheelSlipEffect" lib/SimHub.Plugins.dll`
  - **confirmed working** against exactly the 7 DLLs `tools/fetch-simhub-refs.sh` fetches.
- `ilspycmd -t "WoteverCommon.Extensions.MathExtensions" lib/SimHub.Plugins.dll` - **confirmed
  failing** (`Could not find type definition ... in type system`; the type is absent from
  `ilspycmd -l c lib/SimHub.Plugins.dll`'s full listing too). The checked-in (gitignored)
  `Wotever.MathExtensions.decompiled.cs` must therefore have been produced from a fuller SimHub
  installation than the 7-DLL build-time reference set this repo fetches - documented as an
  **unverified** regeneration path (no full SimHub installation was available in this environment
  to re-derive it).
- Also discovered, and documented, that `docs\reference\SimHub.MathExtensions.decompiled.cs` (the
  differently-named third file already in this folder) is stale: it currently contains only an
  `ilspycmd` "type not found" error trace, not real source. Flagged in the new README as a known
  issue; left untouched since fixing/regenerating it was out of this task's scope and it is
  harmless (gitignored, never read by any build or test).

### Clean-clone verification (the acceptance criterion)

Done for real, from scratch, for the whole solution this time (not just the plugin project as in
Task B):

1. Copied the entire project (source files only, via `find ... -prune` to exclude `bin`/`obj`/`.vs`
   - not a recursive `cp -r` of the whole tree, and no `rm -rf` was used anywhere in this process)
   to a fresh scratch directory in the system temp scratchpad.
2. Deleted, by explicit path, all three `QAdvanceFeedback\Private\*.cs` files AND both
   `QAdvanceFeedback.Tests\Private\*.cs` files in that copy - leaving only `.gitignore`/`README.md`
   in each `Private\` folder, exactly matching what a real clean clone would have.
3. `dotnet build QAdvanceFeedback.sln` (both projects, from a completely empty `obj\`/`bin\`, so no
   cached compile could hide a real failure) -> **Build succeeded, 0 Warning(s), 0 Error(s)**.
4. `dotnet build QAdvanceFeedback\QAdvanceFeedback.csproj -c Release` -> **0 Warning(s), 0
   Error(s)**; `bin\Release\net48\` contained exactly `QAdvanceFeedback.dll` + `.pdb`, confirming
   the split still yields single-DLL, warning-free output with the test-suite changes in place.
5. `dotnet test QAdvanceFeedback.Tests\QAdvanceFeedback.Tests.csproj` -> **Passed! 334/334, 0
   failed, 0 skipped** (348 minus the 11 + 3 = 14 tests that moved into the now-absent `Private\`
   test folder).
6. The scratch copy was then removed - every file deleted individually by explicit full path
   (`find -type f -exec rm`, never `rm -rf`), then every now-empty directory removed individually
   with `rmdir` (never a recursive directory-delete), leaving nothing behind in the scratchpad and
   nothing touched outside it.

### Concerns / unverified (Task C)

1. **Whether SimHub actually loads and runs the plugin** - unchanged from Task B, still cannot be
   verified in this environment (no live SimHub session).
2. **The `docs\reference\` regeneration path for `WoteverCommon.Extensions.MathExtensions`** - see
   above; confirmed NOT reproducible from this repo's own `lib/` alone, and the correct fuller
   source was not available to re-verify.
3. **`docs\reference\SimHub.MathExtensions.decompiled.cs` is a stale, contentless artifact** - noted
   for the owner's awareness, not fixed (out of this task's scope; harmless since gitignored).
4. Defect 1, as literally stated in this task's brief ("the TEST project will not compile"), did
   **not** reproduce in my from-scratch verification before I made any change - I'm flagging this
   discrepancy plainly rather than silently agreeing with the brief's premise, per this project's
   own stated convention of not asserting things that weren't actually checked. The fix was applied
   regardless, because it directly satisfies the brief's own instructions (move the disclosing
   tests into a gitignored `Private\` folder) and is a strictly more robust mechanism than what it
   replaced.
