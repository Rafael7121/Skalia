using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y16.1 (the tester, 17/11, after the short test of 17:17 with
    // ToxicSniper: "deixe estavel o que foi feito").
    //
    // - 17:19:53: it took an Eaglefire with 5 rounds and no magazine for it
    //   and dropped the Cobra with 28 rounds, then the 6 Cobra magazines
    //   ("nao serve em nenhuma arma"). At 17:21:11 it had 2 rounds against
    //   the tester at 11m and none after. A usable weaker gun now stays while
    //   the better one has less than a magazine of its own (or less than the
    //   weaker one carries), loaded and in magazines that fit it.
    // - 17:21:15: out of rounds, it ran and left him (the fight's end), and
    //   with it went the 5 minutes he is remembered (SkaliaBotPvpSpotY): it
    //   healed walking where he could see it and he killed it at 17:21:29.
    //   Left with no rounds, the bot still knows where it last saw him for
    //   those 5 minutes: the bandage looks for a spot he cannot see from
    //   there (or runs away from him), food waits, as in the fight.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private float m_PvpLeftUntil = float.NegativeInfinity;
        // 0.1.0-y16.2: the last hurt from a player or a bot, for the way
        // back after a death (RecordDeathForRecovery).
        private float m_LastHurtByPlayerAt = float.NegativeInfinity;
        private string m_LastHurtByPlayerName = "?";

        private void NotePvpLeftWithoutRounds(bool known, float now)
        {
            if (!known)
            {
                return;
            }
            m_PvpLeftUntil = m_PvpLastSeenAt + PvpMemorySeconds;
            BotLog(LogLevel.Information,
                "PvP: deixei ele para tras {Why}, mas lembro onde vi ele por {Seconds:0}s (cura escondida dali, comida espera).",
                m_PvpNoAmmoOutOfReach ? "fora do alcance da minha arma" : "sem bala",
                Mathf.Max(0f, m_PvpLeftUntil - now));
        }

        private bool IsPvpLeftRecently(float now)
        {
            return m_PvpTarget == null && now < m_PvpLeftUntil;
        }

        // The enemy whose last seen place counts for hiding.
        private bool IsPvpShooterKnown(float now)
        {
            return ((m_PvpTarget != null && m_PvpAware) || IsPvpLeftRecently(now))
                && now - m_PvpLastSeenAt <= PvpMemorySeconds;
        }

        private int CarriedGunRounds(byte page, ItemJar jar, ItemGunAsset gun)
        {
            var equipment = m_Player.equipment;
            var state = equipment.equippedPage == page && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y
                ? equipment.state
                : jar.item.state;
            var loaded = state != null && state.Length > 10 ? state[10] : 0;
            return loaded + CountRoundsFor(gun);
        }

        // The best carried gun has a magazine of rounds, or as many as the
        // weaker one about to go.
        private bool IsBestGunStocked(byte weakerPage, ItemJar weaker, ItemGunAsset weakerGun, float best)
        {
            var need = CarriedGunRounds(weakerPage, weaker, weakerGun);
            var stocked = false;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar == null || jar == weaker
                        || !(jar.GetAsset() is ItemGunAsset gun)
                        || GunScore(gun) < best - 0.01f
                        || !IsUsableCarriedGun(page, jar, gun)
                        || !CanBecomeHoldable(page, gun))
                    {
                        continue;
                    }
                    var rounds = CarriedGunRounds(page, jar, gun);
                    if (rounds >= Mathf.Min(Mathf.Max(1, (int)gun.ammoMax), need))
                    {
                        stocked = true;
                    }
                }
            }
            return stocked;
        }
    }
}
