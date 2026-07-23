# Thunderstore publishing

## Permission requirement

The original RustyMods repository does not currently include a license file. Before publishing this fork or its compiled DLL publicly, obtain explicit written redistribution permission from RustyMods. Keep that permission with the release records.

Do not publish under the original RustyMods namespace or imply that RustyMods maintains the AWL fork.

## Package build

```powershell
Set-Location -LiteralPath C:\path\to\Discordbot-AWL
.\scripts\Build-Release.ps1
.\scripts\Test-ThunderstorePackage.ps1
```

The generated zip is `Thunderstore\DiscordBot_v1.4.0.zip` for this release. Its root contains:

- `manifest.json`
- `README.md`
- `icon.png` at exactly 256 by 256 pixels
- `CHANGELOG.md`
- `NOTICE.md`
- `DiscordBot.dll`

## Upload through Thunderstore

1. Sign in to Thunderstore with the account that owns the intended team namespace.
2. Open the Valheim community.
3. Choose **Upload package**.
4. Select the generated zip.
5. Select the AWL team namespace, not RustyMods.
6. Select the appropriate Valheim categories.
7. Review the rendered README, dependencies, package name, version, and attribution.
8. Publish only after the permission requirement above is satisfied.

For later releases, increment `DiscordBotPlugin.ModVersion`, update `Thunderstore/CHANGELOG.md`, rebuild, validate, then upload the newly versioned zip. Thunderstore versions are immutable, so a broken upload requires another version number.
