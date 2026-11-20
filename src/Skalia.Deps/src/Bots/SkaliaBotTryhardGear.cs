using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2t (doc 11.6-11.9, 11.13, 11.14): what the bot does with its own
    // inventory between fights, walking: clothes into bandages, ammo joined
    // into full magazines, a spare gun stripped, attachments on its gun.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float GearWorkZombieMeters = 15f;
        private const float GearWorkAggroMeters = 25f;
        // The game's craft alerts zombies within 8m (AlertTool.alert).
        private const float CraftQuietMeters = 9f;
        private float m_NextGearWorkAt;

        private bool IsGearWorkCalm()
        {
            return !IsFightObjective()
                && m_TreatmentPhase == 0
                && !m_Player.equipment.isBusy
                && m_CachedNearestZombieDistance > GearWorkZombieMeters
                && GetNearestAggroedZombieDistance() > GearWorkAggroMeters
                && m_Player.stance.stance != EPlayerStance.SWIM;
        }

        // One step per pass, on the move: nothing here stops the bot.
        private void UpdateGearWork(float now)
        {
            if (now < m_NextGearWorkAt || m_Player.life.isDead)
            {
                return;
            }
            m_NextGearWorkAt = now + 0.35f;
            UpdateHuntSweepBoredom(now);
            if (IsGearWorkCalm()
                && (TryStripSpareGun()
                    || TryFitAttachment()
                    || TryDropUselessGunPart()
                    || TryJoinAmmo()))
            {
                return;
            }
            TryCraftBandages();
        }

        // ---- the gun the bot keeps ----

        private bool TryGetKeeperGun(out byte keeperPage, out ItemJar keeperJar, out ItemGunAsset keeperGun)
        {
            keeperPage = byte.MaxValue;
            keeperJar = null!;
            keeperGun = null!;
            var best = float.NegativeInfinity;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (!(jar?.GetAsset() is ItemGunAsset gun) || !IsUsefulGun(gun))
                    {
                        continue;
                    }
                    var rank = GunScore(gun)
                        + (IsUsableCarriedGun(page, jar, gun) ? 1000000f : 0f)
                        + (page < PlayerInventory.SLOTS ? 0.5f : 0f);
                    if (rank > best)
                    {
                        best = rank;
                        keeperPage = page;
                        keeperJar = jar;
                        keeperGun = gun;
                    }
                }
            }
            return keeperJar != null;
        }

        private bool IsEquippedJar(byte page, ItemJar jar)
        {
            var equipment = m_Player.equipment;
            return equipment.asset != null
                && equipment.equippedPage == page
                && equipment.equipped_x == jar.x
                && equipment.equipped_y == jar.y;
        }

        private byte[]? GetCarriedGunState(byte page, ItemJar jar)
        {
            var state = IsEquippedJar(page, jar) ? m_Player.equipment.state : jar.item.state;
            return state != null && state.Length >= 18 ? state : null;
        }

        // ---- 11.7 a gun like its own: strip it ----
        // There was no strip. A gun equal to the bot's failed "better than
        // mine" and stayed on the floor with its magazine inside. The
        // magazine (and a part worth having) comes out and the bare gun is
        // dropped. Done on the item in the bag, the result a player gets by
        // holding the gun and detaching, without the two weapon swaps.
        private bool WantsGunForStrip(ItemGunAsset groundGun, byte[]? state)
        {
            if (state == null
                || state.Length < 18
                || state[10] == 0
                || !groundGun.allowMagazineChange
                || !TryGetKeeperGun(out _, out _, out var keeper))
            {
                return false;
            }
            var magazineId = BitConverter.ToUInt16(state, 8);
            if (magazineId == 0
                || !(Assets.find(EAssetType.ITEM, magazineId) is ItemMagazineAsset magazine)
                || !SharesCaliber(keeper.magazineCalibers, magazine.calibers))
            {
                return false;
            }
            return WantsMagazineRounds(magazine, state[10]);
        }

        // A gun DropWeakerGun would throw away keeps its place until the
        // strip has taken the magazine out.
        private bool IsStripCandidate(ItemJar jar)
        {
            var state = jar.item.state;
            if (state == null
                || state.Length < 18
                || state[10] == 0
                || !(jar.GetAsset() is ItemGunAsset spare)
                || !spare.allowMagazineChange
                || !TryGetKeeperGun(out _, out var keeperJar, out var keeper)
                || ReferenceEquals(jar, keeperJar))
            {
                return false;
            }
            var magazineId = BitConverter.ToUInt16(state, 8);
            return magazineId != 0
                && Assets.find(EAssetType.ITEM, magazineId) is ItemMagazineAsset magazine
                && SharesCaliber(keeper.magazineCalibers, magazine.calibers);
        }

        private bool TryStripSpareGun()
        {
            if (!TryGetKeeperGun(out var keeperPage, out var keeperJar, out var keeper))
            {
                return false;
            }
            var keeperState = GetCarriedGunState(keeperPage, keeperJar);
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar == null
                        || ReferenceEquals(jar, keeperJar)
                        || !(jar.GetAsset() is ItemGunAsset spare)
                        || !spare.allowMagazineChange)
                    {
                        continue;
                    }
                    var state = jar.item.state;
                    if (state == null || state.Length < 18 || state[10] == 0)
                    {
                        continue;
                    }
                    var magazineId = BitConverter.ToUInt16(state, 8);
                    if (magazineId == 0
                        || !(Assets.find(EAssetType.ITEM, magazineId) is ItemMagazineAsset magazine)
                        || !SharesCaliber(keeper.magazineCalibers, magazine.calibers))
                    {
                        continue;
                    }
                    if (IsEquippedJar(page, jar))
                    {
                        // In the hand: put it away first, strip on the next pass.
                        m_Player.equipment.dequip();
                        return true;
                    }
                    var rounds = state[10];
                    var taken = new List<Item> { new Item(magazineId, rounds, state[17]) };
                    var parts = new StringBuilder();
                    state[8] = 0;
                    state[9] = 0;
                    state[10] = 0;
                    if (keeperState != null)
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            var partId = BitConverter.ToUInt16(state, slot * 2);
                            if (partId == 0
                                || !(Assets.find(EAssetType.ITEM, partId) is ItemCaliberAsset part)
                                || !IsAttachmentUpgrade(keeper, keeperState, part))
                            {
                                continue;
                            }
                            taken.Add(new Item(partId, false, state[13 + slot]));
                            state[slot * 2] = 0;
                            state[slot * 2 + 1] = 0;
                            parts.Append(parts.Length > 0 ? ", " : string.Empty).Append(part.itemName);
                        }
                    }
                    m_Player.inventory.sendUpdateInvState(page, jar.x, jar.y, state);
                    BotLog(LogLevel.Information,
                        "Strip: tirou {Magazine} ({Rounds} balas){Parts} de {Spare} para a {Keeper} e largou a arma pelada.",
                        magazine.itemName,
                        rounds,
                        parts.Length > 0 ? " e " + parts : string.Empty,
                        spare.itemName,
                        keeper.itemName);
                    // The gun goes first: its room is what the magazine takes.
                    DropAndForget(page, jar.x, jar.y);
                    foreach (var item in taken)
                    {
                        m_Player.inventory.forceAddItem(item, true);
                    }
                    return true;
                }
            }
            return false;
        }

        // ---- 11.14 attachments ----
        // The plugin neither took nor fitted a sight, grip, barrel or
        // tactical. Gun state: ids at 0 (sight), 2 (tactical), 4 (grip),
        // 6 (barrel); qualities at 13..16.
        private static int GetAttachmentSlot(ItemAsset? asset)
        {
            if (asset is ItemSightAsset)
            {
                return 0;
            }
            if (asset is ItemTacticalAsset)
            {
                return 1;
            }
            if (asset is ItemGripAsset)
            {
                return 2;
            }
            return asset is ItemBarrelAsset ? 3 : -1;
        }

        private static bool GunTakesAttachment(ItemGunAsset gun, int slot)
        {
            switch (slot)
            {
                case 0:
                    return gun.hasSight;
                case 1:
                    return gun.hasTactical;
                case 2:
                    return gun.hasGrip;
                case 3:
                    return gun.hasBarrel;
                default:
                    return false;
            }
        }

        // Lower is better: what is left of the recoil and the spread.
        private static float AttachmentScore(ItemCaliberAsset part)
        {
            var score = Mathf.Max(0.05f, part.recoil_x)
                * Mathf.Max(0.05f, part.recoil_y)
                * Mathf.Max(0.05f, part.spread);
            if (part is ItemBarrelAsset barrel && barrel.isSilenced)
            {
                // Fewer zombies hear the shot.
                score *= 0.6f;
            }
            if (part.ballisticDamageMultiplier > 0.01f)
            {
                score /= part.ballisticDamageMultiplier;
            }
            return score;
        }

        private static bool IsRevealingTactical(ItemCaliberAsset? part)
        {
            return part is ItemTacticalAsset tactical && tactical.isLight;
        }

        private static bool IsAttachmentUpgrade(ItemGunAsset gun, byte[] gunState, ItemCaliberAsset part)
        {
            var slot = GetAttachmentSlot(part);
            if (slot < 0 || !GunTakesAttachment(gun, slot) || gunState.Length < 18)
            {
                return false;
            }
            if (part is ItemTacticalAsset tactical
                && (tactical.isLight || tactical.isRangefinder || tactical.isMelee))
            {
                // A light gives the position away, like the headlamp.
                return false;
            }
            var calibers = part.calibers;
            if (calibers != null && calibers.Length != 0)
            {
                if (!SharesCaliber(gun.attachmentCalibers, calibers))
                {
                    return false;
                }
            }
            else if (gun.requiresNonZeroAttachmentCaliber)
            {
                return false;
            }
            var currentId = BitConverter.ToUInt16(gunState, slot * 2);
            if (currentId == part.id)
            {
                return false;
            }
            if (currentId == 0)
            {
                return true;
            }
            if (!(Assets.find(EAssetType.ITEM, currentId) is ItemCaliberAsset current) || IsRevealingTactical(current))
            {
                return true;
            }
            // The bot does not zoom: any sight will do once it has one.
            return slot != 0 && AttachmentScore(part) < AttachmentScore(current) * 0.92f;
        }

        private bool WantsAttachmentLoot(ItemAsset asset)
        {
            if (!(asset is ItemCaliberAsset part)
                || GetAttachmentSlot(asset) < 0
                || !TryGetKeeperGun(out var page, out var jar, out var keeper))
            {
                return false;
            }
            var state = GetCarriedGunState(page, jar);
            return state != null && IsAttachmentUpgrade(keeper, state, part);
        }

        private bool TryFitAttachment()
        {
            if (!TryGetKeeperGun(out var keeperPage, out var keeperJar, out var keeper))
            {
                return false;
            }
            var state = GetCarriedGunState(keeperPage, keeperJar);
            if (state == null)
            {
                return false;
            }
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (!(jar?.GetAsset() is ItemCaliberAsset part)
                        || GetAttachmentSlot(part) < 0
                        || !IsAttachmentUpgrade(keeper, state, part))
                    {
                        continue;
                    }
                    var slot = GetAttachmentSlot(part);
                    var oldId = BitConverter.ToUInt16(state, slot * 2);
                    var oldName = (Assets.find(EAssetType.ITEM, oldId) as ItemAsset)?.itemName ?? "nada";
                    if (IsEquippedJar(keeperPage, keeperJar))
                    {
                        // In the hand: the request the player's client sends.
                        var useable = m_Player.equipment.useable as UseableGun;
                        if (useable == null || !m_Player.equipment.IsEquipAnimationFinished)
                        {
                            return false;
                        }
                        switch (slot)
                        {
                            case 0:
                                useable.ReceiveAttachSight(page, jar.x, jar.y, part.hash);
                                break;
                            case 1:
                                useable.ReceiveAttachTactical(page, jar.x, jar.y, part.hash);
                                break;
                            case 2:
                                useable.ReceiveAttachGrip(page, jar.x, jar.y, part.hash);
                                break;
                            default:
                                useable.ReceiveAttachBarrel(page, jar.x, jar.y, part.hash);
                                break;
                        }
                    }
                    else
                    {
                        // Holstered: the same change written on the item.
                        var oldQuality = state[13 + slot];
                        var idBytes = BitConverter.GetBytes(part.id);
                        state[slot * 2] = idBytes[0];
                        state[slot * 2 + 1] = idBytes[1];
                        state[13 + slot] = jar.item.quality;
                        m_Player.inventory.removeItem(page, index);
                        m_Player.inventory.sendUpdateInvState(keeperPage, keeperJar.x, keeperJar.y, state);
                        if (oldId != 0)
                        {
                            m_Player.inventory.forceAddItem(new Item(oldId, false, oldQuality), true);
                        }
                    }
                    BotLog(LogLevel.Information,
                        "Attachment: {Part} na {Gun} no lugar de {Old}.",
                        part.itemName,
                        keeper.itemName,
                        oldName);
                    return true;
                }
            }
            return false;
        }

        // The part that came off, or one picked up that another one beat.
        private bool TryDropUselessGunPart()
        {
            var hasKeeper = TryGetKeeperGun(out var keeperPage, out var keeperJar, out var keeper);
            var state = hasKeeper ? GetCarriedGunState(keeperPage, keeperJar) : null;
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (!(jar?.GetAsset() is ItemCaliberAsset part)
                        || GetAttachmentSlot(part) < 0
                        || (state != null && IsAttachmentUpgrade(keeper, state, part)))
                    {
                        continue;
                    }
                    BotLog(LogLevel.Information,
                        "Largando {Part}: nao serve ou nao e melhor que a peca da arma.",
                        part.itemName);
                    DropAndForget(page, jar.x, jar.y);
                    return true;
                }
            }
            return false;
        }

        // A tactical light on a gun found with one: switched off (state 12).
        private void SwitchOffGunLight()
        {
            var equipment = m_Player.equipment;
            if (!(equipment.asset is ItemGunAsset)
                || equipment.state == null
                || equipment.state.Length < 18
                || equipment.state[12] == 0
                || equipment.isBusy)
            {
                return;
            }
            var tacticalId = BitConverter.ToUInt16(equipment.state, 2);
            if (tacticalId == 0
                || !(Assets.find(EAssetType.ITEM, tacticalId) is ItemTacticalAsset tactical)
                || !tactical.isLight)
            {
                return;
            }
            equipment.state[12] = 0;
            equipment.sendUpdateState();
            BotLog(LogLevel.Information,
                "Luz: {Item} da arma desligada (revela a posicao).",
                tactical.itemName);
        }

        // ---- 11.13 ammo joined ----
        // The game's own Ammunition crafts: a magazine (or an ammo crate)
        // filled from another of the same kind. the tester: 5 magazines with
        // 4, 6, 3, 2 and 1 rounds are one with 16; the empty ones go.
        private static Dictionary<ushort, Blueprint>? s_SelfPourBlueprints;
        private static HashSet<ushort>? s_StrongAmmoBoxIds;

        private static void EnsurePourRecipes()
        {
            if (s_SelfPourBlueprints != null)
            {
                return;
            }
            var pours = new Dictionary<ushort, Blueprint>();
            var items = new List<ItemAsset>();
            Assets.find(items);
            foreach (var item in items)
            {
                if (item?.blueprints == null || item.id == 0)
                {
                    continue;
                }
                foreach (var blueprint in item.blueprints)
                {
                    if (blueprint != null
                        && blueprint.Operation == EBlueprintOperation.FillTargetItem
                        && blueprint.supplies != null
                        && blueprint.supplies.Length == 1
                        && blueprint.supplies[0]?.FindItemAsset()?.id == item.id
                        && !pours.ContainsKey(item.id))
                    {
                        pours.Add(item.id, blueprint);
                    }
                }
            }
            s_SelfPourBlueprints = pours;
        }

        // A crate for the guns worth having (the Mega guns of the map's
        // tables) is taken before the gun is found.
        private static bool IsStrongAmmoBox(ushort boxId)
        {
            if (s_StrongAmmoBoxIds == null)
            {
                EnsureAmmoRecipes();
                EnsureItemTableProfiles();
                var strongGuns = new List<ItemGunAsset>();
                var seen = new HashSet<ushort>();
                foreach (var profile in s_ItemTableProfiles!.Values)
                {
                    foreach (var id in profile.Keys)
                    {
                        if (seen.Add(id)
                            && Assets.find(EAssetType.ITEM, id) is ItemGunAsset gun
                            && IsUsefulGun(gun)
                            && GunScore(gun) >= MegaGunScore)
                        {
                            strongGuns.Add(gun);
                        }
                    }
                }
                var ids = new HashSet<ushort>();
                foreach (var entry in s_AmmoBoxRefills!)
                {
                    foreach (var refill in entry.Value)
                    {
                        foreach (var gun in strongGuns)
                        {
                            if (SharesCaliber(gun.magazineCalibers, refill.Magazine.calibers))
                            {
                                ids.Add(entry.Key);
                                break;
                            }
                        }
                    }
                }
                s_StrongAmmoBoxIds = ids;
            }
            return s_StrongAmmoBoxIds.Contains(boxId);
        }

        private static bool IsAmmoBox(ushort itemId)
        {
            EnsureAmmoRecipes();
            return s_AmmoBoxRefills!.ContainsKey(itemId);
        }

        // Room left in the carried items of this id that are not full.
        private int GetFreeRounds(ushort itemId, int capacity, out int withRounds)
        {
            var free = 0;
            withRounds = 0;
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar == null || jar.item.id != itemId)
                    {
                        continue;
                    }
                    if (jar.item.amount > 0)
                    {
                        withRounds++;
                        free += Mathf.Max(0, capacity - jar.item.amount);
                    }
                }
            }
            return free;
        }

        // Up to the cap any loaded magazine; over it only one that pours
        // whole into the ones the bot has and leaves empty.
        private bool WantsMagazineRounds(ItemMagazineAsset magazine, int rounds)
        {
            if (rounds <= 0 || !CountCompatibleMagazines(magazine, out var carried))
            {
                return false;
            }
            if (carried < MaximumMagazinesPerGun)
            {
                return true;
            }
            // 0.1.0-y17.24 (the tester, 20/11, on the drum: "nao precisa ser
            // obrigatorio, mas seria interessante se eles usassem tbm"): at
            // the cap a magazine still comes when it holds more rounds than
            // any carried one can (a drum over full magazines); the reload
            // already takes the fullest one.
            if (rounds > LargestCarriedMagazineCapacity(magazine))
            {
                return true;
            }
            EnsurePourRecipes();
            return s_SelfPourBlueprints!.ContainsKey(magazine.id)
                && rounds <= GetFreeRounds(magazine.id, magazine.amount, out _);
        }

        private int LargestCarriedMagazineCapacity(ItemMagazineAsset magazine)
        {
            var largest = 0;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    if (m_Player.inventory.getItem(page, index)?.GetAsset() is ItemMagazineAsset carried
                        && SharesCaliber(carried.calibers, magazine.calibers))
                    {
                        largest = Mathf.Max(largest, carried.amount);
                    }
                }
            }
            return largest;
        }

        private bool WantsAmmoBox(ItemAsset box, int rounds, out string why)
        {
            why = string.Empty;
            if (rounds <= 0)
            {
                why = "vazia";
                return false;
            }
            if (!IsAmmoBoxForCarriedGun(box.id) && !(Tryhard && IsStrongAmmoBox(box.id)))
            {
                why = "nao serve em arma que o bot carrega nem nas armas fortes do mapa";
                return false;
            }
            if (CountAmmoBoxes() < MaximumAmmoBoxes)
            {
                return true;
            }
            // 0.2.2x13: llo walked the whole x12 test with 1 round in the
            // Zubeknakov and no magazine; he turned down three Ranger and
            // Low Caliber boxes because the two boxes he carried (for guns of
            // the map he did not have) filled the cap. A box for a gun the bot
            // carries counts against the cap only with others of its use.
            if (IsAmmoBoxForCarriedGun(box.id)
                && CountAmmoBoxesForCarriedGuns() < MaximumAmmoBoxes
                && CountAmmoBoxes() < MaximumAmmoBoxes + 2)
            {
                return true;
            }
            EnsurePourRecipes();
            if (s_SelfPourBlueprints!.ContainsKey(box.id)
                && rounds <= GetFreeRounds(box.id, box.amount, out _))
            {
                return true;
            }
            why = "ja carrega " + MaximumAmmoBoxes + " caixas e esta nao cabe inteira nelas";
            return false;
        }

        private int CountAmmoBoxesForCarriedGuns()
        {
            EnsureAmmoRecipes();
            var count = 0;
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar != null
                        && jar.item.amount > 0
                        && s_AmmoBoxRefills!.ContainsKey(jar.item.id)
                        && IsAmmoBoxForCarriedGun(jar.item.id))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private float m_NextAmmoBoxRefusalLogAt;
        // 0.2.2x1 (doc 16.3, item 12): 165 of these in 23 min. Once per kind
        // of box and reason every 10 min.
        private readonly Dictionary<string, float> m_AmmoBoxRefusalLogged = new Dictionary<string, float>();

        private void LogAmmoBoxRefusal(ItemAsset box, string why, float now)
        {
            var key = box.id + "|" + why;
            if (now < m_NextAmmoBoxRefusalLogAt
                || (m_AmmoBoxRefusalLogged.TryGetValue(key, out var until) && now < until))
            {
                return;
            }
            m_AmmoBoxRefusalLogged[key] = now + 600f;
            m_NextAmmoBoxRefusalLogAt = now + 20f;
            BotLog(LogLevel.Information,
                "Caixa de municao {Box} a vista recusada: {Why}.",
                box.itemName,
                why);
        }

        private bool TryJoinAmmo()
        {
            EnsurePourRecipes();
            // 1. Two or more of a kind that are neither full nor empty.
            var partials = new Dictionary<ushort, int>();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    var asset = jar?.GetAsset();
                    if (jar == null
                        || asset == null
                        || jar.item.amount == 0
                        || jar.item.amount >= asset.amount
                        || !s_SelfPourBlueprints!.ContainsKey(asset.id))
                    {
                        continue;
                    }
                    partials.TryGetValue(asset.id, out var count);
                    partials[asset.id] = count + 1;
                }
            }
            foreach (var entry in partials)
            {
                if (entry.Value < 2)
                {
                    continue;
                }
                if (m_Player.crafting.HandleCraftRequestInternal(default, s_SelfPourBlueprints![entry.Key], false, true, false))
                {
                    BotLog(LogLevel.Information,
                        "Municao: juntou {Count} {Item} parciais; agora {Amounts}.",
                        entry.Value,
                        (Assets.find(EAssetType.ITEM, entry.Key) as ItemAsset)?.itemName ?? entry.Key.ToString(),
                        DescribeAmounts(entry.Key));
                    return true;
                }
            }
            // 2. The magazine inside the holstered gun, topped up from the
            // partial magazines, then the crate, then a full magazine.
            return TryTopUpKeeperMagazine();
        }

        private string DescribeAmounts(ushort itemId)
        {
            var text = new StringBuilder();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar != null && jar.item.id == itemId)
                    {
                        text.Append(text.Length > 0 ? ", " : string.Empty).Append(jar.item.amount);
                    }
                }
            }
            return text.Length > 0 ? text.ToString() : "nenhum";
        }

        private bool TryTopUpKeeperMagazine()
        {
            if (!TryGetKeeperGun(out var keeperPage, out var keeperJar, out var keeper)
                || IsEquippedJar(keeperPage, keeperJar)
                || !keeper.allowMagazineChange)
            {
                // In the hand the gun keeps its own count of the rounds:
                // left for when it is holstered again.
                return false;
            }
            var state = keeperJar.item.state;
            if (state == null || state.Length < 18)
            {
                return false;
            }
            var magazineId = BitConverter.ToUInt16(state, 8);
            if (magazineId == 0 || !(Assets.find(EAssetType.ITEM, magazineId) is ItemMagazineAsset magazine))
            {
                return false;
            }
            var capacity = magazine.amount;
            var before = state[10];
            var missing = capacity - before;
            if (missing <= 0)
            {
                return false;
            }
            EnsureAmmoRecipes();
            // Donors: 0 = partial magazine, 1 = crate, 2 = full magazine.
            var donors = new List<(int Order, byte Page, ItemJar Jar, bool Box)>();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar == null || jar.item.amount == 0)
                    {
                        continue;
                    }
                    if (jar.item.id == magazineId)
                    {
                        donors.Add((jar.item.amount < capacity ? 0 : 2, page, jar, false));
                    }
                    else if (s_AmmoBoxRefills!.TryGetValue(jar.item.id, out var refills))
                    {
                        foreach (var refill in refills)
                        {
                            if (refill.Magazine.id == magazineId)
                            {
                                donors.Add((1, page, jar, true));
                                break;
                            }
                        }
                    }
                }
            }
            if (donors.Count == 0)
            {
                return false;
            }
            donors.Sort((left, right) => left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : left.Jar.item.amount.CompareTo(right.Jar.item.amount));
            var sources = new StringBuilder();
            var removals = new List<(byte Page, ItemJar Jar)>();
            foreach (var donor in donors)
            {
                if (missing <= 0)
                {
                    break;
                }
                var moved = Mathf.Min(missing, donor.Jar.item.amount);
                missing -= moved;
                var left = (byte)(donor.Jar.item.amount - moved);
                m_Player.inventory.sendUpdateAmount(donor.Page, donor.Jar.x, donor.Jar.y, left);
                sources.Append(sources.Length > 0 ? ", " : string.Empty)
                    .Append(moved)
                    .Append(donor.Box ? " da caixa" : " de carregador");
                if (left == 0 && donor.Box)
                {
                    removals.Add((donor.Page, donor.Jar));
                }
            }
            foreach (var removal in removals)
            {
                var index = m_Player.inventory.getIndex(removal.Page, removal.Jar.x, removal.Jar.y);
                if (index != byte.MaxValue)
                {
                    m_Player.inventory.removeItem(removal.Page, index);
                }
            }
            state[10] = (byte)(capacity - missing);
            m_Player.inventory.sendUpdateInvState(keeperPage, keeperJar.x, keeperJar.y, state);
            if (IsKnownEmptyGunSlot(keeperPage, keeperJar.x, keeperJar.y))
            {
                ClearKnownEmptyGun();
            }
            // The empty magazines left over go at once.
            m_NextEmptyMagazineCheckAt = 0f;
            BotLog(LogLevel.Information,
                "Municao: carregador da {Gun} de {Before} para {After}/{Capacity} ({Sources}).",
                keeper.itemName,
                before,
                state[10],
                capacity,
                sources.ToString());
            return true;
        }

        private bool CarriesAmmoBoxWithRounds()
        {
            EnsureAmmoRecipes();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar != null
                        && jar.item.amount > 0
                        && s_AmmoBoxRefills!.TryGetValue(jar.item.id, out var refills))
                    {
                        foreach (var refill in refills)
                        {
                            if (CarriesGunFor(refill.Magazine))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        // ---- 11.9 every spare piece of clothing is a bandage ----
        // In the 48 min of the 0.2.2 test clothes were torn twice, both with
        // the bot bleeding, and the craft stopped at the Rag. Bandage is 2
        // Rags, Rag is 2 Cloth; the Dressing needs a pharmacy bench.
        private const int BandageStockCap = MaximumMedicalItems;
        private static List<Blueprint>? s_MedicalByValue;
        private static Dictionary<ushort, Blueprint>? s_TearBySource;
        private static HashSet<ushort>? s_ClothIds;

        private static float BlueprintMedicalValue(Blueprint blueprint)
        {
            var best = 0f;
            foreach (var output in blueprint.outputs)
            {
                if (output?.FindItemAsset() is ItemMedicalAsset medical)
                {
                    best = Mathf.Max(best, MedicalValue(medical));
                }
            }
            return best;
        }

        private static void EnsureBandageRecipes()
        {
            if (s_MedicalByValue != null)
            {
                return;
            }
            EnsureMedicalRecipes();
            var ordered = new List<Blueprint>(s_MedicalBlueprints!);
            ordered.Sort((left, right) => BlueprintMedicalValue(right).CompareTo(BlueprintMedicalValue(left)));
            var tear = new Dictionary<ushort, Blueprint>();
            foreach (var blueprint in s_MedicalMaterialBlueprints!)
            {
                if (blueprint.supplies.Length != 1)
                {
                    continue;
                }
                var source = blueprint.supplies[0]?.FindItemAsset();
                if (source != null && source.id != 0 && !tear.ContainsKey(source.id))
                {
                    tear.Add(source.id, blueprint);
                }
            }
            var cloth = new HashSet<ushort>();
            foreach (var blueprint in ordered)
            {
                foreach (var supply in blueprint.supplies)
                {
                    var asset = supply?.FindItemAsset();
                    if (asset != null && asset.id != 0 && !(asset is ItemMedicalAsset) && blueprint.supplies.Length == 1)
                    {
                        cloth.Add(asset.id);
                    }
                }
            }
            s_ClothIds = cloth;
            s_TearBySource = tear;
            s_MedicalByValue = ordered;
        }

        // Bandages the bot has or can make from what it carries: a Rag is
        // half of one, a Cloth a quarter.
        private float GetBandageStock()
        {
            EnsureBandageRecipes();
            var stock = 0f;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var asset = m_Player.inventory.getItem(page, index)?.GetAsset();
                    if (asset is ItemMedicalAsset medical)
                    {
                        if (medical.bleedingModifier == ItemConsumeableAsset.Bleeding.Heal)
                        {
                            stock += medical.health < 10 ? 0.5f : 1f;
                        }
                    }
                    else if (asset != null && s_ClothIds!.Contains(asset.id))
                    {
                        stock += 0.25f;
                    }
                }
            }
            return stock;
        }

        private const int TryhardOtherMedicalCap = 4;

        private static bool IsBleedHeal(ItemMedicalAsset medical)
        {
            return medical.bleedingModifier == ItemConsumeableAsset.Bleeding.Heal;
        }

        private static float BleedHealWorth(ItemMedicalAsset medical)
        {
            return medical.health < 10 ? 0.5f : 1f;
        }

        // The stock without the cloth (only medicine can be dropped for it).
        private float GetBleedHealStock()
        {
            var stock = 0f;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    if (m_Player.inventory.getItem(page, index)?.GetAsset() is ItemMedicalAsset medical
                        && IsBleedHeal(medical))
                    {
                        stock += BleedHealWorth(medical);
                    }
                }
            }
            return stock;
        }

        private int CountOtherMedical()
        {
            var count = 0;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    if (m_Player.inventory.getItem(page, index)?.GetAsset() is ItemMedicalAsset medical
                        && !IsBleedHeal(medical))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private bool IsBandageStockShort()
        {
            return GetBandageStock() < BandageStockCap;
        }

        private bool WantsClothingAsMaterial(ItemAsset asset)
        {
            EnsureMedicalRecipes();
            return IsTearable(asset)
                && IsBandageStockShort()
                && CountInventoryItemsWithIds(s_MedicalMaterialSourceIds!) < MaximumSpareClothing
                && HasInventorySpaceFor(asset);
        }

        private bool IsTearable(ItemAsset asset)
        {
            EnsureBandageRecipes();
            return s_TearBySource!.ContainsKey(asset.id);
        }

        private int CountCarried(ushort itemId)
        {
            var count = 0;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    if (m_Player.inventory.getItem(page, index)?.item.id == itemId)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private bool HasSuppliesFor(Blueprint blueprint)
        {
            foreach (var supply in blueprint.supplies)
            {
                var asset = supply?.FindItemAsset();
                if (asset == null || CountCarried(asset.id) < Mathf.Max(1, (int)supply!.amount))
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryCraftBandages()
        {
            if (m_TreatmentPhase != 0
                || m_Player.equipment.isBusy
                || m_CachedNearestZombieDistance <= CraftQuietMeters
                || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return false;
            }
            EnsureBandageRecipes();
            // The best thing first: a Bandage from 2 Rags before a Rag from
            // 2 Cloth, so the bot never walks around with Rags.
            foreach (var blueprint in s_MedicalByValue!)
            {
                if (!HasSuppliesFor(blueprint)
                    || !m_Player.crafting.HandleCraftRequestInternal(default, blueprint, false, true, false))
                {
                    continue;
                }
                BotLog(LogLevel.Information,
                    "Curativo: fez {Item} andando (estoque {Stock:0.##} de {Cap}).",
                    DescribeBlueprintOutput(blueprint),
                    GetBandageStock(),
                    BandageStockCap);
                return true;
            }
            if (!IsBandageStockShort())
            {
                return false;
            }
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var asset = m_Player.inventory.getItem(page, index)?.GetAsset();
                    if (asset == null
                        || !s_TearBySource!.TryGetValue(asset.id, out var blueprint)
                        || IsWearUpgrade(asset))
                    {
                        // A better piece waits to be put on: never torn.
                        continue;
                    }
                    if (m_Player.crafting.HandleCraftRequestInternal(default, blueprint, false, true, false))
                    {
                        BotLog(LogLevel.Information,
                            "Curativo: rasgou {Source} ({Item}) andando (estoque {Stock:0.##} de {Cap}).",
                            asset.itemName,
                            DescribeBlueprintOutput(blueprint),
                            GetBandageStock(),
                            BandageStockCap);
                        return true;
                    }
                }
            }
            return false;
        }

        // ---- 11.6 clothes by protection ----
        // Shirt, pants, vest and backpack were compared by room only, and
        // the new one had to be bigger both ways: a bot in a Parka ignored a
        // Military Top beside it. Protection first (armor is what a hit
        // still does), room as the tie-break.
        private bool IsBetterBagOrArmor(ItemAsset asset, ushort wornId)
        {
            if (!(asset is ItemClothingAsset gear))
            {
                return false;
            }
            if (asset.id == wornId)
            {
                return false;
            }
            if (!(Assets.find(EAssetType.ITEM, wornId) is ItemClothingAsset worn))
            {
                return gear.movementSpeedMultiplier >= 0.99f
                    && (gear.armor < 0.99f || IsBiggerBag(asset, wornId));
            }
            if (gear.movementSpeedMultiplier >= 0.99f && gear.armor < worn.armor - 0.04f)
            {
                return true;
            }
            if (gear.armor > worn.armor + 0.04f)
            {
                return false;
            }
            return IsBiggerBag(asset, wornId);
        }

        private static byte GetBagPage(EItemType type)
        {
            switch (type)
            {
                case EItemType.BACKPACK:
                    return PlayerInventory.BACKPACK;
                case EItemType.VEST:
                    return PlayerInventory.VEST;
                case EItemType.SHIRT:
                    return PlayerInventory.SHIRT;
                case EItemType.PANTS:
                    return PlayerInventory.PANTS;
                default:
                    return byte.MaxValue;
            }
        }

        // A piece that protects more but holds less: what is in the old one
        // moves to the other pockets first, so the game drops as little as
        // it can when the page shrinks.
        private int MoveItemsOutOfPage(byte fromPage, ItemJar keep)
        {
            if (fromPage >= PlayerInventory.STORAGE)
            {
                return 0;
            }
            var moved = 0;
            for (var guard = 0; guard < 40; guard++)
            {
                var movedOne = false;
                var itemCount = m_Player.inventory.getItemCount(fromPage);
                for (byte index = 0; index < itemCount && !movedOne; index++)
                {
                    var jar = m_Player.inventory.getItem(fromPage, index);
                    var asset = jar?.GetAsset();
                    if (jar == null || asset == null || ReferenceEquals(jar, keep))
                    {
                        continue;
                    }
                    for (byte page = PlayerInventory.BACKPACK; page < PlayerInventory.STORAGE; page++)
                    {
                        if (page == fromPage
                            || !m_Player.inventory.tryFindSpace(page, asset.size_x, asset.size_y, out var x, out var y, out var rot))
                        {
                            continue;
                        }
                        var before = m_Player.inventory.getItemCount(fromPage);
                        m_Player.inventory.ReceiveDragItem(fromPage, jar.x, jar.y, page, x, y, rot);
                        if (m_Player.inventory.getItemCount(fromPage) < before)
                        {
                            moved++;
                            movedOne = true;
                        }
                        break;
                    }
                }
                if (!movedOne)
                {
                    break;
                }
            }
            return moved;
        }

        private bool IsWearUpgrade(ItemAsset asset)
        {
            switch (asset.type)
            {
                case EItemType.BACKPACK:
                case EItemType.VEST:
                case EItemType.SHIRT:
                case EItemType.PANTS:
                    return IsBetterBagOrArmor(asset, GetWornBagId(asset.type));
                case EItemType.HAT:
                case EItemType.MASK:
                case EItemType.GLASSES:
                    return IsBetterHeadgear(asset);
                default:
                    return false;
            }
        }

        private static string DescribeWear(ItemAsset? asset)
        {
            return asset is ItemClothingAsset gear
                ? DescribeBagSize(asset) + ", protecao " + Mathf.Max(0f, (1f - gear.armor) * 100f).ToString("0") + "%"
                : "0x0";
        }
    }
}
