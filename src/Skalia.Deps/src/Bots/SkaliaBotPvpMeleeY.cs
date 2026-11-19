using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y7 (the tester, 17/11: "quando acaba todas as balas eles puxam a
    // arma branca mas nao atacam"). In the test of 17/11 (07:37:44-53)
    // LilJuninho, its Eaglefire empty, took the Rake out for a zombie and
    // stood 8s with the tester 1.2 to 3.6m in front of it. The fight with a
    // player only knew guns: with no round left it never came on.
    //
    // With no gun that can fire (none loaded, nothing to reload with) and
    // him within 10m (the range where 4 of 5 players at low health shot at
    // once instead of healing, rec/pvp19.py), the bot goes at him with the
    // best blade it carries, or the fists, as the tester did to Ghost: standing,
    // sprinting in, swinging once in reach and facing him. The swing lands
    // on the frame the game's own animation lands (as the zombie swings,
    // 0.44s at least between blows, the players' p25).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpMeleeChaseMeters = 10f;
        // 0.1.0-y17.16 (test of 18/11, 23:40:32-37 and 23:44:05-08; the tester:
        // "eu rushei sabendo que ele me escutou e mesmo assim puxou a faca e
        // saiu andando ... cobertura sempre"). AlemaoGamer and blazex, no
        // round left, hidden from the tester who came at them armed: each heard
        // his steps at 15-16m and walked out of the cover at him with the
        // Military Knife ("na faca, fora da linha do olho"), and from 9-10m
        // went at him in the open; both died, 1 and 0 blows. Against a man
        // with a gun the blade waits in the cover for him to come into
        // reach: 1.75m (the Military Knife's Range) and what a sprint covers
        // (about 5 m/s) in the 0.4s the quickest quarter of the players took
        // to fire at a man who came on their screen (rec/pvp9.py). Further
        // off it is the cover's (SkaliaBotPvpNoAmmoY). A man with no gun to
        // be seen is still the blade's from 10m.
        private const float PvpMeleeArmedMeters = 4f;

        private float GetPvpBladeMeters()
        {
            return m_PvpEnemyArmed ? PvpMeleeArmedMeters : PvpMeleeChaseMeters;
        }
        private const float PvpPunchRange = 1.75f;
        private const float PvpMeleeFaceDegrees = 20f;

        private bool m_PvpMelee;
        private bool m_PvpMeleePending;
        private uint m_PvpMeleeImpactSimulation;
        private bool m_PvpPunchRight;
        private float m_PvpMeleeLogAt = float.NegativeInfinity;

        // 0.1.0-y17.12 (the tester, 18/11: "de motoserra ele nao me ataca").
        // The game's saw (Repeated, Bundles/Items/Melee/Chainsaw) cuts while
        // the button is held, on every impact frame of its swing loop
        // (UseableMelee.simulate); a Stop sent the next packet cut it off
        // before the first impact. At 22:30:24 LilJuninho had the tester 0.9 to
        // 1.4m in front of it, the saw down, and only the zombie's body was
        // traced. Now the saw stays down while he is in reach and in front,
        // and each impact frame sends what it cuts.
        private bool m_PvpMeleeRepeating;
        private uint m_PvpMeleeIntervalFrames;
        // Choosing the blade: what it does to a man in a second. The saw cut
        // every 0.40-0.53s in the bot's own recording (LilJuninho,
        // 22:30:51-52, 18/11); a single swing every 0.65s, the players'
        // median between blows (08/11). Player_Damage from the item files:
        // the saw 25 a cut, the Military Knife 50 a swing.
        private const float PvpRepeatedCutSeconds = 0.45f;
        private const float PvpSwingSeconds = 0.65f;

        private static float PvpMeleeDamagePerSecond(ItemMeleeAsset melee)
        {
            var damage = melee.playerDamageMultiplier != null ? melee.playerDamageMultiplier.damage : 0f;
            return damage / (melee.isRepeated ? PvpRepeatedCutSeconds : PvpSwingSeconds);
        }

        private float GetPvpMeleeRange()
        {
            return m_Player.equipment.asset is ItemMeleeAsset melee ? melee.range : PvpPunchRange;
        }

        // The legs and the head of the melee fight, from TickPvpCore.
        private void TickPvpMeleeMove(float distance)
        {
            m_PvpPosture = 0;
            m_PostureKey = 0;
            var reach = GetPvpMeleeRange();
            // 0.1.0-y16.2: a fence or a coil the jump takes is a way ahead
            // (SkaliaBotPvpHopY).
            if (distance > reach * 0.85f && IsPvpWayAhead())
            {
                m_PvpStepAnalog = 0x12;
                m_IsSprinting = distance > 3.5f;
                // 0.2.2y8: over the pallet, not into it (SkaliaBotPvpChaseY).
                TryPvpHop(0x12, Time.realtimeSinceStartup);
            }
            else
            {
                m_PvpStepAnalog = 0x11;
                m_IsSprinting = false;
            }
        }

        private void NotePvpMeleeStart(Player target, float distance, float now)
        {
            if (now < m_PvpMeleeLogAt)
            {
                return;
            }
            m_PvpMeleeLogAt = now + 10f;
            BotLog(LogLevel.Information,
                "PvP: sem bala para {Name} a {Distance:0}m; indo para cima com {Weapon}.",
                target.channel?.owner?.playerID.characterName ?? "?",
                distance,
                FindBestPvpMelee(out _, out _, out _)?.itemName ?? "os punhos");
        }

        private ItemMeleeAsset? FindBestPvpMelee(out byte page, out byte x, out byte y)
        {
            page = 0;
            x = 0;
            y = 0;
            ItemMeleeAsset? best = null;
            var bestDamage = 0f;
            for (byte p = 0; p < PlayerInventory.STORAGE; p++)
            {
                for (byte index = 0; index < m_Player.inventory.getItemCount(p); index++)
                {
                    var jar = m_Player.inventory.getItem(p, index);
                    // 0.1.0-y17.12: what it does to a man, not to a zombie.
                    if (jar?.GetAsset() is ItemMeleeAsset melee
                        && CanBecomeHoldable(p, melee)
                        && PvpMeleeDamagePerSecond(melee) > bestDamage)
                    {
                        best = melee;
                        bestDamage = PvpMeleeDamagePerSecond(melee);
                        page = p;
                        x = jar.x;
                        y = jar.y;
                    }
                }
            }
            return best;
        }

        // The packet of the melee fight, from ConfigurePvpInputCore.
        private bool ConfigurePvpMeleeInput(WalkingPlayerInputPacket packet, Player target, float now)
        {
            ReleasePvpTrigger(packet, now);
            if (m_PvpAimHeld || m_GunAimHeld)
            {
                packet.secondaryAttack = EAttackInputFlags.Stop;
                m_PvpAimHeld = false;
                m_GunAimHeld = false;
                return true;
            }
            if (m_SecondaryAttackHeld)
            {
                packet.secondaryAttack = EAttackInputFlags.Stop;
                m_SecondaryAttackHeld = false;
            }
            var equipment = m_Player.equipment;
            var packetSimulation = m_Player.input.simulation + (uint)m_Player.input.serversidePackets.Count;
            if (m_PvpMeleePending)
            {
                if (packetSimulation < m_PvpMeleeImpactSimulation)
                {
                    return true;
                }
                if (m_PvpMeleeRepeating)
                {
                    // The saw: a cut on each impact frame while he is in
                    // reach and in front; else the button comes up.
                    if (equipment.asset is ItemMeleeAsset saw
                        && saw.isRepeated
                        && IsPvpMeleeOnHim(target, saw.range))
                    {
                        if (TryTracePvpSwing(saw.range, ERaycastInfoUsage.Melee, out var cut))
                        {
                            packet.clientsideInputs = new List<PlayerInputPacket.ClientRaycast>(1) { cut };
                        }
                        m_PvpMeleeImpactSimulation = packetSimulation + m_PvpMeleeIntervalFrames;
                        m_PvpShots++;
                        m_PvpLastPullAt = now;
                        return true;
                    }
                    packet.primaryAttack = EAttackInputFlags.Stop;
                    m_PrimaryAttackHeld = false;
                    m_PvpMeleeRepeating = false;
                    m_PvpMeleePending = false;
                    m_NextAttackTime = now + 0.1f;
                    return true;
                }
                m_PvpMeleePending = false;
                if (equipment.asset is ItemMeleeAsset impactMelee
                    && TryTracePvpSwing(impactMelee.range, ERaycastInfoUsage.Melee, out var hit))
                {
                    packet.clientsideInputs = new List<PlayerInputPacket.ClientRaycast>(1) { hit };
                }
                return true;
            }
            // The weapon: the best blade carried, else bare hands.
            var best = FindBestPvpMelee(out var page, out var bx, out var by);
            if (best != null && equipment.asset != best)
            {
                if (!equipment.isBusy && now >= m_NextWeaponSelectionAt)
                {
                    m_NextWeaponSelectionAt = now + 0.5f;
                    equipment.ServerEquip(page, bx, by);
                }
                return true;
            }
            if (best == null && equipment.HasValidUseable)
            {
                if (!equipment.isBusy && now >= m_NextWeaponSelectionAt)
                {
                    m_NextWeaponSelectionAt = now + 0.5f;
                    equipment.dequip();
                }
                return true;
            }
            if ((best != null && !equipment.IsEquipAnimationFinished) || equipment.isBusy || now < m_NextAttackTime)
            {
                return true;
            }
            var reach = best != null ? best.range : PvpPunchRange;
            if (!IsPvpMeleeOnHim(target, reach))
            {
                return true;
            }
            if (best == null)
            {
                // Fists: left and right in turn; the blow lands on this packet.
                if (m_PvpPunchRight)
                {
                    packet.secondaryAttack = EAttackInputFlags.Start;
                    m_SecondaryAttackHeld = true;
                }
                else
                {
                    packet.primaryAttack = EAttackInputFlags.Start;
                    m_PrimaryAttackHeld = true;
                }
                m_PvpPunchRight = !m_PvpPunchRight;
                if (TryTracePvpSwing(PvpPunchRange, ERaycastInfoUsage.Punch, out var punch))
                {
                    packet.clientsideInputs = new List<PlayerInputPacket.ClientRaycast>(1) { punch };
                }
                m_NextAttackTime = now + 0.55f;
            }
            else
            {
                packet.primaryAttack = EAttackInputFlags.Start;
                m_PrimaryAttackHeld = true;
                var length = m_Player.animator.GetAnimationLength(best.isRepeated ? "Start_Swing" : "Weak");
                var frames = Mathf.Max(1, Mathf.CeilToInt(length / PlayerInput.RATE));
                m_PvpMeleeIntervalFrames = (uint)Mathf.Max(1, Mathf.FloorToInt(frames * best.weak) + 1);
                m_PvpMeleeImpactSimulation = packetSimulation + m_PvpMeleeIntervalFrames;
                m_PvpMeleePending = true;
                m_PvpMeleeRepeating = best.isRepeated;
                m_NextAttackTime = now + Mathf.Max(0.44f, length + 0.08f);
            }
            m_PvpShots++;
            m_PvpLastPullAt = now;
            return true;
        }

        // The man the bot fights, nearer on the swing's line than the zombie
        // it swings at (or with no zombie on the line).
        private float m_PvpSwingOnManLogAt = float.NegativeInfinity;

        private bool TryTraceMeleeSwingOnMan(
            Vector3 origin,
            Vector3 direction,
            float reach,
            ERaycastInfoUsage usage,
            out PlayerInputPacket.ClientRaycast result)
        {
            result = default;
            if (m_PvpTarget == null
                || !TryHitPvpTarget(origin, direction, reach, out var distance, out var info)
                || !HasClearLineToPoint(info.point))
            {
                return false;
            }
            result = new PlayerInputPacket.ClientRaycast(info, usage);
            var now = Time.realtimeSinceStartup;
            if (now >= m_PvpSwingOnManLogAt)
            {
                m_PvpSwingOnManLogAt = now + 5f;
                BotLog(LogLevel.Information,
                    "PvP: golpe com {Weapon} pegou em {Name} a {Distance:0.#}m, que estava na frente do zumbi.",
                    m_Player.equipment.asset?.itemName ?? "os punhos",
                    m_PvpTarget.channel?.owner?.playerID.characterName ?? "?",
                    distance);
            }
            return true;
        }

        private bool IsPvpMeleeOnHim(Player target, float reach)
        {
            var to = GetPvpAimPoint(target) - m_Player.look.aim.position;
            return new Vector2(to.x, to.z).magnitude <= reach + 0.3f
                && Vector3.Angle(AimDirection(m_CurrentYaw, m_CurrentPitch), to) <= PvpMeleeFaceDegrees;
        }

        private bool TryTracePvpSwing(float range, ERaycastInfoUsage usage, out PlayerInputPacket.ClientRaycast result)
        {
            result = default;
            var origin = m_Player.look.aim.position;
            var direction = AimDirection(m_CurrentYaw, m_CurrentPitch);
            if (!TryHitPvpTarget(origin, direction, range + 0.25f, out _, out var info)
                || !HasClearLineToPoint(info.point))
            {
                return false;
            }
            result = new PlayerInputPacket.ClientRaycast(info, usage);
            return true;
        }
    }
}
