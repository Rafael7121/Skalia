using SDG.Unturned;

namespace SkaliaDeps
{
    // 0.2.2y11 (the tester, 17/11: "no tiro ele esta fraco nao atira direito
    // nao e questao de erro e simplesmente segurar o gatilho"; and "o bot
    // conseguiu matar o outro na pancada sem receber um tiro").
    //
    // The pulls of the players of 08/11 (0.1-0.8s, SkaliaBotPvpFeelY) were
    // all at 40m or more with the automatics. the tester's own, today (t26-t31,
    // rafhold.py): from 8 to 56m taps of 0.1-0.3s, one of 0.8s and one of
    // 1.1s; the one at 5m (08:36:50, Cobra, Ghost coming with the knife)
    // held 1.8s, 17 rounds, until Ghost fell. So within 5m an automatic is
    // held down while the gun stays on him and he is seen, up to the
    // magazine; farther the pulls stay the players'.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpSprayMeters = 5f;
        private const float PvpSpraySeconds = 4f;

        private static bool IsPvpSprayRange(UseableGun gun, float distance)
        {
            return distance <= PvpSprayMeters && gun.firemode == EFiremode.AUTO;
        }
    }
}
