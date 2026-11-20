using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y8 (the tester, 17/11: "ele tbm tem que ter a nocao que estou vendo
    // ele" and "na hr do pvp ainda falta esta questao de nocao deles e
    // cobertura").
    //
    // In sight of the bot, where he looks is as plain as where he is: his
    // gun (in hand, on the back or at the hip, as the game draws it) and
    // his face turned to the bot within 25 degrees with a clear line from
    // his eye to its chest. Then, after the pull it is on, the bot does not
    // stay in the open: a spot within 6m that hides its chest from his eye,
    // reachable in a straight line, is taken sideways or backwards with
    // the face and the gun still on him (the players' steps while shooting,
    // SkaliaBotPvpFeelY: close in mostly back and to a side, A or D held
    // 0.2-1s), never with the back turned. A spot that keeps the head in
    // line is taken first: it shoots on from there.
    //
    // From cover, with him out of the eye's line but known (the camera, or
    // seen in the last 6s), it leans out: a step to the side whose line
    // reaches him, held the players' A/D time, then back as the cover
    // above sends it when he looks at it again. 0.8-2s between leans.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpWatchedDegrees = 25f;
        private const float PvpFightCoverMeters = 6f;
        private const float PvpFightCoverEverySeconds = 2.5f;

        private Vector3 m_PvpFightCoverSpot;
        private float m_PvpFightCoverUntil;
        private float m_PvpFightCoverNextAt;
        private float m_PvpCoverReachedAt = float.NegativeInfinity;
        private byte m_PvpPeekAnalog = 0x11;
        private float m_PvpPeekUntil;
        private float m_PvpPeekNextAt;
        private float m_PvpFightCoverLogAt = float.NegativeInfinity;

        // 0.1.0-y17.6 (test of 18/11, 17:08:16): LilJuninho, empty Avenger,
        // got up from behind the fuel tank and reloaded standing in
        // blazex's line at 40m; 44 off in that reload, dead 4s later.
        // the tester (18/11): reloading or picking up in the middle of a fight
        // is fine "na negativa do inimigo", out of his line. Empty with his
        // eye on its chest, the reload waits for a spot within 10m that
        // hides the chest from him, reached in a straight line sideways or
        // backwards with the face on him (the cover step above); with none,
        // or with him under 6m, it reloads where it is.
        private bool m_PvpReloadCover;
        private float m_PvpReloadCoverNextAt;

        private bool IsPvpReloadWaitingForCover(float now)
        {
            return m_PvpReloadCover && now < m_PvpFightCoverUntil;
        }

        private bool TryStartPvpReloadCover(Player target, float distance, float now)
        {
            if (now < m_PvpReloadCoverNextAt
                || !m_PvpEnemyArmed
                || distance < 6f
                || !(m_Player.equipment.asset is ItemGunAsset gun)
                || !IsPvpGun(gun)
                || IsEquippedGunLoaded(gun)
                || (m_Player.equipment.useable is UseableGun useable && useable.isReloading))
            {
                return false;
            }
            m_PvpReloadCoverNextAt = now + 3f;
            var position = m_Player.transform.position;
            var hisEye = m_PvpVisible ? target.look.aim.position : m_PvpLastSeenPoint;
            // 0.1.0-y17.21: in his line by the same test the cover is
            // looked for with (SkaliaBotPvpCoverY); one bare line called a
            // tree beside him the bot's cover and it reloaded in the open.
            if (!IsChestInHisLine(hisEye, position))
            {
                return false;
            }
            var name = target.channel?.owner?.playerID.characterName ?? "?";
            if (!TryFindPvpWatchCover(position, hisEye, out var spot, out _, PvpCoverSearchMeters, true))
            {
                BotLog(LogLevel.Information,
                    "PvP: sem bala no pente na linha de {Name} a {Distance:0}m e sem cobertura a {Meters:0}m em linha reta; recarregando aqui.",
                    name,
                    distance,
                    PvpCoverSearchMeters);
                return false;
            }
            var way = (spot - position).GetHorizontalMagnitude();
            m_PvpReloadCover = true;
            m_PvpFightCoverSpot = spot;
            m_PvpFightCoverUntil = now + Mathf.Clamp(way / 2f + 1f, 1.5f, 6f);
            m_PvpFightCoverNextAt = now + PvpFightCoverEverySeconds;
            // One walk per empty magazine: if it does not get there, it
            // reloads where it stops.
            m_PvpReloadCoverNextAt = m_PvpFightCoverUntil + 5f;
            BotLog(LogLevel.Information,
                "PvP: sem bala no pente na linha de {Name} a {Distance:0}m; indo de lado {Way:0.0}m para fora da linha dele antes de recarregar.",
                name,
                distance,
                way);
            var first = PvpAnalogToward(spot - position);
            m_PvpStepAnalog = IsPvpStepClear(first) ? first : (byte)0x11;
            return true;
        }

        private bool IsPvpEnemyWatchingMe(Player target)
        {
            var look = target.look;
            if (look?.aim == null)
            {
                return false;
            }
            var eye = look.aim.position;
            var chest = m_Player.transform.position + Vector3.up * 1.3f;
            var to = chest - eye;
            return to.sqrMagnitude > 0.01f
                && Vector3.Angle(AimDirection(look.yaw, look.pitch), to) <= PvpWatchedDegrees
                && !Physics.Linecast(eye, chest, s_PvpSightMask, QueryTriggerInteraction.Ignore);
        }

        // The step, kept facing him, that heads for a point.
        private byte PvpAnalogToward(Vector3 to)
        {
            to.y = 0f;
            var local = Quaternion.Inverse(Quaternion.Euler(0f, m_CurrentYaw, 0f)) * to;
            var size = local.magnitude;
            if (size < 0.01f)
            {
                return 0x11;
            }
            var side = local.x > 0.38f * size ? 1 : local.x < -0.38f * size ? -1 : 0;
            var ahead = local.z > 0.38f * size ? 1 : local.z < -0.38f * size ? -1 : 0;
            return (byte)(((side + 1) << 4) | (ahead + 1));
        }

        // From TickPvpCore while shooting: true when the step is set here.
        private bool TickPvpFightCover(Player target, float distance, float now)
        {
            var position = m_Player.transform.position;
            if (now < m_PvpFightCoverUntil)
            {
                var to = m_PvpFightCoverSpot - position;
                if (to.GetHorizontalMagnitude() < 0.6f)
                {
                    m_PvpFightCoverUntil = 0f;
                    m_PvpReloadCover = false;
                    m_PvpCoverReachedAt = now;
                    m_PvpStepAnalog = 0x11;
                    return true;
                }
                var analog = PvpAnalogToward(to);
                if (analog == 0x11 || !IsPvpStepClear(analog))
                {
                    m_PvpFightCoverUntil = 0f;
                    m_PvpReloadCover = false;
                    return false;
                }
                m_PvpStepAnalog = analog;
                return true;
            }
            m_PvpReloadCover = false;
            if (TryStartPvpReloadCover(target, distance, now))
            {
                return true;
            }
            if (now < m_PvpFightCoverNextAt
                || !m_PvpEnemyArmed
                || m_PvpTriggerHeld
                || distance < 6f
                || distance > PvpHoldAngleMeters
                || !IsPvpEnemyWatchingMe(target))
            {
                return false;
            }
            m_PvpFightCoverNextAt = now + PvpFightCoverEverySeconds;
            var hisEye = target.look.aim.position;
            if (!IsChestInHisLine(hisEye, position)
                || !TryFindPvpWatchCover(position, hisEye, out var spot, out var peek, PvpFightCoverMeters, true))
            {
                return false;
            }
            m_PvpFightCoverSpot = spot;
            m_PvpFightCoverUntil = now + 2.5f;
            if (now >= m_PvpFightCoverLogAt)
            {
                m_PvpFightCoverLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: {Name} esta olhando para mim a {Distance:0}m; indo de lado para cobertura a {Way:0.0}m{Peek}.",
                    target.channel?.owner?.playerID.characterName ?? "?",
                    distance,
                    (spot - position).GetHorizontalMagnitude(),
                    peek ? ", atirando de cima dela" : ", depois espio");
            }
            var first = PvpAnalogToward(spot - position);
            m_PvpStepAnalog = IsPvpStepClear(first) ? first : (byte)0x11;
            return true;
        }

        // 0.1.0-y17.8 (the tester, 18/11: "atira pulando cerca igual eu e
        // esconde"). His recordings of 18/11 (17:33 and 18:00 tests), 7
        // jumps to shoot: behind a fence or a stone wall 2.4-3.7m in front
        // that hid him standing, gun raised, he jumped straight up (eye
        // about 1.65m higher at the top), fired 1-3 rounds from 0.27-0.52s
        // after the jump until he landed (0.65-0.7s), and on landing
        // sprinted to a side (5 of 7) behind the same cover. 1.5-2.5s
        // between jumps. 17 of his 111 rounds in those fights left in the
        // air. The bot does it when the lean to either side has no line
        // to where it last saw him and the jump does.
        private const float PvpJumpPeekCoverMeters = 4f;
        private const float PvpJumpPeekEyeRise = 1.3f;
        private const float PvpJumpPeekAirSeconds = 0.7f;
        private float m_PvpJumpSideFrom;
        private float m_PvpJumpSideUntil;
        private int m_PvpJumpSide;
        private float m_PvpJumpPeekLogAt = float.NegativeInfinity;

        private bool TryStartPvpJumpPeek(Vector3 eye, float now)
        {
            var stance = m_Player.stance.stance;
            if ((stance != EPlayerStance.STAND && stance != EPlayerStance.SPRINT)
                || !m_Player.movement.isGrounded
                || !Physics.Linecast(eye, m_PvpLastSeenPoint, out var cover, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                || cover.distance > PvpJumpPeekCoverMeters
                || Physics.Linecast(eye + Vector3.up * PvpJumpPeekEyeRise, m_PvpLastSeenPoint, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(eye, Vector3.up, PvpJumpPeekEyeRise + 0.4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            m_PvpHopUntil = now + 0.3f;
            m_PvpPeekAnalog = 0x11;
            m_PvpPeekUntil = now + PvpJumpPeekAirSeconds;
            m_PvpJumpSide = Random.value < 0.5f ? 1 : -1;
            m_PvpJumpSideFrom = now + PvpJumpPeekAirSeconds;
            m_PvpJumpSideUntil = m_PvpJumpSideFrom + Mathf.Max(0.3f, DrawFromQuantiles(s_PvpStepQuantiles, s_PvpStepSeconds));
            m_PvpPeekNextAt = m_PvpJumpSideUntil + Random.Range(0.8f, 2f);
            if (now >= m_PvpJumpPeekLogAt)
            {
                m_PvpJumpPeekLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: atras de {What} a {Cover:0.0}m que tapa {Name}; pulando para atirar por cima e saindo de lado.",
                    cover.collider != null ? cover.collider.name : "?",
                    cover.distance,
                    m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?");
            }
            return true;
        }

        // On landing, a step to a side behind the same cover.
        private bool TickPvpJumpSide(float now)
        {
            if (now < m_PvpJumpSideFrom || now >= m_PvpJumpSideUntil)
            {
                return false;
            }
            foreach (var side in new[] { m_PvpJumpSide, -m_PvpJumpSide })
            {
                var analog = (byte)(((side + 1) << 4) | 1);
                if (IsPvpStepClear(analog))
                {
                    m_PvpJumpSide = side;
                    m_PvpPeekAnalog = analog;
                    return true;
                }
            }
            m_PvpJumpSideUntil = 0f;
            m_PvpPeekAnalog = 0x11;
            return false;
        }

        // From TickPvpCore while not shooting: a lean out of cover.
        private void TickPvpPeek(float now)
        {
            if (TickPvpJumpSide(now))
            {
                return;
            }
            if (now < m_PvpPeekUntil)
            {
                if (!IsPvpStepClear(m_PvpPeekAnalog))
                {
                    m_PvpPeekAnalog = 0x11;
                }
                return;
            }
            m_PvpPeekAnalog = 0x11;
            if (!m_PvpLooking
                || !m_PvpEnemyArmed
                || now - m_PvpCoverReachedAt > 30f
                || now < m_PvpPeekNextAt
                || now < m_PvpReactAt
                || m_Player.stance.stance == EPlayerStance.PRONE)
            {
                return;
            }
            var eye = m_Player.look.aim.position;
            var right = Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.right;
            var first = Random.value < 0.5f ? 1 : -1;
            foreach (var side in new[] { first, -first })
            {
                if (Physics.Linecast(eye + right * side * 1.0f, m_PvpLastSeenPoint, s_PvpSightMask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                var analog = (byte)(((side + 1) << 4) | 1);
                if (!IsPvpStepClear(analog))
                {
                    continue;
                }
                m_PvpPeekAnalog = analog;
                m_PvpPeekUntil = now + Mathf.Max(0.3f, DrawFromQuantiles(s_PvpStepQuantiles, s_PvpStepSeconds));
                m_PvpPeekNextAt = m_PvpPeekUntil + Random.Range(0.8f, 2f);
                BotLog(LogLevel.Debug, "PvP: espiando para {Side} da cobertura.", side > 0 ? "a direita" : "a esquerda");
                return;
            }
            if (TryStartPvpJumpPeek(eye, now))
            {
                return;
            }
            m_PvpPeekNextAt = now + 1f;
        }
    }
}
