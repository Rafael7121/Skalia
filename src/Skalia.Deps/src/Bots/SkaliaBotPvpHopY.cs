using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y16.2 (the tester, 17/11, after the y16.1 session of 17:28: "ele
    // ainda nao pula os obstaculos com a faca na mao. theghostsniper ficou me
    // encarando apenas").
    //
    // 17:35:12-26, Ghost with the Military Knife only, the tester with nothing in
    // hand: the tester hopped the wooden fences and the cable coil 8 times in 14s
    // (jump held 0.3 to 0.6s, onto the coil at 1.2m and from it to the
    // walkway 2.1m up) and Ghost jumped 0 times, sprinting along the fence
    // and stopping at it. In the whole session 13 blade fights started and 2
    // had a jump. Two reasons:
    // - the hop of the blade fight (y8) set the jump key and the fight's own
    //   tick took it away on the next frame ("no hop left over from the
    //   route"); the key only went out when the decision fell on a frame
    //   that builds a packet, 1 in 4;
    // - the straight step at him was "clear" only with nothing at 1.2m in
    //   front, so a fence or a coil stopped the bot (or sent it round by the
    //   route) before the hop was ever looked at.
    // Now the hop lasts its 0.55s, and something ahead that the jump clears
    // (a low fence or a window, as the routes hop them, or something to jump
    // up onto, top within 1.4m, as the contact jump measures it, with ground
    // within 1.5m up behind) is a way ahead: the bot runs at it and jumps.
    // Furniture stays out (the routes' counter rule); pallets, barrels and
    // crates are in (y8, Vultox stopped against them at 7m).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpHopProbeMeters = 1.2f;

        private float m_PvpHopUntil;
        private float m_PvpHopLogAt = float.NegativeInfinity;

        // Something in the way that the jump takes the bot over or onto.
        private bool CanPvpHop(Vector3 position, Vector3 direction, out float top)
        {
            top = 0f;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
            {
                return false;
            }
            direction.Normalize();
            if (ShouldJumpTraversableObstacle(position, direction))
            {
                return true;
            }
            // Room for the head over it.
            if (Physics.Raycast(position + Vector3.up * 1.65f, direction, PvpHopProbeMeters, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            if (!Physics.CapsuleCast(
                    position + Vector3.up * 0.3f,
                    position + Vector3.up * 1.25f,
                    0.25f,
                    direction,
                    out var hit,
                    PvpHopProbeMeters,
                    RayMasks.BLOCK_COLLISION,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            if (SkaliaBotObjectMap.TryGetByCollider(hit.collider, out var blocking)
                && blocking.Category == BotObjectCategory.Furniture)
            {
                return false;
            }
            top = MeasureHopTop(position, direction, hit);
            if (top < 0.2f || top > ContactJumpTopMeters)
            {
                return false;
            }
            var landing = position + direction * (hit.distance + 1.4f) + Vector3.up * 2.5f;
            return Physics.Raycast(landing, Vector3.down, out var ground, 6f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                && ground.normal.y >= 0.4f
                && ground.point.y >= position.y - 2.5f
                && ground.point.y <= position.y + 1.5f;
        }

        // Straight at him: clear, or something the jump takes.
        private bool IsPvpWayAhead()
        {
            return IsPvpStepClear(0x12)
                || CanPvpHop(m_Player.transform.position, Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward, out _);
        }

        // The fight's tick keeps a hop it started until it is done.
        private float PvpHopLeft(float now)
        {
            return now < m_PvpHopUntil ? m_PvpHopUntil : 0f;
        }

        private void NotePvpHop(float top, float now)
        {
            if (now < m_PvpHopLogAt)
            {
                return;
            }
            m_PvpHopLogAt = now + 5f;
            BotLog(LogLevel.Information,
                "PvP: pulando {What} indo atras de {Name} a {Distance:0}m.",
                top > 0f ? $"para cima do obstaculo (topo a {top:0.0}m)" : "o obstaculo baixo",
                m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                m_PvpTarget != null ? (m_PvpTarget.transform.position - m_Player.transform.position).magnitude : 0f);
        }
    }
}
