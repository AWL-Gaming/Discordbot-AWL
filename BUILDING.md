# Building DiscordBot AWL

## Requirements

- Windows 10 or newer.
- Visual Studio 2022 or newer Build Tools with the .NET Framework 4.8 targeting pack and MSBuild.
- .NET SDK 8 or newer.
- A current local Valheim installation.
- BepInEx installed either in the Valheim directory or in an r2modman/gale profile.

The build script does not modify the Valheim installation or any mod profile. It creates stripped, publicized compile-time references under `build/publicized_assemblies`.

## Build and package

From an ordinary PowerShell window:

```powershell
Set-Location -LiteralPath C:\path\to\Discordbot-AWL
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Build-Release.ps1
```

The script auto-detects common Steam, r2modman, and gale paths. Override paths when needed:

```powershell
.\scripts\Build-Release.ps1 `
  -GamePath 'D:\SteamLibrary\steamapps\common\Valheim' `
  -BepInExPath "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\My Profile\BepInEx"
```

Expected outputs:

- `bin\Release\DiscordBot.dll`
- `Thunderstore\DiscordBot_v<version>.zip`

## Verification

```powershell
.\scripts\Build-Release.ps1
.\scripts\Test-ThunderstorePackage.ps1
```

The build must complete with zero compiler errors. The inherited GIF encoder currently emits nullable-analysis warnings, but those warnings do not fail the build.

## Local test deployment

Do not replace a DLL while Valheim is running. Back up the installed DLL first, then copy the release DLL into the profile's plugin directory:

```powershell
$profile = "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\My Profile\BepInEx\plugins\RustyMods-DiscordBot"
Copy-Item -LiteralPath "$profile\DiscordBot.dll" -Destination "$profile\DiscordBot.dll.bak" -Force
Copy-Item -LiteralPath '.\bin\Release\DiscordBot.dll' -Destination "$profile\DiscordBot.dll" -Force
```

Start Valheim and verify `BepInEx\LogOutput.log` contains `Loading [DiscordBot 1.4.0]` and no `DiscordBot` exceptions.
