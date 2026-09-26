
Manual Transmission for ETS2LA

Manual Transmission helps your truck choose gears automatically while keeping the game's manual-transmission controls. It includes engine profiles, a compact in-game status display, and an optional Configuration Mode for engines whose RPM information is incomplete.

Install from a release

Download a release package and copy its two separate DLLs into your ETS2LA installation:

Release file
Destination

Libraries/Godspeed.Shared.dll
%LOCALAPPDATA%\ETS2LA\current\Libraries\Godspeed.Shared.dll
Plugins/ManualTransmission.dll
%LOCALAPPDATA%\ETS2LA\current\Plugins\ManualTransmission.dll

Close ETS2LA before replacing these files. Start ETS2LA again after installing or updating the shared library, then enable Manual Transmission in the desktop app. The plugin declares godspeed.shared as a required library, so ETS2LA loads that library before the plugin. A plugin DLL alone is no longer a complete installation: it does not contain or silently install Godspeed.Shared.dll.

If you also use other Godspeed plugins, install the same matching Godspeed.Shared.dll once in Libraries; do not place separate copies beside each plugin. Do not copy ETS2LA's own DLLs from this repository into your installation.

How to use

1. In ETS2LA's desktop app, enable Manual Transmission and open its Adjustments page.
2. Turn on Automatic sequential shifting if you want the plugin to choose gears. It can still monitor the truck when cruise control is off; normal game telemetry and your physical accelerator remain relevant.
3. Select your truck brand, then choose the matching engine by horsepower and torque. The engine list follows the selected brand and is sorted by horsepower.
4. Keep Detailed Diagnostic Mode off for the compact in-game display. Turn it on when you want the Live Telemetry, Powertrain, Diagnostics, and Configuration tabs.

The compact display shows the current gear, RPM and throttle bars, and a status light. It hides low-level controller-slot data. The detailed display is for troubleshooting, not required for everyday driving.

Configuration Mode

Some engine records omit one or more RPM values. In the engine picker, only the affected RPM numbers appear red; the rest of the engine description remains in its usual color. The number shown may be a playable temporary estimate rather than 0. Red means the underlying game data is missing, not that the truck cannot be driven.

Selecting such an engine unlocks Configuration Mode on the desktop Adjustments page. That page explains the warnings and safety prerequisites. Turn on Configuration Mode, enable Detailed Diagnostic Mode, and open the in-game Configuration tab to run a needed test. A warning triangle marks a pending value; the Run button is disabled when a test is unavailable or already completed for this session. Only one test can run at a time.

- Stationary Rev Test: Stop completely, shift to Neutral, apply the parking brake, and follow the on-screen throttle countdown. This measures the engine's RPM limit.
- Rolling Dyno Pull: Use a flat, safe stretch of road with a loaded trailer of at least 15 tons. Select a gear the plugin verifies as close to a 1:1 direct-drive ratio, then follow the on-screen countdown. The test cancels if the gear or safety conditions change. A particular gear number, including 11th, is not assumed to be 1:1 on every truck.

The truck remains drivable with temporary values even if you skip these tests. Measurements replace those values only for the current session; completed tests lock for the remainder of that session. Run a test only in a safe, controlled setting, never in traffic.

Build from source

This repository follows the ETS2LA example-plugin layout: the normal plugin lives in Plugins/ManualTransmission, while its separately loaded LibraryPlugin lives in Libraries/Godspeed.Shared. Both projects target .NET 10 and x64. You also need a compatible, writable ETS2LA source checkout for the framework project references. Do not build into a checkout you intend to keep strictly read-only.

In PowerShell, from this repository root:


./BuildYourPlugins.ps1 -ETS2LASourceRoot 'C:\path\to\your\writable\ETS2LA-source'


That script builds the shared library first, then Manual Transmission. A successful Release build copies only Godspeed.Shared.dll/.pdb to Libraries and ManualTransmission.dll/.pdb to Plugins in the local ETS2LA installation. Close ETS2LA and restart it after the build before testing an updated shared library. Build outputs under bin/ and obj/ are not release packages.

Godspeed.Shared also contains source for the radar API used by other Godspeed plugins. The Git repository does not track the native PlayerRadar.dll binary. Before using the build script or deploying a locally built shared library, obtain the matching native build from the Godspeed release/build environment and place it at Libraries/Godspeed.Shared/Native/PlayerRadar.dll. The deployment target refuses to replace the full shared library without that native resource. Manual Transmission itself does not use radar, but a reduced shared library could affect other installed Godspeed plugins. For a suite-wide release, package one matching full shared-library build alongside the other plugins.

CoreTransmissionEngine.cs is a standalone, framework-agnostic version of the shifting math for developers adapting it to other simulators.
