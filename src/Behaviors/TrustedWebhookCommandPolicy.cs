using System;
using System.Collections.Generic;

namespace DiscordBot;

// Only Discord Gateway message metadata is trusted. Usernames and message content
// must never grant command privileges to webhook messages.
internal sealed class TrustedWebhookCommandPolicy
{
    private const int MaxAnnouncementLength = 200;
    private const int MaxRememberedMessageIds = 1024;
    private static readonly TimeSpan MessageCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SaveCooldown = TimeSpan.FromMinutes(2);

    private readonly HashSet<string> seenMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> seenMessageOrder = new();
    private readonly Dictionary<string, DateTime> lastExecutionByCommand = new(StringComparer.Ordinal);

    public bool TryAuthorize(
        bool isServer,
        bool enabled,
        string? trustedWebhooks,
        string? allowedCommands,
        string? configuredCommandChannel,
        string? webhookId,
        string? authorId,
        string? guildId,
        string? channelId,
        string? messageId,
        int? messageType,
        string? content,
        DateTime nowUtc,
        out string[] arguments)
    {
        arguments = Array.Empty<string>();

        if (!isServer || !enabled ||
            !IsSnowflake(webhookId) || !IsSnowflake(authorId) ||
            !IsSnowflake(guildId) || !IsSnowflake(channelId) ||
            !IsSnowflake(messageId) || messageType != 0 ||
            !string.Equals(authorId, webhookId, StringComparison.Ordinal) ||
            !string.Equals(configuredCommandChannel, channelId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadTrustedWebhooks(trustedWebhooks, out var origins) ||
            !origins.TryGetValue(webhookId!, out var origin) ||
            !string.Equals(origin.Channel, channelId, StringComparison.Ordinal) ||
            !string.Equals(origin.Guild, guildId, StringComparison.Ordinal) ||
            !TryReadCommands(allowedCommands, out var commands) ||
            !TryParseCommand(content, out var command, out var body) ||
            !commands.Contains(command) ||
            seenMessageIds.Contains(messageId!))
        {
            return false;
        }

        TimeSpan cooldown = command == "!save" ? SaveCooldown : MessageCooldown;
        if (lastExecutionByCommand.TryGetValue(command, out DateTime previous) &&
            (nowUtc < previous || nowUtc - previous < cooldown))
        {
            return false;
        }

        // The two supported commands are deliberately not executed through human
        // administrator username checks. Authorization is independent and narrower.
        arguments = command == "!save"
            ? new[] { command }
            : new[] { command, body };

        lastExecutionByCommand[command] = nowUtc;
        seenMessageIds.Add(messageId!);
        seenMessageOrder.Enqueue(messageId!);
        while (seenMessageOrder.Count > MaxRememberedMessageIds)
            seenMessageIds.Remove(seenMessageOrder.Dequeue());

        return true;
    }

    private static bool TryParseCommand(string? content, out string command, out string body)
    {
        command = string.Empty;
        body = string.Empty;

        if (content == null || content.Length == 0 || content.Length > 256)
            return false;

        foreach (char character in content)
        {
            if (char.IsControl(character))
                return false;
        }

        if (content == "!save")
        {
            command = "!save";
            return true;
        }

        if (!content.StartsWith("!message ", StringComparison.Ordinal))
            return false;

        body = content.Substring("!message ".Length).Trim();
        if (body.Length == 0 || body.Length > MaxAnnouncementLength ||
            body.IndexOf('<') >= 0 || body.IndexOf('>') >= 0)
        {
            return false;
        }

        command = "!message";
        return true;
    }

    private static bool TryReadCommands(string? setting, out HashSet<string> commands)
    {
        commands = new HashSet<string>(StringComparer.Ordinal);
        if (setting == null || setting.Trim().Length == 0)
            return false;

        foreach (string part in setting.Split(','))
        {
            string command = part.Trim();
            if ((command != "!save" && command != "!message") || !commands.Add(command))
                return false;
        }

        return commands.Count > 0;
    }

    private static bool TryReadTrustedWebhooks(
        string? setting,
        out Dictionary<string, (string Channel, string Guild)> origins)
    {
        origins = new Dictionary<string, (string Channel, string Guild)>(StringComparer.Ordinal);
        if (setting == null || setting.Trim().Length == 0)
            return false;

        foreach (string part in setting.Split(','))
        {
            string[] fields = part.Trim().Split(':');
            if (fields.Length != 3)
                return false;

            string webhookId = fields[0].Trim();
            string channelId = fields[1].Trim();
            string guildId = fields[2].Trim();
            if (!IsSnowflake(webhookId) || !IsSnowflake(channelId) || !IsSnowflake(guildId) ||
                origins.ContainsKey(webhookId))
            {
                return false;
            }

            origins.Add(webhookId, (channelId, guildId));
        }

        return origins.Count > 0;
    }

    private static bool IsSnowflake(string? id)
    {
        if (id == null || id.Length < 16 || id.Length > 20)
            return false;

        foreach (char digit in id)
        {
            if (digit < '0' || digit > '9')
                return false;
        }

        return ulong.TryParse(id, out ulong value) && value != 0;
    }
}