# docs\reference\

This folder previously held decompiled SimHub source, kept locally as reference material during
early development of this plugin's Layer 2/3 implementation. It no longer does.

## Why the decompiled sources were removed

This project is published under MIT. Keeping decompiled third-party source in the repository -
even git-ignored, even never built - undermines the position that the shipped implementation is
this project's own independent expression of the relevant behaviour, not a transcription of
Wotever's (SimHub's author's) own code. The `*.decompiled.cs` files that used to live here
(`SimHub.WheelSlipEffect`, `Wotever.MathExtensions`, `SimHub.CalibrationDataProvider`, and several
`GameReaderCommon` types) have been deleted, along with the root `.gitignore` entry that used to
keep them out of version control.

See `docs\clean-room-restructure-report.md` for the pass that removed them, rewrote the
implementation that had been developed against them in this project's own terms, and dissolved the
`Private\` split that used to sit alongside this reference material.

## If you need to verify this plugin's behaviour against SimHub's own effect again

The regeneration steps this README used to document (`ilspycmd` against `lib/SimHub.Plugins.dll`,
per SimHub version) still work exactly as before - nothing about SimHub's own installer or
reference assemblies changed. Regenerate a decompiled file locally, compare it against
`QAdvanceFeedback\Core\RawCalculator\` by hand, and then delete the regenerated file again rather
than committing it - the point above applies to any future contributor's own local copy just as
much as it did to this one.

```bash
dotnet tool install -g ilspycmd   # if not already installed
bash tools/fetch-simhub-refs.sh 9.11.22   # if lib/ does not exist yet
ilspycmd -t "<FullTypeName>" lib/SimHub.Plugins.dll > /tmp/whatever-you-are-checking.cs
```

`ilspycmd -l c lib/SimHub.Plugins.dll` lists every type in a reference assembly, useful for finding
the full name to pass to `-t`.
