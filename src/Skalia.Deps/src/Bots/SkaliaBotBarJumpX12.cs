using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x12: the gate arm (cancela). At the Olympia Military Base
    // entrance and the two crossings by the docks the arm is a thin bar,
    // collider "Hinge", top at about 1.3m with open space under it. The
    // window probe looks at 0.3 and 0.6m and saw nothing; the contact jump
    // measured the top 0.4m past the contact, which for a thin bar is the
    // road behind it, so it never fired. The bot ran into the arm until
    // the unstuck. Recordings of two players crossing that same arm
    // show the jump pressed 0.3 to 0.5s before the arm, 1.2 to
    // 2.5m ahead, without touching it. The bot now does the same: a bar
    // with room under and over it, ahead within 0.4s of its pace, is
    // jumped before contact.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float BarJumpLeadSeconds = 0.4f;
        private const float BarJumpMinSpeed = 1.5f;
        private Vector3 m_BarSpeedPosition;
        private float m_BarSpeedAt;
        private float m_BarSpeed;
        private float m_NextBarJumpLogAt;

        // Top of what the body hit, measured on the obstacle itself: a small
        // sphere dropped right behind the contact face catches a thin bar.
        private static float MeasureHopTop(Vector3 position, Vector3 direction, RaycastHit hit)
        {
            var face = hit.distance > 0.01f ? hit.point : position + direction * 0.3f;
            var probe = face + direction * 0.1f;
            probe.y = position.y + 1.75f;
            if (Physics.SphereCast(probe, 0.12f, Vector3.down, out var top, 1.7f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return top.point.y - position.y;
            }
            var over = position + direction * (hit.distance + 0.4f);
            if (Physics.Raycast(over + Vector3.up * 1.6f, Vector3.down, out var ground, 1.6f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return ground.point.y - position.y;
            }
            return -1f;
        }

        private bool TryBarJump(Vector3 position, Vector3 direction, float navigationDistance, float now)
        {
            var sinceSample = now - m_BarSpeedAt;
            if (sinceSample > 0.05f)
            {
                var sample = (position - m_BarSpeedPosition).GetHorizontalMagnitude() / sinceSample;
                m_BarSpeed = sinceSample > 1f ? 0f : Mathf.Lerp(m_BarSpeed, sample, 0.5f);
                m_BarSpeedPosition = position;
                m_BarSpeedAt = now;
            }
            if (!Tryhard || direction.sqrMagnitude < 0.01f || m_BarSpeed < BarJumpMinSpeed)
            {
                // Slow or standing: the contact jump handles it.
                return false;
            }
            direction.y = 0f;
            direction.Normalize();
            var reach = Mathf.Clamp(m_BarSpeed * BarJumpLeadSeconds, 0.5f, 2.4f);
            if (!Physics.CapsuleCast(
                    position + Vector3.up * 0.75f,
                    position + Vector3.up * 1.25f,
                    0.25f,
                    direction,
                    out var hit,
                    reach,
                    RayMasks.BLOCK_COLLISION,
                    QueryTriggerInteraction.Ignore)
                || hit.distance <= 0.01f
                // 0.2.2x13: the route goes on past the bar (Tadeu hopped the
                // rails on top of a Military Tower while turning there).
                || hit.distance > navigationDistance - 0.3f)
            {
                return false;
            }
            if (SkaliaBotObjectMap.TryGetByCollider(hit.collider, out var mapObject)
                && SkaliaBotObjectMap.BlocksBody(mapObject))
            {
                return false;
            }
            // A bar, not a wall: open under it and over it.
            var clear = hit.distance + 0.5f;
            if (Physics.Raycast(position + Vector3.up * 0.35f, direction, clear, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(position + Vector3.up * 0.6f, direction, clear, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(position + Vector3.up * 1.65f, direction, hit.distance + 0.8f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            var height = MeasureHopTop(position, direction, hit);
            if (height < 0.5f || height > ContactJumpTopMeters)
            {
                return false;
            }
            // A gate arm stands on flat ground: the same floor under the bar
            // and past it, never a drop (tower rails, balconies).
            var under = position + direction * (hit.distance + 0.1f) + Vector3.up * 0.3f;
            var landing = position + direction * (hit.distance + 1.4f) + Vector3.up * 0.6f;
            if (!Physics.Raycast(under, Vector3.down, out var floor, 1.2f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || Mathf.Abs(floor.point.y - position.y) > 0.6f
                || !Physics.Raycast(landing, Vector3.down, out var ground, 1.5f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || ground.normal.y < 0.6f
                || Mathf.Abs(ground.point.y - position.y) > 0.6f)
            {
                return false;
            }
            if (now >= m_NextBarJumpLogAt)
            {
                m_NextBarJumpLogAt = now + 5f;
                BotLog(LogLevel.Information,
                    "Barra/cancela a {Distance:0.0}m na frente ({Name}, topo a {Height:0.0}m) em {Position}, a {Speed:0.0}m/s: pulando antes de encostar, como os jogadores.",
                    hit.distance,
                    hit.collider != null ? hit.collider.name : "?",
                    height,
                    FormatPosition(position),
                    m_BarSpeed);
            }
            return true;
        }
    }
}
