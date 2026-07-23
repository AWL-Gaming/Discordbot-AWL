using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DiscordBot.Jobs;
using HarmonyLib;
using JetBrains.Annotations;
using LocalizationManager;
using ServerSync;
using UnityEngine;

namespace DiscordBot;

public static class Extensions
{
    public static string ToURL(this Webhook type) => type switch
    {
        Webhook.Chat => DiscordBotPlugin.ChatWebhookURL,
        Webhook.Notifications => DiscordBotPlugin.NoticeWebhookURL,
        Webhook.Commands => DiscordBotPlugin.CommandWebhookURL,
        Webhook.DeathFeed => DiscordBotPlugin.DeathFeedWebhookURL,
        _ => DiscordBotPlugin.ChatWebhookURL
    };

    public static string ToID(this Channel type) => type switch
    {
        Channel.Chat => DiscordBotPlugin.ChatChannelID,
        Channel.Commands => DiscordBotPlugin.CommandChannelID,
        _ => DiscordBotPlugin.ChatChannelID,
    };

    public static T GetOrAddComponent<T>(this GameObject obj) where T : Component
    {
        return obj.TryGetComponent<T>(out var component) ? component : obj.AddComponent<T>();
    }
}
public enum Toggle { On = 1, Off = 0 }

public enum Webhook
{
    Notifications,
    Chat,
    Commands,
    DeathFeed
}

public enum WebhookRoute
{
    Default,
    WorldStart,
    WorldSave,
    WorldShutdown,
    Login,
    Logout,
    Event,
    NewDay,
    UseCommand,
    Boss,
    PublicApi
}

public enum Channel { Chat, Commands }
public enum ChatDisplay { Player, Bot }

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class DiscordBotPlugin : BaseUnityPlugin
{
    internal const string ModName = "DiscordBot";
    internal const string ModVersion = "1.4.1";
    internal const string Author = "RustyMods";
    private const string ModGUID = Author + "." + ModName;
    private const string ConfigFileName = ModGUID + ".cfg";
    private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    internal static string ConnectionError = "";
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource DiscordBotLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
    public static readonly Dir directory = new(Paths.ConfigPath, "DiscordBot");
    public static DiscordBotPlugin m_instance = null!;

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;
    #region extra webhooks
    private static ConfigEntry<string> m_startWorldHook = null!;
    private static ConfigEntry<string> m_saveWebhook = null!;
    private static ConfigEntry<string> m_shutdownWebhook = null!;
    private static ConfigEntry<string> m_loginWebhook = null!;
    private static ConfigEntry<string> m_logoutWebhook = null!;
    private static ConfigEntry<string> m_eventWebhook = null!;
    private static ConfigEntry<string> m_newDayWebhook = null!;
    private static ConfigEntry<string> m_useCommandWebhook = null!;
    private static ConfigEntry<string> m_bossWebhook = null!;
    #endregion
    #region notices
    private static ConfigEntry<string> m_notificationWebhookURL = null!;
    private static ConfigEntry<Toggle> m_serverStartNotice = null!;
    private static ConfigEntry<Toggle> m_serverStopNotice = null!;
    private static ConfigEntry<Toggle> m_serverSaveNotice = null!;
    private static ConfigEntry<Toggle> m_deathNotice = null!;
    private static ConfigEntry<Toggle> m_loginNotice = null!;
    private static ConfigEntry<Toggle> m_logoutNotice = null!;
    private static ConfigEntry<Toggle> m_eventNotice = null!;
    private static ConfigEntry<Toggle> m_newDayNotice = null!;
    private static ConfigEntry<Toggle> m_coordinateDetails = null!;
    private static ConfigEntry<Toggle> m_commandNotice = null!;
    private static ConfigEntry<Toggle> m_showServerDetails = null!;
    private static ConfigEntry<Toggle> m_showBossDeath = null!;
    #endregion
    #region chat
    private static ConfigEntry<string> m_chatWebhookURL = null!;
    private static ConfigEntry<string> m_chatChannelID = null!;
    private static ConfigEntry<Toggle> m_chatEnabled = null!;
    private static ConfigEntry<ChatDisplay> m_chatType = null!;
    #endregion
    #region commands
    private static ConfigEntry<string> m_commandWebhookURL = null!;
    private static ConfigEntry<string> m_commandChannelID = null!;
    #endregion
    #region death
    private static ConfigEntry<string> m_deathFeedURL = null!;
    #endregion
    #region general
    private static ConfigEntry<string> m_discordAdmins = null!;
    private static ConfigEntry<Toggle> m_logErrors = null!;
    private static ConfigEntry<string> m_botToken = null!;
    private static ConfigEntry<Toggle> m_showDetailedLogs = null!;
    #endregion
    #region screenshot
    private static ConfigEntry<Toggle> m_screenshotDeath = null!;
    private static ConfigEntry<float> m_screenshotDelay = null!;
    private static ConfigEntry<string> m_screenshotResolution = null!;
    private static ConfigEntry<Toggle> m_screenshotGif = null!;
    private static ConfigEntry<int> m_gifFPS = null!;
    private static ConfigEntry<float> m_gifDuration = null!;
    private static ConfigEntry<string> m_gifResolution = null!;
    private static ConfigEntry<KeyCode> m_selfieKey = null!;
    #endregion
    #region ai
    private static ConfigEntry<AIService> m_aiService = null!;
    private static ConfigEntry<string> m_chatGPTAPIKEY = null!;
    private static ConfigEntry<string> m_geminiAPIKEY = null!;
    private static ConfigEntry<GeminiModel> m_geminiModel = null!;
    private static ConfigEntry<string> m_deepSeekAPIKEY = null!;
    private static ConfigEntry<string> m_openRouterAPIKEY = null!;
    private static ConfigEntry<OpenRouterModel> m_openRouterModel = null!;
    private static ConfigEntry<Toggle> m_useServerKeys = null!;
    private static readonly CustomSyncedValue<string> m_serverOptions = new(ConfigSync, "RustyMods.DiscordBot.ServerOptions", "");
    private static ConfigEntry<Toggle> m_allowDiscordPrompt = null!;
    private static ConfigEntry<Toggle> m_improveDeathQuips = null!;
    private static ConfigEntry<Toggle> m_improveDayQuips = null!;
    private static ConfigEntry<string> m_aiProviderOrder = null!;
    private static ConfigEntry<string> m_geminiModels = null!;
    private static ConfigEntry<Toggle> m_geminiAutoDiscover = null!;
    private static ConfigEntry<string> m_openRouterModels = null!;
    private static ConfigEntry<Toggle> m_openRouterAutoDiscover = null!;
    private static ConfigEntry<Toggle> m_openRouterFreeOnly = null!;
    private static ConfigEntry<string> m_openAIModels = null!;
    private static ConfigEntry<string> m_deepSeekModels = null!;
    private static ConfigEntry<int> m_aiMaxAttempts = null!;
    private static ConfigEntry<int> m_aiModelsPerProvider = null!;
    private static ConfigEntry<int> m_aiRequestTimeoutSeconds = null!;
    private static ConfigEntry<int> m_aiCatalogCacheMinutes = null!;
    private static ConfigEntry<int> m_aiMaxOutputTokens = null!;
    private static ConfigEntry<int> m_aiMaxPromptCharacters = null!;
    private static ConfigEntry<float> m_aiRemoteRequestCooldown = null!;
    private static ConfigEntry<int> m_aiRemoteResponseTimeoutSeconds = null!;
    private static ConfigEntry<Toggle> m_serverAIBroker = null!;
    private static ConfigEntry<Toggle> m_allowPlayerAIPrompts = null!;
    #endregion

    private static ConfigEntry<Toggle> m_enableJobs = null!;
    public static bool ShowServerStart => m_serverStartNotice.Value is Toggle.On;
    public static bool ShowBossDeath => m_showBossDeath.Value is Toggle.On;
    public static bool ShowServerDetails => m_showServerDetails.Value is Toggle.On;
    public static bool ShowChat => m_chatEnabled.Value is Toggle.On;
    private static bool LogErrors => m_logErrors.Value is Toggle.On;
    public static bool ShowServerStop => m_serverStopNotice.Value is Toggle.On;
    public static bool ShowServerSave => m_serverSaveNotice.Value is Toggle.On;
    public static bool ShowOnDeath => m_deathNotice.Value is Toggle.On;
    public static bool ShowOnLogin => m_loginNotice.Value is Toggle.On;
    public static bool ShowOnLogout => m_logoutNotice.Value is Toggle.On;
    public static bool ShowEvent => m_eventNotice.Value is Toggle.On;
    public static bool ShowNewDay => m_newDayNotice.Value is Toggle.On;
    public static bool ShowCoordinates => m_coordinateDetails.Value is Toggle.On;
    private static bool ShowDetailedLogs => m_showDetailedLogs.Value is Toggle.On;
    public static bool ShowCommandUse => m_commandNotice.Value is Toggle.On;
    public static ChatDisplay ChatType => m_chatType.Value;
    public static string DiscordAdmins => m_discordAdmins.Value;
    public static void SetDiscordAdmins(string value) => m_discordAdmins.Value = value;
    public static string BOT_TOKEN => m_botToken.Value;
    public static string ChatChannelID => m_chatChannelID.Value;
    public static string CommandChannelID => m_commandChannelID.Value;
    public static string ChatWebhookURL => IsServer ? m_chatWebhookURL.Value : string.Empty;
    public static string CommandWebhookURL => IsServer ? m_commandWebhookURL.Value : string.Empty;
    public static string NoticeWebhookURL => IsServer ? m_notificationWebhookURL.Value : string.Empty;
    public static string DeathFeedWebhookURL => IsServer ? m_deathFeedURL.Value : string.Empty;
    public static bool ScreenshotDeath => m_screenshotDeath.Value is Toggle.On;
    public static bool ScreenshotGif => m_screenshotGif.Value is Toggle.On;
    public static float ScreenshotDelay => m_screenshotDelay.Value;
    public static int GIF_FPS => m_gifFPS.Value;
    public static float GIF_DURATION => m_gifDuration.Value;
    public static Resolution ScreenshotResolution => resolutions[m_screenshotResolution.Value];
    public static Resolution GifResolution => resolutions[m_gifResolution.Value];
    public static KeyCode SelfieKey => m_selfieKey.Value;
    public static AIService AIService => m_aiService.Value;
    private static string ChatGPT_KEY => m_chatGPTAPIKEY.Value;
    private static string Gemini_KEY => m_geminiAPIKEY.Value;
    private static string DeepSeek_KEY => m_deepSeekAPIKEY.Value;
    private static string OpenRouter_KEY => m_openRouterAPIKEY.Value;
    private static bool UseServerKeys => m_useServerKeys.Value is Toggle.On;
    private static OpenRouterModel OpenRouterModel => m_openRouterModel.Value;
    private static GeminiModel GeminiModel => m_geminiModel.Value;
    public static bool AllowDiscordPrompt => m_allowDiscordPrompt.Value is Toggle.On;
    public static bool ImproveDeathQuips => m_improveDeathQuips.Value is Toggle.On;
    public static bool ImproveDayQuips => m_improveDayQuips.Value is Toggle.On;
    public static bool GeminiAutoDiscover => m_geminiAutoDiscover.Value is Toggle.On;
    public static bool OpenRouterAutoDiscover => m_openRouterAutoDiscover.Value is Toggle.On;
    public static bool OpenRouterFreeOnly => m_openRouterFreeOnly.Value is Toggle.On;
    public static bool ServerAIBrokerEnabled => m_serverAIBroker.Value is Toggle.On;
    public static bool AllowPlayerAIPrompts => m_allowPlayerAIPrompts.Value is Toggle.On;
    public static int AIMaxAttempts => Math.Max(1, m_aiMaxAttempts.Value);
    public static int AIModelsPerProvider => Math.Max(1, m_aiModelsPerProvider.Value);
    public static int AIRequestTimeoutSeconds => Math.Max(1, m_aiRequestTimeoutSeconds.Value);
    public static int AICatalogCacheMinutes => Math.Max(1, m_aiCatalogCacheMinutes.Value);
    public static int AIMaxOutputTokens => Math.Max(8, m_aiMaxOutputTokens.Value);
    public static int AIMaxPromptCharacters => Math.Max(128, m_aiMaxPromptCharacters.Value);
    public static float AIRemoteRequestCooldown => Math.Max(0f, m_aiRemoteRequestCooldown.Value);
    public static int AIRemoteResponseTimeoutSeconds => Math.Max(10, m_aiRemoteResponseTimeoutSeconds.Value);

    public static void LogWarning(string message)
    {
        records.Log(LogLevel.Warning, message);
    }

    public static void LogDebug(string message)
    {
        records.Log(LogLevel.Debug, message);
    }

    public static void LogError(string message)
    {
        records.Log(LogLevel.Error, message);
    }

    private static readonly Dictionary<string, Resolution> resolutions = new();
    public static List<string> OnWorldStartHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.WorldStart);
    public static List<string> OnWorldSaveHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.WorldSave);
    public static List<string> OnWorldShutdownHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.WorldShutdown);
    public static List<string> OnLoginHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.Login);
    public static List<string> OnLogoutHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.Logout);
    public static List<string> OnEventHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.Event);
    public static List<string> OnNewDayHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.NewDay);
    public static List<string> OnUseCommandHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.UseCommand);
    public static List<string> OnBossDeathHooks => GetWebhookTargets(Webhook.Notifications, WebhookRoute.Boss);

    public static bool JobsEnabled => m_enableJobs.Value is Toggle.On;

    private static ServerAIOption SyncedAIOption = new();

    private static bool IsServer => ZNet.instance?.IsServer() ?? false;

    public static List<string> GetWebhookTargets(Webhook webhook, WebhookRoute route = WebhookRoute.Default)
    {
        if (!IsServer) return new List<string>();

        string routeValue = route switch
        {
            WebhookRoute.WorldStart => m_startWorldHook.Value,
            WebhookRoute.WorldSave => m_saveWebhook.Value,
            WebhookRoute.WorldShutdown => m_shutdownWebhook.Value,
            WebhookRoute.Login => m_loginWebhook.Value,
            WebhookRoute.Logout => m_logoutWebhook.Value,
            WebhookRoute.Event => m_eventWebhook.Value,
            WebhookRoute.NewDay => m_newDayWebhook.Value,
            WebhookRoute.UseCommand => m_useCommandWebhook.Value,
            WebhookRoute.Boss => m_bossWebhook.Value,
            _ => string.Empty
        };

        List<string> targets = ParseWebhookList(routeValue);
        if (targets.Count == 0)
        {
            string fallback = webhook.ToURL();
            if (IsDiscordWebhookURL(fallback)) targets.Add(fallback);
        }

        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ParseWebhookList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new List<string>();
        return new StringListConfig(value).list
            .Select(item => item.Trim())
            .Where(IsDiscordWebhookURL)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsDiscordWebhookURL(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!(string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase))) return false;
        return uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    public static AIService GetAIServiceOption() => AIService;
    public static OpenRouterModel GetOpenRouterOption() => OpenRouterModel;
    public static GeminiModel GetGeminiOption() => GeminiModel;
    public static string GetChatGPTKey() => ChatGPT_KEY;
    public static string GetGeminiKey() => Gemini_KEY;
    public static string GetDeepSeekKey() => DeepSeek_KEY;
    public static string GetOpenRouterKey() => OpenRouter_KEY;

    public static bool HasAnyLocalAIKey() =>
        !string.IsNullOrWhiteSpace(ChatGPT_KEY) ||
        !string.IsNullOrWhiteSpace(Gemini_KEY) ||
        !string.IsNullOrWhiteSpace(DeepSeek_KEY) ||
        !string.IsNullOrWhiteSpace(OpenRouter_KEY);

    public static bool CanUseServerAI()
    {
        if (!UseServerKeys || !ServerAIBrokerEnabled) return false;
        if (ZNet.instance?.IsServer() ?? false) return HasAnyLocalAIKey();
        return SyncedAIOption.serverAIAvailable;
    }

    public static List<AIService> GetAIProviderOrder()
    {
        List<AIService> providers = ParseEnumList<AIService>(m_aiProviderOrder.Value);
        if (AIService != AIService.None) providers.Insert(0, AIService);
        return providers.Where(provider => provider != AIService.None).Distinct().ToList();
    }

    public static List<string> GetGeminiModels()
    {
        string legacy = GeminiModel.GetAttributeOfType<InternalName>().internalName;
        List<string> configured = ParseStringList(m_geminiModels.Value)
            .Where(model => !IsRetiredGeminiModel(model))
            .ToList();
        return IsRetiredGeminiModel(legacy) ? configured : PrependModel(legacy, configured);
    }

    private static bool IsRetiredGeminiModel(string model)
    {
        string value = model.Trim();
        return string.Equals(value, "gemini-2.0-flash", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "gemini-2.0-pro", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "gemini-2.5-flash", StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> GetOpenRouterModels()
    {
        string legacy = OpenRouterModel.GetAttributeOfType<InternalName>().internalName;
        return PrependModel(legacy, ParseStringList(m_openRouterModels.Value));
    }

    public static List<string> GetOpenAIModels() => ParseStringList(m_openAIModels.Value);
    public static List<string> GetDeepSeekModels() => ParseStringList(m_deepSeekModels.Value);

    private static List<string> ParseStringList(string value) => value
        .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Trim())
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<T> ParseEnumList<T>(string value) where T : struct
    {
        List<T> result = new();
        foreach (string item in ParseStringList(value))
        {
            if (Enum.TryParse(item, true, out T parsed)) result.Add(parsed);
        }
        return result;
    }

    private static List<string> PrependModel(string primary, List<string> models)
    {
        models.Insert(0, primary);
        return models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static readonly Record records = new();

    // TODO : Figure out to make sure connecting peer is connecting to the right server

    public void Awake()
    {
        Keys.Write();
        Localizer.Load();
        m_instance = this;
        _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
        _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
        m_logErrors = config("1 - General", "Log Errors", Toggle.Off, "If on, caught errors will log to console");
        m_showDetailedLogs = config("1 - General", "Detailed Logs", Toggle.Off, "Show detailed logs");
        m_notificationWebhookURL = config("2 - Notifications", "Webhook URL", "", "Set webhook to receive notifications, like server start, stop, save etc... [Server Only]", false);
        m_notificationWebhookURL.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_serverStartNotice = config("2 - Notifications", "Startup", Toggle.On, "If on, bot will send message when server is starting");
        m_showServerDetails = config("2 - Notifications", "Server Details", Toggle.Off, "If on, bot will send server details when starting");
        m_serverStopNotice = config("2 - Notifications", "Shutdown", Toggle.On, "If on, bot will send message when server is shutting down");
        m_serverSaveNotice = config("2 - Notifications", "Saving", Toggle.On, "If on, bot will send message when server is saving");
        m_loginNotice = config("2 - Notifications", "Login", Toggle.On, "If on, bot will send message when player logs in");
        m_logoutNotice = config("2 - Notifications", "Logout", Toggle.On, "If on, bot will send message when player logs out");
        m_eventNotice = config("2 - Notifications", "Random Events", Toggle.On, "If on, bot will send message when random event starts");
        m_newDayNotice = config("2 - Notifications", "New Day", Toggle.Off, "If on, bot will send message when a new day begins");
        m_coordinateDetails = config("2 - Notifications", "Show Coordinates", Toggle.On, "If on, coordinates will be added to login/logout notifications");
        m_showBossDeath = config("2 - Notifications", "Show Boss Death", Toggle.Off, "If on, bot will send boss death notifications");
        m_commandNotice = config("2 - Notifications", "Show Command Use", Toggle.Off, "If on, bot will send message when a player uses a cheat terminal command");
        m_chatWebhookURL = config("3 - Chat", "Webhook URL", "", "Set discord webhook to display chat messages [Server Only]", false);
        m_chatWebhookURL.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_chatChannelID = config("3 - Chat", "Channel ID", "", "Set channel ID to monitor for messages");
        m_chatEnabled = config("3 - Chat", "Enabled", Toggle.On, "If on, bot will send message when player shouts and monitor discord for messages");
        m_chatType = config("3 - Chat", "Display As", ChatDisplay.Player, "Set how chat messages appear, if Player, message sent by player, else sent by bot with a prefix that player is saying");

        m_commandWebhookURL = config("4 - Commands", "Webhook URL", "", "Set discord webhook to display feedback messages from commands [Server Only]", false);
        m_commandWebhookURL.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_commandChannelID = config("4 - Commands", "Channel ID", "", "Set channel ID to monitor for input commands");
        m_discordAdmins = config("4 - Commands", "Discord Admin", "", new ConfigDescription("List of discord admins, who can run commands", null, new ConfigurationManagerAttributes()
        {
            CustomDrawer = StringListConfig.Draw
        }));
        m_enableJobs = config("4 - Commands", "Jobs", Toggle.On, "If on, jobs are enabled");

        m_botToken = config("5 - Setup", "BOT TOKEN", "", "Add bot token here, server only", false);

        m_deathNotice = config("6 - Death Feed", "Enabled", Toggle.On, "If on, bot will send message when player dies");
        m_deathFeedURL = config("6 - Death Feed", "Webhook URL", "", "Set webhook to receive death feed messages [Server Only]", false);
        m_deathFeedURL.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_screenshotDeath = config("6 - Death Feed", "Screenshot", Toggle.On, "If on, bot will post screenshot of death", false);
        m_screenshotDelay = config("6 - Death Feed", "Screenshot Delay", 0.3f, new ConfigDescription("Set delay", new AcceptableValueRange<float>(0.1f, 5f)), false);

        Resolution med = new(800, 600);
        Resolution medium = new(960, 540);
        Resolution hd = new(1280, 720);
        Resolution super = new(1920, 1080);

        m_screenshotResolution = config("6 - Death Feed", "Screenshot Resolution", medium.ToString(),
            new ConfigDescription("Set resolution",
                new AcceptableValueList<string>(
                    med.ToString(),
                    medium.ToString(),
                    hd.ToString(),
                    super.ToString()
                    )),
            false);
        m_screenshotGif = config("6 - Death Feed", "Screenshot GIF", Toggle.On, "If on, bot will post gif of death", false);
        m_gifFPS = config("6 - Death Feed", "GIF FPS", 30, new ConfigDescription("Set frames per second", new AcceptableValueRange<int>(1, 30)), false);
        m_gifDuration = config("6 - Death Feed", "GIF Record Duration", 3f, new ConfigDescription("Set recording duration for gif, in seconds", new AcceptableValueRange<float>(1f, 3f)), false);

        Resolution thumbnail = new(256, 144);
        Resolution small = new(320, 180);
        Resolution standard = new(480, 270);
        Resolution banner = new(640, 360);

        m_gifResolution = config("6 - Death Feed", "GIF Resolution", standard.ToString(),
            new ConfigDescription("Set resolution",
                new AcceptableValueList<string>(
                    thumbnail.ToString(),
                    small.ToString(),
                    standard.ToString(),
                    banner.ToString()
                )),
            false);

        m_selfieKey = config("1 - General", "Selfie", KeyCode.None, "Hotkey to take selfie and send to discord", false);

        m_startWorldHook = config("7 - Webhooks", "On World Start", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_saveWebhook = config("7 - Webhooks", "On World Save", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_shutdownWebhook = config("7 - Webhooks", "On World Shutdown", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_loginWebhook = config("7 - Webhooks", "On Login", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_logoutWebhook = config("7 - Webhooks", "On Logout", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_eventWebhook = config("7 - Webhooks", "On Event", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_newDayWebhook = config("7 - Webhooks", "On New Day", "", new ConfigDescription("If empty, will use default notification webhook [Server Only]", null, new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_useCommandWebhook = config("7 - Webhooks", "On Use Command", "",
            new ConfigDescription("If empty, will use default notification webhook [Server Only]", null,
                new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_bossWebhook = config("7 - Webhooks", "On Boss Death", "", new ConfigDescription(
            "If empty, will use default notification webhook [Server Only]", null,
            new ConfigurationManagerAttributes() { CustomDrawer = StringListConfig.Draw }), false);
        m_startWorldHook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_saveWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_shutdownWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_loginWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_logoutWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_eventWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_newDayWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_useCommandWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();
        m_bossWebhook.SettingChanged += (_, _) => UpdateServerWebhooks();

        m_aiService = config("8 - AI", "Provider", AIService.Gemini, "Set which Artificial Intelligence API to use", false);
        m_chatGPTAPIKEY = config("8 - AI", "ChatGPT", "", "Set ChatGPT key", false);
        m_geminiAPIKEY = config("8 - AI", "Gemini", "", "Set Gemini key", false);
        m_deepSeekAPIKEY = config("8 - AI", "DeepSeek", "", "Set DeepSeek key", false);
        m_openRouterAPIKEY = config("8 - AI", "OpenRouter", "", "Set OpenRouter key", false);
        m_openRouterModel = config("8 - AI", "OpenRouter Model", OpenRouterModel.AutoFree, "Legacy primary OpenRouter model. Prefer OpenRouter Models for ordered failover.", false);
        m_useServerKeys = config("8 - AI", "Use Server Keys", Toggle.On, "If enabled, clients without local keys send AI requests to the server broker. API keys are never synchronized to clients.");
        m_geminiModel = config("8 - AI", "Gemini Model", GeminiModel.Flash3_6, "Legacy primary Gemini model. Prefer Gemini Models for ordered failover.", false);
        m_allowDiscordPrompt = config("8 - AI", "Discord Prompt", Toggle.Off, "If on, Discord users can prompt the server AI using !prompt");
        m_aiProviderOrder = config("8 - AI", "Provider Order", "Gemini, OpenRouter, ChatGPT, DeepSeek", "Ordered provider failover. The Provider setting is tried first when enabled.", false);
        m_geminiModels = config("8 - AI", "Gemini Models", "gemini-3.6-flash, gemini-3.5-flash-lite, gemini-3.5-flash, gemini-3.1-flash-lite, gemini-flash-latest, gemini-flash-lite-latest, gemini-3.1-pro-preview, gemini-3-flash-preview, gemini-pro-latest, gemini-2.5-flash-lite, gemini-2.5-pro", "Ordered Gemini model failover list.", false);
        m_geminiAutoDiscover = config("8 - AI", "Gemini Auto Discover", Toggle.On, "Discover text-generation models available to the configured Gemini key.", false);
        m_openRouterModels = config("8 - AI", "OpenRouter Models", "openrouter/free", "Ordered OpenRouter model failover list. Discovered models are appended when enabled.", false);
        m_openRouterAutoDiscover = config("8 - AI", "OpenRouter Auto Discover", Toggle.On, "Discover models usable by the configured OpenRouter key and account preferences.", false);
        m_openRouterFreeOnly = config("8 - AI", "OpenRouter Free Only", Toggle.On, "Only auto-discover OpenRouter models with zero prompt, completion, and request price.", false);
        m_openAIModels = config("8 - AI", "OpenAI Models", "gpt-5.6-luna, gpt-5.6-terra, gpt-5.6-sol, gpt-5.6, gpt-5.5, gpt-5.4-mini, gpt-5.4-nano, gpt-4.1-mini", "Ordered OpenAI model failover list. Cost-efficient models are tried before frontier models by default.", false);
        m_deepSeekModels = config("8 - AI", "DeepSeek Models", "deepseek-chat, deepseek-reasoner", "Ordered DeepSeek model failover list.", false);
        m_aiMaxAttempts = config("8 - AI", "Max Attempts", 12, "Maximum provider/model attempts per AI request.", false);
        m_aiModelsPerProvider = config("8 - AI", "Models Per Provider", 3, "Maximum model attempts before failing over to the next configured provider.", false);
        m_aiRequestTimeoutSeconds = config("8 - AI", "Request Timeout Seconds", 30, "Timeout for each AI API request.", false);
        m_aiCatalogCacheMinutes = config("8 - AI", "Model Catalog Cache Minutes", 60, "How long discovered model catalogs are cached.", false);
        m_aiMaxOutputTokens = config("8 - AI", "Max Output Tokens", 160, "Maximum output tokens requested for a response.", false);
        m_aiMaxPromptCharacters = config("8 - AI", "Max Prompt Characters", 4000, "Maximum client prompt length accepted by the server AI broker.", false);
        m_aiRemoteRequestCooldown = config("8 - AI", "Remote Request Cooldown Seconds", 2f, "Minimum delay between AI broker requests from the same client.", false);
        m_aiRemoteResponseTimeoutSeconds = config("8 - AI", "Remote Response Timeout Seconds", 120, "Maximum time a client waits for a server AI broker response.", false);
        m_serverAIBroker = config("8 - AI", "Server AI Broker", Toggle.On, "Execute client death/day quip AI requests on the server without exposing API keys.", false);
        m_allowPlayerAIPrompts = config("8 - AI", "Allow Player AI Prompts", Toggle.Off, "Allow manual in-game player prompts through the server AI broker. Automatic death/day quips remain allowed.", false);
        m_chatGPTAPIKEY.SettingChanged += (_, _) => UpdateServerAIKeys();
        m_geminiAPIKEY.SettingChanged += (_, _) => UpdateServerAIKeys();
        m_deepSeekAPIKEY.SettingChanged += (_, _) => UpdateServerAIKeys();
        m_openRouterAPIKEY.SettingChanged += (_, _) => UpdateServerAIKeys();
        m_aiService.SettingChanged += (_, _) => UpdateServerAIOption();
        m_openRouterModel.SettingChanged += (_, _) => UpdateServerAIOption();
        m_geminiModel.SettingChanged += (_, _) => UpdateServerAIOption();
        m_aiProviderOrder.SettingChanged += (_, _) => UpdateServerAIOption();
        m_serverAIBroker.SettingChanged += (_, _) => UpdateServerAIOption();
        m_serverOptions.ValueChanged += () =>
        {
            if (string.IsNullOrWhiteSpace(m_serverOptions.Value) || (ZNet.instance?.IsServer() ?? false)) return;
            SyncedAIOption = new ServerAIOption(m_serverOptions.Value);
            LogDebug("Received server AI options");
        };
        m_improveDeathQuips = config("8 - AI", "Death Quips", Toggle.On, "If on and AI is setup, will prompt to improve quip");
        m_improveDayQuips = config("8 - AI", "Day Quips", Toggle.On, "If on, and AI is setup, will prompt to improve quip");
        DiscordCommands.Setup();
        DeathQuips.Setup();
        DayQuips.Setup();
        Assembly assembly = Assembly.GetExecutingAssembly();
        _harmony.PatchAll(assembly);
        SetupWatcher();
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
    private static class ZNet_Awake_Patch
    {
        [UsedImplicitly]
        private static void Postfix(ZNet __instance)
        {
            m_instance.gameObject.GetOrAddComponent<Discord>();
            m_instance.gameObject.GetOrAddComponent<Screenshot>();
            m_instance.gameObject.GetOrAddComponent<Recorder>();
            m_instance.gameObject.GetOrAddComponent<ChatAI>();

            if (!__instance.IsServer()) return;
            m_instance.gameObject.GetOrAddComponent<DiscordGatewayClient>();
            m_instance.gameObject.GetOrAddComponent<JobManager>();
            UpdateServerAIKeys();
            UpdateServerAIOption();
            UpdateServerWebhooks();
        }
    }

    private static void UpdateServerWebhooks()
    {
        if (!IsServer) return;
        records.Log(LogLevel.Info, "Reloaded server-only webhook configuration");
    }

    private static void UpdateServerAIKeys()
    {
        if (!(ZNet.instance?.IsServer() ?? false)) return;
        // Never synchronize bearer credentials to clients. Clients use the server AI broker.
        UpdateServerAIOption();
        records.Log(LogLevel.Info, "Updated server AI credential availability without synchronizing API keys");
    }

    private static void UpdateServerAIOption()
    {
        if (!(ZNet.instance?.IsServer() ?? false)) return;
        ServerAIOption option = new(AIService, OpenRouterModel, GeminiModel, ServerAIBrokerEnabled && HasAnyLocalAIKey());
        m_serverOptions.Value = option.ToString();
        records.Log(LogLevel.Info, "Updating server AI options");
    }

    private void OnDestroy()
    {
        Config.Save();
        records.Write();
    }

    public class StringListConfig
    {
        public readonly List<string> list;
        public StringListConfig(List<string> items) => list = items;
        public StringListConfig(string items) => list = items.Split(',').ToList();
        public static void Draw(ConfigEntryBase cfg)
        {
            bool locked = cfg.Description.Tags
                .Select(a =>
                    a.GetType().Name == "ConfigurationManagerAttributes"
                        ? (bool?)a.GetType().GetField("ReadOnly")?.GetValue(a)
                        : null).FirstOrDefault(v => v != null) ?? false;
            bool wasUpdated = false;
            List<string> strings = new();
            GUILayout.BeginVertical();
            foreach (var prefab in new StringListConfig((string)cfg.BoxedValue).list)
            {
                GUILayout.BeginHorizontal();
                var prefabName = prefab;
                var nameField = GUILayout.TextField(prefab);
                if (nameField != prefab && !locked)
                {
                    wasUpdated = true;
                    prefabName = nameField;
                }

                if (GUILayout.Button("x", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
                {
                    wasUpdated = true;
                }
                else
                {
                    strings.Add(prefabName);
                }

                if (GUILayout.Button("+", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
                {
                    strings.Add("");
                    wasUpdated = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            if (wasUpdated)
            {
                cfg.BoxedValue = new StringListConfig(strings).ToString();
            }
        }

        public override string ToString() => string.Join(",", list);
    }

    public class Resolution
    {
        public readonly int width;
        public readonly int height;

        public Resolution(int width, int height)
        {
            this.width = width;
            this.height = height;
            resolutions[ToString()] = this;
        }

        public sealed override string ToString() => $"{width}x{height}";
    }

    private void SetupWatcher()
    {
        FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
        watcher.Changed += ReadConfigValues;
        watcher.Created += ReadConfigValues;
        watcher.Renamed += ReadConfigValues;
        watcher.IncludeSubdirectories = true;
        watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        if (!File.Exists(ConfigFileFullPath)) return;
        try
        {
            DiscordBotLogger.LogDebug("ReadConfigValues called");
            Config.Reload();
        }
        catch
        {
            DiscordBotLogger.LogError($"There was an issue loading your {ConfigFileName}");
            DiscordBotLogger.LogError("Please check your config entries for spelling and format!");
        }
    }


    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription =
            new(
                description.Description +
                (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
                description.AcceptableValues, description.Tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);

        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    public ConfigEntry<T> config<T>(string group, string name, T value, string description,
        bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    public class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null!;
        [UsedImplicitly] public bool? Browsable = null!;
        [UsedImplicitly] public string? Category = null!;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
    }

    public class ServerAIOption
    {
        public readonly AIService service = AIService.Gemini;
        public readonly OpenRouterModel openRouterModel = OpenRouterModel.Claude3_5Sonnet;
        public readonly GeminiModel geminiModel = GeminiModel.Flash3_6;
        public readonly bool serverAIAvailable;
        public ServerAIOption() { }
        public ServerAIOption(string config)
        {
            var parts = config.Split(';');
            if (parts.Length < 3) return;
            Enum.TryParse(parts[0], true, out service);
            Enum.TryParse(parts[1], true, out openRouterModel);
            Enum.TryParse(parts[2], true, out geminiModel);
            if (parts.Length >= 4) bool.TryParse(parts[3], out serverAIAvailable);
        }
        public ServerAIOption(AIService service, OpenRouterModel openRouterModel, GeminiModel geminiModel, bool serverAIAvailable)
        {
            this.service = service;
            this.openRouterModel = openRouterModel;
            this.geminiModel = geminiModel;
            this.serverAIAvailable = serverAIAvailable;
        }

        public override string ToString() => $"{service};{openRouterModel};{geminiModel};{serverAIAvailable}";
    }

    public class Record
    {
        private readonly List<string> logs = new();
        public void Log(LogLevel level, string log)
        {
            logs.Add($"[{DateTime.Now:HH:mm:ss}][{level}]: {log}");
            switch (level)
            {
                case LogLevel.Error:
                    if (LogErrors) DiscordBotLogger.Log(level, log);
                    break;
                default:
                    if (ShowDetailedLogs) DiscordBotLogger.Log(level, log);
                    break;
            }
        }
        public void Write()
        {
            directory.WriteAllLines("RustyMods.DiscordBot.log", logs);
        }
    }
}
