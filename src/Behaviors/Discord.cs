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

    private const int WebhookBrokerProtocolVersion = 3;
    private const int MaxRemoteWebhookJsonCharacters = 32 * 1024;
    internal const int RemoteAttachmentSafetyBytes = 8 * 1024 * 1024;
    private const int RemoteAttachmentChunkBytes = 16 * 1024;
    private const int MaxRemoteAttachmentChunks =
        (RemoteAttachmentSafetyBytes + RemoteAttachmentChunkBytes - 1) / RemoteAttachmentChunkBytes;
    private const int MaxRemoteControlPackageBytes = (MaxRemoteWebhookJsonCharacters * 4) + 4096;
    private const int MaxRemoteChunkPackageBytes = RemoteAttachmentChunkBytes + 1024;
    private const int MaxRemoteRequestsPerWindow = 12;
    private const int MaxRemoteAttachmentsPerWindow = 4;
    private const int MaxConcurrentRemoteTransfers = 8;
    private const int MaxConcurrentRemoteTransferBytes = 32 * 1024 * 1024;
    private const int MaxQueuedRemoteAttachments = 4;
    private const int RemoteAttachmentChunkWindowSize = 2;
    private const int RemoteAttachmentMaxRetries = 3;
    private const float RemoteRequestWindowSeconds = 10f;
    private const float RemoteAttachmentWindowSeconds = 30f;
    private const float RemoteTransferTimeoutSeconds = 120f;
    private const float RemoteCompletedTransferRetentionSeconds = 120f;
    private const float RemoteAttachmentAckTimeoutSeconds = 2f;
    private const float RemoteAttachmentRetryDelaySeconds = 0.15f;

    private static readonly Dictionary<ZRpc, Queue<float>> RemoteWebhookRequestTimes = new();
    private static readonly Dictionary<ZRpc, Queue<float>> RemoteWebhookAttachmentTimes = new();
    private static readonly Dictionary<ZRpc, RemoteWebhookTransfer> RemoteWebhookTransfers = new();
    private static readonly Dictionary<ZRpc, Dictionary<string, float>> RemoteWebhookCompletedTransfers = new();

    private Coroutine? m_remoteAttachmentUpload;
    private readonly Queue<PendingRemoteAttachment> m_remoteAttachmentQueue = new();
    private readonly Dictionary<string, AttachmentAckWaitState> m_remoteAttachmentAckWaits =
        new(StringComparer.Ordinal);
    private float m_nextRemoteTransferCleanup;

    private enum AttachmentAckStage
    {
        Start,
        Chunk,
        Complete,
        Abort
    }

    private enum AttachmentAckCode
    {
        None = 0,
        Accepted = 1,
        Acknowledged = 2,
        Completed = 3,
        AlreadyCompleted = 4,
        Aborted = 5,
        Rejected = 100
    }

    private sealed class AttachmentAckWaitState
    {
        public readonly string RequestId;
        public readonly AttachmentAckStage Stage;
        public readonly int ChunkIndex;
        public bool Received;
        public bool Success;
        public AttachmentAckCode Code;
        public string Reason = string.Empty;

        public AttachmentAckWaitState(string requestId, AttachmentAckStage stage, int chunkIndex)
        {
            RequestId = requestId;
            Stage = stage;
            ChunkIndex = chunkIndex;
        }
    }

    private sealed class RemoteUploadResult
    {
        public bool Success;
        public AttachmentAckCode Code;
        public string Reason = string.Empty;
    }

    private sealed class PendingChunkSend
    {
        public readonly int ChunkIndex;
        public readonly byte[] Data;
        public int Attempts;
        public bool Success;
        public string Reason = string.Empty;

        public PendingChunkSend(int chunkIndex, byte[] data)
        {
            ChunkIndex = chunkIndex;
            Data = data;
        }
    }

    private sealed class PendingRemoteAttachment
    {
        public readonly Webhook Webhook;
        public readonly WebhookRoute Route;
        public readonly DiscordWebhookData Data;
        public readonly byte[] Attachment;
        public readonly string Filename;
        public readonly string MimeType;
        public readonly byte[] FallbackAttachment;
        public readonly string FallbackFilename;
        public readonly string FallbackMimeType;
        public readonly string TransferLabel;

        public PendingRemoteAttachment(
            Webhook webhook,
            WebhookRoute route,
            DiscordWebhookData data,
            byte[] attachment,
            string filename,
            string mimeType,
            byte[] fallbackAttachment,
            string fallbackFilename,
            string fallbackMimeType,
            string transferLabel)
        {
            Webhook = webhook;
            Route = route;
            Data = data;
            Attachment = attachment;
            Filename = filename;
            MimeType = mimeType;
            FallbackAttachment = fallbackAttachment;
            FallbackFilename = fallbackFilename;
            FallbackMimeType = fallbackMimeType;
            TransferLabel = transferLabel;
        }
    }

    private sealed class RemoteWebhookTransfer
    {
        public readonly string RequestId;
        public readonly Webhook Webhook;
        public readonly WebhookRoute Route;
        public readonly DiscordWebhookData Data;
        public readonly List<string> Targets;
        public readonly string MimeType;
        public readonly string Filename;
        public readonly string TransferLabel;
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
            string transferLabel,
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
            TransferLabel = transferLabel;
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

        m_remoteAttachmentQueue.Clear();
        m_remoteAttachmentAckWaits.Clear();
        RemoteWebhookRequestTimes.Clear();
        RemoteWebhookAttachmentTimes.Clear();
        RemoteWebhookTransfers.Clear();
        RemoteWebhookCompletedTransfers.Clear();
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
            peer.m_rpc.Register<ZPackage>(nameof(RPC_WebhookAttachmentAbort), RPC_WebhookAttachmentAbort);
            peer.m_rpc.Register<ZPackage>(nameof(RPC_WebhookAttachmentAck), RPC_WebhookAttachmentAck);
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
            RemoteWebhookCompletedTransfers.Remove(peer.m_rpc);
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
        WebhookRoute route = WebhookRoute.Default,
        string transferLabel = "PNG")
    {
        Embed screenshot = new(title, content);
        screenshot.AddImage($"attachment://{Path.GetFileName(filename)}");
        screenshot.AddThumbnail(thumbnail);
        DispatchWebhook(
            webhook,
            route,
            new DiscordWebhookData(username, screenshot),
            attachment: imageData,
            filename: filename,
            mimeType: "image/png",
            transferLabel: transferLabel);
    }

    public void SendGifMessage(
        Webhook webhook,
        string title,
        string content,
        byte[] gif,
        string filename,
        string username = "",
        string thumbnail = "",
        WebhookRoute route = WebhookRoute.Default,
        byte[]? fallbackPng = null,
        string fallbackFilename = "",
        string transferLabel = "GIF")
    {
        Embed screenshot = new(title, content);
        screenshot.AddImage($"attachment://{Path.GetFileName(filename)}");
        screenshot.AddThumbnail(thumbnail);
        DispatchWebhook(
            webhook,
            route,
            new DiscordWebhookData(username, screenshot),
            attachment: gif,
            filename: filename,
            mimeType: "image/gif",
            fallbackAttachment: fallbackPng,
            fallbackFilename: fallbackFilename,
            fallbackMimeType: "image/png",
            transferLabel: transferLabel);
    }

    private void DispatchWebhook(
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data,
        List<string>? explicitTargets = null,
        byte[]? attachment = null,
        string filename = "",
        string mimeType = "",
        byte[]? fallbackAttachment = null,
        string fallbackFilename = "",
        string fallbackMimeType = "",
        string transferLabel = "attachment")
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

        SendWebhookRequestToServer(
            webhook,
            route,
            data,
            attachment,
            filename,
            mimeType,
            fallbackAttachment,
            fallbackFilename,
            fallbackMimeType,
            transferLabel);
    }

    private void SendWebhookRequestToServer(
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data,
        byte[]? attachment,
        string filename,
        string mimeType,
        byte[]? fallbackAttachment,
        string fallbackFilename,
        string fallbackMimeType,
        string transferLabel)
    {
        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        if (serverRpc == null || !serverRpc.IsConnected())
        {
            OnError?.Invoke("Server webhook broker is unavailable because the server RPC is not connected");
            return;
        }

        byte[] bytes = attachment ?? Array.Empty<byte>();
        if (bytes.Length == 0)
        {
            SendTextOnlyWebhookRequest(serverRpc, webhook, route, data, "no attachment bytes were available");
            return;
        }

        string json = JsonConvert.SerializeObject(data);
        if (json.Length == 0 || json.Length > MaxRemoteWebhookJsonCharacters)
        {
            OnError?.Invoke("Webhook broker payload exceeded the configured safety limit");
            return;
        }

        int queuedTransfers = m_remoteAttachmentQueue.Count + (m_remoteAttachmentUpload != null ? 1 : 0);
        if (queuedTransfers >= MaxQueuedRemoteAttachments)
        {
            SendTextOnlyWebhookRequest(
                serverRpc,
                webhook,
                route,
                data,
                $"the client attachment queue reached its limit of {MaxQueuedRemoteAttachments}");
            return;
        }

        PendingRemoteAttachment pending = new(
            webhook,
            route,
            data,
            bytes,
            Path.GetFileName(filename ?? string.Empty),
            mimeType ?? string.Empty,
            fallbackAttachment ?? Array.Empty<byte>(),
            Path.GetFileName(fallbackFilename ?? string.Empty),
            fallbackMimeType ?? string.Empty,
            SanitizeTransferLabel(transferLabel));
        m_remoteAttachmentQueue.Enqueue(pending);
        DiscordBotPlugin.LogDebug(
            $"Queued {pending.TransferLabel} webhook attachment with {SizeFormatter.FormatBytes(pending.Attachment.Length)}; " +
            $"queue depth is {m_remoteAttachmentQueue.Count}");

        if (m_remoteAttachmentUpload == null)
        {
            m_remoteAttachmentUpload = StartCoroutine(ProcessRemoteAttachmentQueue());
        }
    }

    private IEnumerator ProcessRemoteAttachmentQueue()
    {
        try
        {
            while (m_remoteAttachmentQueue.Count > 0)
            {
                PendingRemoteAttachment pending = m_remoteAttachmentQueue.Dequeue();
                ZRpc? serverRpc = null;
                float connectionDeadline = Time.realtimeSinceStartup + 15f;
                while ((serverRpc = GetConnectedServerRpc()) == null &&
                       Time.realtimeSinceStartup < connectionDeadline)
                {
                    yield return new WaitForSecondsRealtime(1f);
                }

                if (serverRpc == null)
                {
                    OnError?.Invoke(
                        $"Dropped {pending.TransferLabel} because the server RPC did not reconnect within 15 seconds");
                    continue;
                }

                byte[] selectedAttachment = pending.Attachment;
                string selectedFilename = pending.Filename;
                string selectedMimeType = pending.MimeType;
                string selectedLabel = pending.TransferLabel;
                bool usingFallback = false;

                if (selectedAttachment.Length > RemoteAttachmentSafetyBytes)
                {
                    string reason =
                        $"{selectedLabel} was {SizeFormatter.FormatBytes(selectedAttachment.Length)}, above the " +
                        $"{SizeFormatter.FormatBytes(RemoteAttachmentSafetyBytes)} limit";
                    if (CanUseFallbackAttachment(pending))
                    {
                        DiscordBotPlugin.LogWarning($"{reason}; switching to PNG fallback before transport");
                        selectedAttachment = pending.FallbackAttachment;
                        selectedFilename = pending.FallbackFilename;
                        selectedMimeType = pending.FallbackMimeType;
                        selectedLabel = $"PNG fallback: {reason}";
                        usingFallback = true;
                    }
                    else
                    {
                        SendTextOnlyWebhookRequest(serverRpc, pending.Webhook, pending.Route, pending.Data, reason);
                        continue;
                    }
                }

                SetAttachmentReference(pending.Data, selectedFilename);
                RemoteUploadResult result = new();
                yield return UploadAttachmentToServer(
                    serverRpc,
                    pending.Webhook,
                    pending.Route,
                    pending.Data,
                    selectedAttachment,
                    selectedFilename,
                    selectedMimeType,
                    selectedLabel,
                    result);

                if (result.Success)
                {
                    DiscordBotPlugin.LogInfo(
                        $"Completed acknowledged webhook transport for {selectedLabel} " +
                        $"({SizeFormatter.FormatBytes(selectedAttachment.Length)})");
                    continue;
                }

                if (!usingFallback && CanUseFallbackAttachment(pending))
                {
                    string fallbackReason = string.IsNullOrWhiteSpace(result.Reason)
                        ? "the GIF transfer failed"
                        : result.Reason;
                    string fallbackLabel = $"PNG fallback: {fallbackReason}";
                    DiscordBotPlugin.LogWarning(
                        $"{fallbackReason}; retrying the death notice with " +
                        $"{SizeFormatter.FormatBytes(pending.FallbackAttachment.Length)} PNG fallback");
                    SetAttachmentReference(pending.Data, pending.FallbackFilename);
                    RemoteUploadResult fallbackResult = new();
                    serverRpc = GetConnectedServerRpc();
                    if (serverRpc != null)
                    {
                        yield return UploadAttachmentToServer(
                            serverRpc,
                            pending.Webhook,
                            pending.Route,
                            pending.Data,
                            pending.FallbackAttachment,
                            pending.FallbackFilename,
                            pending.FallbackMimeType,
                            fallbackLabel,
                            fallbackResult);
                    }
                    else
                    {
                        fallbackResult.Reason = "server connection closed before the PNG fallback could start";
                    }

                    if (fallbackResult.Success)
                    {
                        DiscordBotPlugin.LogInfo("Completed acknowledged webhook transport for the PNG fallback");
                        continue;
                    }

                    result = fallbackResult;
                }

                serverRpc = GetConnectedServerRpc();
                if (serverRpc != null)
                {
                    SendTextOnlyWebhookRequest(
                        serverRpc,
                        pending.Webhook,
                        pending.Route,
                        pending.Data,
                        string.IsNullOrWhiteSpace(result.Reason)
                            ? "all attachment transport attempts failed"
                            : result.Reason);
                }
                else
                {
                    OnError?.Invoke(
                        $"Could not send text-only fallback for {pending.TransferLabel} because the server RPC disconnected");
                }
            }
        }
        finally
        {
            m_remoteAttachmentAckWaits.Clear();
            m_remoteAttachmentUpload = null;
        }
    }

    private static ZRpc? GetConnectedServerRpc()
    {
        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        return serverRpc != null && serverRpc.IsConnected() ? serverRpc : null;
    }

    private static bool CanUseFallbackAttachment(PendingRemoteAttachment pending)
    {
        return pending.FallbackAttachment.Length > 0 &&
               pending.FallbackAttachment.Length <= RemoteAttachmentSafetyBytes &&
               string.Equals(pending.FallbackMimeType, "image/png", StringComparison.Ordinal) &&
               pending.FallbackFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
               HasExpectedImageSignature("image/png", pending.FallbackAttachment);
    }

    private void SendTextOnlyWebhookRequest(
        ZRpc serverRpc,
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data,
        string reason)
    {
        RemoveAttachmentReferences(data);
        DiscordBotPlugin.LogWarning($"Sending text-only webhook fallback because {reason}");
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

    private static void SetAttachmentReference(DiscordWebhookData data, string filename)
    {
        string cleanName = Path.GetFileName(filename ?? string.Empty);
        foreach (Embed embed in data.embeds ?? Array.Empty<Embed>())
        {
            if (embed.image != null)
            {
                embed.image.url = $"attachment://{cleanName}";
            }
        }
    }

    private IEnumerator UploadAttachmentToServer(
        ZRpc serverRpc,
        Webhook webhook,
        WebhookRoute route,
        DiscordWebhookData data,
        byte[] attachment,
        string filename,
        string mimeType,
        string transferLabel,
        RemoteUploadResult result)
    {
        string requestId = Guid.NewGuid().ToString("N");
        int chunkCount = (attachment.Length + RemoteAttachmentChunkBytes - 1) / RemoteAttachmentChunkBytes;
        if (attachment.Length <= 0 ||
            attachment.Length > RemoteAttachmentSafetyBytes ||
            chunkCount <= 0 ||
            chunkCount > MaxRemoteAttachmentChunks)
        {
            result.Reason =
                $"{transferLabel} required an invalid transport size " +
                $"({SizeFormatter.FormatBytes(attachment.Length)}, {chunkCount} chunks)";
            yield break;
        }

        string json = JsonConvert.SerializeObject(data);
        if (json.Length == 0 || json.Length > MaxRemoteWebhookJsonCharacters)
        {
            result.Reason = "the webhook metadata exceeded the configured safety limit";
            yield break;
        }

        string cleanLabel = SanitizeTransferLabel(transferLabel);
        RemoteUploadResult startResult = new();
        yield return SendPackageWithAck(
            serverRpc,
            nameof(RPC_WebhookAttachmentStart),
            () =>
            {
                ZPackage package = new();
                package.Write(WebhookBrokerProtocolVersion);
                package.Write(requestId);
                package.Write((int)webhook);
                package.Write((int)route);
                package.Write(json);
                package.Write(mimeType);
                package.Write(filename);
                package.Write(cleanLabel);
                package.Write(attachment.Length);
                package.Write(chunkCount);
                return package;
            },
            requestId,
            AttachmentAckStage.Start,
            -1,
            startResult);
        if (!startResult.Success)
        {
            result.Code = startResult.Code;
            result.Reason = $"server rejected or did not acknowledge {cleanLabel} start: {startResult.Reason}";
            yield break;
        }

        for (int windowStart = 0; windowStart < chunkCount; windowStart += RemoteAttachmentChunkWindowSize)
        {
            List<PendingChunkSend> window = new();
            int windowEnd = Math.Min(chunkCount, windowStart + RemoteAttachmentChunkWindowSize);
            for (int chunkIndex = windowStart; chunkIndex < windowEnd; ++chunkIndex)
            {
                int offset = chunkIndex * RemoteAttachmentChunkBytes;
                int length = Math.Min(RemoteAttachmentChunkBytes, attachment.Length - offset);
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(attachment, offset, chunk, 0, length);
                window.Add(new PendingChunkSend(chunkIndex, chunk));
            }

            RemoteUploadResult windowResult = new();
            yield return SendChunkWindowWithAck(serverRpc, requestId, window, windowResult);
            if (!windowResult.Success)
            {
                RemoteUploadResult abortResult = new();
                yield return AbortAttachmentOnServer(
                    serverRpc,
                    requestId,
                    $"chunk window {windowStart + 1}-{windowEnd}/{chunkCount} failed",
                    abortResult);
                result.Code = windowResult.Code;
                result.Reason =
                    $"{cleanLabel} chunk window {windowStart + 1}-{windowEnd}/{chunkCount} failed: " +
                    windowResult.Reason;
                yield break;
            }

            if (windowEnd % 32 == 0 || windowEnd == chunkCount)
            {
                DiscordBotPlugin.LogDebug(
                    $"Acknowledged {cleanLabel} chunks {windowStart + 1}-{windowEnd}/{chunkCount} " +
                    $"({SizeFormatter.FormatBytes(Math.Min(attachment.Length, windowEnd * RemoteAttachmentChunkBytes))})");
            }
        }

        RemoteUploadResult completeResult = new();
        yield return SendPackageWithAck(
            serverRpc,
            nameof(RPC_WebhookAttachmentComplete),
            () =>
            {
                ZPackage package = new();
                package.Write(WebhookBrokerProtocolVersion);
                package.Write(requestId);
                return package;
            },
            requestId,
            AttachmentAckStage.Complete,
            -1,
            completeResult);
        if (completeResult.Success)
        {
            result.Success = true;
            result.Code = completeResult.Code;
            result.Reason = completeResult.Reason;
            yield break;
        }

        RemoteUploadResult finalAbortResult = new();
        yield return AbortAttachmentOnServer(
            serverRpc,
            requestId,
            "completion acknowledgement failed",
            finalAbortResult);
        if (finalAbortResult.Success && finalAbortResult.Code == AttachmentAckCode.AlreadyCompleted)
        {
            result.Success = true;
            result.Code = finalAbortResult.Code;
            result.Reason = "server had already completed the transfer";
            yield break;
        }

        result.Code = completeResult.Code;
        result.Reason =
            $"server rejected or did not acknowledge {cleanLabel} completion: {completeResult.Reason}";
    }

    private IEnumerator SendChunkWindowWithAck(
        ZRpc serverRpc,
        string requestId,
        List<PendingChunkSend> window,
        RemoteUploadResult result)
    {
        for (int round = 1; round <= RemoteAttachmentMaxRetries; ++round)
        {
            if (!serverRpc.IsConnected())
            {
                result.Reason = "server connection is closed";
                yield break;
            }

            foreach (PendingChunkSend entry in window.Where(entry => !entry.Success))
            {
                entry.Attempts++;
                AttachmentAckWaitState waitState = new(requestId, AttachmentAckStage.Chunk, entry.ChunkIndex);
                m_remoteAttachmentAckWaits[GetAckKey(requestId, AttachmentAckStage.Chunk, entry.ChunkIndex)] = waitState;

                ZPackage package = new();
                package.Write(WebhookBrokerProtocolVersion);
                package.Write(requestId);
                package.Write(entry.ChunkIndex);
                package.Write(entry.Data);
                serverRpc.Invoke(nameof(RPC_WebhookAttachmentChunk), package);
            }

            float deadline = Time.realtimeSinceStartup + RemoteAttachmentAckTimeoutSeconds;
            while (serverRpc.IsConnected() && Time.realtimeSinceStartup < deadline)
            {
                bool allReceived = window.Where(entry => !entry.Success).All(entry =>
                {
                    string key = GetAckKey(requestId, AttachmentAckStage.Chunk, entry.ChunkIndex);
                    return m_remoteAttachmentAckWaits.TryGetValue(key, out AttachmentAckWaitState? wait) && wait.Received;
                });
                if (allReceived) break;
                yield return null;
            }

            foreach (PendingChunkSend entry in window.Where(entry => !entry.Success))
            {
                string key = GetAckKey(requestId, AttachmentAckStage.Chunk, entry.ChunkIndex);
                if (m_remoteAttachmentAckWaits.TryGetValue(key, out AttachmentAckWaitState? waitState))
                {
                    if (waitState.Received && waitState.Success)
                    {
                        entry.Success = true;
                        entry.Reason = waitState.Reason;
                    }
                    else
                    {
                        entry.Reason = waitState.Received
                            ? waitState.Reason
                            : $"ACK timed out after {RemoteAttachmentAckTimeoutSeconds:0.##} seconds";
                    }
                    m_remoteAttachmentAckWaits.Remove(key);
                }
                else
                {
                    entry.Reason = "ACK state was unavailable";
                }
            }

            if (window.All(entry => entry.Success))
            {
                result.Success = true;
                result.Code = AttachmentAckCode.Acknowledged;
                yield break;
            }

            if (round < RemoteAttachmentMaxRetries)
            {
                yield return new WaitForSecondsRealtime(RemoteAttachmentRetryDelaySeconds * round);
            }
        }

        PendingChunkSend failed = window.First(entry => !entry.Success);
        result.Code = AttachmentAckCode.Rejected;
        result.Reason =
            $"chunk {failed.ChunkIndex + 1} failed after {failed.Attempts} attempts: {failed.Reason}";
    }

    private IEnumerator AbortAttachmentOnServer(
        ZRpc serverRpc,
        string requestId,
        string reason,
        RemoteUploadResult result)
    {
        if (!serverRpc.IsConnected())
        {
            result.Reason = "server connection is closed";
            yield break;
        }

        yield return SendPackageWithAck(
            serverRpc,
            nameof(RPC_WebhookAttachmentAbort),
            () =>
            {
                ZPackage package = new();
                package.Write(WebhookBrokerProtocolVersion);
                package.Write(requestId);
                package.Write(SanitizeTransferLabel(reason));
                return package;
            },
            requestId,
            AttachmentAckStage.Abort,
            -1,
            result);
    }

    private IEnumerator SendPackageWithAck(
        ZRpc serverRpc,
        string rpcName,
        Func<ZPackage> packageFactory,
        string requestId,
        AttachmentAckStage stage,
        int chunkIndex,
        RemoteUploadResult result)
    {
        string ackKey = GetAckKey(requestId, stage, chunkIndex);
        for (int attempt = 1; attempt <= RemoteAttachmentMaxRetries; ++attempt)
        {
            if (!serverRpc.IsConnected())
            {
                result.Reason = "server connection is closed";
                yield break;
            }

            AttachmentAckWaitState waitState = new(requestId, stage, chunkIndex);
            m_remoteAttachmentAckWaits[ackKey] = waitState;
            serverRpc.Invoke(rpcName, packageFactory());

            float deadline = Time.realtimeSinceStartup + RemoteAttachmentAckTimeoutSeconds;
            while (!waitState.Received &&
                   serverRpc.IsConnected() &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (waitState.Received && waitState.Success)
            {
                result.Success = true;
                result.Code = waitState.Code;
                result.Reason = waitState.Reason;
                m_remoteAttachmentAckWaits.Remove(ackKey);
                yield break;
            }

            string failureReason = waitState.Received
                ? waitState.Reason
                : $"ACK timed out after {RemoteAttachmentAckTimeoutSeconds:0.##} seconds";
            result.Code = waitState.Code;
            result.Reason = failureReason;
            DiscordBotPlugin.LogWarning(
                $"Webhook attachment {stage} " +
                $"{(chunkIndex >= 0 ? chunkIndex.ToString() : string.Empty)} " +
                $"attempt {attempt}/{RemoteAttachmentMaxRetries} failed: {failureReason}");
            m_remoteAttachmentAckWaits.Remove(ackKey);
            if (attempt < RemoteAttachmentMaxRetries)
            {
                yield return new WaitForSecondsRealtime(RemoteAttachmentRetryDelaySeconds * attempt);
            }
        }
    }

    private static string GetAckKey(string requestId, AttachmentAckStage stage, int chunkIndex)
    {
        return $"{requestId}:{(int)stage}:{chunkIndex}";
    }

    private static void RPC_WebhookAttachmentAck(ZRpc rpc, ZPackage package)
    {
        if (isServer || instance == null) return;
        try
        {
            int protocolVersion = package.ReadInt();
            string requestId = package.ReadString();
            int stageValue = package.ReadInt();
            int chunkIndex = package.ReadInt();
            bool success = package.ReadBool();
            int codeValue = package.ReadInt();
            string reason = package.ReadString();
            if (protocolVersion != WebhookBrokerProtocolVersion ||
                !Enum.IsDefined(typeof(AttachmentAckStage), stageValue) ||
                !Enum.IsDefined(typeof(AttachmentAckCode), codeValue))
            {
                return;
            }

            AttachmentAckStage stage = (AttachmentAckStage)stageValue;
            string ackKey = GetAckKey(requestId, stage, chunkIndex);
            if (!instance.m_remoteAttachmentAckWaits.TryGetValue(
                    ackKey,
                    out AttachmentAckWaitState? waitState) ||
                !string.Equals(waitState.RequestId, requestId, StringComparison.Ordinal) ||
                waitState.Stage != stage ||
                waitState.ChunkIndex != chunkIndex)
            {
                return;
            }

            waitState.Success = success;
            waitState.Code = (AttachmentAckCode)codeValue;
            waitState.Reason = SanitizeTransferLabel(reason);
            waitState.Received = true;
        }
        catch (Exception ex)
        {
            DiscordBotPlugin.LogWarning(
                $"Rejected malformed webhook attachment acknowledgement: {ex.Message}");
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
        string requestId = string.Empty;

        try
        {
            int protocolVersion = package.ReadInt();
            requestId = package.ReadString();
            int webhookValue = package.ReadInt();
            int routeValue = package.ReadInt();
            string json = package.ReadString();
            string mimeType = package.ReadString();
            string filename = Path.GetFileName(package.ReadString());
            string transferLabel = SanitizeTransferLabel(package.ReadString());
            int totalLength = package.ReadInt();
            int chunkCount = package.ReadInt();

            if (!IsValidRequestId(requestId) ||
                totalLength <= 0 ||
                totalLength > RemoteAttachmentSafetyBytes ||
                chunkCount <= 0 ||
                chunkCount > MaxRemoteAttachmentChunks ||
                chunkCount !=
                (totalLength + RemoteAttachmentChunkBytes - 1) / RemoteAttachmentChunkBytes ||
                transferLabel.Length == 0)
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, false, "invalid attachment metadata");
                return;
            }

            if (IsCompletedRemoteTransfer(rpc, requestId))
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, true, AttachmentAckCode.AlreadyCompleted, "already completed");
                return;
            }

            if (RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? existing))
            {
                if (string.Equals(existing.RequestId, requestId, StringComparison.Ordinal))
                {
                    SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, true, AttachmentAckCode.Accepted, "transfer already accepted");
                }
                else
                {
                    SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, false, "another attachment transfer is active");
                }
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
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, false, "webhook metadata validation failed");
                return;
            }

            if (RemoteWebhookTransfers.Count >= MaxConcurrentRemoteTransfers)
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, false, "server attachment capacity is full");
                return;
            }

            long activeTransferBytes = RemoteWebhookTransfers.Values.Sum(transfer => (long)transfer.Buffer.Length);
            if (activeTransferBytes + totalLength > MaxConcurrentRemoteTransferBytes)
            {
                SendAttachmentAck(
                    rpc,
                    requestId,
                    AttachmentAckStage.Start,
                    -1,
                    false,
                    $"server attachment memory limit would be exceeded ({SizeFormatter.FormatBytes((int)activeTransferBytes)} active)");
                return;
            }

            if (!IsAllowedAttachmentMetadata(webhook, mimeType, filename))
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, false, "attachment type or filename is not allowed");
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
                transferLabel,
                totalLength,
                chunkCount);

            DiscordBotPlugin.LogInfo(
                $"Accepted {transferLabel} transfer from a client: " +
                $"{chunkCount} acknowledged chunks, {SizeFormatter.FormatBytes(totalLength)}");
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, true, AttachmentAckCode.Accepted, "accepted");
        }
        catch (Exception ex)
        {
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Start, -1, false, "malformed attachment start package");
            DiscordBotPlugin.LogWarning($"Rejected malformed webhook attachment start package: {ex.Message}");
        }
    }

    private static void RPC_WebhookAttachmentChunk(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, MaxRemoteChunkPackageBytes)) return;
        CleanupExpiredRemoteTransfers();
        string requestId = string.Empty;
        int chunkIndex = -1;

        try
        {
            int protocolVersion = package.ReadInt();
            requestId = package.ReadString();
            chunkIndex = package.ReadInt();
            byte[] chunk = package.ReadByteArray();

            if (protocolVersion != WebhookBrokerProtocolVersion ||
                !RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? transfer) ||
                !string.Equals(transfer.RequestId, requestId, StringComparison.Ordinal) ||
                chunkIndex < 0 ||
                chunkIndex >= transfer.ReceivedChunks.Length)
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Chunk, chunkIndex, false, "unknown transfer or chunk index");
                return;
            }

            int offset = chunkIndex * RemoteAttachmentChunkBytes;
            int expectedLength = Math.Min(RemoteAttachmentChunkBytes, transfer.Buffer.Length - offset);
            if (chunk.Length != expectedLength)
            {
                AbortRemoteTransfer(rpc, $"Rejected {transfer.TransferLabel} chunk {chunkIndex + 1}: invalid length");
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Chunk, chunkIndex, false, "chunk length was invalid");
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
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Chunk, chunkIndex, true, AttachmentAckCode.Acknowledged, "acknowledged");
        }
        catch (Exception ex)
        {
            AbortRemoteTransfer(rpc, $"Rejected malformed webhook attachment chunk: {ex.Message}");
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Chunk, chunkIndex, false, "malformed chunk package");
        }
    }

    private static void RPC_WebhookAttachmentComplete(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, 1024)) return;
        CleanupExpiredRemoteTransfers();
        string requestId = string.Empty;

        try
        {
            int protocolVersion = package.ReadInt();
            requestId = package.ReadString();

            if (protocolVersion != WebhookBrokerProtocolVersion)
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Complete, -1, false, "unsupported transport protocol");
                return;
            }

            if (!RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? transfer) ||
                !string.Equals(transfer.RequestId, requestId, StringComparison.Ordinal))
            {
                bool completed = IsCompletedRemoteTransfer(rpc, requestId);
                SendAttachmentAck(
                    rpc,
                    requestId,
                    AttachmentAckStage.Complete,
                    -1,
                    completed,
                    completed ? AttachmentAckCode.AlreadyCompleted : AttachmentAckCode.Rejected,
                    completed ? "already completed" : "unknown transfer");
                return;
            }

            RemoteWebhookTransfers.Remove(rpc);
            if (transfer.ReceivedChunkCount != transfer.ReceivedChunks.Length ||
                transfer.ReceivedBytes != transfer.Buffer.Length)
            {
                string reason =
                    $"incomplete transfer: received " +
                    $"{transfer.ReceivedChunkCount}/{transfer.ReceivedChunks.Length} chunks and " +
                    $"{SizeFormatter.FormatBytes(transfer.ReceivedBytes)}/{SizeFormatter.FormatBytes(transfer.Buffer.Length)}";
                DiscordBotPlugin.LogWarning($"Rejected {transfer.TransferLabel}: {reason}");
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Complete, -1, false, reason);
                return;
            }

            if (!IsAllowedAttachment(transfer.Webhook, transfer.MimeType, transfer.Filename, transfer.Buffer))
            {
                DiscordBotPlugin.LogWarning($"Rejected {transfer.TransferLabel}: image signature validation failed");
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Complete, -1, false, "image signature validation failed");
                return;
            }

            RememberCompletedRemoteTransfer(rpc, requestId);
            instance!.StartCoroutine(
                instance.SendAttachmentToMultipleHooks(
                    transfer.Data,
                    transfer.Targets,
                    transfer.Buffer,
                    transfer.Filename,
                    transfer.MimeType));
            DiscordBotPlugin.LogInfo(
                $"Completed {transfer.TransferLabel} transfer from a client: " +
                $"{SizeFormatter.FormatBytes(transfer.Buffer.Length)}");
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Complete, -1, true, AttachmentAckCode.Completed, "completed");
        }
        catch (Exception ex)
        {
            AbortRemoteTransfer(rpc, $"Rejected malformed webhook attachment completion: {ex.Message}");
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Complete, -1, false, "malformed completion package");
        }
    }

    private static void RPC_WebhookAttachmentAbort(ZRpc rpc, ZPackage package)
    {
        if (!CanAcceptRemoteWebhookPackage(rpc, package, 2048)) return;
        CleanupExpiredRemoteTransfers();
        string requestId = string.Empty;

        try
        {
            int protocolVersion = package.ReadInt();
            requestId = package.ReadString();
            string reason = SanitizeTransferLabel(package.ReadString());
            if (protocolVersion != WebhookBrokerProtocolVersion || !IsValidRequestId(requestId))
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Abort, -1, false, "invalid abort request");
                return;
            }

            if (IsCompletedRemoteTransfer(rpc, requestId))
            {
                SendAttachmentAck(rpc, requestId, AttachmentAckStage.Abort, -1, true, AttachmentAckCode.AlreadyCompleted, "already completed");
                return;
            }

            if (RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? transfer) &&
                string.Equals(transfer.RequestId, requestId, StringComparison.Ordinal))
            {
                RemoteWebhookTransfers.Remove(rpc);
                DiscordBotPlugin.LogWarning($"Aborted {transfer.TransferLabel} transfer from a client: {reason}");
            }

            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Abort, -1, true, AttachmentAckCode.Aborted, "aborted");
        }
        catch (Exception ex)
        {
            SendAttachmentAck(rpc, requestId, AttachmentAckStage.Abort, -1, false, "malformed abort package");
            DiscordBotPlugin.LogWarning($"Rejected malformed webhook attachment abort package: {ex.Message}");
        }
    }

    private static void SendAttachmentAck(
        ZRpc rpc,
        string requestId,
        AttachmentAckStage stage,
        int chunkIndex,
        bool success,
        string reason)
    {
        SendAttachmentAck(
            rpc,
            requestId,
            stage,
            chunkIndex,
            success,
            success ? AttachmentAckCode.Acknowledged : AttachmentAckCode.Rejected,
            reason);
    }

    private static void SendAttachmentAck(
        ZRpc rpc,
        string requestId,
        AttachmentAckStage stage,
        int chunkIndex,
        bool success,
        AttachmentAckCode code,
        string reason)
    {
        if (!rpc.IsConnected() || !IsValidRequestId(requestId)) return;
        ZPackage package = new();
        package.Write(WebhookBrokerProtocolVersion);
        package.Write(requestId);
        package.Write((int)stage);
        package.Write(chunkIndex);
        package.Write(success);
        package.Write((int)code);
        package.Write(SanitizeTransferLabel(reason));
        rpc.Invoke(nameof(RPC_WebhookAttachmentAck), package);
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

    private static void AbortRemoteTransfer(ZRpc rpc, string reason)
    {
        RemoteWebhookTransfers.Remove(rpc);
        DiscordBotPlugin.LogWarning(reason);
    }

    private static void RememberCompletedRemoteTransfer(ZRpc rpc, string requestId)
    {
        if (!RemoteWebhookCompletedTransfers.TryGetValue(
                rpc,
                out Dictionary<string, float>? completed))
        {
            completed = new Dictionary<string, float>(StringComparer.Ordinal);
            RemoteWebhookCompletedTransfers[rpc] = completed;
        }

        completed[requestId] = Time.realtimeSinceStartup + RemoteCompletedTransferRetentionSeconds;
    }

    private static bool IsCompletedRemoteTransfer(ZRpc rpc, string requestId)
    {
        if (!IsValidRequestId(requestId) ||
            !RemoteWebhookCompletedTransfers.TryGetValue(
                rpc,
                out Dictionary<string, float>? completed) ||
            !completed.TryGetValue(requestId, out float expiry))
        {
            return false;
        }

        if (Time.realtimeSinceStartup <= expiry) return true;
        completed.Remove(requestId);
        if (completed.Count == 0)
        {
            RemoteWebhookCompletedTransfers.Remove(rpc);
        }
        return false;
    }

    private static void CleanupExpiredRemoteTransfers()
    {
        float now = Time.realtimeSinceStartup;
        foreach (ZRpc rpc in RemoteWebhookTransfers
                     .Where(pair =>
                         !pair.Key.IsConnected() ||
                         now - pair.Value.LastActivity > RemoteTransferTimeoutSeconds)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            if (RemoteWebhookTransfers.TryGetValue(rpc, out RemoteWebhookTransfer? transfer))
            {
                DiscordBotPlugin.LogWarning(
                    $"Discarded expired {transfer.TransferLabel} transfer after " +
                    $"{RemoteTransferTimeoutSeconds:0} seconds without progress");
            }
            RemoteWebhookTransfers.Remove(rpc);
        }

        foreach (ZRpc rpc in RemoteWebhookCompletedTransfers.Keys.ToList())
        {
            Dictionary<string, float> completed = RemoteWebhookCompletedTransfers[rpc];
            foreach (string requestId in completed
                         .Where(pair => !rpc.IsConnected() || now > pair.Value)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                completed.Remove(requestId);
            }
            if (completed.Count == 0)
            {
                RemoteWebhookCompletedTransfers.Remove(rpc);
            }
        }
    }

    private static string SanitizeTransferLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "attachment";
        string sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 128 ? sanitized : sanitized.Substring(0, 128);
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

