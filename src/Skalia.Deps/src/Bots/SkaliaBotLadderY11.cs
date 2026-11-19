using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y11 (the tester, 17/11, test of 08:54, with a picture: "the ghost
    // ... quando chegou la em cima ficou travado nao concluiu a escada").
    // 08:57:48 Ghost let go of Ladder_Wood_0 at 55.8m, its top at 54.5m:
    // perched on the top of the ladder beside the wall of the bridge tower,
    // not on the floor. The climb counted as done there; the next route
    // found no floor of the mesh within 8m ("inicio atras de
    // 'Bridge_Turn_1'"), took the same ladder down, failed for 25s and the
    // bot was moved 8m by the stranded check.
    //
    // A player at the top keeps W into the ladder until his feet are on the
    // floor. So does the bot now: off the ladder at the top and off the
    // mesh, forward the way the ladder faces (toward the nearest mesh floor
    // within 2m when there is one), a hop when that does not move it, for up
    // to 3s.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float LadderStepOffSeconds = 3f;

        private float m_LadderStepOffSince = float.NegativeInfinity;
        private Vector3 m_LadderStepOffAnchor;

        private bool SteerLadderStepOff(BotLadder ladder, Vector3 position, float now)
        {
            if (position.y < ladder.Top.y - LadderTopReachMeters
                || SkaliaBotUnityNavmesh.IsOnMesh(position))
            {
                return false;
            }
            if (m_LadderStepOffSince < m_LadderPhaseStartedAt)
            {
                m_LadderStepOffSince = now;
                m_LadderStepOffAnchor = position;
                BotLog(LogLevel.Information,
                    "Escada {Name}: largou no topo em {Position} fora do chao; andando para o piso.",
                    ladder.Name,
                    FormatPosition(position));
            }
            if (now - m_LadderStepOffSince >= LadderStepOffSeconds)
            {
                return false;
            }
            var forward = Quaternion.Euler(0f, ladder.FacingYaw, 0f) * Vector3.forward;
            var aim = position + forward;
            if (SkaliaBotUnityNavmesh.TrySnap(position + forward * 0.8f, 2f, out var floor)
                && Mathf.Abs(floor.y - ladder.Top.y) <= 2f
                && (floor - position).GetHorizontalMagnitude() > 0.2f)
            {
                aim = floor;
            }
            var to = aim - position;
            m_StealthCrouch = false;
            m_IsSprinting = false;
            m_IsWalking = true;
            m_NavigationAnalog = 0x12;
            m_DesiredYaw = to.GetHorizontalMagnitude() > 0.05f ? DirectionToYaw(to) : ladder.FacingYaw;
            if (now - m_LadderStepOffSince >= 1f
                && (position - m_LadderStepOffAnchor).GetHorizontalMagnitude() < 0.3f
                && m_Player.movement.isGrounded
                && now >= m_NextNavigationJumpAt)
            {
                m_NavigationJumpUntil = now + 0.55f;
                m_NextNavigationJumpAt = now + 1.2f;
                m_LadderStepOffAnchor = position;
            }
            return true;
        }
    }
}
