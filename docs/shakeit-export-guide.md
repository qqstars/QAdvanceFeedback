# Exposing SimHub's own ShakeIt wheel lock/slip as properties

This is a corrected, decompilation-verified walkthrough. An earlier note from the owner said
"ShakeIt publishes nothing" - that was **wrong**, and has been corrected here. ShakeIt DOES have an
opt-in, per-effect "export output value as a property" feature. Everything below was confirmed by
decompiling `SimHub.Plugins.dll` with `ilspycmd` (not guessed) - see "How this was confirmed" at the
end for the exact classes/members.

> **NOTE FOR WHOEVER MERGES THIS INTO THE README**: this file is a self-contained section that
> should be folded into `README.md` (and its `README.zh-Hans.md` counterpart) under a heading such
> as "Alternative: using SimHub's own ShakeIt effect instead of `Raw.*`". It was written to
> `docs\shakeit-export-guide.md` rather than edited directly into the README because another task is
> concurrently packaging the README/solution/screenshots for this repository.

## 1. Enable the right ShakeIt plugin

SimHub ships ShakeIt as **two separate plugins** (SimHub Settings -> Additional Plugins), and which
one you enable changes the property PREFIX you will get later (step 6):

- **`ShakeIt Motors`** (internal type `ShakeITMotorsV3Plugin`) - "Provide ShakeIt (tactile feedback)
  features for motors and fans".
- **`ShakeIt Bass Shakers`** (internal type `ShakeITBSV3Plugin`) - "Provide ShakeIt (tactile
  feedback) features for bass shakers".

Enable whichever matches your actual hardware (or either one, if you only want the exported
properties and do not care which physical output type is nominally selected - see step 5).

## 2. Create a profile and add the effect

In the ShakeIt tab, create/open a profile for your car or game, then add an effect. The two
relevant effects are both in the **"Slip and lock"** group:

- **`Wheels lock`** - SimHub's own description: "Provide localized wheel slip and lock feedback
  under braking".
- **`Wheels slip`** - SimHub's own description: "Provide localized wheel slip feedback".

(There is a third, simpler effect, **`Wheels spin and lock`**, described as "Provide simple non
localized wheel spin and lock feedback" - it produces a single combined value, not four per-wheel
values, so it is NOT what you want for per-wheel export; use `Wheels lock`/`Wheels slip` above.)

Both `Wheels lock` and `Wheels slip` are **always** structured as exactly four sub-effects - Front
left, Front right, Rear left, Rear right - confirmed from the decompiled container constructors:

```
FrontLeft  = new WheelSlipEffect("Front left",  FFBPlacement.FrontLeft);
FrontRight = new WheelSlipEffect("Front right", FFBPlacement.FrontRight);
RearLeft   = new WheelSlipEffect("Rear left",   FFBPlacement.RearLeft);
RearRight  = new WheelSlipEffect("Rear right",  FFBPlacement.RearRight);
```

There is **no** "mono / left-right / front-rear / 4 wheels" channel selector for these two effects
specifically - that four-way split is fixed, not optional. (A "mono / Left-Right / Front-Rear /
Corners" selector does exist elsewhere in ShakeIt - it is the **audio output channel mapping**
`ChannelsMode` enum, used when routing a Bass Shakers effect's sound to physical sound-card
channels: `Mono`, `Stereo` ("Left / Right"), `StereoFR` ("Front / Rear"), `FrontLR`/`RearLR`, `Quad`
(displayed as **"Corners"**), `Custom` ("Custom channel map (up to 8 channels)"). It governs
hardware routing, not which/how-many properties get exported, so it does not matter for this
guide.)

## 3. Turn on the legacy iRacing algorithm (optional, matches this plugin's own Layer 3)

Both effects have a checkbox whose underlying member is `UseLegacyIracingAlgorythm` (SimHub's own
spelling - not a typo introduced here). Its on-screen label, exactly as SimHub displays it:

> **"Use legacy IRacing algorythm"**

(SimHub's own spelling: "IRacing" with a capital R, "algorythm" misspelled - quoted verbatim.)

This makes the effect use the exact same decompiled algorithm
(`WheelSlipEffect.GetRpmSpeedSlipLegacy`) that this plugin's own Layer 3 (`LegacySlipAlgorithm`,
withheld under `Private\` - see `Private\README.md`) reproduces. You do not have to enable it - it
only matters if you want ShakeIt's own live number to match this plugin's `Raw.*` as closely as
possible; the export mechanism below works regardless of which algorithm branch is active.

## 4. Keep it from driving your actual hardware (optional)

If you only want the computed value exported as a property, and do NOT want this effect to also
drive a shaker/motor, tick:

> **"Disable output"** (underlying member `OutputBase.DisableOutput`)

Confirmed by decompilation that this is safe to combine with exporting: `DisableOutput` only
suppresses the physical output routing (`OutputBase.OnDisableOutputChanged` calls
`Container.ParentProfile.RefreshOutputChannels()`) - it is NOT consulted anywhere in ShakeIt's
export path (`ShakeITV3PluginBase.ExportProperties` only checks
`container.GetEffectiveIsEnabled()`, `output.ExportProperty`, and a non-empty `PropertyName` - never
`DisableOutput`). So: leave the effect **enabled** (do not disable/remove the whole effect, just
tick "Disable output" on its output), and it keeps computing and exporting while driving nothing.

If SimHub's UI does not expose an explicit "no output device" choice for your setup, the equally
valid alternative is to simply not assign any physical channel/device group to this effect at all -
either approach keeps it from actually vibrating anything.

## 5. Export the value as a property

On the same output/channel row, tick:

> **"Export output value as a property"**

(Resource key `ShakeItExportOutputToPropery_Label_ExportOutputValueAsAProperty` - note SimHub's own
typo, "Propery", in the internal resource KEY only; the text actually displayed on screen is
spelled correctly, "property".)

Then type a name into the **"Property name"** field that appears next to it (SimHub label:
**"Property name"**, same text used for both the field's label and its placeholder/watermark).

**Required convention, as of the "ShakeIt Plugin Output Properties" toggle** (see step 7): type
exactly `WheelLock.IRacing` as the property name on the `Wheels lock` effect, and `WheelSlip.IRacing`
on the `Wheels slip` effect. This plugin's own toggle checks for these EXACT names (see
`Core/ShakeIt/ShakeItPropertyNames.cs`) - only the "ShakeIt Motors" plugin variant is supported by
the toggle (not "ShakeIt Bass Shakers"). If you use a different property name, or Bass Shakers
instead of Motors, ShakeIt mode will not actually resolve (the settings UI's inline note will say
so), but you can still point a Layer 4 Source field at your own exported name manually in Manual
mode (step 7 below covers this either way).

## 6. The exact resulting property name (confirmed by decompilation)

Decompiling `ShakeITV3PluginBase.ExportProperties` shows exactly how the final name is built:

```csharp
// i = the effect's container (e.g. your "Wheels lock" effect), j = one of its 4 sub-effects
"Export." + i.Output.PropertyName + "." + j.Placement
```

That string is then passed to `PluginManager.AttachDelegate(name, pluginType, ...)`, and
`PluginManager.GetName(name, pluginType)` is hard-coded as:

```csharp
pluginType.Name + "." + name
```

`pluginType` here is the concrete ShakeIt plugin class's own runtime type - i.e. **which of the two
plugins from step 1 you enabled**:

| Plugin enabled (step 1) | `pluginType.Name` |
|---|---|
| `ShakeIt Motors` | `ShakeITMotorsV3Plugin` |
| `ShakeIt Bass Shakers` | `ShakeITBSV3Plugin` |

So, following the REQUIRED `WheelLock.IRacing`/`WheelSlip.IRacing` naming from step 5, on
**ShakeIt Motors**, the `Wheels lock` effect's four sub-effects export as exactly these four
properties (confirmed by decompiling both `ShakeITMotorsV3Plugin`'s `[PluginName(...)]` attribute
AND `GameReaderCommon.Enums.FFBPlacement`'s own enum member names - `FrontLeft`/`FrontRight`/
`RearLeft`/`RearRight`, no custom `ToString()` override):

```
ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft
ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight
ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft
ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight
```

and `Wheels slip` (property name `WheelSlip.IRacing`) exports:

```
ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft
ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontRight
ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearLeft
ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight
```

**This plugin's own "ShakeIt Plugin Output Properties" toggle (Wheel Lock/Wheel Slip tabs) checks
for EXACTLY these eight names** - it only supports the ShakeIt Motors variant (not Bass Shakers) and
only the `.IRacing`-suffixed property names above. If you enabled **ShakeIt Bass Shakers** instead,
or typed a different property name, ShakeIt mode will not actually resolve (SimHub is not reporting
the expected properties - the settings UI's inline note says so, but the toggle itself is always
selectable regardless) - but you can still point a Layer 4 Source field at whatever you actually
exported manually, in Manual mode (step 7 below).

**Range and per-wheel semantics (confirmed by decompilation)**: each of the four exported
properties is a single wheel's value, clamped to **0-100** -
`EffectsContainerBase`'s own per-frame update does
`effect.LastOutput = Clamp(computedValue, 0.0, 100.0)` immediately before this same `LastOutput` is
what gets exported - so this is genuinely four independent per-wheel numbers on this plugin's own
0-100 scale, not one combined value and not a different range needing rescaling.

## 7. Using an exported ShakeIt property as a Layer 4 source

### Automatic: the "ShakeIt Plugin Output Properties" toggle

This plugin's own Wheel Lock/Wheel Slip tabs show a toggle at the very TOP of the Sources section:
**ShakeIt Plugin Output Properties** (the shipped default, both channels) vs. **Manual** (the four
Source fields below). CORRECTED BEHAVIOUR (a previous version of this toggle hid itself whenever
SimHub was not yet reporting all four expected properties, which meant it was invisible on any
system that had not already followed steps 1-6 - exactly backwards, since the toggle is what a
driver needs in order to be told how to set ShakeIt up in the first place): **the toggle is now
ALWAYS visible and switchable**, regardless of whether SimHub is currently reporting the four
properties. If it is not (yet) reporting them, the tab shows an inline note saying so, together
with the concise setup steps above - selecting ShakeIt mode never leaves you with dead output
either way, since that channel automatically falls back to its own Raw property until the export
appears. Selecting ShakeIt mode switches all four Source fields to the confirmed ShakeIt property
names automatically and hides the per-wheel script-type controls (a ShakeIt export is always read
as a plain property, never a scripted expression). Selecting Manual instead shows a short note that
each wheel's Source field must itself supply a 0-100 value (0 = none, 100 = fully locked/spinning).
The per-channel "Reset to default" button under the four fields always restores whichever mode is
CURRENTLY selected (ShakeIt names, or this channel's own Raw names) - it never switches you to the
other mode.

### Manual: point a Source field at any property yourself

This plugin's own Wheel Lock/Wheel Slip tabs (Layer 4) also let each of the four wheels' "Source"
field point at ANY registered SimHub property, defaulting to this plugin's own
`QAdvanceFeedback.WheelLock.Raw.<Wheel>` / `QAdvanceFeedback.WheelSlip.Raw.<Wheel>`. If you used
ShakeIt Bass Shakers instead of Motors, or a different property name than the required
`WheelLock.IRacing`/`WheelSlip.IRacing` (so the automatic toggle above does not apply), you can
still manually point a Source field at, e.g., `ShakeITBSV3Plugin.Export.MyCustomName.FrontLeft` -
either type the name directly into the field, or use the picker button next to it to browse
SimHub's currently registered properties.

**`Raw.*` stays Manual mode's OWN default - deliberately, not as an oversight.** (The plugin's
GLOBAL shipped default, across a fresh install, is ShakeIt mode - see the toggle's own remarks
above; this paragraph is about what Manual mode itself defaults to once you switch to it.) A
driver's own free-text property name and choice of ShakeIt variant cannot be predicted in general
(only the specific `WheelLock.IRacing`/`WheelSlip.IRacing`-on-ShakeIt-Motors convention is
predictable enough for the automatic toggle above), so Manual mode's own default cannot safely
guess at either.

**The fallback is still automatic and silent once you DO point a Source field at a ShakeIt
property**: `WheelSourceResolver`/`ClampMath.CoerceOrFallback` reads the named property every
frame via `PluginManager.GetPropertyValue`, and whenever it is missing (not yet exported, ShakeIt
not enabled, a typo in the name, etc.) transparently falls back to this plugin's own `Raw.<Wheel>`
value for that same wheel that frame - a missing property is never read as 0, and switching a
Source field never leaves you with dead output while ShakeIt is unavailable.

## How this was confirmed (decompiled sources, `ilspycmd` against `SimHub.Plugins.dll`)

- `SimHub.Plugins.DataPlugins.ShakeItV3.ShakeITV3PluginBase.ExportProperties`/
  `CheckAndExportProperties` - the exact name-building expression and the `AttachDelegate` call.
- `SimHub.Plugins.PluginManager.GetName` - `pluginType.Name + "." + name`.
- `SimHub.Plugins.DataPlugins.ShakeItV3.ShakeITMotorsV3Plugin`/`ShakeITBSV3Plugin` - the
  `[PluginName(...)]` attributes ("ShakeIt Motors" / "ShakeIt Bass Shakers") and each concrete
  class's own `Type.Name`.
- `SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers.WheelsLockContainer`/`WheelsSlipContainer`
  - the `[ShakeItContainerMetadata(...)]` attributes ("Wheels lock"/"Wheels slip", group "Slip and
    lock"), the four `FrontLeft`/`FrontRight`/`RearLeft`/`RearRight` `WheelSlipEffect` fields, and
    the `UseLegacyIracingAlgorythm` property.
- `SimHub.Plugins.DataPlugins.ShakeItV3.Outputs.OutputBase` - `ExportProperty`, `PropertyName`,
  `DisableOutput` fields and their change-notification wiring.
- `SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers.EffectsContainerBase` - the `Output`
  property (type `OutputBase`), `GetEffectiveIsEnabled`, and the per-frame update that computes and
  clamps `LastOutput` to `[0, 100]`.
- Embedded XAML/resource strings (extracted with `strings`) -
  `ShakeItExportOutputToPropery_Label_ExportOutputValueAsAProperty` = "Export output value as a
  property", `ShakeItExportOutputToPropery_Label_DisableOutput` = "Disable output",
  `ShakeItExportOutputToPropery_Label_PropertyName`/`_Watermark_PropertyName` = "Property name",
  `ShakeItEffectWheelsSlip_Label_LegacyIracingAlgorythm` = "Use legacy IRacing algorythm",
  `ShakeItSettings_Mono`/`_Stereo`/`_StereoFR`/`_4Channels` = "Mono"/"Left / Right"/"Front /
  Rear"/"Corners" (the `ChannelsMode` audio-routing selector, see step 2's note).

None of this was guessed or taken from memory - every property name, label, and behaviour above is
traceable to a specific decompiled member or embedded resource string.
