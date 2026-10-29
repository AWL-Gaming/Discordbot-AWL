using DiscordBot;

const string Webhook = "111111111111111111";
const string OtherWebhook = "666666666666666666";
const string Channel = "222222222222222222";
const string Guild = "333333333333333333";
const string Webhooks = Webhook + ":" + Channel + ":" + Guild;
const string Allowed = "!message,!save";
var now = new DateTime(2026, 10, 27, 4, 0, 0, DateTimeKind.Utc);
long nextMessageId = 444444444444444444;
int checks = 0;

void Check(bool condition, string title)
{
    if (!condition) throw new Exception("FAIL: " + title);
    checks++;
}

bool Try(
    TrustedWebhookCommandPolicy policy,
    string? content,
    DateTime time,
    out string[] args,
    bool enabled = true,
    string? webhookId = null,
    string? authorId = null,
    string? guildId = null,
    string? channelId = null,
    string? messageId = null,
    int? type = 0,
    bool isServer = true,
    string? origins = null,
    string? allowlist = null,
    string? commandChannelId = null)
{
    return policy.TryAuthorize(
        isServer,
        enabled,
        origins ?? Webhooks,
        allowlist ?? Allowed,
        commandChannelId ?? Channel,
        webhookId ?? Webhook,
        authorId ?? Webhook,
        guildId ?? Guild,
        channelId ?? Channel,
        messageId ?? (++nextMessageId).ToString(),
        type,
        content,
        time,
        out args);
}

var policy = new TrustedWebhookCommandPolicy();
Check(!Try(policy, "!save", now, out _, enabled: false), "disabled by default");
Check(!Try(policy, "!save", now, out _, origins: ""), "empty trusted origins");
Check(!Try(policy, "!save", now, out _, allowlist: ""), "empty explicit command allowlist");
Check(!Try(policy, "!save", now, out _, isServer: false), "client cannot authorize commands");

Check(Try(policy, "!message Restart in 10 minutes", now, out var announcement, messageId: "555555555555555555"), "authorized announcement");
Check(announcement.Length == 2 && announcement[0] == "!message" &&
      announcement[1] == "Restart in 10 minutes", "announcement arguments preserved");
Check(!Try(policy, "!message Replay", now.AddSeconds(20), out _, messageId: "555555555555555555"), "replay of same ID blocked after cooldown");
Check(!Try(policy, "!message Flood", now.AddSeconds(5), out _), "announcement cooldown");
Check(Try(policy, "!message Restart in 5 minutes", now.AddSeconds(10), out _), "announcement allowed after cooldown");

Check(Try(policy, "!save", now, out var save), "authorized save");
Check(save.Length == 1 && save[0] == "!save", "save arguments strictly empty");
Check(!Try(policy, "!save", now.AddSeconds(119), out _), "save rate limit");
Check(Try(policy, "!save", now.AddSeconds(120), out _), "save allowed after 2 minutes");

// Every negative authorization check uses a fresh policy so it cannot
// accidentally pass because of a previous command cooldown.
bool Denied(
    string? content,
    string? webhookId = null,
    string? authorId = null,
    string? guildId = null,
    string? channelId = null,
    string? messageId = null,
    int? type = 0,
    string? origins = null,
    string? allowlist = null,
    string? commandChannelId = null)
{
    return !Try(new TrustedWebhookCommandPolicy(), content, now, out _,
        webhookId: webhookId,
        authorId: authorId,
        guildId: guildId,
        channelId: channelId,
        messageId: messageId,
        type: type,
        origins: origins,
        allowlist: allowlist,
        commandChannelId: commandChannelId);
}

Check(Denied("!save", webhookId: "888888888888888888"), "unknown webhook ID");
Check(Denied("!save", webhookId: "888888888888888888", authorId: Webhook), "spoofed author cannot override webhook ID");
Check(Denied("!save", authorId: "888888888888888888"), "author and webhook ID mismatch");
Check(Denied("!save", channelId: "888888888888888888"), "wrong channel");
Check(Denied("!save", guildId: "888888888888888888"), "wrong guild");
Check(Denied("!save", commandChannelId: "999999999999999999"), "command channel restriction");
Check(Denied("!save", webhookId: ""), "missing Discord-authenticated webhook ID");
Check(Denied("!save", messageId: ""), "missing Discord message ID");
Check(Denied("!save", type: null), "missing message type");
Check(Denied("!save", type: 19), "reply and interaction message types rejected");
Check(Denied("!addadmin fake"), "webhook cannot add admins");
Check(Denied("!kick player"), "webhook cannot execute arbitrary commands");
Check(Denied("!env Clear"), "webhook cannot change game environment");
Check(Denied("!help"), "webhook cannot enumerate other commands");
Check(Denied("!SAVE"), "case variations fail closed");
Check(Denied("!save anything"), "save has no arguments");
Check(Denied("!save\n!message fake"), "newline command injection blocked");
Check(Denied("!message notice\n!save"), "newline message injection blocked");
Check(Denied("!message text\tother"), "control characters blocked");
Check(Denied("!message <color=red>bad</color>"), "rich text markup blocked");
Check(Denied("!message "), "empty announcement");
Check(Denied("!message " + new string('A', 201)), "oversized announcement");
Check(Denied("!message hello", allowlist: "!save"), "command not listed");
Check(Denied("!save", allowlist: "!message"), "save excluded from allowlist");
Check(Denied("!save", allowlist: "!save,!env"), "unsupported command in config fails closed");
Check(Denied("!save", allowlist: "!save,!save"), "duplicate command in config fails closed");
Check(Denied("!save", origins: Webhooks + ",invalid"), "malformed origin fails closed");
Check(Denied("!save", origins: Webhooks + "," + Webhooks), "duplicate webhook ID fails closed");
Check(Denied("!save", origins: Webhook + ":" + Channel), "missing guild restriction fails closed");
Check(Denied("!save", origins: "+" + Webhooks), "noncanonical snowflake fails closed");
Check(Denied("!save", origins: Webhook + ":" + Channel + ":0"), "invalid guild snowflake fails closed");

var perWebhookPolicy = new TrustedWebhookCommandPolicy();
string bothOrigins = Webhooks + "," + OtherWebhook + ":" + Channel + ":" + Guild;
Check(Try(perWebhookPolicy, "!save", now, out _, origins: bothOrigins), "first configured webhook accepted");
Check(!Try(perWebhookPolicy, "!save", now.AddSeconds(5), out _,
    webhookId: OtherWebhook, authorId: OtherWebhook, origins: bothOrigins), "save cooldown shared across webhooks");
Check(Try(perWebhookPolicy, "!save", now.AddSeconds(120), out _,
    webhookId: OtherWebhook, authorId: OtherWebhook, origins: bothOrigins), "second configured webhook accepted after cooldown");

Console.WriteLine($"PASS: {checks} trusted webhook security and command regression assertions.");
