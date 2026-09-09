using System.Collections.Generic;
using HarmonyLib;
using JetBrains.Annotations;

namespace DiscordBot.Notices;

public static class OnLogin
{
    private static readonly HashSet<ZNetPeer> AnnouncedPeers = new();

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SendPlayerList))]
    private static class ZNet_SendPlayerList_Patch
    {
        [UsedImplicitly]
        private static void Postfix(ZNet __instance)
        {
            if (!__instance.IsServer()) return;

            foreach (var peer in __instance.GetPeers())
            {
                if (!peer.IsReady() || string.IsNullOrWhiteSpace(peer.m_playerName) || !AnnouncedPeers.Add(peer)) continue;
                if (!DiscordBotPlugin.ShowOnLogin) continue;

                var msg = $"{peer.m_playerName} {Keys.HasJoined}";
                if (DiscordBotPlugin.ShowCoordinates)
                {
                    var coordinates = $"{peer.m_refPos.x:0.0}, {peer.m_refPos.y:0.0}, {peer.m_refPos.z:0.0}";
                    var biome = WorldGenerator.instance.GetBiome(peer.m_refPos).ToString();
                    var details = new Dictionary<string, string>() { ["Coordinates"] = coordinates, ["Biome"] = biome };
                    Discord.instance?.SendEvent(Webhook.Notifications, DiscordBotPlugin.OnLoginHooks, msg, ColorExtensions.SoftBlue, details, route: WebhookRoute.Login);
                }
                else
                {
                    Discord.instance?.SendMessage(Webhook.Notifications, message: msg, hooks: DiscordBotPlugin.OnLoginHooks, route: WebhookRoute.Login);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    private static class ZNet_Disconnect_Patch
    {
        [UsedImplicitly]
        private static void Prefix(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer()) AnnouncedPeers.Remove(peer);
        }
    }
}
