using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y6 (the tester, 17/11: "como perspective é both eu tenho mais visão
    // em 3 pessoa"). In the test of 17/11 he stood behind a pillar in third
    // person and watched Ghost and LilJuninho round the corner; LilJuninho,
    // 5.7m away, stood 6s without seeing him and went prone.
    //
    // The server has the camera in both modes, so a bot gets the same
    // screen a player gets there, built as the game builds it
    // (PlayerLook.Update and sphereCastCamera): from the eye height
    // (clamped to 0.395 under the top of the body), 2m along
    // -1.5 forward + 0.25 up + 1 right (shoulder camera; with it off,
    // 0.5 up and no side), stopped 0.39m short of walls (BLOCK_PLAYERCAM).
    // The shoulder is the game's default (right) side.
    //
    // It only shows him. The bullets leave from the eye, as the game fires
    // them, so the trigger still needs the eye on him; with him only on the
    // camera the bot keeps its face on him and waits, as the tester did.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpCameraLength = 2f;
        private const float PvpCameraRadius = 0.39f;

        private float m_PvpLastEyeAt = float.NegativeInfinity;
        private bool m_PvpCameraOnly;
        private float m_PvpCameraLogAt = float.NegativeInfinity;
        private float m_PvpCameraOnlySince;
        private float m_PvpCameraOnlyWait;

        private void NotePvpCameraOnly(bool cameraOnly, Player target, float distance, float now)
        {
            if (cameraOnly && !m_PvpCameraOnly && now >= m_PvpCameraLogAt)
            {
                m_PvpCameraLogAt = now + 10f;
                BotLog(Microsoft.Extensions.Logging.LogLevel.Information,
                    "PvP: vendo {Name} a {Distance:0}m so pela camera de terceira pessoa ({Blocked}).",
                    target.channel?.owner?.playerID.characterName ?? "?",
                    distance,
                    DescribePvpEyeBlock(target));
            }
            if (cameraOnly && !m_PvpCameraOnly)
            {
                m_PvpCameraOnlySince = now;
                // As long as the players took from a man in view to their
                // first shot (SkaliaBotPvpBotsY).
                m_PvpCameraOnlyWait = DrawFromQuantiles(s_PvpStepQuantiles,
                    distance <= PvpOpenCloseMeters ? s_PvpOpenCloseSeconds : s_PvpOpenSeconds) * SkaliaBotModifiers.ReactionTime;
            }
            m_PvpCameraOnly = cameraOnly;
        }

        // 0.1.0-y17.3: what is between the eye and his chest, for the log
        // (41 times "so pela camera" in the two tests of 18/11, 9 of them
        // ending in a shot).
        private string DescribePvpEyeBlock(Player target)
        {
            var eye = m_Player.look.aim.position;
            var chest = GetPvpAimPoint(target);
            if (Physics.Linecast(eye, chest, out var hit, s_PvpSightMask, QueryTriggerInteraction.Ignore))
            {
                var name = hit.collider != null ? hit.collider.name : "?";
                return $"olho bloqueado por {name} a {hit.distance:0.0}m";
            }
            return IsBushOnLine(eye, chest) ? "olho bloqueado por moita" : "peito livre, outro ponto do corpo";
        }

        private static bool IsPvpThirdPersonAllowed()
        {
            return Provider.cameraMode == ECameraMode.BOTH || Provider.cameraMode == ECameraMode.THIRD;
        }

        private bool TryGetPvpCameraPosition(out Vector3 camera)
        {
            camera = default;
            if (!IsPvpThirdPersonAllowed()
                || m_Player.movement.getVehicle() != null
                || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return false;
            }
            var feet = m_Player.transform.position;
            var eye = m_Player.look.aim.position.y - feet.y;
            var top = m_Player.movement.controller != null ? m_Player.movement.controller.height : 2f;
            var origin = feet + Vector3.up * Mathf.Clamp(eye, 0.395f, Mathf.Max(0.395f, top - 0.395f));
            var rotation = Quaternion.Euler(m_CurrentPitch - 90f, m_CurrentYaw, 0f);
            var shoulder = Provider.modeConfigData?.Gameplay?.Allow_Shoulder_Camera ?? true;
            var direction = rotation * (shoulder
                ? new Vector3(1f, 0.25f, -1.5f)
                : new Vector3(0f, 0.5f, -1.5f));
            direction.Normalize();
            var length = PvpCameraLength;
            if (Physics.SphereCast(origin, PvpCameraRadius, direction, out var hit, PvpCameraLength,
                    RayMasks.BLOCK_PLAYERCAM, QueryTriggerInteraction.Ignore))
            {
                length = hit.distance;
            }
            camera = origin + direction * length;
            return true;
        }

        // Him on the third-person screen: in its 120 degrees and in line
        // from the camera, bushes hiding him as from the eye.
        private bool TryGetPvpCameraSight(Player target, out Vector3 point, out float fraction)
        {
            point = default;
            fraction = PvpChestFraction;
            if (!TryGetPvpCameraPosition(out var camera))
            {
                return false;
            }
            foreach (var candidate in s_PvpSightFractions)
            {
                var spot = GetPvpAimPoint(target, candidate);
                var to = spot - camera;
                if (Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, DirectionToYaw(to))) > PvpViewHalfDegrees)
                {
                    continue;
                }
                if (!Physics.Linecast(camera, spot, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                    && !IsBushOnLine(camera, spot))
                {
                    point = spot;
                    fraction = candidate;
                    return true;
                }
            }
            return false;
        }
    }
}
