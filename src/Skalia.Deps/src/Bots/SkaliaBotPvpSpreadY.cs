using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y6 (the tester, 17/11: "eu ainda acho que erramos menos. teve
    // alguns tiros muito imprecisos por conta do spread").
    //
    // The aim error of SkaliaBotPvpFeelY comes from the players' hits per
    // round, so it already holds the spread of their guns. The game then
    // threw the round off again by the bot's own gun spread
    // (UseableGun.CalculateSpreadAngleRadians: a cone, uniform over the
    // disk, so sigma = half the cone on each axis), counting it twice.
    // From the hip that is most of the miss: Cobra 16.7 degrees, Eaglefire
    // 5.71 (aimed 0.84 and 0.29). The bot now draws only what is left of the
    // players' miss after its gun's spread as it will fire (aimed or from
    // the hip, quality, stance, attachments, skill), and an aimed round
    // waits for the sights to be all the way up.
    //
    // The first-sight error (up to 6 degrees, settling in 1.5s) was drawn
    // again every time the fight came back on: after a crawler shot or a
    // second out of sight, with the gun still on him. In the test of 17/11
    // Ghost shot 6.5 and 6.1 degrees off LilJuninho (41-48m) right after
    // two crawlers. It is now drawn only when the gun has to come round to
    // him (more than 8 degrees off).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // 0.1.0-y14: low enough for a scope at 200m (0.07 for 90%).
        private const float PvpAimFloorDegrees = 0.05f;
        private const float PvpOnHimDegrees = 8f;

        private float PvpGunSpreadSigmaDegrees()
        {
            if (!(m_Player.equipment.useable is UseableGun gun))
            {
                return 0f;
            }
            try
            {
                var aimAlpha = m_PvpHipFire || m_PvpAimFailed ? 0f : 1f;
                var cone = gun.CalculateSpreadAngleRadians(m_Player.equipment.quality / 100f, aimAlpha) * Mathf.Rad2Deg;
                return 0.5f * cone;
            }
            catch (System.Exception)
            {
                return 0f;
            }
        }

        private float PvpAimSigmaLessSpread(float sigma)
        {
            var spread = PvpGunSpreadSigmaDegrees();
            return Mathf.Sqrt(Mathf.Max(sigma * sigma - spread * spread, PvpAimFloorDegrees * PvpAimFloorDegrees));
        }

        // The sights still coming up: the round would leave with the hip
        // spread the drawn error does not leave room for.
        private static bool IsPvpSightsComingUp(UseableGun gun)
        {
            return gun.isAiming && gun.GetSimulationAimAlpha() < 0.999f;
        }

        // The gun already on him when the fight comes back on.
        private bool IsPvpGunOnHim(Vector3 point)
        {
            var to = point - m_Player.look.aim.position;
            return to.sqrMagnitude > 0.01f
                && Vector3.Angle(AimDirection(m_CurrentYaw, m_CurrentPitch), to) <= PvpOnHimDegrees;
        }
    }
}
