# Mock host (`tools\mock-host`)

A stand-alone WPF app that runs the real `QAdvanceFeedback.dll` the way SimHub does, with no SimHub install.
It is for trying settings, ShakeIt effect editors and learning behaviour at a desk.

It is part of `QAdvanceFeedback.sln`, listed first, so it is Visual Studio's startup project. Building the
solution still produces the plugin's own DLL in `QAdvanceFeedback\bin`; the mock host only adds its exe in
its own `bin`. It needs the same `lib\*.dll` SimHub reference assemblies.

## Build and run

**In Visual Studio,** press F5: the mock host starts under the debugger, and breakpoints in the plugin's code
are hit. Visual Studio remembers the startup project per user (in `.vs\`). If it still starts another
project, right-click **MockHost** and choose **Set as Startup Project** once.

**From the command line** (repo root):

```
dotnet build tools\mock-host\MockHost.csproj -c Release
tools\mock-host\bin\Release\net48\MockHost.exe
```

The plugin is built through the project reference, so the mock always runs the current source.

## Where files go

SimHub's storage paths are relative (`PluginsData\Common\<file>`). The mock always runs from its exe folder,
whatever folder it was started from, so everything lands in `tools\mock-host\bin\<Debug|Release>\net48\PluginsData\Common\`,
which works like a SimHub install. The Debug and Release builds therefore keep separate data:

| File | Written by |
|---|---|
| `QAdvanceFeedback.config.json` | the plugin (settings page **Apply**, exit) |
| `QAdvanceFeedback.Parameters.json` | the plugin (learned values, exit) |
| `QAdvanceFeedback.SlipAngleSource.json` | the plugin (created from the compiled copy; the Slip Angle tab's **Apply**) |
| `MockHost.ShakeItProfile.json` | the mock (the added ShakeIt effects) |
| `MockHost.Simulation.json` | the mock (the Game Simulation tab's values) |
| `MockHost.Settings.json` | the mock (the Language tab's choice) |

Delete that folder to start from scratch. A UI fault is written to `MockHost.log` next to the exe.

## Tabs

**Plugin Settings** shows the plugin's own settings page (`GetWPFSettingsControl`), as SimHub shows it.

**ShakeIt Effects**
- **Add Effect** lists the plugin's eight ShakeIt effects: Wheel Lock, Wheel Slip, the four G-Force pads,
  Clutch Bite and Slip Angle. Each uses its localized name and description.
- The added effects are listed with their routing. **Remove Effect** deletes the selected one.
- The selected effect shows SimHub's header part (name, enabled, gain), then the plugin's own editor and,
  for Wheel Lock / Wheel Slip / Slip Angle, the graph ShakeIt shows in its response-curve slot.
- The list is saved to `MockHost.ShakeItProfile.json` in SimHub's container fields: `ContainerType`,
  `ContainerId`, `Description`, `IsEnabled`, `Gain` and `AggregationMode` (the routing). This is close to
  SimHub's profile, not a byte-for-byte copy. The effects' own settings are in the plugin's config.

**Language** switches the plugin's UI language (English or 简体中文). The plugin's settings page and the effect
editors are rebuilt at once (edits not yet applied on the settings page are lost); the mock host's own tabs stay in
English. Kept in `MockHost.Settings.json`.

**Game Simulation** is the fake game that `DataUpdate` sees, 60 times a second, on a background thread:
- **Game:** running / paused / in menu, the SimHub game id (an editable list that includes every game in the
  source JSON), car id, car model and track id.
- **Driving:** speed, RPM, max RPM, gear, throttle, brake and clutch.
- **Car dynamics:** lateral and longitudinal G, yaw rate and lateral velocity.
  - **Auto wave** swings lateral G, yaw rate and lateral velocity as a sine with the chosen period.
  - **Also swing the numeric extra properties** does the same to the extra properties, for example a
    per-wheel slip angle.
- **Wheels:** for each wheel, speed as a share of the car's speed (100 rolls freely, 0 is locked, over 100
  spins), SimHub's slip value, the grass/gravel flag and the kerb flag.
- **Reported by the simulated game:** which per-wheel readings SimHub's capabilities say the game supports
  (wheel speed, RPS, slip, ground speed, yaw rate). Without them the plugin's own lock/slip calculation sees
  "unknown".
- **Extra SimHub properties:** any property by its full name, with a number, true/false or text value.
  - **Add this game's slip-angle source properties** adds the current game's slip-angle, longitudinal and
    surface properties from `QAdvanceFeedback.SlipAngleSource.json`.
  - They also work for a Custom source.
- **Status line:** frames sent, the last `DataUpdate` time, the number of SimHub properties (the plugin's
  own included), and the plugin's health entries.
- **Source hint:** when the current game has a per-wheel slip-angle source but some of its properties are not in the
  extra properties, an orange line says so. Without them the plugin first says it is checking the source, then (after
  10 s of driving) that its properties are not reported - which is right, since the simulated game does not publish
  them.

## Capture mode

```
MockHost.exe --capture <folder> [seconds]
```

This shows each tab in turn - the plugin page's first tab and its Slip Angle tab, each added effect, Game
Simulation and Language. Each page runs for `seconds`
(default 2) and is saved as `mockhost-<n>-<name>.png`, then the app closes and saves as a normal exit does.
Use a longer time when the plugin needs to observe the simulation first: a game's own tyre angle is promoted
only after 5 s of moving, cornering data.

## How it works without SimHub

- **PluginManager.** Its constructor needs `WoteverCommon.dll`, which only a SimHub install has. The mock
  creates it without running the constructor and fills in its collection fields. It also sets the static
  `PluginManager.Instance`, which is how the plugin's `AttachDelegate` properties register.
  `GetCommonStoragePath`, `GetPropertyValue` and the property table are SimHub's own code.
- **Game manager.** `MockGameManager` implements SimHub's `IGameManager`. Only
  `GetFeedbackCapabilities` (from the Game Simulation tab) and the game name do anything.
- **Frames.** `GameData`, a `StatusDataBase` subclass and `FeedbackData` are filled through their internal
  setters each frame. The previous frame's data is passed as `OldData`.
- **ShakeIt containers.** SimHub's `EffectsContainerBase` constructor needs `WoteverLocalization.dll`. A
  container is created without its constructor, with only its routing set, the same way
  `tools\screenshot-harness` does it. Its own `EditControl` and its view model are SimHub's and the plugin's
  real code.
- **The ShakeIt profile check.** The last step of the plugin's `Init` creates the ShakeIt profile inspector.
  Its readers name SimHub's ShakeIt plugin types, which need `GongSolutions.WPF.DragDrop.dll`, so `Init`
  throws there, after everything else is set up. The mock finishes that step with an inspector that has no
  readers, and clears the Init failure that caused.

## Limits

- No ShakeIt output: no audio or motor device, and no SimHub output, frequency or channel-mapping settings.
- No SimHub ShakeIt profile, so the plugin's "effect is set up in a ShakeIt profile" check always reads "not
  found".
- SimHub's expression engine (NCalc / JavaScript), its property picker and Viper's scripts live in SimHub
  assemblies the mock does not have. Plain property names in source fields work; expect those three not to
  (not tested).
- SimHub's own computed properties (`DataCorePlugin.GameData.*`) are not published. Add the ones you need
  as extra properties.
