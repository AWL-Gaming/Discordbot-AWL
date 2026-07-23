using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using DiscordBot.Notices;
using HarmonyLib;
using JetBrains.Annotations;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace DiscordBot;

public class Discord : MonoBehaviour
{
    private static bool isServer => ZNet.instance?.IsServer() ?? false;

    public static Discord? instance;
    public event Action<Sprite>? OnImageDownloaded;
    public event Action<AudioClip>? OnAudioDownloaded;
    public event Action<string>? OnError;
    public event Action<string>? OnLog;

    public AudioSource? m_soundSource;

    private bool m_isDownloadingImage;
    private bool m_isDownloadingSound;

    private const int WebhookBrokerProtocolVersion = 1;
    private const int MaxRemoteWebhookJsonCharacters = 32 * 1024;
    private const int MaxRemoteAttachmentBytes = 20 * 1024 * 1024;
    private const int MaxRemoteWebhookPackageBytes = MaxRemoteAttachmentBytes + (MaxRemoteWebhookJsonCharacters * 4) + 1024;
    private const int MaxRemoteRequestsPerWindow = 12;
    private const int MaxRemoteAttachmentsPerWindow = 4;
    private const float RemoteRequestWindowSeconds = 10f;
    private const float RemoteAttachmentWindowSeconds = 30f;

    private static readonly Dictionary<ZRpc, Queue<float>> RemoteWebhookRequestTimes = new();
    private static readonly Dictionary<ZRpc, Queue<float>> RemoteWebhookAttachmentTimes = new();

    public void Awake()
    {
        instance = this;
        OnImageDownloaded += HandleImage;
        OnError += HandleError;
        OnAudioDownloaded += HandleSound;
        OnLog += HandleLog;

        ZRoutedRpc.instance.Register<string, string, bool>(nameof(RPC_DisplayChat), RPC_DisplayChat);

        DiscordBotPlugin.LogDebug("Initializing Discord Webhook");
    }

    private void Start()
    {
        SetupAudioSource();
        if (!isServer) return;
        SendMessage(Webhook.Commands, ZNet.instance.GetWorldName(), $"{EmojiHelper.Emoji("question")} type `!help` to find list of available commands");
        if (!DiscordBotPlugin.ShowServerStart) return;
        SendStatus(Webhook.Notifications, DiscordBotPlugin.OnWorldStartHooks, Keys.ServerStart, ZNet.instance.GetWorldName(), Keys.Launching, new Color(0.4f, 0.98f, 0.24f), route: WebhookRoute.WorldStart);
        if (!DiscordBotPlugin.ShowServerDetails) return;
        SendTableEmbed(Webhook.Notifications, "Server Details", new()
        {
            ["IP Address"] = ZNet.instance.GetServerIP(),
            ["Local Address"] = ZNet.instance.LocalIPAddress(),
        }, hooks: DiscordBotPlugin.OnWorldStartHooks, route: WebhookRoute.WorldStart);
    }

    private void OnDestroy()
    {
        instance = null;
    }

    public void SetupAudioSource()
    {
        m_soundSource = gameObject.AddComponent<AudioSource>();
        m_soundSource.loop = false;
        m_soundSource.spatialBlend = 0.0f;
        m_soundSource.outputAudioMixerGroup = MusicMan.instance.m_musicMixer;
        m_soundSource.priority = 0;
        m_soundSource.bypassReverbZones = true;
        m_soundSource.volume = 1f;

        DiscordBotPlugin.LogDebug("Initializing audio source");
    }

    private static void HandleError(string message) => DiscordBotPlugin.LogError(message);
    private static void HandleImage(Sprite sprite) => ImageHud.instance?.Show(sprite);
    private static void HandleLog(string message) => DiscordBotPlugin.records.Log(LogLevel.Info, message);
    private void HandleSound(AudioClip clip)
    {
        m_soundSource?.PlayOneShot(clip);
        StartCoroutine(UnloadClip(clip));
    }

    #region Image Download

    public void GetImage(string imageUrl)
    {
        if (m_isDownloadingImage) return;
        m_isDownloadingImage = true;
        StartCoroutine(DownloadImage(imageUrl));
    }

    private IEnumerator DownloadImage(string imageUrl)
    {
        using UnityWebRequest request = UnityWebRequestTexture.GetTexture(imageUrl);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            string message = $"Failed to download image from {imageUrl}: {request.error}";
            OnError?.Invoke(message);
        }
        else
        {
            Texture2D texture = DownloadHandlerTexture.GetContent(request);
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f) // Pivot at center
            );
            OnImageDownloaded?.Invoke(sprite);
        }
        m_isDownloadingImage = false;
    }
    #endregion

    #region Sound Download

    public void GetSound(string url, AudioType type)
    {
        if (m_isDownloadingSound) return;
        m_isDownloadingSound = true;
        if (IsDirectAudioUrl(url))
        {
            StartCoroutine(DownloadSound(url, type));
        }
        else
        {
            OnError?.Invoke("Invalid audio url: " + url);
        }
    }

    private static bool IsDirectAudioUrl(string url)
    {
        string lowerUrl = url.ToLower();
        return lowerUrl.EndsWith(".mp3") ||
               lowerUrl.EndsWith(".wav") ||
               lowerUrl.EndsWith(".ogg") ||
               lowerUrl.EndsWith(".m4a") ||
               lowerUrl.Contains(".mp3?") ||
               lowerUrl.Contains(".wav?") ||
               lowerUrl.Contains(".ogg?");
    }
    private IEnumerator DownloadSound(string url, AudioType type)
    {
        using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, type);
        ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            OnError?.Invoke("Failed to download audio: " + request.error);
        }
        else
        {
            AudioClip? clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip is null)
            {
                OnError?.Invoke("Failed to download audio");
            }
            else
            {
                OnAudioDownloaded?.Invoke(clip);
            }
        }
        m_isDownloadingSound = false;
    }

    private static IEnumerator UnloadClip(AudioClip? clip)
    {
        if (clip is null) yield break;
        yield return new WaitForSeconds(clip.length);
        Destroy(clip);
    }
    #endregion

    #region RPC Handlers

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
    private static class ZNet_OnNewConnection_Patch
    {
        [UsedImplicitly]
        private static void Postfix(ZNetPeer peer)
        {
            peer.m_rpc.Register<string, string, bool>(nameof(RPC_ClientBotMessage), RPC_ClientBotMessage);
            peer.m_rpc.Register<string>(nameof(RPC_GetSound), RPC_GetSound);
            peer.m_rpc.Register<ZPackage>(nameof(RPC_WebhookRequest), RPC_WebhookRequest);
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    private static class ZNet_Disconnect_Patch
    {
        [UsedImplicitly]
        private static void Prefix(ZNetPeer peer)
        {
            RemoteWebhookRequestTimes.Remove(peer.m_rpc);
            RemoteWebhookAttachmentTimes.Remove(peer.m_rpc);
        }
    }

    public void BroadcastMessage(string username, string message, bool showDiscord = true)
    {
        if (!ZNet.instance || !ZNet.m_isServer) return;
        foreach (var peer in ZNet.instance.GetPeers()) peer.m_rpc.Invoke(nameof(RPC_ClientBotMessage), username, message, showDiscord);
        if (!Player.m_localPlayer) return;
        DisplayChatMessage(username, message, showDiscord);
    }

    public void RPC_DisplayChat(long sender, string username, string message, bool showDiscord) =>
        DisplayChatMessage(username, message, showDiscord);

    public void Internal_BroadcastMessage(string username, string message, bool showDiscord)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, nameof(RPC_DisplayChat), username, message, showDiscord);
    }

    public static void RPC_ClientBotMessage(ZRpc rpc, string username, string message, bool showDiscord) => DisplayChatMessage(username, message, showDiscord);
    public static void DisplayChatMessage(string userName, string message, bool showDiscord = true)
    {
        string text = $"{(showDiscord ? $"<color=#{ColorUtility.ToHtmlStringRGB(new Color(0f, 0.5f, 0.5f, 1f))}>[Discord]</color>" : "")}<color=orange>{userName}</color>: {message}";
        Chat.instance.AddString(Localization.instance.Localize(text));
        Chat.instance.Show();
    }

    public static void BroadcastSound(string url)
    {
        if (!ZNet.instance || !ZNet.m_isServer) return;
        foreach (var peer in ZNet.instance.GetPeers()) peer.m_rpc.Invoke(nameof(RPC_GetSound), url);
    }
    public static void RPC_GetSound(ZRpc rpc, string url) => instance?.GetSound(url, AudioType.UNKNOWN);

    #endregion

    #region Sending Messages to Discord

    public void SendMessage(
        Webhook webhook,
        string username = "",
        string message = "",
        List<string>? hooks = null,
        WebhookRoute route = WebhookRoute.Default)
    {
        DispatchWebhook(webhook, route, new DiscordWebhookData(username, message), hooks);
    }

    public void SendImage(
        Webhook webhook,
        string username,
        Texture2D image,
        WebhookRoute route = WebhookRoute.Default)
    {
        byte[] data = image.EncodeToPNG();
        string filename = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".png";
        DispatchWebhook(webhook, route, new DiscordWebhookData(username), attachment: data, filename: filename, mimeType: "image/png");
    }

    public void SendEmbedMessage(
        Webhook webhook,
        string title,
        string content,
        string username = "",
        string thumbnail = "",
        WebhookRoute route = WebhookRoute.Default)
    {
        Embed embed = new(title, content);
        embed.AddThumbnail(thumbnail);
        DispatchWebhook(webhook, route, new DiscordWebhookData(username, embed));
    }

    public void SendTableEmbed(
        Webhook webhook,
        string title,
        Dictionary<string, string> tableData,
        string username = "",
        string thumbnail = "",
        List<string>? hooks = null,
        WebhookRoute route = WebhookRoute.Default)
    {
        if (tableData.Count <= 0)
        {
            OnError?.Invoke("Table data is empty");
            return;
        }

        List<EmbedField> fields = new();
        foreach (KeyValuePair<string, string> kvp in tableData)
        {
            fields.Add(new EmbedField(kvp.Key, kvp.Value));
        }

        Embed embed = new(title, fields);
        embed.AddThumbnail(thumbnail);
        DispatchWebhook(webhook, route, new DiscordWebhookData(username, embed), hooks);
    }

    public void SendStatus(
        Webhook webhook,
        List<string> hooks,
        string content,
        string worldName,
        string status,
        Color color,
        string username = "",
        string thumbnail = "",
        WebhookRoute route = WebhookRoute.Default)
    {
        Embed embed = new(content);
        embed.SetColor(color);
        embed.fields = new[]
        {
            new EmbedField(Keys.WorldName, worldName),
            new EmbedField(Keys.Status, status)
        };
        embed.AddThumbnail(thumbnail);
        DispatchWebhook(webhook, route, new DiscordWebhookData(username, embed), hooks);
    }

    public void SendEvent(
        Webhook webhook,
        List<string> hooks,
        string content,
        Color color,
        Dictionary<string, string>? extra = null,
        string thumbnail = "",
        WebhookRoute route = WebhookRoute.Default)
    {
        Embed embed = new(content);
        embed.SetColor(color);
        if (extra?.Count > 0)
        {
            List<EmbedField> fields = new();
            foreach (KeyValuePair<string, string> kvp in extra)
            {
                fields.Add(new EmbedField(kvp.Key, kvp.Value));
            }
            embed.fields = fields.ToArray();
        }
        embed.AddThumbnail(thumbnail);
        DispatchWebhook(webhook, route, new DiscordWebhookData("", embed), hooks);
    }

    public void SendImageMessage(
        Webhook webhook,
        string title,
        string content,
        byte[] imageData,
        string filename,
        string username = "",
        string thumbnail = "",
        WebhookRoute route = WebhookRoute.Default)
    {
        Embed screenshot = new(title, content);
        screenshot.AddImage($"attachment://{Path.GetFileName(filename)}");
        screenshot.AddThumbnail(thumbnail);
        DispatchWebhook(webhook, route, new DiscordWebhookData(username, screenshot), attachment: imageData, filename: filename, mimeType: "image/png");
    }

    public void SendGifMessage(
        Webhook webhook,
        string title,
        string content,
        byte[] gif,
        string filename,
        string username = "",
        string thumbnail = "",
        WebhookRoute route = WebhookRoute.Default)
    {
        Embed screenshot = new(title, content);
        screenshot.AddImage($"attachment://{Path.GetFileName(filename)}");
        screenshot.AddThumbnail(thumbnail);
        DispatchWebhook(webhook, route, new DiscordWebhookData(username, screenshot), attachment: gif, filename: filename, mimeType: "image/gif");
    }

    private void DispatchWebhook(
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data,
        List<string>? explicitTargets = null,
        byte[]? attachment = null,
        string filename = "",
        string mimeType = "")
    {
        data.allowed_mentions = new AllowedMentions();
        if (!ValidateWebhookData(data, out string validationError))
        {
            OnError?.Invoke(validationError);
            return;
        }

        if (isServer)
        {
            List<string> targets = explicitTargets is { Count: > 0 }
                ? explicitTargets.Where(IsValidDiscordWebhookURL).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : DiscordBotPlugin.GetWebhookTargets(webhook, route);

            if (targets.Count == 0)
            {
                OnError?.Invoke($"No valid Discord webhook is configured for {webhook}/{route}");
                return;
            }

            if (attachment is { Length: > 0 })
            {
                StartCoroutine(SendAttachmentToMultipleHooks(data, targets, attachment, filename, mimeType));
            }
            else
            {
                StartCoroutine(SendToMultipleHooks(data, targets));
            }
            return;
        }

        SendWebhookRequestToServer(webhook, route, data, attachment, filename, mimeType);
    }

    private void SendWebhookRequestToServer(
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data,
        byte[]? attachment,
        string filename,
        string mimeType)
    {
        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        if (serverRpc == null)
        {
            OnError?.Invoke("Server webhook broker is unavailable because the server RPC is not connected");
            return;
        }

        string json = JsonConvert.SerializeObject(data);
        byte[] bytes = attachment ?? Array.Empty<byte>();
        if (json.Length > MaxRemoteWebhookJsonCharacters || bytes.Length > MaxRemoteAttachmentBytes)
        {
            OnError?.Invoke("Webhook broker payload exceeded the configured safety limit");
            return;
        }

        ZPackage package = new();
        package.Write(WebhookBrokerProtocolVersion);
        package.Write((int)webhook);
        package.Write((int)route);
        package.Write(json);
        package.Write(mimeType ?? string.Empty);
        package.Write(Path.GetFileName(filename ?? string.Empty));
        package.Write(bytes);
        serverRpc.Invoke(nameof(RPC_WebhookRequest), package);
    }

    private static void RPC_WebhookRequest(ZRpc rpc, ZPackage package)
    {
        if (!(ZNet.instance?.IsServer() ?? false) || instance == null) return;
        if (package.Size() <= 0 || package.Size() > MaxRemoteWebhookPackageBytes)
        {
            DiscordBotPlugin.LogWarning("Rejected oversized webhook broker package");
            return;
        }

        if (!ZNet.instance.GetPeers().Any(peer => peer.m_rpc == rpc))
        {
            DiscordBotPlugin.LogWarning("Rejected webhook broker request from an unknown peer");
            return;
        }

        try
        {
            int protocolVersion = package.ReadInt();
            int webhookValue = package.ReadInt();
            int routeValue = package.ReadInt();
            string json = package.ReadString();
            string mimeType = package.ReadString();
            string filename = package.ReadString();
            byte[] attachment = package.ReadByteArray();

            if (protocolVersion != WebhookBrokerProtocolVersion ||
                !Enum.IsDefined(typeof(Webhook), webhookValue) ||
                !Enum.IsDefined(typeof(WebhookRoute), routeValue))
            {
                DiscordBotPlugin.LogWarning("Rejected malformed webhook broker request");
                return;
            }

            Webhook webhook = (Webhook)webhookValue;
            WebhookRoute route = (WebhookRoute)routeValue;
            if (!IsAllowedRoute(webhook, route))
            {
                DiscordBotPlugin.LogWarning("Rejected webhook broker request with an invalid route");
                return;
            }

            if (json.Length == 0 || json.Length > MaxRemoteWebhookJsonCharacters || attachment.Length > MaxRemoteAttachmentBytes)
            {
                DiscordBotPlugin.LogWarning("Rejected oversized webhook broker request");
                return;
            }

            if (!TryConsumeRateLimit(RemoteWebhookRequestTimes, rpc, RemoteRequestWindowSeconds, MaxRemoteRequestsPerWindow))
            {
                DiscordBotPlugin.LogWarning("Rejected rate-limited webhook broker request");
                return;
            }

            bool hasAttachment = attachment.Length > 0;
            if (hasAttachment)
            {
                if (!TryConsumeRateLimit(RemoteWebhookAttachmentTimes, rpc, RemoteAttachmentWindowSeconds, MaxRemoteAttachmentsPerWindow) ||
                    !IsAllowedAttachment(webhook, mimeType, filename, attachment))
                {
                    DiscordBotPlugin.LogWarning("Rejected invalid or rate-limited webhook attachment request");
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(mimeType) || !string.IsNullOrWhiteSpace(filename))
            {
                DiscordBotPlugin.LogWarning("Rejected inconsistent webhook broker request");
                return;
            }

            DiscordWebhookData? data = JsonConvert.DeserializeObject<DiscordWebhookData>(json);
            if (data == null)
            {
                DiscordBotPlugin.LogWarning("Rejected empty webhook broker payload");
                return;
            }

            if (!ValidateWebhookData(data, out string validationError))
            {
                DiscordBotPlugin.LogWarning($"Rejected webhook broker payload: {validationError}");
                return;
            }
            data.allowed_mentions = new AllowedMentions();

            List<string> targets = DiscordBotPlugin.GetWebhookTargets(webhook, route);
            if (targets.Count == 0)
            {
                DiscordBotPlugin.LogWarning($"No valid Discord webhook is configured for {webhook}/{route}");
                return;
            }

            if (hasAttachment)
            {
                instance.StartCoroutine(instance.SendAttachmentToMultipleHooks(data, targets, attachment, Path.GetFileName(filename), mimeType));
            }
            else
            {
                instance.StartCoroutine(instance.SendToMultipleHooks(data, targets));
            }
        }
        catch (Exception ex)
        {
            DiscordBotPlugin.LogWarning($"Rejected malformed webhook broker package: {ex.Message}");
        }
    }

    private static bool IsAllowedRoute(Webhook webhook, WebhookRoute route)
    {
        if (route == WebhookRoute.PublicApi)
        {
            return webhook is Webhook.Notifications or Webhook.Chat or Webhook.Commands;
        }

        return webhook switch
        {
            Webhook.Chat => route == WebhookRoute.Default,
            Webhook.Commands => route == WebhookRoute.Default,
            Webhook.DeathFeed => route == WebhookRoute.Default,
            Webhook.Notifications => route is WebhookRoute.Login or WebhookRoute.UseCommand,
            _ => false
        };
    }

    private static bool IsAllowedAttachment(Webhook webhook, string mimeType, string filename, byte[] attachment)
    {
        if (webhook != Webhook.Chat && webhook != Webhook.DeathFeed) return false;
        if (mimeType != "image/png" && mimeType != "image/gif") return false;

        string cleanName = Path.GetFileName(filename ?? string.Empty);
        if (cleanName.Length == 0 || cleanName.Length > 128) return false;

        bool extensionMatches = mimeType == "image/png"
            ? cleanName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            : cleanName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        return extensionMatches && HasExpectedImageSignature(mimeType, attachment);
    }

    private static bool HasExpectedImageSignature(string mimeType, byte[] attachment)
    {
        if (mimeType == "image/png")
        {
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            return attachment.Length >= signature.Length &&
                   signature.Select((value, index) => attachment[index] == value).All(matches => matches);
        }

        if (attachment.Length < 6) return false;
        string signatureText = Encoding.ASCII.GetString(attachment, 0, 6);
        return signatureText == "GIF87a" || signatureText == "GIF89a";
    }

    private static bool TryConsumeRateLimit(Dictionary<ZRpc, Queue<float>> buckets, ZRpc rpc, float windowSeconds, int maximum)
    {
        float now = Time.realtimeSinceStartup;
        if (!buckets.TryGetValue(rpc, out Queue<float>? bucket))
        {
            bucket = new Queue<float>();
            buckets[rpc] = bucket;
        }

        while (bucket.Count > 0 && now - bucket.Peek() >= windowSeconds)
        {
            bucket.Dequeue();
        }

        if (bucket.Count >= maximum) return false;
        bucket.Enqueue(now);
        return true;
    }

    private static bool ValidateWebhookData(DiscordWebhookData data, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(data.content) && (data.embeds?.Length ?? 0) == 0)
        {
            error = "Webhook payload did not contain content or embeds";
            return false;
        }
        if ((data.username?.Length ?? 0) > 80)
        {
            error = "Webhook username exceeded 80 characters";
            return false;
        }
        if ((data.content?.Length ?? 0) > 2000)
        {
            error = "Webhook content exceeded 2000 characters";
            return false;
        }
        if ((data.embeds?.Length ?? 0) > 10)
        {
            error = "Webhook payload exceeded 10 embeds";
            return false;
        }

        int totalEmbedCharacters = 0;
        foreach (Embed embed in data.embeds ?? Array.Empty<Embed>())
        {
            if ((embed.title?.Length ?? 0) > 256 || (embed.description?.Length ?? 0) > 4096 || (embed.fields?.Length ?? 0) > 25)
            {
                error = "Webhook embed exceeded Discord limits";
                return false;
            }
            totalEmbedCharacters += (embed.title?.Length ?? 0) + (embed.description?.Length ?? 0);
            foreach (EmbedField field in embed.fields ?? Array.Empty<EmbedField>())
            {
                if ((field.name?.Length ?? 0) > 256 || (field.value?.Length ?? 0) > 1024)
                {
                    error = "Webhook embed field exceeded Discord limits";
                    return false;
                }
                totalEmbedCharacters += (field.name?.Length ?? 0) + (field.value?.Length ?? 0);
            }
        }

        if (totalEmbedCharacters > 6000)
        {
            error = "Webhook embeds exceeded 6000 total characters";
            return false;
        }
        return true;
    }

    private IEnumerator SendToMultipleHooks(DiscordWebhookData data, List<string> urls)
    {
        string jsonData = JsonConvert.SerializeObject(data);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);

        foreach (string url in urls.Where(IsValidDiscordWebhookURL).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = new(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                OnError?.Invoke(FormatWebhookError(request));
            }
            else
            {
                OnLog?.Invoke("Sent webhook message to a configured Discord destination");
            }
        }
    }

    private IEnumerator SendAttachmentToMultipleHooks(
        DiscordWebhookData data,
        List<string> urls,
        byte[] attachment,
        string filename,
        string mimeType)
    {
        string cleanName = Path.GetFileName(filename);
        string json = JsonConvert.SerializeObject(data);

        foreach (string url in urls.Where(IsValidDiscordWebhookURL).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<IMultipartFormSection> formData = new()
            {
                new MultipartFormFileSection("file", attachment, cleanName, mimeType),
                new MultipartFormDataSection("payload_json", json, "application/json")
            };

            using UnityWebRequest request = UnityWebRequest.Post(url, formData);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                OnError?.Invoke(FormatWebhookError(request));
            }
            else
            {
                OnLog?.Invoke("Sent webhook attachment to a configured Discord destination");
            }
        }
    }

    private static bool IsValidDiscordWebhookURL(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!(string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase))) return false;
        return uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatWebhookError(UnityWebRequest request)
    {
        return $"Discord webhook request failed with HTTP {request.responseCode}: {request.error}";
    }

    #endregion

    #region Utility Methods

    [Description("Color utility to format into int for discord")]
    private static int ColorToInt(Color color)
    {
        int r = Mathf.RoundToInt(color.r * 255);
        int g = Mathf.RoundToInt(color.g * 255);
        int b = Mathf.RoundToInt(color.b * 255);
        return (r << 16) + (g << 8) + b;
    }

    #endregion

    #region Discord Webhook

    [Serializable]
    [UsedImplicitly]
    public class AllowedMentions
    {
        public string[] parse = Array.Empty<string>();
    }

    [Serializable]
    [UsedImplicitly]
    public class DiscordWebhookData
    {
        public string? content; // up to 2000 characters
        public bool tts; // text-to-speech
        public Embed[]? embeds; // up to 10
        public string? username; // override username display
        public string? avatar_url;
        public AllowedMentions allowed_mentions = new();

        public DiscordWebhookData(string username, string content)
        {
            if (!string.IsNullOrEmpty(username)) this.username = Localization.instance.Localize(username);
            this.content = Localization.instance.Localize(content);
        }

        public DiscordWebhookData(string username, params Embed[] embeds)
        {
            if (!string.IsNullOrEmpty(username)) this.username = username;
            this.embeds = embeds;
        }
    }

    [Serializable]
    [UsedImplicitly]
    public class Embed
    {
        public string? title;
        public string? description;
        public string? url;
        public string? timestamp;
        public int? color;
        public Footer? footer;
        public EmbedImage? image;
        public EmbedImage? thumbnail;
        public EmbedVideo? video;
        public EmbedProvider? provider;
        public EmbedAuthor? author;
        public EmbedField[]? fields; // max 25

        public Embed()
        {
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        public Embed(string title, string description) : this()
        {
            this.title = Localization.instance.Localize(title);
            this.description = Localization.instance.Localize(description);
        }

        public Embed(string title, params EmbedField[] fields) : this()
        {
            this.title = Localization.instance.Localize(title);
            this.fields = fields;
        }

        public Embed(string description) : this()
        {
            this.description = Localization.instance.Localize(description);
        }

        public Embed(string title, List<EmbedField> fields) : this(title, fields.ToArray()) { }

        public void AddImage(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return;
            image = new EmbedImage(imageUrl);
        }

        public void AddThumbnail(string thumbnailUrl)
        {
            if (string.IsNullOrEmpty(thumbnailUrl)) return;
            thumbnail = new EmbedImage(thumbnailUrl);
        }

        public void SetColor(Color Color)
        {
            color = ColorToInt(Color);
        }
    }

    [Serializable]
    [UsedImplicitly]
    public class EmbedImage
    {
        public string? url;
        public int width;
        public int height;

        public EmbedImage(string url, int width = 256, int height = 256)
        {
            this.url = url;
            this.width = width;
            this.height = height;
        }
    }

    [Serializable]
    [UsedImplicitly]
    public class EmbedVideo
    {
        public string? url;
        public int height;
        public int width;
    }

    [Serializable]
    [UsedImplicitly]
    public class EmbedProvider
    {
        public string? name;
        public string? url;
    }

    [Serializable]
    [UsedImplicitly]
    public class EmbedAuthor
    {
        public string? name;
        public string? icon_url;
    }

    [Serializable]
    [UsedImplicitly]
    public class EmbedField
    {
        public string? name;
        public string? value;
        public bool inline;

        public EmbedField(string name, string value, bool inline = true)
        {
            this.name = Localization.instance.Localize(name);
            this.value = Localization.instance.Localize(value);
            this.inline = inline;
        }
    }

    [Serializable]
    [UsedImplicitly]
    public class Footer
    {
        public string? text;
        public string? icon_url;

        public Footer(string text)
        {
            this.text = Localization.instance.Localize(text);
        }

        public void AddIcon(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            icon_url = url;
        }
    }
    #endregion
}

