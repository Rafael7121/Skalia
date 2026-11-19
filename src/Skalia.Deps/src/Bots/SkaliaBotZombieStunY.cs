using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y (the tester, 17/11: "melhore o combate deles contra os zumbis ...
    // a questão do stun é a skill overkill e não a dificuldade").
    //
    // The game (Zombie.askDamage, DamageTool.damageZombie, UseableMelee):
    // a blow stuns a zombie for 1s when it is over the zombie's threshold
    // (20 a common one, 150 the Mega). The blow is the weapon's damage on
    // that limb (skull x1.1, spine x0.6 on the blades) x Overkill
    // (1 + 0.5 x level/7) x the blade's wear (under 50% quality: 0.5 + q)
    // x the zombie's hat x the server's Armor_Multiplier. A punch is 15
    // x1.1: it stuns only with Overkill 4 or more.
    //
    // The recordings (08/11 players against the x19 bots, 16/11):
    // - The players had every skill at the top (their Skills.dat on the
    //   public server 3): Military Knife on the head 70-82, 105-123 strong, a
    //   punch 21-24. The bots never spent their experience (Vultox: 755
    //   saved, Overkill 0) and swung worn blades: knife on the head 26-47,
    //   on the body 14-26, so a body blow did not stun.
    // - Damage taken per zombie killed: players 0.17 from a common one and
    //   2.8 from a sprinter; bots 1.5 and 7.9.
    // - The first swing at a zombie coming: players at 1.8m (common, 6 m/s)
    //   and 2.8m (sprinter, 12 m/s), so the blade lands as it gets in
    //   reach; bots at 1.4m and 1.2m, and the sprinter (which strikes the
    //   moment it is in its own reach, no 0.5s wait) hit them first.
    // - Players hit the head with 96% of the blade blows; bots 86% (one
    //   swing in ten went at the body on purpose).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // the tester: the stun is the Overkill. After it, the rest in the
        // game's menu order: the players' saves have all of them at the top.
        private const float SkillSpendSeconds = 3f;
        // Players' first swing at a sprinter: p75 4.1m with the Military
        // Knife (reach 1.75m), so up to 2.4m past the reach.
        private const float MeleeLeadMaxMetersPastReach = 2.4f;
        private const float MeleeLeadMinClosingSpeed = 1f;

        private float m_NextSkillSpendAt;
        private Zombie? m_SwingLeadZombie;
        private float m_SwingLeadDistance;
        private float m_SwingLeadAt;
        private float m_SwingLeadSpeed;
        private float m_NextSwingLeadLogAt;

        // ---- experience into skills, as the players do ----

        private void TrySpendSkillPoints(float now)
        {
            if (now < m_NextSkillSpendAt)
            {
                return;
            }
            m_NextSkillSpendAt = now + SkillSpendSeconds;
            var skills = m_Player.skills;
            if (skills == null
                || skills.skills == null
                || !skills.doesLevelAllowSkills
                || m_ObjectiveType == ObjectiveType.Combat
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_PvpTarget != null)
            {
                return;
            }
            if (!TryPickSkillToUpgrade(skills, out var speciality, out var index))
            {
                return;
            }
            var skill = skills.skills[speciality][index];
            var before = skill.level;
            var cost = skills.cost(speciality, index);
            // The same request the skills menu sends, one level at a time.
            skills.ReceiveUpgradeRequest((byte)speciality, (byte)index, false);
            if (skill.level > before)
            {
                BotLog(LogLevel.Information,
                    "Habilidade: {Skill} {Before} -> {After} por {Cost} de experiencia (sobra {Left}).",
                    DescribeSkill(speciality, index),
                    before,
                    skill.level,
                    cost,
                    skills.experience);
            }
        }

        private static bool TryPickSkillToUpgrade(PlayerSkills skills, out int speciality, out int index)
        {
            speciality = 0;
            index = 0;
            if (CanUpgradeSkill(skills, 0, 0))
            {
                return skills.experience >= skills.cost(0, 0);
            }
            for (var s = 0; s < skills.skills.Length; s++)
            {
                for (var i = 0; i < skills.skills[s].Length; i++)
                {
                    if (!CanUpgradeSkill(skills, s, i))
                    {
                        continue;
                    }
                    // In order: wait for this one rather than buy a cheaper
                    // one further down.
                    speciality = s;
                    index = i;
                    return skills.experience >= skills.cost(s, i);
                }
            }
            return false;
        }

        private static bool CanUpgradeSkill(PlayerSkills skills, int speciality, int index)
        {
            var skill = skills.skills[speciality][index];
            return skill.level < skill.GetClampedMaxUnlockableLevel();
        }

        private static string DescribeSkill(int speciality, int index)
        {
            if (speciality == 0)
            {
                switch (index)
                {
                    case 0: return "Overkill";
                    case 1: return "Sharpshooter";
                    case 2: return "Dexterity";
                    case 3: return "Cardio";
                    case 4: return "Exercise";
                    case 5: return "Diving";
                    case 6: return "Parkour";
                }
            }
            if (speciality == 1 && System.Enum.IsDefined(typeof(EPlayerDefense), index))
            {
                return ((EPlayerDefense)index).ToString();
            }
            if (speciality == 2 && System.Enum.IsDefined(typeof(EPlayerSupport), index))
            {
                return ((EPlayerSupport)index).ToString();
            }
            return "habilidade " + speciality + "/" + index;
        }

        // ---- the stun, worked out the way the game does it ----

        private float OverkillMultiplier()
        {
            return 1f + m_Player.skills.mastery(0, 0) * 0.5f;
        }

        private static float MeleeQualityFactor(byte quality)
        {
            var q = quality / 100f;
            return q < 0.5f ? 0.5f + q : 1f;
        }

        // A blade's blow on the head with this wear, before the zombie's hat.
        private float MeleeHeadBlow(ItemMeleeAsset melee, byte quality)
        {
            if (melee.zombieDamageMultiplier == null)
            {
                return 0f;
            }
            return melee.zombieDamageMultiplier.multiply(ELimb.SKULL)
                * OverkillMultiplier()
                * MeleeQualityFactor(quality)
                * Provider.modeConfigData.Zombies.Armor_Multiplier;
        }

        // Does this blow stun the zombie (null: a common one, no hat)?
        private static bool BlowStuns(float blow, Zombie? zombie)
        {
            var config = Provider.modeConfigData.Zombies;
            if (!config.Can_Stun || config.Only_Critical_Stuns)
            {
                return false;
            }
            if (zombie != null)
            {
                blow *= DamageTool.getZombieArmor(ELimb.SKULL, zombie);
            }
            var threshold = zombie != null ? zombie.getStunDamageThreshold() : 20;
            return Mathf.FloorToInt(blow) > threshold;
        }

        private bool MeleeStuns(ItemMeleeAsset melee, byte quality, Zombie? zombie)
        {
            return !melee.isRepeated && BlowStuns(MeleeHeadBlow(melee, quality), zombie);
        }

        private bool BestCarriedMeleeStuns()
        {
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar?.GetAsset() is ItemMeleeAsset melee && MeleeStuns(melee, jar.item.quality, null))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // What a blade really does, wear included (UseableMelee: under 50%
        // quality the blow is 0.5 + quality).
        private static float MeleeHitDamage(ItemMeleeAsset melee, byte quality)
        {
            return MeleeDamage(melee) * MeleeQualityFactor(quality);
        }

        // ---- the swing that meets the zombie as it gets in reach ----

        // Called with the swing's reach check: true when the zombie is not in
        // reach yet but will be when the blade lands. Only a zombie coming at
        // the bot: a spitter stops at range and a swing at it is a miss.
        private bool IsMeleeSwingLeadReady(Zombie target, float distance, float reach, float impactSeconds, float now)
        {
            var dt = now - m_SwingLeadAt;
            if (m_SwingLeadZombie == target && dt > 0.02f && dt < 0.5f)
            {
                var speed = (m_SwingLeadDistance - distance) / dt;
                m_SwingLeadSpeed = Mathf.Lerp(m_SwingLeadSpeed, speed, 0.5f);
            }
            else if (m_SwingLeadZombie != target || dt >= 0.5f)
            {
                m_SwingLeadSpeed = 0f;
            }
            m_SwingLeadZombie = target;
            m_SwingLeadDistance = distance;
            m_SwingLeadAt = now;
            if (distance <= reach
                || distance > reach + MeleeLeadMaxMetersPastReach
                || m_SwingLeadSpeed < MeleeLeadMinClosingSpeed
                || target.isMega
                || target.speciality == EZombieSpeciality.ACID
                || !IsHuntingBot(target))
            {
                return false;
            }
            var landing = distance - m_SwingLeadSpeed * impactSeconds;
            if (landing > reach * 0.95f)
            {
                return false;
            }
            if (now >= m_NextSwingLeadLogAt)
            {
                m_NextSwingLeadLogAt = now + 10f;
                BotLog(LogLevel.Debug,
                    "Corpo a corpo: golpe adiantado no zumbi {Kind} a {Distance:0.0}m vindo a {Speed:0.0} m/s; a lamina chega quando ele entra no alcance ({Reach:0.00}m).",
                    DescribeZombieKind(target),
                    distance,
                    m_SwingLeadSpeed,
                    reach);
            }
            return true;
        }
    }
}
