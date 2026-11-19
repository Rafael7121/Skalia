using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y3 (the tester, 17/11: "ele nao recarrega"). In the test of 17/11
    // Ghost emptied the Heartbreaker at the tester (01:21:10-15), reloaded 0->30
    // with the sights of the zombie fight still pressed, and from then on
    // pressed the sights every 0.45s for 1m45s without them ever coming up
    // and without one shot, 1 to 44m from the tester.
    //
    // The game (PlayerEquipment.simulate_UseableInput) takes a press of the
    // sights only when the last one was let go; a reload, a jam or a bolt
    // drops the sights and leaves the button down. The PvP code cleared its
    // own flag and pressed again, never letting go. Now it lets go first.
    //
    // In the fights with a player (rec/pvp15.py, players of 08/11) a gun was
    // in hand in 93% of the rows, a medical item 4.9%, a knife 0.9%, food
    // never; in the 10s after, food 0.3%. While the fight is on (the shooter
    // seen or a hit taken in the last 10s) there is no meal, no knife for a
    // zombie with a loaded gun, and no gear work.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpActiveSeconds = 10f;
        private const int PvpAimDropsBeforeHip = 3;
        private const float PvpNoFireLogAfter = 4f;
        private const float PvpNoFireLogEvery = 5f;

        private int m_PvpAimDrops;
        private bool m_PvpAimFailed;
        private float m_PvpLastPullAt;
        private float m_PvpNoFireLogAt = float.NegativeInfinity;
        private float m_PvpAimDropLogAt = float.NegativeInfinity;

        // The fight with a player is on: him seen, or a hit from him, in
        // the last 10s.
        private bool IsPvpActive(float now)
        {
            if (m_PvpTarget == null)
            {
                return false;
            }
            return (m_PvpAware && now - m_PvpLastSeenAt <= PvpActiveSeconds)
                || now - m_PvpLastHitAt <= PvpActiveSeconds;
        }

        // Lets go of the sights button; the next press goes on a later
        // packet, which the game then takes.
        private void ReleasePvpAim(WalkingPlayerInputPacket packet, float now, string why)
        {
            packet.secondaryAttack = EAttackInputFlags.Stop;
            m_PvpAimHeld = false;
            m_GunAimHeld = false;
            m_GunAimReadyAt = 0f;
            if (now >= m_PvpAimDropLogAt)
            {
                m_PvpAimDropLogAt = now + 10f;
                BotLog(LogLevel.Debug, "PvP: soltando o botao de mira ({Why}); mira de novo no proximo pacote.", why);
            }
        }

        // The sights pressed and not up three times with no shot between:
        // something else holds them down. The rest of this fight goes from
        // the hip (29% of the players' rows close in), and what the gun
        // said is written once to find it.
        private void NotePvpAimDropped(ItemGunAsset gun, UseableGun readyGun, float now)
        {
            m_PvpAimDrops++;
            if (m_PvpAimFailed || m_PvpAimDrops < PvpAimDropsBeforeHip)
            {
                return;
            }
            m_PvpAimFailed = true;
            BotLog(LogLevel.Warning,
                "PvP: a mira nao subiu {Drops} vezes sem tiro com {Gun}; seguindo sem mirar{Must}. Arma: mirando {Aiming}, correndo {Sprinting}, modo {Firemode}, municao {Ammo} (estado {State}), recarregando {Reloading}, martelo {Hammering}, ferrolho {Rechamber}, ocupado {Busy}, postura {Stance}.",
                m_PvpAimDrops,
                gun.itemName,
                gun.MustAimToShoot || gun.action == EAction.String ? " (mas esta arma so atira mirando)" : string.Empty,
                readyGun.isAiming,
                readyGun.isSprinting,
                readyGun.firemode,
                readyGun.ammo,
                m_Player.equipment.state != null && m_Player.equipment.state.Length > 10 ? m_Player.equipment.state[10] : -1,
                readyGun.isReloading,
                readyGun.isHammering,
                readyGun.needsRechamber,
                m_Player.equipment.isBusy,
                m_Player.stance.stance);
        }

        // In the fight, he in reach and no pull for 4s: why, every 5s.
        private void LogPvpNotFiring(ItemGunAsset gun, UseableGun readyGun, float distance, bool onTarget, float now)
        {
            if (now - m_PvpLastPullAt < PvpNoFireLogAfter || now < m_PvpNoFireLogAt)
            {
                return;
            }
            m_PvpNoFireLogAt = now + PvpNoFireLogEvery;
            BotLog(LogLevel.Information,
                "PvP: sem atirar ha {Seconds:0}s em {Name}: visivel {Visible}, na mira {OnTarget}, {Distance:0}m (alcance {Range:0}), mira {Aiming} (botao {Held}, pronta em {Ready:0.0}s), sem mirar {Hip}, municao {Ammo}, postura {Stance}, cano livre {Clear}.",
                now - m_PvpLastPullAt,
                m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                m_PvpVisible,
                onTarget,
                distance,
                gun.range,
                readyGun.isAiming,
                m_PvpAimHeld,
                Mathf.Max(0f, m_PvpAimReadyAt - now),
                m_PvpHipFire || m_PvpAimFailed,
                readyGun.ammo,
                m_Player.stance.stance,
                m_PvpMuzzleClear);
        }
    }
}
