# Thunderstore publishing

## Current publication status

DiscordBot_AWL is published under the **AWLGaming** Thunderstore namespace:

https://thunderstore.io/c/valheim/p/AWLGaming/DiscordBot_AWL/

It is an unofficial community-maintained fork of RustyMods' DiscordBot. Do not publish it under the RustyMods namespace or imply that RustyMods maintains, endorses, or supports the AWL fork.

The upstream repository did not contain a license file or explicit redistribution terms when this fork was created and first published. Keep `NOTICE.md` in every public source and binary release so the original authorship, fork status, and licensing ambiguity remain transparent. Preserve any later written permission or upstream license information with the release records.

## Package build

```powershell
Set-Location -LiteralPath C:\path\to\Discordbot-AWL
.\scripts\Build-Release.ps1
.\scripts\Test-ThunderstorePackage.ps1
```

The generated ZIP contains:

- `manifest.json`
- `README.md`
- `icon.png` at exactly 256 by 256 pixels
- `CHANGELOG.md`
- `NOTICE.md`
- `DiscordBot.dll`

## Uploading an update

Thunderstore package versions are immutable. Changes to the icon, README, NOTICE, DLL, or other package files require a new version.

1. Increment `DiscordBotPlugin.ModVersion` and `Thunderstore/manifest.json` using semantic versioning.
2. Update `Thunderstore/CHANGELOG.md`.
3. Rebuild with `scripts/Build-Release.ps1`.
4. Validate with `scripts/Test-ThunderstorePackage.ps1`.
5. Sign in to Thunderstore with an account that belongs to the AWLGaming team.
6. Open the Valheim community and choose **Upload package**.
7. Select the newly generated ZIP.
8. Upload it under the same **AWLGaming** team and keep the package name `DiscordBot_AWL` unchanged.
9. Review the rendered README, dependencies, package name, version, icon, and attribution before publishing.

Version `1.4.1` is a security release. It removes webhook URL synchronization to clients, adds the server-side webhook broker, filters the retired Gemini 2.5 Flash model, and includes the branded icon and updated documentation.