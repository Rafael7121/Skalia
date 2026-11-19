using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y14 (the tester, 17/11: "a gente da rajadas para acertar mais
    // tiros mas esta acontecendo o oposto aqui. dão rajadas mas erram
    // todas").
    //
    // The test of 17/11 16:19 (y13): ToxicSniper on the tester at 40-60m, the
    // rounds of a burst climbing 1-4 degrees over the first (pitch -1.7,
    // -3.3, -4.0; -1.2, -3.0, -3.3; -1.9, -2.9), the kick of each round
    // added to what was left of the ones before, since 0.15s of fade does
    // not take it back between rounds 0.1-0.2s apart. A player firing a
    // burst to land more rounds holds the gun on him: each round still
    // carries the kick of the one before it, but it does not climb.
    //
    // 0.1.0-y15 (the tester, 17/11, on the y14 test: "pode fazer", for the kick
    // pulled back between the rounds and no round into the cover). In the
    // y14 test (blazex and LilJuninho13 at 27-34m) the rounds after the first
    // still went 0.8 degrees up: 0.15s of fade leaves 37-50% of a kick at
    // the next round. A player on the mouse brings it back down before the
    // next one: in a gun fight it now fades in 0.04s (8% left at 0.1s).
    //
    // 5 of the 18 rounds within 50m of that test hit a wall or a prop in
    // front of the gun (Hangar_1 at 1m, a tower at 9m): the bot saw him over
    // the cover it was shooting from, but the round's line, with the drawn
    // miss and the kick on it, ran into the cover. The trigger now waits for
    // that line to be clear (walls, props, ground; not a player or a
    // zombie in the way).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpRecoilFadeSeconds = 0.04f;
        private const float PvpMuzzleTargetMarginMeters = 1.5f;
        private bool m_PvpMuzzleClear = true;

        private bool IsPvpRecoilPulled()
        {
            return m_PvpEngaged && !m_PvpMelee;
        }

        private bool IsPvpMuzzleClear(float distance)
        {
            var reach = distance - PvpMuzzleTargetMarginMeters;
            if (reach <= 0.1f)
            {
                return true;
            }
            var origin = m_Player.look.aim.position;
            var direction = AimDirection(GetSentYaw(), GetSentPitch());
            var hits = Physics.RaycastAll(origin, direction, reach, RayMasks.DAMAGE_SERVER, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (hit.collider == null)
                {
                    continue;
                }
                if (hit.collider.GetComponentInParent<Player>() != null
                    || hit.collider.GetComponentInParent<Zombie>() != null)
                {
                    continue;
                }
                return false;
            }
            return true;
        }

        private void PullPvpRecoilBack()
        {
            if (m_PvpEngaged && !m_PvpMelee)
            {
                ClearRecoil();
            }
        }
    }
}
