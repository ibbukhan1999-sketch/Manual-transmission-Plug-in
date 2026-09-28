# Manual Transmission for ETS2LA

Manual Transmission helps your truck choose gears automatically while keeping the game's manual-transmission controls. It includes engine profiles, a compact in-game status display, and an optional Configuration Mode for engines whose RPM information is incomplete.

---

## At a glance

| Feature | What it does |
| --- | --- |
| Automatic sequential shifting | Chooses gears even after cruise control is switched off. |
| Engine profiles | Lets you select your truck brand and an engine sorted by horsepower. |
| Compact or detailed display | Shows everyday driving information or full diagnostic tabs. |
| Configuration Mode | Measures missing RPM values during optional, controlled tests. |

## Install from a release

> [!IMPORTANT]
> The older **v2.1.0** release predates this two-DLL layout. Use a release containing **both** matching DLLs below; do not mix files from different builds.

1. Close ETS2LA before replacing plugin files.
2. Download a release package containing **both separate DLLs**.
3. Copy each file into your ETS2LA installation:

   | Release file | Destination |
   | --- | --- |
   | `Godspeed.Shared.dll` | `%LOCALAPPDATA%\ETS2LA\current\Libraries\` |
   | `ManualTransmission.dll` | `%LOCALAPPDATA%\ETS2LA\current\Plugins\` |

4. Restart ETS2LA, then enable **Manual Transmission** in the desktop app.

The plugin declares `godspeed.shared` as a required library, so ETS2LA loads that library before the plugin. **A plugin DLL alone is not a complete installation**: it does not contain or silently install `Godspeed.Shared.dll`.

If you also use other Godspeed plugins, use a matching **full-suite** `Godspeed.Shared.dll` once in `Libraries`; do not replace it with the lighter Manual Transmission-only build. Do not place separate copies beside each plugin or copy ETS2LA's own DLLs from this repository into your installation.

## How to use

1. In ETS2, set the transmission to **Sequential**.
2. In ETS2LA's desktop app, enable Manual Transmission and open its **Adjustments** page.
3. Turn on **Automatic sequential shifting** if you want the plugin to choose gears. It can still monitor the truck when cruise control is off; normal game telemetry and your physical accelerator remain relevant.
4. Select your truck brand, then choose the matching engine by horsepower and torque. The engine list follows the selected brand and is sorted by horsepower.
5. Keep **Detailed Diagnostic Mode** off for the compact in-game display. Turn it on when you want the full diagnostic view.

| Display mode | What you see |
| --- | --- |
| **Compact** | Current gear, RPM and throttle bars, and a status light—without controller-slot clutter. |
| **Detailed Diagnostic** | Live Telemetry, Powertrain, Diagnostics, and Configuration tabs. |

The detailed display is for troubleshooting, not required for everyday driving.

## Configuration Mode

Some engine records omit one or more RPM values. In the engine picker, **only the affected RPM numbers** appear red; the rest of the engine description remains in its usual color. The number shown may be a playable temporary estimate rather than `0`. Red means the underlying game data is missing, not that the truck cannot be driven.

Selecting such an engine unlocks **Configuration Mode** on the desktop Adjustments page. That page explains the warnings and safety prerequisites. Turn on Configuration Mode, enable Detailed Diagnostic Mode, and open the in-game **Configuration** tab to run a needed test. A warning triangle marks a pending value; the Run button is disabled when a test is unavailable or already completed for this session. Only one test can run at a time.

| Test | Before pressing **Run** | What it measures |
| --- | --- | --- |
| **Stationary Rev Test** | Stop completely, shift to Neutral, and apply the parking brake. | Maximum engine RPM. |
| **Rolling Dyno Pull** | Attach a trailer carrying at least **15 tonnes**, find a flat, clear road, and select a verified gear close to a **1:1 direct-drive** ratio. | The useful RPM range. |

Follow the on-screen throttle countdown. Do not change gears during the rolling test. **11th gear is not automatically 1:1 on every truck.** The test cancels if the gear or safety conditions change.

The truck remains drivable with temporary values even if you skip these tests. Measurements replace those values only for the current session; completed tests lock for the remainder of that session. Run a test only in a safe, controlled setting, never in traffic.

## Build from source

This repository follows the [ETS2LA example-plugin layout](https://github.com/ETS2LA/example-plugin): the normal plugin lives in [`Plugins/ManualTransmission`](Plugins/ManualTransmission), while its separately loaded `LibraryPlugin` lives in [`Libraries/Godspeed.Shared`](Libraries/Godspeed.Shared). Both projects target .NET 10 and x64. You also need a compatible, **writable** ETS2LA source checkout for the framework project references. Do not build into a checkout you intend to keep strictly read-only.

In PowerShell, from this repository root:

```powershell
./BuildYourPlugins.ps1 -ETS2LASourceRoot 'C:\path\to\your\writable\ETS2LA-source'
```

That script builds the shared library first, then Manual Transmission. A successful Release build copies `ManualTransmission.dll`/`.pdb` to `Plugins` and installs the lighter `Godspeed.Shared.dll`/`.pdb` in `Libraries` **only if no shared library is already installed**. It will not overwrite a full-suite library used by other Godspeed plugins. Close ETS2LA and restart it after installing a shared library. Build outputs under `bin/` and `obj/` are not release packages.

On Linux, build the two projects with `dotnet` instead; the Windows deployment
step is skipped:

```bash
dotnet build Libraries/Godspeed.Shared/Godspeed.Shared.csproj -c Release -p:Platform=x64 -p:ETS2LASourceRoot=/path/to/ETS2LA-source
dotnet build Plugins/ManualTransmission/ManualTransmission.csproj -c Release -p:Platform=x64 -p:ETS2LASourceRoot=/path/to/ETS2LA-source
```

The plugin's visible version comes from its `PluginInformation.Version` field.
No custom build-number stamp or PowerShell target is needed to compile it.

### Does Manual Transmission need PlayerRadar?

**No—not to drive or shift gears.** Manual Transmission uses `Godspeed.Shared.dll` for driving-intent sharing and controller compatibility, but does not read radar targets or need `PlayerRadar.dll` at runtime.

This repository's lighter `Godspeed.Shared.dll` does **not** embed or install the native `PlayerRadar.dll`. Manual Transmission does not need it. The shared library keeps the radar-related managed types for compatibility, but other Godspeed plugins that need radar must use a matching full-suite library and their native radar hook.

For a suite-wide release, package one matching full shared-library build alongside the other plugins.

[`CoreTransmissionEngine.cs`](Plugins/ManualTransmission/CoreTransmissionEngine.cs) is a standalone, framework-agnostic version of the shifting math for developers adapting it to other simulators.

## Diagnostics and troubleshooting

| Symptom | First check |
| --- | --- |
| Plugin does not load | Confirm the matching `Godspeed.Shared.dll` is in the top-level `Libraries` folder, then restart ETS2LA. |
| No automatic gear changes | Confirm ETS2 is set to Sequential, automatic shifting is enabled, and your selected brand and engine match the truck. |
| Another Godspeed plugin cannot install its radar hook | Keep or install the matching full-suite shared library; the Manual Transmission-only build intentionally has no embedded `PlayerRadar.dll`. |

Look at ETS2LA's normal application logs when reporting a problem; this version uses `ETS2LA.Logging.Logger` rather than creating a separate Godspeed session-log file. Include the plugin version, truck and engine selection, what you were doing, and the relevant log excerpt.

## Contributing

Bug reports with reproducible steps and relevant ETS2LA logs are welcome. Keep proposed source changes focused, explain how you tested them, and do not include `bin/`, `obj/`, local settings, logs, or downloaded binaries in a source contribution. Follow the upstream ETS2LA project's contributor rules if you are considering a change to ETS2LA itself.

This repository currently contains a [GPL-3.0 license](LICENSE); ETS2LA and other dependencies have their own terms.
