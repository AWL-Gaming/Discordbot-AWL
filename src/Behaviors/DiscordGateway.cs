using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace DiscordBot;

public class DiscordGatewayClient : MonoBehaviour
{
    private static readonly JsonSerializerSettings settings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        Error = (_, args) => args.ErrorContext.Handled = true,
    };

    public static DiscordGatewayClient? instance;
    public event Action<Message>? OnChatReceived;
    public event Action<Message>? OnCommandReceived;
    public event Action<string>? OnError;
    public event Action<string>? OnLog;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnClosed;

    private const int BUFFER_SIZE = 16384; // 16kb
    private ClientWebSocket? websocket;
    private CancellationTokenSource? cancellationTokenSource;

    private string? gatewayUrl;
    private readonly GatewayRecoveryState gatewayState = new();
    private Coroutine? heartbeatCoroutine;
    private Coroutine? reconnectCoroutine;
    private Coroutine? listenCoroutine;
    private Coroutine? messageQueueCoroutine;
    
    private bool isConnecting;
    private bool isConnected;
    private bool shouldReconnect = true;

    private readonly PriorityQueue<object> messageQueue = new();
    private bool isSending;
    
    private static readonly WaitForSeconds processMessageRate = new (0.1f);
    private static readonly WaitForSeconds retryConnectionDelay = new(5f);
    public void Awake()
    {
        instance = this;
        OnChatReceived += HandleChatMessage;
        OnCommandReceived += HandleCommands;
        OnError += HandleError;
        OnLog += HandleLog;
        OnClosed += DiscordBotPlugin.LogDebug;
        OnConnected += () => DiscordBotPlugin.LogDebug("Connected to discord gateway");
        OnDisconnected += () => DiscordBotPlugin.LogDebug("Disconnected from discord gateway");
        
        DiscordBotPlugin.LogDebug("Initializing Discord Gateway");
    }

    private void Start()
    {
        if (string.IsNullOrEmpty(DiscordBotPlugin.BOT_TOKEN))
        {
            DiscordBotPlugin.LogWarning("Bot token not set");
            return;
        }
        StartCoroutine(InitializeGateway());
    }
    private void OnDestroy()
    {
        shouldReconnect = false;
        StopAllCoroutines();
        DisconnectWebSocket();
        gatewayState.ResetForTokenChange();
        instance = null;
    }

    public void RestartForTokenChange()
    {
        StopAllCoroutines();
        reconnectCoroutine = null;
        gatewayState.ResetForTokenChange();
        DisconnectWebSocket();
        gatewayUrl = null;
        isConnecting = false;
        shouldReconnect = true;

        if (string.IsNullOrWhiteSpace(DiscordBotPlugin.BOT_TOKEN))
        {
            DiscordBotPlugin.LogWarning("Bot token not set");
            return;
        }

        StartCoroutine(InitializeGateway());
    }
    private static void HandleError(string message)
    {
        DiscordBotPlugin.LogError(message);
    }

    private static void HandleLog(string message) => DiscordBotPlugin.records.Log(LogLevel.Info, message);
    
    private static void HandleChatMessage(Message message)
    {
        if (!DiscordBotPlugin.DiscordToGameChat) return;
        instance?.OnLog?.Invoke($"Received discord chat message: username: {message.author?.username ?? "null"} - content: {message.content ?? "null"}");
        var content = message.content ?? "";
        
        Discord.instance?.BroadcastMessage(message.author?.GetDisplayName() ?? "", RemoveMentions(content));
    }
    
    private static string RemoveMentions(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;
    
        // Only removes Discord user mentions: <@userid> or <@!userid>
        // Won't match general text with < and >
        return System.Text.RegularExpressions.Regex.Replace(
            content, 
            @"<@!?\d+>", 
            ""
        ).Trim();
    }
    
    private static void HandleCommands(Message message)
    {
        instance?.OnLog?.Invoke($"Received discord command: username: {message.author?.username ?? "null"} - content: {message.content ?? "null"}");
        if (message.content == null) return;
        string[] args = message.content.Split(' ');
        string command = args[0].Trim();
        
        if (!DiscordCommands.m_commands.TryGetValue(command, out DiscordCommands.DiscordCommand discordCommand))
        {
            Discord.instance?.SendMessage(Webhook.Commands, ZNet.instance.GetWorldName(), "Failed to find command: " + command);
        }
        else if (!discordCommand.IsAllowed(message.author?.GetFullUsername() ?? ""))
        {
            Discord.instance?.SendMessage(Webhook.Commands, ZNet.instance.GetWorldName(), message.author?.GetFullUsername() + " not allowed to use command: " + command);
        }
        else
        {
            discordCommand.Run(args, message.author?.GetDisplayName());
        }
    }

    public void TriggerProcessing()
    {
        if (isSending || !isConnected || messageQueue.Count <= 0) return;
        isSending = true;
        messageQueueCoroutine = StartCoroutine(ProcessMessageQueue());
    }

    private IEnumerator ProcessMessageQueue()
    {
        while (messageQueue.Count > 0 && isConnected)
        {
            object message = messageQueue.Dequeue();
            yield return SendGatewayMessage(message);
            yield return processMessageRate;
        }

        isSending = false;
        messageQueueCoroutine = null;
    }

    #region Gateway Initialization
    private IEnumerator InitializeGateway()
    {
        if (isConnecting || isConnected || !shouldReconnect) yield break;
        isConnecting = true;
        using UnityWebRequest request = UnityWebRequest.Get("https://discord.com/api/v10/gateway/bot");
        request.SetRequestHeader("Authorization", $"Bot {DiscordBotPlugin.BOT_TOKEN.Trim()}");
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();
        if (!shouldReconnect)
        {
            isConnecting = false;
            yield break;
        }

        if (request.responseCode == 401 || request.responseCode == 403)
        {
            shouldReconnect = false;
            isConnecting = false;
            OnError?.Invoke($"Discord rejected the configured bot token (HTTP {request.responseCode}). Check [5 - Setup] BOT TOKEN. Gateway reconnect is paused until the token changes.");
            yield break;
        }

        if (request.result == UnityWebRequest.Result.Success)
        {
            try
            {
                gatewayUrl = FormatGatewayUrl(JObject.Parse(request.downloadHandler.text)["url"]?.ToString() ?? "");
            }
            catch (Exception e)
            {
                OnError?.Invoke($"Failed to read gateway URL: {e.Message}");
                gatewayUrl = null;
            }

            if (!string.IsNullOrEmpty(gatewayUrl))
            {
                yield return StartCoroutine(ConnectToGateway());
                yield break;
            }

            OnError?.Invoke("Failed to get gateway URL");
        }
        else
        {
            OnError?.Invoke($"Failed to get gateway URL: {request.error}");
        }

        isConnecting = false;
        RequestReconnect();
    }

    private static string? FormatGatewayUrl(string? url)
    {
        if (url == null || url.Trim().Length == 0) return null;
        return url.TrimEnd('/') + "/?v=10&encoding=json";
    }

    #endregion
    
    #region WebSocket Connection
    
    private IEnumerator ConnectToGateway()
    {
        var socket = new ClientWebSocket();
        var tokenSource = new CancellationTokenSource();
        websocket = socket;
        cancellationTokenSource = tokenSource;

        Task connectTask;
        try
        {
            connectTask = socket.ConnectAsync(new Uri(gatewayUrl ?? ""), tokenSource.Token);
        }
        catch (Exception e)
        {
            OnError?.Invoke($"Failed to connect: {e.Message}");
            isConnecting = false;
            RequestReconnect();
            yield break;
        }

        yield return new WaitUntil(() => connectTask.IsCompleted);
        if (!ReferenceEquals(websocket, socket)) yield break;

        switch (connectTask.Status)
        {
            case TaskStatus.RanToCompletion:
                isConnected = true;
                isConnecting = false;
                listenCoroutine = StartCoroutine(ListenForMessages(socket, tokenSource.Token));
                break;
            case TaskStatus.Canceled:
                OnClosed?.Invoke("Connection task canceled");
                isConnecting = false;
                RequestReconnect();
                break;
            case TaskStatus.Faulted:
                OnError?.Invoke($"Failed to connect: {connectTask.Exception?.GetBaseException().Message}");
                isConnecting = false;
                RequestReconnect();
                break;
            default:
                OnError?.Invoke("Failed to connect to Discord gateway");
                isConnecting = false;
                RequestReconnect();
                break;
        }
    }

    private IEnumerator ListenForMessages(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BUFFER_SIZE];

        while (isConnected && ReferenceEquals(websocket, socket) && socket.State == WebSocketState.Open)
        {
            Task<WebSocketReceiveResult> receiveTask;
            try
            {
                receiveTask = socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            }
            catch (Exception e)
            {
                OnError?.Invoke($"WebSocket receive error: {e.Message}");
                OnWebSocketClose();
                yield break;
            }

            yield return new WaitUntil(() => receiveTask.IsCompleted);
            if (!isConnected || !ReferenceEquals(websocket, socket)) yield break;

            if (receiveTask.Status == TaskStatus.RanToCompletion)
            {
                WebSocketReceiveResult result = receiveTask.Result;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        GatewayEvent? payload = JsonConvert.DeserializeObject<GatewayEvent>(message, settings);
                        if (payload != null) HandleGatewayPayload(payload);
                    }
                    catch (Exception e)
                    {
                        OnError?.Invoke($"Failed to process gateway message: {e.Message}");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus.HasValue)
                    {
                        int closeCode = (int)result.CloseStatus.Value;
                        if (closeCode is 4007 or 4009) gatewayState.InvalidateSession();
                        if (closeCode is 4004 or 4010 or 4011 or 4012 or 4013 or 4014)
                        {
                            shouldReconnect = false;
                            OnError?.Invoke($"Discord gateway rejected the connection (close code {closeCode}). Reconnect is paused until the bot configuration changes.");
                        }
                    }
                    OnClosed?.Invoke("WebSocket closed by server");
                    OnWebSocketClose();
                    yield break;
                }
            }
            else if (receiveTask.Status == TaskStatus.Canceled)
            {
                if (shouldReconnect) OnWebSocketClose();
                yield break;
            }
            else
            {
                OnError?.Invoke($"WebSocket receive error: {receiveTask.Exception?.GetBaseException().Message}");
                OnWebSocketClose();
                yield break;
            }
        }

        if (isConnected && ReferenceEquals(websocket, socket)) OnWebSocketClose();
    }

    private void OnWebSocketClose()
    {
        OnClosed?.Invoke("WebSocket connection closed");
        DisconnectWebSocket();
        RequestReconnect();
    }

    #endregion
    
    #region Gateway Protocol Handling
    [Serializable]
    public class GatewayEvent
    {
        public int op;
        public JToken? d;
        public int? s;
        public string? t;
    }
    
    [UsedImplicitly]
    public enum Opcodes
    {
        Dispatch = 0,
        Heartbeat = 1,
        Identify = 2,
        PresenceUpdate = 3,
        VoiceStateUpdate = 4,
        Resume = 6,
        Reconnect = 7,
        RequestGuildMembers = 8,
        InvalidSession = 9,
        Hello = 10,
        HeartbeatOK = 11,
        RequestSoundboardSounds = 31
    }
    
    private void HandleGatewayPayload(GatewayEvent? payload)
    {
        if (payload == null) return;
        gatewayState.OnDispatchSequence(payload.s);

        switch ((Opcodes)payload.op)
        {
            case Opcodes.Dispatch:
                HandleDispatchEvent(payload.t, payload.d as JObject);
                break;
            case Opcodes.Heartbeat:
                SendHeartbeat();
                break;
            case Opcodes.Reconnect:
                RequestReconnect();
                break;
            case Opcodes.InvalidSession:
                bool resumable = payload.d?.Type == JTokenType.Boolean && payload.d.Value<bool>();
                OnLog?.Invoke(resumable ? "Discord gateway requested a session resume" : "Discord gateway session expired, reconnecting");
                RequestReconnect(invalidateSession: !resumable);
                break;
            case Opcodes.Hello:
                HandleHello(payload.d as JObject);
                break;
            case Opcodes.HeartbeatOK:
                gatewayState.OnHeartbeatAck();
                break;
        }
    }

    private void HandleDispatchEvent(string? eventType, JObject? data)
    {
        if (data == null) return;
        switch (eventType)
        {
            case "READY":
                HandleReady(data);
                break;
            case "MESSAGE_CREATE":
                HandleMessageCreate(data);
                break;
            case "RESUMED":
                OnConnected?.Invoke();
                break;
        }
    }
    
    private void HandleHello(JObject? data)
    {
        if (data == null) return;
        gatewayState.OnHello();
        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        heartbeatCoroutine = StartCoroutine(HeartbeatLoop((data["heartbeat_interval"]?.Value<int>() ?? 41250) / 1000f));
        if (gatewayState.CanResume) SendResume();
        else SendIdentify();
    }

    private void HandleReady(JToken data)
    {
        gatewayState.OnReady(data["session_id"]?.Value<string>(), data["resume_gateway_url"]?.Value<string>());
        OnConnected?.Invoke();
    }

    private void HandleMessageCreate(JObject? data)
    {
        if (data == null) return;
        try
        {
            Message? message = JsonConvert.DeserializeObject<Message>(data.ToString(), settings);
            if (message == null) return;
            if (message.author?.bot == true || string.IsNullOrEmpty(message.content)) return;

            if (Channel.Commands.ToID() == Channel.Chat.ToID() && message.channel_id == Channel.Commands.ToID())
            {
                string[] args = message.content!.Split(' ');
                string command = args[0].Trim();
                if (DiscordCommands.IsCommand(command))
                {
                    OnCommandReceived?.Invoke(message);
                }
                else
                {
                    OnChatReceived?.Invoke(message);
                }
            }
            else
            {
                if (message.channel_id == Channel.Commands.ToID())
                {
                    OnCommandReceived?.Invoke(message);
                }
                else if (message.channel_id == Channel.Chat.ToID())
                {
                    OnChatReceived?.Invoke(message);
                }
            }
            
        }
        catch (Exception e)
        {
            OnError?.Invoke($"Failed to parse message: {e.Message}");
        }
    }
    
    #endregion
    
    #region Gateway Commands

    [UsedImplicitly]
    public enum Intent
    {
        Guilds = 1,
        GuildMessages = 512,
        MessageContent = 32768
    }
    
    private void SendIdentify()
    {
        var identify = new
        {
            op = (int)Opcodes.Identify,
            d = new
            {
                token = DiscordBotPlugin.BOT_TOKEN,
                intents = (int)Intent.Guilds + (int)Intent.GuildMessages + (int)Intent.MessageContent,
                properties = new
                {
                    os = "unity",
                    browser = "unity-bot",
                    device = "unity-bot"
                }
            }
        };
        messageQueue.Enqueue(identify, true);
    }
    
    private void SendResume()
    {
        var resume = new
        {
            op = (int)Opcodes.Resume,
            d = new
            {
                token = DiscordBotPlugin.BOT_TOKEN,
                session_id = gatewayState.SessionId,
                seq = gatewayState.LastSequence
            }
        };
        messageQueue.Enqueue(resume, true);
    }

    private void SendHeartbeat()
    {
        if (!isConnected) return;
        gatewayState.OnHeartbeatSent();

        var heartbeat = new
        {
            op = (int)Opcodes.Heartbeat,
            d = gatewayState.LastSequence
        };
        messageQueue.Enqueue(heartbeat, true);
    }

    private IEnumerator SendGatewayMessage(object payload)
    {
        if (websocket?.State != WebSocketState.Open || cancellationTokenSource == null) yield break;
        string json = JsonConvert.SerializeObject(payload);
        byte[] data = Encoding.UTF8.GetBytes(json);
            
        Task sendTask = websocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, cancellationTokenSource.Token);
            
        yield return new WaitUntil(() => sendTask.IsCompleted);
            
        if (sendTask.Status == TaskStatus.Faulted)
        {
            OnError?.Invoke($"Failed to send gateway message: {sendTask.Exception?.Message}");
        }
    }
    
    #endregion
    
    #region Heartbeat System
    
    private IEnumerator HeartbeatLoop(float intervalSeconds)
    {
        yield return new WaitForSeconds(UnityEngine.Random.Range(0f, intervalSeconds));

        while (isConnected)
        {
            if (!gatewayState.HeartbeatAcknowledged)
            {
                OnError?.Invoke("Heartbeat not acknowledged, reconnecting...");
                RequestReconnect();
                yield break;
            }

            SendHeartbeat();
            yield return new WaitForSeconds(intervalSeconds);
        }
    }

    #endregion
    
    #region Reconnection
    
    private void RequestReconnect(bool invalidateSession = false)
    {
        if (!shouldReconnect)
        {
            if (invalidateSession) gatewayState.InvalidateSession();
            return;
        }
        if (!gatewayState.TryBeginReconnect(invalidateSession)) return;
        reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
    }

    private IEnumerator ReconnectAfterDelay()
    {
        DisconnectWebSocket();

        if (!shouldReconnect)
        {
            gatewayState.EndReconnect();
            reconnectCoroutine = null;
            yield break;
        }

        OnError?.Invoke("Attempting to reconnect in 5 seconds...");
        yield return retryConnectionDelay;

        gatewayState.EndReconnect();
        reconnectCoroutine = null;
        if (!shouldReconnect) yield break;

        if (gatewayState.CanResume && !string.IsNullOrWhiteSpace(gatewayState.ResumeGatewayUrl))
        {
            gatewayUrl = FormatGatewayUrl(gatewayState.ResumeGatewayUrl);
            isConnecting = true;
            yield return StartCoroutine(ConnectToGateway());
        }
        else
        {
            yield return StartCoroutine(InitializeGateway());
        }
    }

    private void DisconnectWebSocket()
    {
        bool wasConnected = isConnected;
        isConnected = false;
        isConnecting = false;
        gatewayState.OnHello();
        messageQueue.Clear();
        isSending = false;

        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }
        if (listenCoroutine != null)
        {
            StopCoroutine(listenCoroutine);
            listenCoroutine = null;
        }
        if (messageQueueCoroutine != null)
        {
            StopCoroutine(messageQueueCoroutine);
            messageQueueCoroutine = null;
        }

        CancellationTokenSource? tokenSource = cancellationTokenSource;
        cancellationTokenSource = null;
        if (tokenSource != null)
        {
            tokenSource.Cancel();
            tokenSource.Dispose();
        }

        ClientWebSocket? socket = websocket;
        websocket = null;
        if (socket != null)
        {
            socket.Abort();
            socket.Dispose();
        }

        if (wasConnected) OnDisconnected?.Invoke();
    }

    #endregion
    
    private class PriorityQueue<T>
    {
        private readonly Queue<T> highPriority = new Queue<T>();
        private readonly Queue<T> normalPriority = new Queue<T>();
    
        public void Enqueue(T item, bool priority = false)
        {
            if (!instance?.isConnected ?? false) return;
            if (priority) highPriority.Enqueue(item);
            else normalPriority.Enqueue(item);
            instance?.TriggerProcessing();
        }
    
        public T Dequeue()
        {
            return highPriority.Count > 0 ? highPriority.Dequeue() : normalPriority.Dequeue();
        }
    
        public int Count => highPriority.Count + normalPriority.Count;
        public void Clear() { highPriority.Clear(); normalPriority.Clear(); }
    }
}

[Serializable]
public class Message
{
    public string? timestamp;
    public string? id;
    public string? channel_id;
    public Author? author;
    public string? content;
}

[Serializable]
public class Author
{
    public string? id;
    public string? username;
    public string? global_name;
    public string? discriminator;
    public bool bot;
    public string? avatar;
    
    public string GetDisplayName() => (!string.IsNullOrEmpty(global_name) ? global_name : username) ?? string.Empty;
    public string GetFullUsername()
    {
        return !string.IsNullOrEmpty(discriminator) && discriminator != "0"
            ? $"{username}#{discriminator}"
            : username ?? string.Empty;
    }
}

