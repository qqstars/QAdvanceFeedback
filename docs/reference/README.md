# docs\reference\

This folder holds **decompiled SimHub source**, kept locally as reference material during
development of the withheld Layer 2/3 implementations (see the top-level `Private\README.md`,
which covers both `Private\QAdvanceFeedback\` and `Private\QAdvanceFeedback.Tests\`). It is not
this project's code - it is Wotever's
(SimHub's author's) own code, recovered with a decompiler, and publishing it verbatim would be a
far more direct problem than publishing this project's own derived implementation.

## Gitignore status

The root `.gitignore` already ignores every `*.cs` file in this folder:

```
docs/reference/*.cs
```

This `README.md` is not matched by that pattern (only `*.cs` is), so it is tracked normally -
this folder itself, and the fact that it is used for reference material, is not a secret; only
the decompiled source text inside it is withheld.

## What is normally kept here

- `SimHub.WheelSlipEffect.decompiled.cs` - the ShakeIt effect class
  (`SimHub.Plugins.DataPlugins.ShakeItV3.Effects.WheelSlipEffect`) whose
  `GetRpmSpeedSlipLegacy` method this project's withheld Layer 3 algorithm reproduces.
- `Wotever.MathExtensions.decompiled.cs` - the `WoteverCommon.Extensions.MathExtensions` helper
  class (`Offset`/`Map`-style methods) that `WheelSlipEffect` itself calls into.
- `SimHub.MathExtensions.decompiled.cs` - an earlier, differently-named attempt at the same helper
  class; **known to be stale** (it currently contains only an `ilspycmd` "type not found" error
  trace, not real source - confirmed while writing this README). It is harmless (gitignored, never
  read by any build or test), but should not be trusted as a reference; use
  `Wotever.MathExtensions.decompiled.cs` instead, or regenerate as described below.

## How to regenerate locally

You need `ilspycmd` (the .NET global tool for ILSpy) and this project's own `lib/` reference
assemblies (see the root `README.md`'s "Building" section - `tools/fetch-simhub-refs.sh` rebuilds
`lib/` from a fresh clone).

```bash
dotnet tool install -g ilspycmd   # if not already installed
bash tools/fetch-simhub-refs.sh 9.11.22   # if lib/ does not exist yet
```

Then decompile a specific type by its full name against the relevant reference assembly:

```bash
ilspycmd -t "SimHub.Plugins.DataPlugins.ShakeItV3.Effects.WheelSlipEffect" lib/SimHub.Plugins.dll \
  > docs/reference/SimHub.WheelSlipEffect.decompiled.cs
```

This one is confirmed to work against exactly the 7 DLLs `tools/fetch-simhub-refs.sh` fetches into
`lib/` - nothing extra is needed.

**Caveat, confirmed while writing this README:** `WoteverCommon.Extensions.MathExtensions` (the
type behind `Wotever.MathExtensions.decompiled.cs`) is **not** present in any of the 7 DLLs
`lib/` normally contains -
`ilspycmd -t "WoteverCommon.Extensions.MathExtensions" lib/SimHub.Plugins.dll` fails with
`Could not find type definition ... in type system`, and the type does not appear in
`ilspycmd -l c lib/SimHub.Plugins.dll`'s full type listing either. Reproducing that specific file
therefore requires locating the assembly that actually contains it from a fuller SimHub
installation (not just the trimmed build-time reference set this repository fetches) - for
example, by installing SimHub directly and searching its own program directory for the DLL that
exports the `WoteverCommon.Extensions` namespace, then running `ilspycmd -t` against that DLL
instead of `lib/SimHub.Plugins.dll`. This was not re-derived from scratch for this README (no
full SimHub installation was available in this environment); it is recorded here as an
**unverified** regeneration path so a future maintainer does not waste time assuming
`lib/SimHub.Plugins.dll` alone is sufficient.

To list every type in a reference assembly (useful for finding the exact full name to pass to
`-t`):

```bash
ilspycmd -l c lib/SimHub.Plugins.dll
```

## Why this matters for the open-source split

`Private\QAdvanceFeedback\` and `Private\QAdvanceFeedback.Tests\` hold this project's OWN code
derived from reading these files - reproducing SimHub's arithmetic, not copying its source. Both
of those folders are withheld from the public repository for the same underlying reason this
folder is: none of it should appear on GitHub. If you have supplied your own Layer 2/3
implementation (see `Private\README.md`'s "What a third party must implement"
section) and want to verify it against SimHub's own behaviour, regenerating the files in this
folder locally (as above) is the way to check your port without ever committing SimHub's source,
or your own derived reproduction of it, back into version control.
