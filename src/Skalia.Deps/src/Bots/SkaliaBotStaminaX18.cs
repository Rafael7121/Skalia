using Microsoft.Extensions.Logging;
using SDG.Unturned;

namespace SkaliaDeps
{
    // x18, from the x17 test on Skalia (Normal, 15/11): Vultox spent 14% of
    // the time out of breath and LilJuninho13 10% (players 1.1%), 7 of the 9
    // deaths came out of breath, and from 0 back to 30 took the bots 19.7s
    // against 3.4s for the players.
    //
    // The game (PlayerLife.simulate) gives stamina back only 33 ticks
    // (2.6s) after the last point spent sprinting, and the stance goes back
    // to SPRINT whenever the key is held with stamina above 0
    // (PlayerStance). A bot holding the key spent each point as it came
    // back and restarted the 2.6s wait: stuck at 0. In the recordings, after
    // running out the players sprint again at a median of 54 stamina (13%
    // under 10); the bots at 14 on Skalia and 3 on the public server (41% and 89%
    // under 10). Out of breath, the bot lets go of sprint until 50.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const byte StaminaResumeSprint = 50;
        private bool m_StaminaRecovering;
        private float m_StaminaRecoveringSince;

        // True while the sprint key stays up after running out.
        private bool IsStaminaRecovering()
        {
            var stamina = m_Player.life.stamina;
            if (!m_StaminaRecovering)
            {
                if (stamina == 0)
                {
                    m_StaminaRecovering = true;
                    m_StaminaRecoveringSince = UnityEngine.Time.realtimeSinceStartup;
                }
                return m_StaminaRecovering;
            }
            if (stamina >= StaminaResumeSprint)
            {
                m_StaminaRecovering = false;
                BotLog(LogLevel.Debug,
                    "Folego de volta a {Stamina} em {Seconds:0.0}s sem correr; pode correr de novo.",
                    stamina,
                    UnityEngine.Time.realtimeSinceStartup - m_StaminaRecoveringSince);
            }
            return m_StaminaRecovering;
        }
    }
}
