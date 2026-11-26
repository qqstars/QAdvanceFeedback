# Auto-update (1.3.0)

Owner request, 2026-11-25. The design is modelled on the AZOM plugin's updater.

## Check

- **When it runs:** at plugin start, and again from the General tab's **Check For Update** button. Each check runs on
  a new background thread at below-normal priority. Nothing waits for it, and one check never runs twice at once.
- **Steps:**
  1. Read `https://api.github.com/repos/qqstars/QAdvanceFeedback/releases/latest`.
  2. Take the release's package (see Releases below): a zipped package first, otherwise the bare `QAdvanceFeedback.dll`.
     Only https URLs on GitHub's hosts are used.
  3. Download it, delete `%TEMP%\QAdvanceFeedbackLatest`, and create it again. A zip is unpacked into it (an entry that
     would land outside the folder is refused); a bare DLL is put in as `QAdvanceFeedback.dll`. The asset's name is kept
     in `_package.txt` there, for the notice.
  4. The package may hold other files. The `QAdvanceFeedback.dll` found at any depth is the one compared.
- **Comparison:** the DLL's file version is compared with the running DLL's. Any difference, larger or smaller, is a
  new version. `1.3.0` and `1.3.0.0` are the same version.
- **Request headers:** a generic User-Agent, `QAdvanceFeedback-Updater/<version>`. GitHub requires one, and no user or
  machine detail is sent.

## Display (MVVM, one-way)

- **Shared status:** one `UpdateStatusViewModel.Current`. The check thread only calls `Set`, and every view binds to it
  one-way.
- **Green notice** (`UpdateNoticeBar`): *New version x.x.x.x detected (QAdvanceFeedback_x.x.x.zip).*, with a smaller
  second line saying to go to the General tab to update. The version is the downloaded DLL's, and the name in brackets is
  the release file it came from (left out when unknown).
  - It appears on top of the Wheel Lock, Wheel Slip, Slip Angle and G-Force tabs, and of every ShakeIt panel: Wheel
    Lock / Slip, G-Force and Slip Angle. The Clutch view carries it too, so it shows on both the Clutch tab and the
    Clutch panel.
  - It does not appear on the General tab.
- **General tab, VERSION UPDATE section:**
  - The version line *QAdvanceFeedback version: x.x.x.x*, with *(latest)* when the downloaded DLL is this version.
    Nothing is added when nothing has been downloaded.
  - A green *(new version: x.x.x.x)* when the downloaded DLL is a different version.
  - **Check For Update**, and **Update**, which is shown only while a different version is downloaded.
  - The last check's problem, if any.

## Update

1. The downloaded DLL is copied next to the running one as `QAdvanceFeedback.dll.new`.
2. The running DLL is renamed to `QAdvanceFeedback.dll.old`. Windows allows renaming a loaded DLL, not overwriting it.
3. The new DLL takes the name `QAdvanceFeedback.dll`.
4. The config and the learned data are saved now, then SimHub is asked to restart itself:
   `PluginManager.RequestApplicationExit(restart: true)`, the same call AZOM makes. It raises SimHub's
   `ApplicationExitRequested`, and the SimHub application runs its normal exit (every plugin's `End`) and starts again.
   The first version closed the WPF application itself and relied on a detached helper process to start SimHub again.
   SimHub did not come back (owner-reported); the helper may die with SimHub, wait for a process that never exits, or
   hit SimHub's single-instance check. So that approach was dropped.

If a step fails, the running DLL keeps its name and the page shows the error.

The staged and old files deliberately do not end in `.dll`, so a leftover can never be loaded as a second plugin. The
next start deletes both (`PluginUpdater.DeleteLeftovers`).

## Releases

A release carries the plugin as one of these assets (owner, 2026-11-25), case ignored:
- `Release_x.x.x.zip` or `Release_x.x.x.x.zip`;
- `QAdvanceFeedback_x.x.x.zip` or `QAdvanceFeedback_x.x.x.x.zip`;
- `QAdvanceFeedback.dll` itself.

The `_` may be a space. A dot is accepted there too, because GitHub renames a space in an uploaded asset's name to a dot
(`Release 1.3.0.zip` is published as `Release.1.3.0.zip`). A zip is used before the bare DLL; within each, the
release's own order decides. The existing `release-1.2.1` (asset `QAdvanceFeedback_1.2.1.zip`) is therefore picked up.

## Code and tests

- Code: `PluginUpdater.cs` (check, unpack, swap), `SimHubRestarter.cs` (restart), `Ui\ViewModels\UpdateStatusViewModel.cs`,
  `Ui\Views\UpdateNoticeBar.xaml`, and the `IUpdateHost` host capability.
- Tests: `AutoUpdateTests`.
