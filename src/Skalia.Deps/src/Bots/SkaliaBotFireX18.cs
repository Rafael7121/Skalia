using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // x18, from the x17 test on Skalia (Normal, 15/11): a burner seen once
    // stayed the priority target at any distance. LilJuninho13 ran after one
    // 395m away (09:13) and another 1227m away (09:21) with a knife, 2.5 min
    // sprinting, out of breath; in the x16 test thigas went after one 800m
    // away. The bot only marks a burner within its sight (65m,
    // FireZombieVisualRadiusMeters); past that it forgets it, and a burner
    // that comes back into sight is marked again.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private float m_NextFarFireLogAt;

        private void ForgetFarFireZombie(Vector3 position, float now)
        {
            var fire = m_RememberedFireZombie;
            if (fire == null || fire.isDead || !fire.gameObject.activeInHierarchy)
            {
                return;
            }
            var distance = (fire.transform.position - position).GetHorizontalMagnitude();
            if (distance <= FireZombieVisualRadiusMeters)
            {
                return;
            }
            m_RememberedFireZombie = null;
            if (m_TargetZombie == fire)
            {
                m_TargetZombie = null;
                if (m_ObjectiveType == ObjectiveType.Combat)
                {
                    ClearObjective();
                }
            }
            if (m_ObjectiveType == ObjectiveType.RepositionFire)
            {
                ClearObjective();
            }
            if (now >= m_NextFarFireLogAt)
            {
                m_NextFarFireLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "Zumbi de fogo a {Distance:0}m, fora da vista ({Radius:0}m): esquecido.",
                    distance,
                    FireZombieVisualRadiusMeters);
            }
        }
    }
}
