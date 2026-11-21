using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y16 (the tester, 17/11: "falta adicionar ... a questao dele entender
    // que se ele atirar a posicao que ele esta fica vulneravel" / "isso se
    // encaixa em uma situacao onde ambos nao sabem de si. um atirou o outro
    // escutou. se o outro atirar ele vai se revelar" / "agora ele esquece
    // muito rapido do combate e larga o player").
    //
    // The players of 08/11 fighting for real (rec/m16.py):
    // - The spot a shooter fires from (46 of them, rounds within 3m of the
    //   first): p25 4 rounds, median 9, p75 14, p90 21, over a median 1.1s;
    //   then he was more than 3m away a median 1.0s after the last one
    //   (0.7s with the other one looking at him); 44 of 46 left. The bot
    //   now draws how many rounds it fires from a spot from those, then
    //   holds the trigger and steps aside (the clear side, crouched if it
    //   lay prone) until it is 3m off, 2.5s at most.
    // - A shot heard from someone not seen (30-300m, off the screen, no
    //   fight in the 30s before; 12 times): none fired in the next 30s (9%
    //   at any moment out of a fight), the first that did at 40s, 5 of 12
    //   within 120s. The bot that hears one now keeps its gun quiet 40 to
    //   120s: a common zombie gets the blade. A player seen, a hit taken or
    //   a zombie it has no blade for still get the gun.
    // - After losing the other one (63 times with 5 minutes of recording
    //   after): facing where he was 64% of the first 30s and still 40% at
    //   4-5 minutes (25% by chance); 37 met the same one again within 5
    //   minutes (median 37s, up to 216s); food in hand 2 times in 30s,
    //   median 137s. The fight is now remembered 5 minutes (60s before),
    //   and food waits for it unless starving.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private static readonly float[] s_PvpSpotQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
        private static readonly float[] s_PvpSpotRounds = { 1f, 4f, 9f, 14f, 21f, 30f };
        private const float PvpSpotMeters = 3f;
        private const float PvpSpotLeaveMaxSeconds = 2.5f;
        private const float PvpSilenceMinMeters = 30f;
        private const float PvpSilenceMinSeconds = 40f;
        private const float PvpSilenceMaxSeconds = 120f;

        private Vector3 m_PvpSpot;
        private int m_PvpSpotRounds;
        private int m_PvpSpotLimit;
        private bool m_PvpSpotBurned;
        private float m_PvpSpotBurnedAt;
        private int m_PvpSpotSide = 1;
        private float m_PvpSilentUntil = float.NegativeInfinity;

        // From the shot loop: the rounds that left in a gun fight.
        private void NotePvpRounds(int fired)
        {
            if (fired <= 0 || !m_PvpEngaged || m_PvpMelee)
            {
                return;
            }
            var now = Time.realtimeSinceStartup;
            var position = m_Player.transform.position;
            if (m_PvpSpotRounds == 0 || (position - m_PvpSpot).GetHorizontalMagnitude() > PvpSpotMeters)
            {
                m_PvpSpot = position;
                m_PvpSpotRounds = 0;
                m_PvpSpotLimit = Mathf.Max(1, Mathf.RoundToInt(DrawFromQuantiles(s_PvpSpotQuantiles, s_PvpSpotRounds) / SkaliaBotModifiers.Repositioning));
                m_PvpSpotBurned = false;
            }
            m_PvpSpotRounds += fired;
            if (!m_PvpSpotBurned && m_PvpSpotRounds >= m_PvpSpotLimit)
            {
                m_PvpSpotBurned = true;
                m_PvpSpotBurnedAt = now;
                m_PvpSpotSide = PickPvpSpotSide();
                BotLog(LogLevel.Information,
                    "PvP: {Rounds} tiros daqui em {Name}; posicao queimada, saindo pela {Side}.",
                    m_PvpSpotRounds,
                    m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                    m_PvpSpotSide < 0 ? "esquerda" : "direita");
            }
        }

        private int PickPvpSpotSide()
        {
            var first = Random.value < 0.5f ? -1 : 1;
            if (IsPvpStepClear(SideAnalog(first)))
            {
                return first;
            }
            return IsPvpStepClear(SideAnalog(-first)) ? -first : first;
        }

        private static byte SideAnalog(int side)
        {
            return (byte)(((side + 1) << 4) | 1);
        }

        // Still on the spot it gave away: no round from here.
        private bool IsPvpSpotBurned(float now)
        {
            if (!m_PvpSpotBurned)
            {
                return false;
            }
            var away = (m_Player.transform.position - m_PvpSpot).GetHorizontalMagnitude() > PvpSpotMeters;
            if (away || now - m_PvpSpotBurnedAt > PvpSpotLeaveMaxSeconds)
            {
                m_PvpSpotBurned = false;
                m_PvpSpotRounds = 0;
                return false;
            }
            return true;
        }

        // The step aside off the burned spot, the aim kept on him.
        private bool TickPvpSpotLeave(float now)
        {
            if (!IsPvpSpotBurned(now))
            {
                return false;
            }
            if (m_PvpPosture == 2 || m_Player.stance.stance == EPlayerStance.PRONE)
            {
                m_PostureKey = 1;
            }
            var analog = SideAnalog(m_PvpSpotSide);
            if (!IsPvpStepClear(analog))
            {
                m_PvpSpotSide = -m_PvpSpotSide;
                analog = SideAnalog(m_PvpSpotSide);
                if (!IsPvpStepClear(analog))
                {
                    // Neither side: back, away from him.
                    analog = 0x10;
                }
            }
            m_PvpStepAnalog = analog;
            return true;
        }

        private void ResetPvpSpot()
        {
            m_PvpSpotRounds = 0;
            m_PvpSpotBurned = false;
        }

        // A shot from someone it does not see, not at it.
        private void NotePvpShotHeard(string name, float distance, bool aimed)
        {
            if (aimed || distance < PvpSilenceMinMeters)
            {
                return;
            }
            var now = Time.realtimeSinceStartup;
            if (now < m_PvpSilentUntil)
            {
                return;
            }
            var seconds = Random.Range(PvpSilenceMinSeconds, PvpSilenceMaxSeconds);
            m_PvpSilentUntil = now + seconds;
            BotLog(LogLevel.Information,
                "Ouvi tiro de quem nao vejo a {Distance:0}m; em silencio por {Seconds:0}s (zumbi comum na faca).",
                distance,
                seconds);
        }

        private bool IsPvpSilent()
        {
            return Time.realtimeSinceStartup < m_PvpSilentUntil && m_PvpTarget == null;
        }
    }
}
