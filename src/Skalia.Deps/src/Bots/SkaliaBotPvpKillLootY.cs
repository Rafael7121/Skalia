using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y11 (the tester, 17/11: "depois de matar o foco dele e sempre
    // lootear ele, igual faz com o mega. ver se tem algo bom se nao so
    // sair"). A man the bot killed leaves his things where he fell; the bot
    // goes to them as to the Mega's pile (SkaliaBotTryhardCombat,
    // TickLootBehavior): what is worth taking within 10m of the body, then
    // on with its day. Zombies near the body or a fight stop it as they
    // stop the Mega's.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // From the service's death hook, on the killer's simulation.
        internal void NotePvpKill(Vector3 body, string name)
        {
            if (!Tryhard || m_Player == null || m_Player.life == null || m_Player.life.isDead)
            {
                return;
            }
            var now = Time.realtimeSinceStartup;
            var gap = (body - m_Player.transform.position).GetHorizontalMagnitude();
            if (gap > MegaDropReachMeters)
            {
                return;
            }
            m_DropPickupUntil = now + MegaDropSeconds;
            m_DropPickupCenter = body;
            m_MegaDropPending = true;
            m_MegaDropPicked = 0;
            m_MegaPileArrivedAt = 0f;
            m_DisengageUntil = 0f;
            m_LastKilledZombieAt = now;
            m_DropPileName = "de " + name;
            BotLog(LogLevel.Information,
                "Matei {Name} a {Distance:0}m em {Position}; indo ver o que ele deixou antes de seguir.",
                name,
                gap,
                FormatPosition(body));
        }
    }
}
