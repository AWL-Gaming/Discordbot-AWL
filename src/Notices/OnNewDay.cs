using HarmonyLib;
using JetBrains.Annotations;

namespace DiscordBot.Notices;

public static class OnNewDay
{
    [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.UpdateTriggers))]
    private static class EnvMan_UpdateTriggers_Patch
    {
        [UsedImplicitly]
        private static void Postfix(EnvMan __instance, float oldDayFraction, float newDayFraction, float dt)
        {
            if (!DiscordBotPlugin.ShowNewDay || !(ZNet.instance?.IsServer() ?? false)) return;
            if (oldDayFraction <= 0.20000000298023224 || oldDayFraction >= 0.25 || newDayFraction <= 0.25 || newDayFraction >= 0.30000001192092896) return;

            int day = __instance.GetCurrentDay();
            string message = DayQuips.GenerateNewDayQuip(day);
            ChatAI? chatAI = ChatAI.instance;

            if (DiscordBotPlugin.ImproveDayQuips && ChatAI.HasKey() && chatAI != null)
            {
                chatAI.AskDayQuip(day, message);
                return;
            }

            Discord.instance?.SendMessage(Webhook.Notifications, message: message, hooks: DiscordBotPlugin.OnNewDayHooks, route: WebhookRoute.NewDay);
            Discord.instance?.BroadcastMessage(ZNet.instance.GetWorldName(), message, false);
        }
    }
}
