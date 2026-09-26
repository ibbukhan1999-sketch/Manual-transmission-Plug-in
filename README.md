# Manual Transmission for ETS2LA

Manual Transmission changes gears for you in Euro Truck Simulator 2 while you drive. It reads the truck's live RPM and speed, uses the engine profile you choose in ETS2LA, and keeps working whether cruise control is on or off. You can still use your own accelerator.

## Installation

1. In ETS2, set the transmission type to **Sequential**. Close ETS2 and ETS2LA before copying the plugin.
2. Download **ManualTransmission.dll** from this repository's **Releases** page. Copy it into `%LOCALAPPDATA%\ETS2LA\current\Plugins\`.
3. Open the ETS2LA desktop app and enable **ManualTransmission**. The plugin installs its required shared library in the background if it is missing or outdated. If ETS2LA asks you to restart, close and reopen it once before driving.
4. Open the plugin's **Adjustments** page, switch on **Automatic sequential shifting**, then choose your truck brand and engine. Test shifting at low speed in a safe place.

Use a release made for your ETS2LA version (the plugin declares support for ETS2LA 2026.9.5026 or newer). If you use other Godspeed plugins, keep their release files together so they use the same shared library.

## How to use

- Leave **Automatic sequential shifting** on for automatic gear changes. Turn it off when you want to shift yourself.
- Match the **brand** and **engine specification** to the truck you are driving. The engine list is sorted by horsepower. Changing the brand refreshes the list.
- The small in-game panel shows the current gear and simple RPM and throttle bars. Turn on **Detailed Diagnostic Mode** in Adjustments to see the full telemetry view and its **Configuration** tab.
- The plugin continues watching speed and your accelerator after cruise control is switched off. If something seems wrong, stop safely and check that ETS2 is in Sequential mode and the plugin reports a connected controller and telemetry feed.

## Configuration Mode: estimated RPM values in red

Some engine profiles lack an exact RPM limit or part of their useful RPM range. The engine list still shows a **playable estimated number** for each missing field—for example, `2000 RPM`, not `0 RPM`. Only that estimated number is red; the engine name and known values stay their normal color. Select one to unlock the desktop **Configuration Mode** switch, which now matches the other settings switches. The desktop page explains what is estimated and how to prepare. You can drive immediately without running a test.

To start a test, turn on **Detailed Diagnostic Mode**, open the in-game overlay's **Configuration** tab, and use its **Run** button. A red warning triangle marks a missing measurement; a green dot shows an active test or one that is complete. The Run button is bright when the test is available and fades when it cannot be run or has finished. Detailed safety instructions and explanations stay in the desktop app. Only one test can run at a time.

### Stationary Rev Test

1. Stop the truck in a safe place, select **Neutral**, and apply the **parking brake**.
2. In the in-game **Configuration** tab, press **Run** beside **RPM limit**.
3. During the 10-second countdown, press and hold the physical accelerator fully. The plugin records the highest RPM during the final five seconds.

### Rolling Dyno Pull

1. Attach a trailer carrying at least **15 tonnes of cargo** and find a flat, clear road.
2. Select a gear with a true **1:1 direct-drive** ratio and release the clutch. This may be **11th gear**, but check your truck's gearbox—11th is not 1:1 in every truck. The plugin will refuse the test if the selected gear is not close enough to 1:1.
3. Begin a gentle, low-RPM roll, then press **Run** beside **RPM range** in the in-game Configuration tab and hold full physical throttle through its 10-second countdown. Do not change gears during the test. The plugin estimates where useful power begins and falls off.

When a test succeeds, its warning triangle changes to a green dot and its Run button locks for the rest of that ETS2LA session. Measured values are kept **for the current session only**; they are not permanently written into the engine catalog. Tests also require fresh game telemetry, and the rolling test checks that the road is nearly level.

## Shared library and releases

`ManualTransmission.dll` contains a copy of the matching `Godspeed.Shared.dll`. On first enable, it checks ETS2LA's `Libraries` folder. If installation or an update is needed, it places the shared library there and asks for an ETS2LA restart before the plugin starts. This library lets Godspeed plugins share safety and driving-state information. You do **not** need to copy it by hand when installing a complete release plugin DLL.

This source repository does not commit compiled DLLs or the ETS2LA host. A maintainer must build, test, and upload release files.

## Troubleshooting and logs

Session logs are written to `%USERPROFILE%\Documents\GitHub\GS AI\.session_logs` (or the Windows Documents folder's redirected location). Paste the path into File Explorer when collecting a `.log` file for a bug report. If a new shared-library version was installed, restart ETS2LA before testing again.

## Build from source

For developers: use Windows x64, the .NET 10 SDK, a compatible ETS2LA source checkout, and a matching `Godspeed.Shared.dll` from a Godspeed release or local build. The project uses that DLL as a build reference and embeds it in the finished plugin; it does not commit a binary dependency.

```powershell
dotnet build .\ManualTransmission.csproj -c Release -p:Platform=x64 -p:ETS2LASourceRoot="C:\path\to\ETS2LA" -p:GodspeedSharedPath="C:\path\to\Godspeed.Shared.dll"
```

A normal successful Release build copies `ManualTransmission.dll` and its debug symbols to the local ETS2LA Plugins folder. Add `-p:GodspeedDeploymentInProgress=true` when you only want to compile without installing. The standalone `CoreTransmissionEngine.cs` contains framework-independent shifting math for other simulators; the ETS2LA plugin does not yet call that extracted core.

## Contributing and license

Issues and human-authored contributions are welcome. Describe the truck, engine, gearbox, and steps to reproduce; include relevant logs, but not personal data. Do not submit agent-generated changes as a pull request to the upstream ETS2LA repository—its repository rules reject them.

This repository's original files are offered under the [MIT License](LICENSE). ETS2LA, TruckersMP, and other dependencies have their own terms.
