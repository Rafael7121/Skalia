using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // x19: food by what it really gives, the virus, the shot line and a
    // loot that stopped moving (x18 test, Normal, 11h).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- item 1: food and drink by what they really give ----
        // UseableConsumeable: food and water are scaled by the item's
        // quality, and under 50% the item infects. The bots picked food by
        // its type: Eggs, Cheese and juices at 10-25% (1 to 3 points each)
        // filled the three slots, counted as stock, and the bot passed real
        // food by. 8 of the 90 deaths of the x18 test were thirst, hunger and
        // infection, 18-33 min after the bar went under 30.
        private const byte MealSafeQuality = 50;
        private const byte MealUrgentBar = 30;

        private static bool IsGoodMealQuality(ItemAsset? asset, byte quality, bool water)
        {
            return asset is ItemConsumeableAsset meal
                && quality >= MealSafeQuality
                && Mathf.Floor((water ? meal.water : meal.food) * (quality / 100f)) >= 5f;
        }

        private static bool IsGoodMealJar(ItemJar? jar, bool water)
        {
            return jar != null && IsGoodMealQuality(jar.GetAsset(), jar.item.quality, water);
        }

        private static bool IsGoodGroundMeal(ItemAsset asset, byte quality)
        {
            return IsGoodMealQuality(asset, quality, true) || IsGoodMealQuality(asset, quality, false);
        }

        // Food or a drink the bot lacks first; under 30 of the bar, before
        // weapons too. An infection item when the virus is falling, also.
        private int AdjustNeedLootPriority(ItemAsset asset, int priority, bool needFood, bool needWater)
        {
            var life = m_Player.life;
            if ((needFood && asset.type == EItemType.FOOD) || (needWater && asset.type == EItemType.WATER))
            {
                priority = Mathf.Min(priority, 1);
                if ((needFood && asset.type == EItemType.FOOD && life.food < MealUrgentBar)
                    || (needWater && asset.type == EItemType.WATER && life.water < MealUrgentBar))
                {
                    priority = -1;
                }
            }
            if (asset is ItemMedicalAsset medical
                && medical.disinfectant > 0
                && IsVirusFalling()
                && !HasDisinfectant())
            {
                priority = -1;
            }
            // 0.1.0-y16.2: every gun carried empty, what loads one comes
            // with the guns, before medicine and clothes. ToxicSniper went
            // 18 min with an empty Peacemaker and no magazine (17:34-17:52),
            // picking up bandages, a splint, food, clothes and a knife; 23 of
            // the 87 fights of the session ended with no round left.
            if (priority > 0
                && (asset is ItemMagazineAsset || asset.type == EItemType.SUPPLY || IsAmmoBox(asset.id))
                && IsEveryCarriedGunEmpty()
                && ((asset is ItemMagazineAsset magazine && CarriesGunFor(magazine)) || IsAmmoBoxForCarriedGun(asset.id)))
            {
                priority = 0;
            }
            return priority;
        }

        private float m_EmptyGunsCheckedAt = float.NegativeInfinity;
        private bool m_EmptyGuns;

        // A gun carried and none of them loaded or with rounds to load.
        private bool IsEveryCarriedGunEmpty()
        {
            var now = Time.realtimeSinceStartup;
            if (now - m_EmptyGunsCheckedAt < 0.5f)
            {
                return m_EmptyGuns;
            }
            m_EmptyGunsCheckedAt = now;
            m_EmptyGuns = false;
            if (HasLoadedRangedWeapon())
            {
                return false;
            }
            for (byte page = 0; page < PlayerInventory.STORAGE && !m_EmptyGuns; page++)
            {
                for (byte index = 0; index < m_Player.inventory.getItemCount(page); index++)
                {
                    if (m_Player.inventory.getItem(page, index)?.GetAsset() is ItemGunAsset)
                    {
                        m_EmptyGuns = true;
                        break;
                    }
                }
            }
            return m_EmptyGuns;
        }

        // ---- item 2: the virus ----
        // PlayerLife.simulate: under Virus_Infect the virus drops by itself,
        // and at 0 it takes health until death. The bots never read it:
        // TheGhostSniper went from 70 to 0 health without a hit, using only
        // bandages. Antibiotics, Vitamins and Vaccine carry Disinfectant.
        private float m_NextVirusLogAt;
        private byte m_TreatmentVirusBefore;

        private static int GetVirusInfectLevel()
        {
            var players = Provider.modeConfigData?.Players;
            return players != null ? (int)players.Virus_Infect : 50;
        }

        private bool IsVirusFalling()
        {
            return m_Player.life.virus < GetVirusInfectLevel();
        }

        private bool HasDisinfectant()
        {
            return TryFindDisinfectant(0, out _, out _, out _);
        }

        // The one that brings the virus back over the line with the least
        // waste; with none that does, the strongest.
        private bool TryFindDisinfectant(int need, out byte bestPage, out ItemJar bestJar, out ItemMedicalAsset bestAsset)
        {
            bestPage = byte.MaxValue;
            bestJar = null!;
            bestAsset = null!;
            var bestScore = float.MaxValue;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (!(jar?.GetAsset() is ItemMedicalAsset medical) || medical.disinfectant == 0)
                    {
                        continue;
                    }
                    var amount = (int)medical.disinfectant;
                    var score = amount >= need ? amount - need : 1000f + (need - amount);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPage = page;
                        bestJar = jar;
                        bestAsset = medical;
                    }
                }
            }
            return bestJar != null;
        }

        private bool TryTreatVirus(float now)
        {
            var life = m_Player.life;
            var infect = GetVirusInfectLevel();
            if (life.virus >= infect || life.isBleeding)
            {
                return false;
            }
            if (!TryFindDisinfectant(infect - life.virus, out var page, out var jar, out var medical))
            {
                if (now >= m_NextVirusLogAt)
                {
                    m_NextVirusLogAt = now + 30f;
                    BotLog(LogLevel.Information,
                        "Imunidade {Virus} (abaixo de {Infect}, cai sozinha e a 0 tira vida) e nada que desinfete no inventario; vida {Health}.",
                        life.virus,
                        infect,
                        life.health);
                }
                return false;
            }
            if (GetNearestAggroedZombieDistance() < TreatAggroMeters
                || m_CachedNearestZombieDistance < 6f
                || m_Player.equipment.isBusy)
            {
                return false;
            }
            StartTreatment(page, jar, medical, life, false, now);
            m_TreatmentVirusBefore = life.virus;
            m_NextTreatmentAt = now + 1f;
            BotLog(LogLevel.Information,
                "Imunidade {Virus} (abaixo de {Infect}): usando {Item} (desinfeta {Amount}); vida {Health}.",
                life.virus,
                infect,
                medical.itemName,
                medical.disinfectant,
                life.health);
            return true;
        }

        // ---- item 3: in sight is where the bullet goes ----
        // At 11:39 in the x18 test Vultox stood 3 min 55m from a Mega with
        // the sights up and never fired: the line to the head collider was
        // clear (so "in sight": stand and shoot), the line to the point the
        // gun aims at went into a tent (so the trigger never went). With a
        // gun the line that counts is the bullet's; without it the bot walks
        // the route until the shot opens.
        private const float CombatShotLineGraceSeconds = 1.5f;
        private float m_NextShotLineLogAt;

        private bool HasCombatShotLine(Zombie target)
        {
            if (!(m_Player.equipment.asset is ItemGunAsset))
            {
                return HasClearLineToPoint(GetZombieAimPoint(target));
            }
            if (HasClearLineToPoint(GetGunAimPoint(target)))
            {
                return true;
            }
            var now = Time.realtimeSinceStartup;
            if (now >= m_NextShotLineLogAt && m_ObjectiveType == ObjectiveType.Combat)
            {
                m_NextShotLineLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "Sem linha de tiro para o zumbi {Kind} a {Distance:0.#}m (linha ate a cabeca livre: {Head}); andando ate abrir o tiro.",
                    DescribeZombieKind(target),
                    (target.transform.position - m_Player.transform.position).GetHorizontalMagnitude(),
                    HasClearLineToPoint(GetZombieAimPoint(target)));
            }
            return false;
        }

        // ---- item 5: back to the body only for loot worth it ----
        // x18 test: 28 trips back, median 892m (up to 1779m) with fists;
        // 12 got the items, 19 died on the way (21% of all deaths). the tester:
        // go back when there is really good loot there. Good is what lies
        // on the ground by the body: a gun that is worth carrying, or a
        // backpack or vest better than the one worn.
        private const float DeathLootRadiusMeters = 6f;
        private readonly List<RegionCoordinate> m_DeathLootRegions = new List<RegionCoordinate>();

        private bool HasWorthyDeathLoot(Vector3 spot, out string what)
        {
            what = string.Empty;
            if (ItemManager.regions == null)
            {
                what = "itens nao conferidos";
                return true;
            }
            m_DeathLootRegions.Clear();
            Regions.getRegionsInRadius(spot, DeathLootRadiusMeters, m_DeathLootRegions);
            foreach (var coordinate in m_DeathLootRegions)
            {
                var region = ItemManager.regions[coordinate.x, coordinate.y];
                if (region == null)
                {
                    continue;
                }
                foreach (var item in region.items)
                {
                    if (item == null || (item.point - spot).GetHorizontalMagnitude() > DeathLootRadiusMeters)
                    {
                        continue;
                    }
                    var asset = Assets.find(EAssetType.ITEM, item.item.id) as ItemAsset;
                    if (asset is ItemGunAsset gun && IsUsefulGun(gun))
                    {
                        what = gun.itemName;
                        return true;
                    }
                    if ((asset?.type == EItemType.BACKPACK || asset?.type == EItemType.VEST)
                        && (GetWornBagId(asset.type) == 0 || IsBetterBagOrArmor(asset, GetWornBagId(asset.type))))
                    {
                        what = asset.itemName;
                        return true;
                    }
                }
            }
            return false;
        }

        // ---- item 4: a loot that stopped moving ----
        // At 11:51 in the x18 test LilJuninho13 stood 108s 3.4m from a Rake
        // (Loot objective, no input) and nothing ended it. 20s without
        // getting 0.5m closer ends any loot, whichever branch held it.
        private const float LootStallSeconds = 20f;
        private uint m_LootStallId;
        private float m_LootStallSince;
        private float m_LootStallBest;

        private void UpdateLootStallWatchdog(Vector3 position, float now)
        {
            if (m_ObjectiveType != ObjectiveType.Loot || m_LootInstanceId == 0)
            {
                m_LootStallId = 0;
                return;
            }
            var distance = (m_ObjectivePosition - position).GetHorizontalMagnitude();
            if (m_LootStallId != m_LootInstanceId || distance < m_LootStallBest - 0.5f)
            {
                m_LootStallId = m_LootInstanceId;
                m_LootStallBest = distance;
                m_LootStallSince = now;
                return;
            }
            if (now - m_LootStallSince < LootStallSeconds)
            {
                return;
            }
            var name = TryGetTargetLoot(out var loot) ? DescribeItem(loot.item) : m_LootInstanceId.ToString();
            BotLog(LogLevel.Warning,
                "Loot {Item} parado {Seconds:0}s a {Distance:0.#}m sem chegar mais perto (ao alcance e vendo {InReach}, parado sobre ele {Settled}, andando {Walking}, zumbi mais perto {Zombie:0.#}m, utilidade de loot {LootUtility:0}, de combate {CombatUtility:0}); ignorado por {Ignore:0} min.",
                name,
                now - m_LootStallSince,
                distance,
                IsLootInReachAndSight(position),
                m_LootSettledId == m_LootInstanceId,
                m_IsWalking,
                m_CachedNearestZombieDistance,
                GetLootUtility(),
                GetCombatUtility(),
                UnreachableLootIgnoreSeconds / 60f);
            IgnoreLoot(m_LootInstanceId, now + UnreachableLootIgnoreSeconds);
            m_LootStallId = 0;
            ClearObjective();
            m_NextLootScanTime = now + 0.75f;
        }
    }
}
