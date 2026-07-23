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

    private const int WebhookBrokerProtocolVersion = 2;
    private const int MaxRemoteWebhookJsonCharacters = 32 * 1024;
    private const int MaxRemoteAttachmentBytes = 8 * 1024 * 1024;
    private const int RemoteAttachmentChunkBytes = 24 * 1024;
    private const int MaxRemoteAttachmentChunks = (MaxRemoteAttachmentBytes + RemoteAttachmentChunkBytes - 1) / RemoteAttachmentChunkBytes;
    private const int MaxRemoteControlPackageBytes = (MaxRemoteWebhookJsonCharacters * 4) + 2048;
    private const int MaxRemoteChunkPackageBytes = RemoteAttachmentChunkBytes + 1024;
    private const int MaxRemoteRequestsPerWindow = 12;
    private const int MaxRemoteAttachmentsPerWindow = 4;
    private const int MaxConcurrentRemoteTransfers = 8;
    private const float RemoteRequestWindowSeconds = 10f;
    private const float RemoteAttachmentWindowSeconds = 30f;
    private const float RemoteTransferTimeoutSeconds = 60f;

    private static readonly Dictionary<ZRpc, Queue<float>> RemoteWebhookRequestTimes = new();
    private static readonly Dictionary<ZRpc, Queue<float>> RemoteWebhookAttachmentTimes = new();
    private static readonly Dictionary<ZRpc, RemoteWebhookTransfer> RemoteWebhookTransfers = new();

    private Coroutine? m_remoteAttachmentUpload;
    private float m_nextRemoteTransferCleanup;

    private sealed class RemoteWebhookTransfer
    {
        public readonly string RequestId;
        public readonly Webhook Webhook;
        public readonly WebhookRoute Route;
        public readonly DiscordWebhookData Data;
        public readonly List<string> Targets;
        public readonly string MimeType;
        public readonly string Filename;
        public readonly byte[] Buffer;
        public readonly bool[] ReceivedChunks;
        public int ReceivedChunkCount;
        public int ReceivedBytes;
        public float LastActivity;

        public RemoteWebhookTransfer(
            string requestId,
            Webhook webhook,
            WebhookRoute route,
            DiscordWebhookData data,
            List<string> targets,
            string mimeType,
            string filename,
            int totalLength,
            int chunkCount)
        {
            RequestId = requestId;
            Webhook = webhook;
            Route = route;
            Data = data;
            Targets = targets;
            MimeType = mimeType;
            Filename = filename;
            Buffer = new byte[totalLength];
            ReceivedChunks = new bool[chunkCount];
            LastActivity = Time.realtimeSinceStartup;
        }
    }

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

    private void Update()
    {
        if (!isServer || Time.realtimeSinceStartup < m_nextRemoteTransferCleanup) return;
        m_nextRemoteTransferCleanup = Time.realtimeSinceStartup + 5f;
        CleanupExpiredRemoteTransfers();
    }

    private void OnDestroy()
    {
        if (m_remoteAttachmentUpload != null)
        {
            StopCoroutine(m_remoteAttachmentUpload);
            m_remoteAttachmentUpload = null;
        }

        RemoteWebhookRequestTimes.Clear();
        RemoteWebhookAttachmentTimes.Clear();
        RemoteWebhookTransfers.Clear();
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
            peer.m_rpc.Register<ZPackage>(nameof(RPC_WebhookAttachmentStart), RPC_WebhookAttachmentStart);
            peer.m_rpc.Register<ZPackage>(nameof(RPC_WebhookAttachmentChunk), RPC_WebhookAttachmentChunk);
            peer.m_rpc.Register<ZPackage>(nameof(RPC_WebhookAttachmentComplete), RPC_WebhookAttachmentComplete);
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
            RemoteWebhookTransfers.Remove(peer.m_rpc);
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
        if (serverRpc == null || !serverRpc.IsConnected())
        {
            OnError?.Invoke("Server webhook broker is unavailable because the server RPC is not connected");
            return;
        }

        byte[] bytes = attachment ?? Array.Empty<byte>();
        if (bytes.Length > MaxRemoteAttachmentBytes)
        {
            OnError?.Invoke("Webhook attachment exceeded the transport safety limit; sending a text-only fallback");
            SendTextOnlyWebhookRequest(serverRpc, webhook, route, data);
            return;
        }

        if (bytes.Length == 0)
        {
            SendTextOnlyWebhookRequest(serverRpc, webhook, route, data);
            return;
        }

        if (m_remoteAttachmentUpload != null)
        {
            OnError?.Invoke("A webhook attachment is already being uploaded; sending a text-only fallback");
            SendTextOnlyWebhookRequest(serverRpc, webhook, route, data);
            return;
        }

        string json = JsonConvert.SerializeObject(data);
        if (json.Length == 0 || json.Length > MaxRemoteWebhookJsonCharacters)
        {
            OnError?.Invoke("Webhook broker payload exceeded the configured safety limit");
            return;
        }

        string cleanFilename = Path.GetFileName(filename ?? string.Empty);
        m_remoteAttachmentUpload = StartCoroutine(
            SendWebhookAttachmentToServer(serverRpc, webhook, route, json, bytes, cleanFilename, mimeType ?? string.Empty));
    }

    private void SendTextOnlyWebhookRequest(
        ZRpc serverRpc,
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data)
    {
        RemoveAttachmentReferences(data);
        string json = JsonConvert.SerializeObject(data);
        if (json.Length == 0 || json.Length > MaxRemoteWebhookJsonCharacters)
        {
            OnError?.Invoke("Webhook broker text payload exceeded the configured safety limit");
            return;
        }

        ZPackage package = new();
        package.Write(WebhookBrokerProtocolVersion);
        package.Write((int)webhook);
        package.Write((int)route);
        package.Write(json);
        serverRpc.Invoke(nameof(RPC_WebhookRequest), package);
    }

    private static void RemoveAttachmentReferences(DiscordWebhookData data)
    {
        foreach (Embed embed in data.embeds ?? Array.Empty<Embed>())
        {
            if (embed.image?.url?.StartsWith("attachment://", StringComparison.OrdinalIgnoreCase) == true)
            {
                embed.image = null;
            }
        }
    }

    private IEnumerator SendWebhookAttachmentToServer(
        ZRpc serverRpc,
        Webhook webhook,
        WebhookRoute route,
        string json,
        byte[] attachment,
        string filename,
        string mimeType)
    {
        string requestId = Guid.NewGuid().ToString("N");
        int chunkCount = (attachment.Length + RemoteAttachmentChunkBytes - 1) / RemoteAttachmentChunkBytes;

        try
        {
            if (chunkCount <= 0 || chunkCount > MaxRemoteAttachmentChunks)
            {
                OnError?.Invoke("Webhook attachment required an invalid number of transport chunks");
                yield break;
            }

            ZPackage startPackage = new();
            startPackage.Write(WebhookBrokerProtocolVersion);
            startPackage.Write(requestId);
            startPackage.Write((int)webhook);
            startPackage.Write((int)route);
            startPackage.Write(json);
            startPackage.Write(mimeType);
            startPackage.Write(filename);
            startPackage.Write(attachment.Length);
            startPackage.Write(chunkCount);
            serverRpc.Invoke(nameof(RPC_WebhookAttachmentStart), startPackage);

            for (int chunkIndex = 0; chunkIndex < chunkCount; ++chunkIndex)
            {
                if (!serverRpc.IsConnected())
                {
                    OnError?.Invoke("Webhook attachment upload stopped because the server connection closed");
                    yield break;
                }

                int offset = chunkIndex * RemoteAttachmentChunkBytes;
                int length = Math.Min(RemoteAttachmentChunkBytes, attachment.Length - offset);
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(attachment, offset, chunk, 0, length);

                ZPackage chunkPackage = new();
                chunkPackage.Write(WebhookBrokerProtocolVersion);
                chunkPackage.Write(requestId);
                chunkPackage.Write(chunkIndex);
                chunkPackage.Write(chunk);
                serverRpc.Invoke(nameof(RPC_WebhookAttachmentChunk), chunkPackage);

                if ((chunkIndex + 1) % 2 == 0)
                {
                    yield return null;
                }
            }

            if (!serverRpc.IsConnected())
            {
                OnError?.Invoke("Webhook attachment upload stopped because the server connection closed");
                yield break;
            }

            ZPackage completePackage = new();
            completePackage.Write(WebhookBrokerProtocolVersion);
            completePackage.Write(requestId);
            serverRpc.Invoke(nameof(RPC_WebhookAttachmentComplete), completePackage);
        }
        finally
        {
            m_remoteAttachmentUpload = null;
        }
    }

    private static void RPC_WebhookRequest(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, MaxRemoteControlPackageBytes)) return;

        try
        {
            int protocolVersion = package.ReadInt();
            int webhookValue = package.ReadInt();
            int routeValue = package.ReadInt();
            string json = package.ReadString();

            if (!TryValidateRemoteWebhookMetadata(
                    protocolVersion,
                    webhookValue,
                    routeValue,
                    json,
                    rpc,
                    consumeAttachmentLimit: false,
                    out Webhook webhook,
                    out WebhookRoute route,
                    out DiscordWebhookData? data,
                    out List<string>? targets))
            {
                return;
            }

            instance!.StartCoroutine(instance.SendToMultipleHooks(data!, targets!));
        }
        catch (Exception ex)
        {
            DiscordBotPlugin.LogWarning($"Rejected malformed webhook broker package: {ex.Message}");
        }
    }

    private static void RPC_WebhookAttachmentStart(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, MaxRemoteControlPackageBytes)) return;
        CleanupExpiredRemoteTransfers();

        try
        {
            int protocolVersion = package.ReadInt();
            string requestId = package.ReadString();
            int webhookValue = package.ReadInt();
            int routeValue = package.ReadInt();
            string json = package.ReadString();
            string mimeType = package.ReadString();
            string filename = Path.GetFileName(package.ReadString());
            int totalLength = package.ReadInt();
            int chunkCount = package.ReadInt();

            if (!IsValidRequestId(requestId) ||
                totalLength <= 0 ||
                totalLength > MaxRemoteAttachmentBytes ||
                chunkCount <= 0 ||
                chunkCount > MaxRemoteAttachmentChunks ||
                chunkCount != (totalLength + RemoteAttachmentChunkBytes - 1) / RemoteAttachmentChunkBytes)
            {
                DiscordBotPlugin.LogWarning("Rejected malformed webhook attachment metadata");
                return;
            }

            if (!TryValidateRemoteWebhookMetadata(
                    protocolVersion,
                    webhookValue,
                    routeValue,
                    json,
                    rpc,
                    consumeAttachmentLimit: true,
                    out Webhook webhook,
                    out WebhookRoute route,
                    out DiscordWebhookData? data,
                    out List<string>? targets))
            {
                return;
            }

            if (RemoteWebhookTransfers.ContainsKey(rpc))
            {
                SendRemoteTextFallback(data!, targets!, "Rejected concurrent webhook attachment transfer");
                return;
            }

            if (RemoteWebhookTransfers.Count >= MaxConcurrentRemoteTransfers)
            {
                SendRemoteTextFallback(data!, targets!, "Webhook attachment capacity is full");
                return;
            }

            if (!IsAllowedAttachmentMetadata(webhook, mimeType, filename))
            {
                DiscordBotPlugin.LogWarning("Rejected invalid webhook attachment metadata");
                return;
            }

            RemoteWebhookTransfers[rpc] = new RemoteWebhookTransfer(
                requestId,
                webhook,
                route,
                data!,
                targets!,
                mimeType,
                filename,
                totalLength,
                chunkCount);

            DiscordBotPlugin.LogDebug(
                $"Accepted webhook attachment transfer with {chunkCount} chunks and {totalLength} bytes");
        }
        catch (Exception ex)
        {
            DiscordBotPlugin.LogWarning($"Rejected malformed webhook attachment start package: {ex.Message}");
        }
    }

    private static void RPC_WebhookAttachmentChunk(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, MaxRemoteChunkPackageBytes)) return;
        CleanupExpiredRemoteTransfers();

        try
        {
            int protocolVersion = package.ReadInt();
            string requestId = package.ReadString();
            int chunkIndex = package.ReadInt();
            byte[] chunk = package.ReadByteArray();

            if (protocolVersion != WebhookBrokerProtocolVersion ||
                !RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? transfer) ||
                !string.Equals(transfer.RequestId, requestId, StringComparison.Ordinal) ||
                chunkIndex < 0 ||
                chunkIndex >= transfer.ReceivedChunks.Length)
            {
                return;
            }

            int offset = chunkIndex * RemoteAttachmentChunkBytes;
            int expectedLength = Math.Min(RemoteAttachmentChunkBytes, transfer.Buffer.Length - offset);
            if (chunk.Length != expectedLength)
            {
                AbortRemoteTransfer(rpc, "Rejected webhook attachment chunk with an invalid length");
                return;
            }

            if (!transfer.ReceivedChunks[chunkIndex])
            {
                Buffer.BlockCopy(chunk, 0, transfer.Buffer, offset, chunk.Length);
                transfer.ReceivedChunks[chunkIndex] = true;
                transfer.ReceivedChunkCount++;
                transfer.ReceivedBytes += chunk.Length;
            }

            transfer.LastActivity = Time.realtimeSinceStartup;
        }
        catch (Exception ex)
        {
            AbortRemoteTransfer(rpc, $"Rejected malformed webhook attachment chunk: {ex.Message}");
        }
    }

    private static void RPC_WebhookAttachmentComplete(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, 1024)) return;
        CleanupExpiredRemoteTransfers();

        try
        {
            int protocolVersion = package.ReadInt();
            string requestId = package.ReadString();

            if (protocolVersion != WebhookBrokerProtocolVersion ||
                !RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? transfer) ||
                !string.Equals(transfer.RequestId, requestId, StringComparison.Ordinal))
            {
                return;
            }

            RemoteWebhookTransfers.Remove(rpc);
            if (transfer.ReceivedChunkCount != transfer.ReceivedChunks.Length ||
                transfer.ReceivedBytes != transfer.Buffer.Length ||
                !IsAllowedAttachment(transfer.Webhook, transfer.MimeType, transfer.Filename, transfer.Buffer))
            {
                DiscordBotPlugin.LogWarning("Rejected incomplete or invalid webhook attachment transfer");
                return;
            }

            instance!.StartCoroutine(
                instance.SendAttachmentToMultipleHooks(
                    transfer.Data,
                    transfer.Targets,
                    transfer.Buffer,
                    transfer.Filename,
                    transfer.MimeType));
        }
        catch (Exception ex)
        {
            AbortRemoteTransfer(rpc, $"Rejected malformed webhook attachment completion: {ex.Message}");
        }
    }

    private static bool CanAcceptRemoteWebhookPackage(ZRpc rpc, ZPackage package, int maximumPackageBytes)
    {
        if (!(ZNet.instance?.IsServer() ?? false) || instance == null) return false;
        if (package.Size() <= 0 || package.Size() > maximumPackageBytes)
        {
            DiscordBotPlugin.LogWarning("Rejected oversized webhook broker package");
            return false;
        }

        if (!rpc.IsConnected() || !ZNet.instance.GetPeers().Any(peer => peer.m_rpc == rpc))
        {
            DiscordBotPlugin.LogWarning("Rejected webhook broker request from an unknown peer");
            return false;
        }

        return true;
    }

    private static bool TryValidateRemoteWebhookMetadata(
        int protocolVersion,
        int webhookValue,
        int routeValue,
        string json,
        ZRpc rpc,
        bool consumeAttachmentLimit,
        out Webhook webhook,
        out WebhookRoute route,
        out DiscordWebhookData? data,
        out List<string>? targets)
    {
        webhook = default;
        route = default;
        data = null;
        targets = null;

        if (protocolVersion != WebhookBrokerProtocolVersion ||
            !Enum.IsDefined(typeof(Webhook), webhookValue) ||
            !Enum.IsDefined(typeof(WebhookRoute), routeValue) ||
            string.IsNullOrWhiteSpace(json) ||
            json.Length > MaxRemoteWebhookJsonCharacters)
        {
            DiscordBotPlugin.LogWarning("Rejected malformed webhook broker request");
            return false;
        }

        webhook = (Webhook)webhookValue;
        route = (WebhookRoute)routeValue;
        if (!IsAllowedRoute(webhook, route))
        {
            DiscordBotPlugin.LogWarning("Rejected webhook broker request with an invalid route");
            return false;
        }

        if (!TryConsumeRateLimit(RemoteWebhookRequestTimes, rpc, RemoteRequestWindowSeconds, MaxRemoteRequestsPerWindow))
        {
            DiscordBotPlugin.LogWarning("Rejected rate-limited webhook broker request");
            return false;
        }

        if (consumeAttachmentLimit &&
            !TryConsumeRateLimit(RemoteWebhookAttachmentTimes, rpc, RemoteAttachmentWindowSeconds, MaxRemoteAttachmentsPerWindow))
        {
            DiscordBotPlugin.LogWarning("Rejected rate-limited webhook attachment request");
            return false;
        }

        try
        {
            data = JsonConvert.DeserializeObject<DiscordWebhookData>(json);
        }
        catch (JsonException ex)
        {
            DiscordBotPlugin.LogWarning($"Rejected malformed webhook broker JSON: {ex.Message}");
            return false;
        }

        if (data == null)
        {
            DiscordBotPlugin.LogWarning("Rejected empty webhook broker payload");
            return false;
        }

        if (!ValidateWebhookData(data, out string validationError))
        {
            DiscordBotPlugin.LogWarning($"Rejected webhook broker payload: {validationError}");
            return false;
        }


        data.allowed_mentions = new AllowedMentions();
        targets = DiscordBotPlugin.GetWebhookTargets(webhook, route);
        if (targets.Count == 0)
        {
            DiscordBotPlugin.LogWarning($"No valid Discord webhook is configured for {webhook}/{route}");
            return false;
        }

        return true;
    }

    private static bool IsAllowedAttachmentMetadata(Webhook webhook, string mimeType, string filename)
    {
        if (webhook != Webhook.Chat && webhook != Webhook.DeathFeed) return false;
        if (mimeType != "image/png" && mimeType != "image/gif") return false;

        string cleanName = Path.GetFileName(filename ?? string.Empty);
        if (cleanName.Length == 0 || cleanName.Length > 128) return false;

        return mimeType == "image/png"
            ? cleanName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            : cleanName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidRequestId(string requestId)
    {
        return requestId.Length == 32 && requestId.All(Uri.IsHexDigit);
    }

    private static void SendRemoteTextFallback(
        DiscordWebhookData data,
        List<string> targets,
        string reason)
    {
        RemoveAttachmentReferences(data);
        DiscordBotPlugin.LogWarning($"{reason}; sending a text-only fallback");
        instance!.StartCoroutine(instance.SendToMultipleHooks(data, targets));
    }

    private static void AbortRemoteTransfer(ZRpc rpc, string reason)
    {
        RemoteWebhookTransfers.Remove(rpc);
        DiscordBotPlugin.LogWarning(reason);
    }

    private static void CleanupExpiredRemoteTransfers()
    {
        if (RemoteWebhookTransfers.Count == 0) return;

        float now = Time.realtimeSinceStartup;
        foreach (ZRpc rpc in RemoteWebhookTransfers
                     .Where(pair => !pair.Key.IsConnected() || now - pair.Value.LastActivity > RemoteTransferTimeoutSeconds)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            RemoteWebhookTransfers.Remove(rpc);
            DiscordBotPlugin.LogWarning("Discarded an expired webhook attachment transfer");
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

        [JsonConstructor]
        public DiscordWebhookData()
        {
        }

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

        [JsonConstructor]
        public EmbedImage()
        {
        }

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

        [JsonConstructor]
        public EmbedField()
        {
        }

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

        [JsonConstructor]
        public Footer()
        {
        }

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

