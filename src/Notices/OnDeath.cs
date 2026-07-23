using HarmonyLib;
using JetBrains.Annotations;

namespace DiscordBot.Notices;

public static class OnDeath
{
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    private static class Player_OnDeath_Patch
    {
        [UsedImplicitly]
        private static void Prefix(Player __instance)
        {
            if (!DiscordBotPlugin.ShowOnDeath || __instance != Player.m_localPlayer || __instance.m_nview.GetZDO() == null) return;

            string playerName = __instance.GetPlayerName();
            string avatar = "";
            string quip = "";

            if (__instance.m_lastHit is { } hit)
            {
                if (hit.GetAttacker() is { } killer)
                {
                    avatar = Links.GetCreatureIcon(killer.name);
                    quip = DeathQuips.GenerateDeathQuip(playerName, killer.m_name, killer.m_level, killer.IsBoss());
                }
                else
                {
                    quip = DeathQuips.GenerateEnvironmentalQuip(playerName, hit.m_hitType);
                }
            }

            if (string.IsNullOrWhiteSpace(quip))
            {
                quip = DeathQuips.GenerateEnvironmentalQuip(playerName, HitData.HitType.Undefined);
            }

            ChatAI? chatAI = ChatAI.instance;
            bool isGeneratingQuip = DiscordBotPlugin.ImproveDeathQuips && ChatAI.HasKey() && chatAI != null;
            string title = $"{playerName} {Keys.HasDied}";

            if (DiscordBotPlugin.ScreenshotGif)
            {
                Recorder.instance?.StartRecording(title, quip, avatar);
            }
            else if (DiscordBotPlugin.ScreenshotDeath)
            {
                Screenshot.instance?.StartCapture(title, quip, avatar);
            }
            else if (isGeneratingQuip)
            {
                chatAI!.OnDeathQuip = message =>
                {
                    Discord.instance?.SendEmbedMessage(Webhook.DeathFeed, title, message, thumbnail: avatar);
                    string worldName = ZNet.instance?.GetWorldName() ?? "Server";
                    Discord.instance?.Internal_BroadcastMessage(worldName, message, false);
                };
            }
            else
            {
                Discord.instance?.SendEmbedMessage(Webhook.DeathFeed, title, quip, thumbnail: avatar);
                string worldName = ZNet.instance?.GetWorldName() ?? "Server";
                Discord.instance?.Internal_BroadcastMessage(worldName, quip, false);
            }

            if (isGeneratingQuip)
            {
                chatAI!.AskDeathQuip(playerName, quip);
            }
        }
    }
}
