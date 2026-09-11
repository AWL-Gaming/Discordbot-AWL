# Building DiscordBot AWL

## Requirements

- Windows 10 or newer.
- Visual Studio 2022 or newer Build Tools with the .NET Framework 4.8 targeting pack and MSBuild.
- .NET SDK 8 or newer.
- A current local Valheim installation.
- BepInEx installed either in the Valheim directory or in an r2modman/gale profile.

The build script does not modify the Valheim installation or any mod profile. It creates stripped, publicized compile-time references under `build/publicized_assemblies`.

## Build and package

Open PowerShell in the repository root and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Build-Release.ps1
```

The script auto-detects Valheim from Steam metadata and searches installed r2modman and gale profiles for BepInEx. If auto-detection is not suitable, pass the paths explicitly:

```powershell
.\scripts\Build-Release.ps1 `
  -GamePath '<Valheim installation directory>' `
  -BepInExPath '<BepInEx directory>'
```

Expected outputs:

- `bin\Release\DiscordBot.dll`
- `Thunderstore\DiscordBot_v<version>.zip`

To build the Hexium package with FAQ entries:

```powershell
.\scripts\Build-HexiumPackage.ps1
```

This produces `build\hexium\DiscordBot_v<version>.zip`.

## Verification

```powershell
.\scripts\Build-Release.ps1
.\scripts\Test-ThunderstorePackage.ps1
```

The build must complete with zero compiler errors. The inherited GIF encoder currently emits nullable-analysis warnings, but those warnings do not fail the build.

## Local test deployment

Do not replace a DLL while Valheim is running. Back up the installed DLL first, then copy the release DLL into the profile's plugin directory. Replace `<BepInEx directory>` with the BepInEx directory used by the test profile:

```powershell
$pluginDirectory = Join-Path '<BepInEx directory>' 'plugins\RustyMods-DiscordBot'
Copy-Item -LiteralPath "$pluginDirectory\DiscordBot.dll" -Destination "$pluginDirectory\DiscordBot.dll.bak" -Force
Copy-Item -LiteralPath '.\bin\Release\DiscordBot.dll' -Destination "$pluginDirectory\DiscordBot.dll" -Force
```

Start Valheim and verify `BepInEx\LogOutput.log` contains a `Loading [DiscordBot <version>]` entry for the version being tested and no `DiscordBot` exceptions.
