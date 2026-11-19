using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// The bot has no remote BattlEye client. Registering its local transport
    /// with BattlEye makes the anti-cheat correctly conclude that the client is
    /// not responding and kick it. Real network transports are not affected.
    /// </summary>
    [HarmonyPatch]
    internal static class SkaliaBotBattlEyePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Provider), "AddClientToThirdpartyAntiCheat")]
        private static bool AddClientPrefix(ITransportConnection clientId)
        {
            return !(clientId is SkaliaBotTransportConnection);
        }

        // A bot answers pings from inside the server, so its "ping" is only
        // the server's own frame time: a 1.5s freeze kicked all three bots
        // at once four times in the 0.1.123 test. Reset their samples before
        // the check; a bot that really stops answering still hits the timeout.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Provider), "KickClientsWithBadConnection")]
        private static void KickClientsPrefix()
        {
            foreach (var client in Provider.clients)
            {
                if (client?.transportConnection is SkaliaBotTransportConnection && client.ping > 0.2f)
                {
                    for (var sample = 0; sample < 16; sample++)
                    {
                        client.lag(0.05f);
                    }
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Provider), "RemoveClientFromThirdpartyAntiCheat")]
        private static bool RemoveClientPrefix(SteamPlayer clientToRemove)
        {
            return !(clientToRemove.transportConnection is SkaliaBotTransportConnection);
        }
    }
}
