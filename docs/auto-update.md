# Auto-update (1.3.0)

Owner request, 2026-11-25. The design is modelled on the AZOM plugin's updater.

## Check

- **When it runs:** at plugin start, and again from the General tab's **Check For Update** button. Each check runs on
  a new background thread at below-normal priority. Nothing waits for it, and one check never runs twice at once.
- **Steps:**
  1. Read `https://api.github.com/repos/qqstars/QAdvanceFeedback/releases/latest`.
  2. Take the release's asset named `Release_<version>.zip`. Only https URLs on GitHub's hosts are used.
  3. Download it, delete `%TEMP%\QAdvanceFeedbackLatest`, create it again, and unpack the package into it. An entry
     that would land outside the folder is refused.
  4. The package may hold other files. The `QAdvanceFeedback.dll` found at any depth is the one compared.
- **Comparison:** the DLL's file version is compared with the running DLL's. Any difference, larger or smaller, is a
  new version. `1.3.0` and `1.3.0.0` are the same version.
- **Request headers:** a generic User-Agent, `QAdvanceFeedback-Updater/<version>`. GitHub requires one, and no user or
  machine detail is sent.

## Display (MVVM, one-way)

- **Shared status:** one `UpdateStatusViewModel.Current`. The check thread only calls `Set`, and every view binds to it
  one-way.
- **Green notice** (`UpdateNoticeBar`): *New version x.x.x.x detected.*, with a smaller second line saying to go to the
  General tab to update.
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
4. SimHub restarts. A hidden helper waits for this SimHub process to exit and starts the same executable again; SimHub
   is then closed normally, so every plugin's `End` still runs and saves.

If a step fails, the running DLL keeps its name and the page shows the error.

The staged and old files deliberately do not end in `.dll`, so a leftover can never be loaded as a second plugin. The
next start deletes both (`PluginUpdater.DeleteLeftovers`).

## Releases

A release must carry the package as an asset named `Release_<version>.zip`, holding `QAdvanceFeedback.dll`. The
release `release-1.2.1` (asset `QAdvanceFeedback_1.2.1.zip`) predates this naming, so it is not picked up.

## Code and tests

- Code: `PluginUpdater.cs` (check, unpack, swap), `SimHubRestarter.cs` (restart), `Ui\ViewModels\UpdateStatusViewModel.cs`,
  `Ui\Views\UpdateNoticeBar.xaml`, and the `IUpdateHost` host capability.
- Tests: `AutoUpdateTests`.
