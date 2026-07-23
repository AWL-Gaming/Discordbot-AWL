# DiscordBot AWL for Valheim

AWL Gaming maintenance fork of the original [RustyMods DiscordBot](https://github.com/RustyMods/DiscordBot). It preserves the existing two-way Valheim and Discord integration while adding resilient AI model discovery, ordered provider/model failover, and safer client capture cleanup.

Original author: **RustyMods**. AWL Gaming maintains this fork and does not claim authorship of the upstream implementation. See [NOTICE.md](https://github.com/AWL-Gaming/Discordbot-AWL/blob/main/NOTICE.md).

## AWL 1.4.1 highlights

- Gemini model discovery through Google's model catalog, with configured models filtered against what the current API key can actually use.
- OpenRouter account-aware model discovery, optional free-only filtering, and ordered model failover.
- Provider failover across Gemini, OpenRouter, OpenAI, and DeepSeek.
- Server-side AI broker for automatic death/day quips without synchronizing API keys to clients.
- Server-side Discord webhook broker, so webhook URLs remain on the dedicated server and are never synchronized to clients.
- Retired Gemini model filtering, including automatic removal of `gemini-2.5-flash` from the request plan.
- Per-request timeout, attempt limits, credential-error short-circuiting, and client broker response timeout.
- Death GIF and screenshot capture now restore the HUD if recording is interrupted or the component is disabled.
- Reproducible release build and Thunderstore package validation scripts.

## Prerequisites

- **BepInEx** installed
- [JsonDotNet](https://thunderstore.io/c/valheim/p/ValheimModding/JsonDotNET) installed
- **Discord Webhooks** configured for your server
- **Discord Bot Token** (If you want your discord server to be able to send messages into your game)
- Required on `client` and `server`

## Installation

### Discord Setup

#### How to create Discord Bot

1. **Create a Discord Application**
    - Go to the [Discord Developer Portal](https://discord.com/developers/applications)
    - Click "New Application" (top-right)
    - Give your application a name (e.g. ValheimBot) and click "Create".
2. **Add a `Bot` to the Application**
    - In the left sidebar, click "Bot"
    - Click "Add Bot" ----> confirm by clicking "Yes, do it!"
    - You now have a bot user attached to your application.
    - You must enable `Message Content Intent`
3. **Copy the `Bot Token`**
    - On the Bot page, under the "Token" section, click "Reset Token" (or "Copy" if it is already shown).
    - Confirm, then copy the generated token.
    - Keep this token secret!, if it leaks, click "Reset Token" to generate a new one
4. **Invite the Bot to your Discord Server**
    - In the sidebar, click "O2Auth2" ----> "URL Generator".
    - Under **SCOPES**, check `bot`
    - Under **BOT PERMISSIONS**, check the permissions your bot will need
        - Send Messages
        - Read Message History
        - View Channels
    - Copy the generated URL at the bottom
    - Open that URL in your browser and invite the bot to your Discord server

### Creating Discord Webhooks

Webhooks allow the plugin to send messages to Discord channels without using the bot's identity.

1. **Create Webhook**
    - Go to "Integrations" tab
    - Click "Create Webhook"
    - Give it a name (e.g., "Valheim Chat")

2. **Copy Webhook URL**
    - Click "Copy Webhook URL"
    - Save this URL for your plugin configuration

### Getting Channel IDs

You'll need Discord Channel IDs for the bot to read messages:

1. **Enable Developer Mode**
    - In Discord, go to User Settings (gear icon)
    - Go to "Advanced" settings
    - Enable "Developer Mode"

2. **Copy Channel IDs**
    - Right-click on each channel you want to use
    - Select "Copy ID"
    - Save these IDs for your configuration

### Configure the Plugin

After first run, configuration files will be generated in `BepInEx/config/`. Edit the Discord Bot config file to set up your:

- Channel IDs
- Webhook URLs [SERVER ONLY]
- Bot Token [SERVER ONLY]
- AI API keys [SERVER ONLY when using the server broker]

## Configurations

Find DiscordBot plugin configurations in `BepinEx/config/RustyMods.DiscordBot.cfg`.

Configure webhook URLs, the Discord bot token, and server AI API keys only in the dedicated server config. Version 1.4.1 no longer synchronizes webhook URLs or API keys to clients. Connected clients submit bounded webhook requests to the server broker, which resolves the destination locally.

`Webhook URL` requires the complete Discord webhook URL. `Channel ID` requires only the numeric Discord channel ID. They are not interchangeable.

Config file edits are watched and reloaded automatically after saving. No console command is required. Replacing `DiscordBot.dll` is not hot-reloadable and requires restarting the affected server or client process.

Here's what your configuration might look like:

```ini
[1 - General]

## If on, the configuration is locked and can be changed by server admins only. [Synced with Server]
Lock Configuration = On

## Set interval between check for messages in discord, in seconds [Synced with Server]
Poll Interval = 5

## If on, errors will log to console as warnings [Synced with Server]
Log Errors = Off

[2 - Notifications]

## Set webhook to receive notifications, like server start, stop, save etc... [Server Only] [Not Synced with Server]
Webhook URL = <your-discord-webhook-url>

## If on, bot will send message when server is starting [Synced with Server]
Startup = Off

## If on, bot will send message when server is shutting down [Synced with Server]
Shutdown = Off

## If on, bot will send message when server is saving [Synced with Server]
Saving = Off

## If on, bot will send message when player dies [Synced with Server]
On Death = On

## If on, bot will send message when new player connects [Synced with Server]
New Connection = Off

[3 - Chat]

## Set discord webhook to display chat messages [Server Only] [Not Synced with Server]
Webhook URL = <your-discord-webhook-url>

## Set channel ID to monitor for messages [Synced with Server]
Channel ID = 9839768234583209

## If on, bot will send message when player shouts and monitor discord for messages [Synced with Server]
Enabled = On

[4 - Commands]

## Set discord webhook to display feedback messages from commands [Server Only] [Not Synced with Server]
Webhook URL = <your-discord-webhook-url>

## Set channel ID to monitor for input commands [Synced with Server]
Channel ID = 1106947857194165898

## List of discord admins, who can run commands [Synced with Server]
Discord Admin = .rusty,.warp

[5 - Setup]
## Add bot token here, server only
BOT TOKEN = 


```

## Usage

### In-Game to Discord

- Any message sent as a `shout` in the in-game chat will appear in your configured Discord chat channel
- Server events (like startup) will be posted to the notification channel
- Death events will be posted to the death feed channel

### Discord to In-Game

- Messages sent in the configured Discord chat channel will appear in the in-game chat
- Commands sent in the configured Discord command channel will be executed on the server

### Discord Commands

Send commands in your designated command channel:
- Commands should start with a command prefix `!`
- Example: `!listplayers` to list online players
- Example: `!save` to save the world

## Commands

**Legend:**
```
- <string:Parameter> - Text parameter
- <int:Parameter> - Number parameter
- <float:Parameter> - Decimal number parameter
- <parameter?> - Optional parameter
- [Admin Only] - Command restricted to registered Discord admins
```
### General Commands

### ❓ `help`
**Description:** List of all available commands  
**Usage:** `!help`

### ⚠️ `listadmins` **[Admin Only]**
**Description:** List of discord admins registered to plugin  
**Usage:** `!listadmins`

---

### Player Management

### 🐉 `listplayers` **[Admin Only]**
**Description:** List of active players with their positions  
**Usage:** `!listplayers`

### ❌ `kick` **[Admin Only]**
**Description:** Kicks player from server  
**Usage:** `!kick <string:PlayerName>`

### 🎁 `give` **[Admin Only]**
**Description:** Adds item directly into player inventory  
**Usage:** `!give <string:PlayerName> <string:ItemName> <int:Stack> <int?:Quality> <int?:Variant>`  
**Example:** `!give PlayerName IronSword 1 3 0`

### 🌹 `pos` **[Admin Only]**
**Description:** Get player's current position coordinates  
**Usage:** `!pos <string:PlayerName>`

### 🐅 `die` **[Admin Only]**
**Description:** Kills specified player  
**Usage:** `!die <string:PlayerName>`

---

### Teleportation Commands

### 🏃 `teleport` **[Admin Only]**
**Description:** Teleport player to location, bed, or another player  
**Usage:**
- `teleport <string:PlayerName> bed` - Teleport to bed
- `teleport <string:PlayerName> <string:OtherPlayerName>` - Teleport to another player
- `teleport <string:PlayerName> <float:x> <float:y> <float:z>` - Teleport to coordinates

### ⛳ `teleportall` **[Admin Only]**
**Description:** Teleports all players to specified coordinates  
**Usage:** `!teleportall <float:x> <float:y> <float:z>`

---

### Environment & Weather

### 🌪️ `listenv`
**Description:** List of available environments  
**Usage:** `!listenv`

### ✨ `env` **[Admin Only]**
**Description:** Force environment on all players  
**Usage:** `!env <string:EnvironmentName>`  
**Example:** `!env Twilight_Clear`

### ✨ `resetenv` **[Admin Only]**
**Description:** Reset environment on all players to default  
**Usage:** `!resetenv`

---

### Spawning & Creatures

### ❗ `spawn` **[Admin Only]**
**Description:** Spawns prefab at location  
**Usage:**
- `!spawn <string:PrefabName> <int:Level> <string:PlayerName>` - Spawn at player location
- `!spawn <string:PrefabName> <int:Level> <float:x> <float:y> <float:z>` - Spawn at coordinates  
  **Example:** `!spawn Troll 3 PlayerName`

---

### Events

### 🌙 `listevents` **[Admin Only]**
**Description:** List of available event names  
**Usage:** `!listevents`

### ⭐ `event` **[Admin Only]**
**Description:** Starts an event on a player  
**Usage:** `!event <string:EventName> <string:PlayerName>`  
**Example:** `!event Wolves PlayerName`

---

### Player Effects & Skills

### 🚀 `liststatus`
**Description:** List of available status effects  
**Usage:** `!liststatus`

### 🍕 `addstatus` **[Admin Only]**
**Description:** Add status effect to player  
**Usage:** `!addstatus <string:PlayerName> <string:StatusEffect> <float:Duration>`  
**Example:** `!addstatus PlayerName Rested 300`

### 🙏 `listskills`
**Description:** List of available skills  
**Usage:** `!listskills`

### 💪 `raiseskill` **[Admin Only]**
**Description:** Raises player's skill level  
**Usage:** `!raiseskill <string:PlayerName> <string:SkillType> <float:Amount>`  
**Example:** `!raiseskill PlayerName Swords 10`

---

### Server Management

### 💾 `save` **[Admin Only]**
**Description:** Save player profiles and world  
**Usage:** `!save`

### 😊 `message` **[Admin Only]**
**Description:** Broadcast message to all players (appears center screen)  
**Usage:** `!message <message text>`  
**Example:** `!message Server restart in 5 minutes!`

### 🦄 `setkey` **[Admin Only]**
**Description:** Set global key (affects world state)  
**Usage:** `!setkey <string:GlobalKeyName>`  
**Example:** `!setkey defeated_bonemass`

---

### Admin Management

### 🔑 `addadmin` **[Admin Only]**
**Description:** Adds discord username to admin list  
**Usage:** `!addadmin <string:Username>`

### 🔒 `removeadmin` **[Admin Only]**
**Description:** Remove discord username from admin list  
**Usage:** `!removeadmin <string:Username>`

---

### ChatAI

ChatAI supports Gemini, OpenRouter, OpenAI, and DeepSeek. Requests are attempted in configured provider order, with `Models Per Provider` limiting each provider before moving to the next one. Authentication, quota, and rate-limit failures immediately fail over to the next provider.

API keys remain local to the machine that owns them. With `Use Server Keys = On` and `Server AI Broker = On`, clients without local keys can request automatic death/day quips from the server. The server returns only the generated text and provider/model metadata. It never synchronizes bearer credentials to clients.

Manual player prompts through the server broker remain disabled by default. Enable `Allow Player AI Prompts` only when you intentionally want that behavior.

Relevant settings:

```ini
[8 - AI]
Provider = Gemini
Provider Order = Gemini, OpenRouter, ChatGPT, DeepSeek

Gemini =
Gemini Model = Flash3_6
Gemini Models = gemini-3.6-flash, gemini-3.5-flash-lite, gemini-3.5-flash, gemini-3.1-flash-lite, gemini-flash-latest, gemini-flash-lite-latest, gemini-3.1-pro-preview, gemini-3-flash-preview, gemini-pro-latest, gemini-2.5-flash-lite, gemini-2.5-pro
Gemini Auto Discover = On

OpenRouter =
OpenRouter Model = AutoFree
OpenRouter Models = openrouter/free
OpenRouter Auto Discover = On
OpenRouter Free Only = On

ChatGPT =
OpenAI Models = gpt-5.6-luna, gpt-5.6-terra, gpt-5.6-sol, gpt-5.6, gpt-5.5, gpt-5.4-mini, gpt-5.4-nano, gpt-4.1-mini

DeepSeek =
DeepSeek Models = deepseek-chat, deepseek-reasoner

Max Attempts = 12
Models Per Provider = 3
Request Timeout Seconds = 30
Remote Response Timeout Seconds = 120
Model Catalog Cache Minutes = 60
Max Output Tokens = 160
Max Prompt Characters = 4000
Remote Request Cooldown Seconds = 2

Use Server Keys = On
Server AI Broker = On
Allow Player AI Prompts = Off
Discord Prompt = Off
Death Quips = On
Day Quips = On
```

`Gemini Model` and `OpenRouter Model` are retained for backward compatibility. Known retired Gemini models are removed before the request plan is built, even when an older config still selects one as the legacy primary. Auto-discovery then appends currently usable models, and unavailable models fail over to the next model/provider.

OpenAI API usage is separate from ChatGPT subscriptions. Configure an OpenAI API key with API billing if the OpenAI provider is used.

### Jobs [beta]

Job allows to invoke discord commands on a set interval, beginning whenever the server starts.

Format:
```yml
command: !shout
interval: 1000.0
args: This is a reoccurring shout message from the server every 16 minutes
```

Example:
- List current active players every 30 minutes
```yml
command: !listplayers
interval: 1800
```
- Save world and active player profiles, every 1 hour
```yml
command: !save
interval: 3600
 ```

## Development and packaging

- [BUILDING.md](https://github.com/AWL-Gaming/Discordbot-AWL/blob/main/BUILDING.md) contains the reproducible Windows build and validation commands.
- [PUBLISHING.md](https://github.com/AWL-Gaming/Discordbot-AWL/blob/main/PUBLISHING.md) contains the Thunderstore package/upload procedure and the required upstream permission warning.
- [NOTICE.md](https://github.com/AWL-Gaming/Discordbot-AWL/blob/main/NOTICE.md) records original-author attribution and redistribution status.

### Notes

- **Admin Commands:** Commands marked with **[Admin Only]** can only be used by Discord users registered in the admin list
- **Player Names:** Use exact player names as they appear in-game (case sensitive)
- **Coordinates:** Use world coordinates (you can get these with the `pos` command)
- **Item Names:** Use exact prefab names from the game
- **Error Handling:** The bot will respond with error messages if commands fail or parameters are incorrect
