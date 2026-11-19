using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y8 (the tester, 17/11, test of 07:55: "faca ele pular os obstaculos
    // para vir atras de mim se for me matar na facada" and "ele sabe que
    // nao tenho arma de fogo entao ele podia simplesmente rushar e me
    // matar").
    //
    // 07:56:48-07:57:08 Vultox, out of rounds, went at the tester with the
    // Butterfly Knife from 10m and stopped against pallets and barrels at
    // 7m: the melee fight only walked straight at him.
    //
    // Now:
    // - straight at him, a low obstacle ahead is jumped as the routes jump
    //   them (ShouldJumpTraversableObstacle);
    // - a wall ahead, no gain of half a metre in 1.5s, or him out of the
    //   eye's line: the route of the walking code takes the bot to where he
    //   was last seen, refreshed every half second, until the eye has him
    //   again;
    // - a man with no gun to be seen on him (in the hands, on the back or
    //   at the hip, as the game draws the slots) is rushed: the bot walks
    //   at him shooting, and when it loses him it goes to where he was
    //   instead of holding, going round or taking cover.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const string PvpChaseName = "atras do jogador";
        private const float PvpRushLostSeconds = 10f;
        // The walking code's arrival radius for this flee (TickForcedMovementBehavior).
        private const float PvpChaseArriveMeters = 1.2f;

        private bool m_PvpEnemyArmed = true;
        private float m_PvpChaseRefreshAt;
        private float m_PvpChaseLogAt = float.NegativeInfinity;
        private float m_PvpMeleeProgressAt;
        private Vector3 m_PvpMeleeProgressAnchor;
        private float m_PvpMeleeRouteUntil;
        private bool m_PvpChaseOwn;
        private float m_PvpChaseHoldUntil = float.NegativeInfinity;

        // The walking code's route has the bot: after him, or (no round
        // left) out of his sight (SkaliaBotPvpNoAmmoY).
        private bool IsPvpChasing()
        {
            return m_ObjectiveType == ObjectiveType.Flee
                && (m_FleeingFromPlayerName == PvpChaseName || m_FleeingFromPlayerName == PvpNoAmmoName);
        }

        private bool IsPvpChasingHim()
        {
            return m_ObjectiveType == ObjectiveType.Flee && m_FleeingFromPlayerName == PvpChaseName;
        }

        // What the game shows of him: the item in hand, the long gun on the
        // back (slot 0) and the sidearm at the hip (slot 1).
        private static bool IsPvpArmedToTheEye(Player player)
        {
            if (player.equipment?.asset is ItemGunAsset)
            {
                return true;
            }
            var inventory = player.inventory;
            if (inventory == null)
            {
                return false;
            }
            for (byte page = 0; page < PlayerInventory.SLOTS; page++)
            {
                if (inventory.getItemCount(page) > 0 && inventory.getItem(page, 0)?.GetAsset() is ItemGunAsset)
                {
                    return true;
                }
            }
            return false;
        }

        private void NotePvpEnemyLook(Player target, bool seen)
        {
            if (seen)
            {
                m_PvpEnemyArmed = IsPvpArmedToTheEye(target);
            }
        }

        private bool IsPvpRushing(float now)
        {
            return !m_PvpEnemyArmed
                && m_PvpTarget != null
                && now - m_PvpLastSeenAt <= PvpRushLostSeconds
                && (GetPvpGunRange() > 0f || m_PvpMelee);
        }

        // From TickPvpCore before the steering: true while the walking
        // code's route has the bot (no planting, no head on him).
        private bool TickPvpChase(float distance, bool eyeOnHim, float gunRange, float now)
        {
            var wantRoute = false;
            var why = string.Empty;
            // 0.1.0-y16.2: a chase route the walking code dropped (a zombie
            // on the bot, the point reached, no way there) waits 2s before
            // the next one. blazex, after Ghost with zombies round it,
            // went "Flee" and back 930 times in a minute (18:12), and Ghost
            // 3 times in a second at the foot of the walkway (17:35:26).
            if (m_PvpChaseOwn && !IsPvpChasingHim())
            {
                m_PvpChaseOwn = false;
                m_PvpChaseHoldUntil = now + 2f;
            }
            if (m_PvpMelee)
            {
                // 0.2.2y9: stuck is the bot's own feet not moving while it
                // walks at him. In the test of 08:15 the tester kept 2 to 4m off
                // Ghost, sidestepping and going prone, and the distance that
                // did not shrink read as "stuck": the route then took the
                // head off him for the rest of the fight.
                var feet = m_Player.transform.position;
                if (m_PvpStepAnalog != 0x12
                    || (feet - m_PvpMeleeProgressAnchor).GetHorizontalMagnitude() > 0.5f)
                {
                    m_PvpMeleeProgressAnchor = feet;
                    m_PvpMeleeProgressAt = now;
                }
                else if (now - m_PvpMeleeProgressAt > 1.5f && distance > GetPvpMeleeRange())
                {
                    m_PvpMeleeRouteUntil = now + 3f;
                    m_PvpMeleeProgressAnchor = feet;
                    m_PvpMeleeProgressAt = now;
                }
                if (!eyeOnHim)
                {
                    wantRoute = true;
                    why = "fora da linha do olho";
                }
                else if (now < m_PvpMeleeRouteUntil)
                {
                    wantRoute = true;
                    why = "travado no caminho reto";
                }
                else if (distance > GetPvpMeleeRange() && !IsPvpWayAhead())
                {
                    wantRoute = true;
                    why = "parede no caminho reto";
                }
            }
            else
            {
                m_PvpMeleeProgressAnchor = m_Player.transform.position;
                m_PvpMeleeProgressAt = now;
                // 0.1.0-y17.16: not out of the cover at a man with a gun
                // (SkaliaBotPvpMeleeY, GetPvpBladeMeters).
                if (gunRange <= 0f
                    && !m_PvpEnemyArmed
                    && !eyeOnHim
                    && now - m_PvpLastSeenAt <= PvpRushLostSeconds
                    && distance <= PvpMeleeChaseMeters + 5f)
                {
                    wantRoute = true;
                    why = "na faca, fora da linha do olho";
                }
                // 0.1.0-y16.2: within the 60m the players went after a man
                // (SkaliaBotPvpHuntY); blazex took a route at Ghost from
                // 177m (18:11:41).
                else if (IsPvpRushing(now) && !eyeOnHim && !m_PvpEngaged && distance <= PvpHuntMaxMeters)
                {
                    wantRoute = true;
                    why = "sem arma de fogo a vista, indo em cima";
                }
            }
            // 0.2.2y9: the walking code ends a flee whose point is within
            // its arrival radius, so a point at the bot's feet set again
            // every tick flipped "Flee"/"Patrol" 290 times in 10s (Ghost,
            // 08:16:51-08:17:01). There a route has nothing to do: the
            // straight steps take him.
            if (wantRoute
                && (m_PvpLastSeenPoint - m_Player.transform.position).GetHorizontalMagnitude() <= PvpChaseArriveMeters + 0.3f)
            {
                wantRoute = false;
            }
            // 0.1.0-y16.2: the zombie on the bot first, as the gun fight
            // does (PvpZombieFirstMeters), and the 2s after a dropped route.
            if (wantRoute
                && (m_CachedNearestZombieDistance <= PvpZombieFirstMeters || now < m_PvpChaseHoldUntil))
            {
                wantRoute = false;
            }
            // 0.1.0-y17.12: losing the swarm, or killing the group it chose
            // to kill, no route after him (SkaliaBotHordeCommitY). At
            // 22:30:32-33 and 22:30:58 (18/11) LilJuninho left the escape for
            // the route "na faca" at the tester and back, 3 times in 30s.
            if (wantRoute && (m_SwarmEscapeActive || m_HordeFightCommit))
            {
                wantRoute = false;
            }
            if (!wantRoute)
            {
                if (IsPvpChasingHim())
                {
                    m_PvpChaseOwn = false;
                    ClearObjective();
                }
                return false;
            }
            if (!IsPvpChasingHim() || now >= m_PvpChaseRefreshAt)
            {
                m_PvpChaseRefreshAt = now + 0.5f;
                m_FleeingFromPlayerName = PvpChaseName;
                SetObjective(ObjectiveType.Flee, m_PvpLastSeenPoint - Vector3.up * 1.2f, now + 3f);
                m_PvpChaseOwn = IsPvpChasingHim();
            }
            if (now >= m_PvpChaseLogAt)
            {
                m_PvpChaseLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: indo atras de {Name} pela rota ({Why}), {Distance:0}m.",
                    m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                    why,
                    distance);
            }
            return true;
        }

        // A hop over what is in the way, as the routes do.
        private void TryPvpHop(byte analog, float now)
        {
            if (analog != 0x12 || !m_Player.movement.isGrounded || now < m_NextNavigationJumpAt)
            {
                return;
            }
            var forward = Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward;
            // 0.1.0-y16.2: over or onto, and the key held its 0.55s
            // (SkaliaBotPvpHopY).
            if (CanPvpHop(m_Player.transform.position, forward, out var top))
            {
                m_PvpHopUntil = now + 0.55f;
                m_NavigationJumpUntil = m_PvpHopUntil;
                m_NextNavigationJumpAt = now + 1.2f;
                NotePvpHop(top, now);
            }
        }

        // Shooting a man with no gun: at him, standing, walking.
        private void TickPvpRushSteps(float distance, float now)
        {
            m_PvpPosture = 0;
            m_PostureKey = 0;
            m_PvpStepAnalog = distance > 4f && IsPvpWayAhead() ? (byte)0x12 : (byte)0x11;
            TryPvpHop(m_PvpStepAnalog, now);
        }
    }
}
