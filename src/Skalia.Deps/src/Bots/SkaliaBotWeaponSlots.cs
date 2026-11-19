using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.174: a loaded gun in the bag that could never be held.
    //
    // In the 0.1.173 test Tadeu carried a loaded Avenger and its magazine in
    // the bag for 20 minutes: the primary slot held an Eaglefire with no ammo
    // and no magazine, and a gun only went from the bag to an EMPTY slot. The
    // swap failed every time without a word, while "has a loaded gun" (the
    // bag counted) sent him at a burner with his Katana, fleeing at 10m, for
    // 4 minutes. XXlloqiartyXX had a knife in the primary slot and dropped a
    // working 1911 for an Eaglefire that went to the bag. In all, 101 of 177
    // "shoot the burner" decisions had no gun in hand 8s later.
    //
    // Now a slot is freed the way a player would: a gun that can't shoot is
    // put in the bag (swapped with the loaded gun, or after a repack of the
    // bag with the server's /sort; dropped only when nothing fits even then), and a melee weapon moves to
    // the other slot when that one is free. "Loaded gun" and "better gun"
    // only count guns that are, or can be put, in a slot.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private float m_NextWeaponSlotLogAt = float.NegativeInfinity;
        private float m_NextWeaponArrangeAt = float.NegativeInfinity;
        private const float WeaponSwapZombieMeters = 6f;

        private bool HasLoadedGunInSlot()
        {
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (m_Player.inventory.getItemCount(slot) == 0)
                {
                    continue;
                }
                var jar = m_Player.inventory.getItem(slot, 0);
                if (jar?.GetAsset() is ItemGunAsset gun && IsUsableCarriedGun(slot, jar, gun))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsDeadGunInSlot(byte slot)
        {
            if (m_Player.inventory.getItemCount(slot) == 0)
            {
                return false;
            }
            var jar = m_Player.inventory.getItem(slot, 0);
            return jar?.GetAsset() is ItemGunAsset gun && !IsUsableCarriedGun(slot, jar, gun);
        }

        // Can this slot be cleared for a gun coming from the bag (score
        // forScore)? 0.1.175: also a loaded gun clearly weaker than it. In
        // the 0.1.174 test Tadeu kept a loaded Crossbow in the slot and two
        // Vipers with four magazines in the bag, fought with his knife for 8
        // minutes, gathered 9 guns and then dropped nearly all of them.
        private bool CanFreeWeaponSlot(byte slot, float forScore, out string why)
        {
            why = string.Empty;
            if (m_Player.inventory.getItemCount(slot) == 0)
            {
                return true;
            }
            var occupant = m_Player.inventory.getItem(slot, 0);
            var asset = occupant?.GetAsset();
            if (asset is ItemGunAsset gun)
            {
                if (IsUsableCarriedGun(slot, occupant!, gun) && !IsClearlyWeakerGun(gun, forScore))
                {
                    why = gun.itemName + " com municao no slot " + slot;
                    return false;
                }
                return true;
            }
            if (asset is ItemMeleeAsset)
            {
                var other = (byte)(1 - slot);
                if (asset.slot.canEquipInPage(other)
                    && (m_Player.inventory.getItemCount(other) == 0 || IsDeadGunInSlot(other)))
                {
                    return true;
                }
                why = asset.itemName + " no slot " + slot + " e o outro slot ocupado";
                return false;
            }
            why = (asset?.itemName ?? "item") + " no slot " + slot;
            return false;
        }

        private static bool IsClearlyWeakerGun(ItemGunAsset gun, float forScore)
        {
            return forScore > 0f && GunScore(gun) * GunUpgradeRatio <= forScore;
        }

        private static bool IsClearlyWeakerGunScore(float score, float forScore)
        {
            return forScore > 0f && score * GunUpgradeRatio <= forScore;
        }

        private static float HoldScore(ItemAsset asset)
        {
            return asset is ItemGunAsset gun ? GunScore(gun) : 0f;
        }

        private bool CanBecomeHoldable(byte page, ItemAsset asset)
        {
            if (page < PlayerInventory.SLOTS)
            {
                return true;
            }
            var score = HoldScore(asset);
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (asset.slot.canEquipInPage(slot) && CanFreeWeaponSlot(slot, score, out _))
                {
                    return true;
                }
            }
            return false;
        }

        private string DescribeWeaponSlotBlock(ItemAsset asset)
        {
            var reasons = new System.Collections.Generic.List<string>();
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (!asset.slot.canEquipInPage(slot))
                {
                    continue;
                }
                if (!CanFreeWeaponSlot(slot, HoldScore(asset), out var why))
                {
                    reasons.Add(why);
                }
            }
            return reasons.Count == 0 ? "nenhum slot aceita essa arma" : string.Join("; ", reasons);
        }

        // One step toward holding the gun at (page, jar); true when something
        // moved (the caller tries again on a later tick).
        private bool TryMakeGunHoldable(byte page, ItemJar jar, ItemAsset asset, float now)
        {
            var inventory = m_Player.inventory;
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (!asset.slot.canEquipInPage(slot) || inventory.getItemCount(slot) > 0)
                {
                    continue;
                }
                inventory.ReceiveDragItem(page, jar.x, jar.y, slot, 0, 0, 0);
                if (inventory.getItemCount(slot) > 0)
                {
                    ClearKnownEmptyGun();
                    BotLog(LogLevel.Information,
                        "Bot passou {Item} da mochila para o slot {Slot} para poder empunhar.",
                        asset.itemName,
                        slot);
                    return true;
                }
            }
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (!asset.slot.canEquipInPage(slot) || inventory.getItemCount(slot) == 0)
                {
                    continue;
                }
                var occupant = inventory.getItem(slot, 0);
                var occupantAsset = occupant?.GetAsset();
                if (occupant == null || occupantAsset == null)
                {
                    continue;
                }
                if (occupantAsset is ItemGunAsset occupantGun
                    && (!IsUsableCarriedGun(slot, occupant, occupantGun)
                        || IsClearlyWeakerGun(occupantGun, HoldScore(asset))))
                {
                    if (TryStowInBag(slot, occupant, occupantAsset, asset, now))
                    {
                        return true;
                    }
                    continue;
                }
                if (occupantAsset is ItemMeleeAsset)
                {
                    var other = (byte)(1 - slot);
                    if (!occupantAsset.slot.canEquipInPage(other))
                    {
                        continue;
                    }
                    if (inventory.getItemCount(other) == 0)
                    {
                        UnequipIfHeld(slot, occupant);
                        inventory.ReceiveDragItem(slot, occupant.x, occupant.y, other, 0, 0, 0);
                        if (inventory.getItemCount(other) > 0)
                        {
                            BotLog(LogLevel.Information,
                                "Bot passou {Melee} do slot {Slot} para o slot {Other} para abrir lugar para {Gun}.",
                                occupantAsset.itemName,
                                slot,
                                other,
                                asset.itemName);
                            return true;
                        }
                        continue;
                    }
                    if (IsDeadGunInSlot(other))
                    {
                        var deadGun = inventory.getItem(other, 0);
                        var deadAsset = deadGun?.GetAsset();
                        if (deadGun != null && deadAsset != null
                            && TryStowInBag(other, deadGun, deadAsset, asset, now))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void UnequipIfHeld(byte page, ItemJar jar)
        {
            var equipment = m_Player.equipment;
            if (equipment.equippedPage == page && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y)
            {
                equipment.dequip();
            }
        }

        // A gun that can't shoot leaves its slot for the bag: into free bag
        // space, else swapped with the gun that takes its slot (what a player
        // does by dragging one onto the other), else after repacking the bag
        // the bag with the server's /sort. Only when nothing fits even then is
        // it dropped (it has no ammo and no magazine for it).
        private bool TryStowInBag(byte slot, ItemJar jar, ItemAsset asset, ItemAsset forGun, float now)
        {
            var inventory = m_Player.inventory;
            var why = asset is ItemGunAsset stowGun && IsUsableCarriedGun(slot, jar, stowGun)
                ? "mais fraca"
                : "sem municao";
            if (!TryFindBagSpace(asset, out var page, out var x, out var y, out var rot)
                && !TrySwapIntoSlot(slot, jar, asset, forGun, why))
            {
                // The bag in order first (/sort), then this again.
                if (IsSortSettling(now)
                    || TryTypeSort(asset, "guardar " + asset.itemName + " (" + why + ") e empunhar " + forGun.itemName, now))
                {
                    return false;
                }
                if (inventory.getItemCount(slot) == 0
                    || inventory.getItem(slot, 0)?.GetAsset() is ItemGunAsset { } held && held.id == forGun.id)
                {
                    return true;
                }
                UnequipIfHeld(slot, jar);
                BotLog(LogLevel.Information,
                    "Sem espaco na mochila para {Item} ({Why}) nem reorganizando; largando para empunhar {Gun}.",
                    asset.itemName,
                    why,
                    forGun.itemName);
                DropAndForget(slot, jar.x, jar.y);
                ClearKnownEmptyGun();
                return inventory.getItemCount(slot) == 0;
            }
            if (inventory.getItemCount(slot) > 0
                && inventory.getItem(slot, 0)?.GetAsset() is ItemGunAsset swapped
                && swapped.id == forGun.id)
            {
                // Swapped: the loaded gun is already in the slot.
                return true;
            }
            UnequipIfHeld(slot, jar);
            inventory.ReceiveDragItem(slot, jar.x, jar.y, page, x, y, rot);
            if (inventory.getItemCount(slot) == 0)
            {
                ClearKnownEmptyGun();
                BotLog(LogLevel.Information,
                    "Bot guardou {Item} ({Why}) na mochila para empunhar {Gun}.",
                    asset.itemName,
                    why,
                    forGun.itemName);
                return true;
            }
            return false;
        }

        // The loaded gun in the bag and the empty one in the slot trade places.
        private bool TrySwapIntoSlot(byte slot, ItemJar jar, ItemAsset asset, ItemAsset forGun, string why)
        {
            var inventory = m_Player.inventory;
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var count = inventory.getItemCount(page);
                for (byte index = 0; index < count; index++)
                {
                    var bagJar = inventory.getItem(page, index);
                    if (bagJar?.GetAsset() is ItemGunAsset bagGun && bagGun.id == forGun.id)
                    {
                        UnequipIfHeld(slot, jar);
                        UnequipIfHeld(page, bagJar);
                        var bx = bagJar.x;
                        var by = bagJar.y;
                        var brot = bagJar.rot;
                        foreach (var rot in new[] { brot, (byte)(brot ^ 1) })
                        {
                            inventory.ReceiveSwapItem(slot, jar.x, jar.y, rot, page, bx, by, 0);
                            if (inventory.getItemCount(slot) > 0
                                && inventory.getItem(slot, 0)?.GetAsset() is ItemGunAsset now && now.id == forGun.id)
                            {
                                ClearKnownEmptyGun();
                                BotLog(LogLevel.Information,
                                    "Bot trocou {Item} ({Why}) do slot {Slot} por {Gun} da mochila.",
                                    asset.itemName,
                                    why,
                                    slot,
                                    forGun.itemName);
                                return true;
                            }
                        }
                        return false;
                    }
                }
            }
            return false;
        }

        // The plan of a repack for a change of clothes (SkaliaBotWearX11).
        private struct BagPlacement
        {
            public byte Page;
            public byte Index;
            public Item Item;
            public byte SizeX;
            public byte SizeY;
            public byte NewPage;
            public byte NewX;
            public byte NewY;
            public byte NewRot;
        }

        // 0.1.0-y17.10: the bag is put in order with the server's /sort,
        // typed in the chat like a player (SkaliaBotSortY). This only plans,
        // on a grid, whether the bag pages repacked biggest item first leave
        // room for one more item; nothing is moved here. The item in hand
        // stays where it is.
        private bool CanPackBagWith(ItemAsset stow)
        {
            var inventory = m_Player.inventory;
            var equipment = m_Player.equipment;
            var grids = new bool[PlayerInventory.STORAGE][,];
            var sizes = new System.Collections.Generic.List<int>();
            for (byte p = PlayerInventory.SLOTS; p < PlayerInventory.STORAGE; p++)
            {
                var items = inventory.items[p];
                if (items == null || items.width == 0 || items.height == 0)
                {
                    continue;
                }
                grids[p] = new bool[items.width, items.height];
                var count = items.getItemCount();
                for (byte index = 0; index < count; index++)
                {
                    var jar = items.getItem(index);
                    if (jar == null)
                    {
                        continue;
                    }
                    var sizeX = jar.size_x;
                    var sizeY = jar.size_y;
                    if (equipment.equippedPage == p && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y)
                    {
                        if (!MarkCells(grids[p], jar.x, jar.y, (jar.rot & 1) == 0 ? sizeX : sizeY, (jar.rot & 1) == 0 ? sizeY : sizeX))
                        {
                            return false;
                        }
                        continue;
                    }
                    sizes.Add(sizeX << 8 | sizeY);
                }
            }
            if (!TryPlace(grids, stow.size_x, stow.size_y, out _, out _, out _, out _))
            {
                return false;
            }
            sizes.Sort((left, right) => ((right >> 8) * (right & 0xFF)).CompareTo((left >> 8) * (left & 0xFF)));
            foreach (var size in sizes)
            {
                if (!TryPlace(grids, (byte)(size >> 8), (byte)(size & 0xFF), out _, out _, out _, out _))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryPlace(bool[][,] grids, byte sizeX, byte sizeY, out byte page, out byte x, out byte y, out byte rot)
        {
            for (byte p = 0; p < grids.Length; p++)
            {
                var grid = grids[p];
                if (grid == null)
                {
                    continue;
                }
                for (byte r = 0; r < 2; r++)
                {
                    var w = r == 0 ? sizeX : sizeY;
                    var h = r == 0 ? sizeY : sizeX;
                    for (var gy = 0; gy + h <= grid.GetLength(1); gy++)
                    {
                        for (var gx = 0; gx + w <= grid.GetLength(0); gx++)
                        {
                            if (MarkCells(grid, (byte)gx, (byte)gy, w, h))
                            {
                                page = p;
                                x = (byte)gx;
                                y = (byte)gy;
                                rot = r;
                                return true;
                            }
                        }
                    }
                }
            }
            page = x = y = rot = 0;
            return false;
        }

        // Marks the cells when all are free; false (nothing marked) otherwise.
        private static bool MarkCells(bool[,] grid, byte x, byte y, byte w, byte h)
        {
            if (x + w > grid.GetLength(0) || y + h > grid.GetLength(1))
            {
                return false;
            }
            for (var gx = x; gx < x + w; gx++)
            {
                for (var gy = y; gy < y + h; gy++)
                {
                    if (grid[gx, gy])
                    {
                        return false;
                    }
                }
            }
            for (var gx = x; gx < x + w; gx++)
            {
                for (var gy = y; gy < y + h; gy++)
                {
                    grid[gx, gy] = true;
                }
            }
            return true;
        }

        private bool TryFindBagSpace(ItemAsset asset, out byte page, out byte x, out byte y, out byte rot)
        {
            if (m_Player.inventory.tryFindSpace(asset.size_x, asset.size_y, out page, out x, out y, out rot)
                && page >= PlayerInventory.SLOTS
                && page < PlayerInventory.STORAGE)
            {
                return true;
            }
            page = x = y = rot = 0;
            return false;
        }

        private void LogWeaponSlotBlocked(ItemAsset asset, float now)
        {
            if (now < m_NextWeaponSlotLogAt)
            {
                return;
            }
            m_NextWeaponSlotLogAt = now + 10f;
            BotLog(LogLevel.Information,
                "Arma {Item} na mochila nao pode ser empunhada: {Why}.",
                asset.itemName,
                DescribeWeaponSlotBlock(asset));
        }

        // Out of a fight: when no gun in a slot can shoot, or one in the bag
        // is clearly better (0.1.175), make the best one holdable now instead
        // of in the middle of a fight.
        private void ArrangeWeaponSlots(float now)
        {
            if (now < m_NextWeaponArrangeAt
                || m_TreatmentPhase != 0
                || IsFightObjective()
                || m_Player.equipment.isBusy
                || m_CachedNearestZombieDistance <= WeaponSwapZombieMeters)
            {
                return;
            }
            m_NextWeaponArrangeAt = now + 1f;
            var slotBest = 0f;
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (m_Player.inventory.getItemCount(slot) > 0
                    && m_Player.inventory.getItem(slot, 0) is ItemJar slotJar
                    && slotJar.GetAsset() is ItemGunAsset slotGun
                    && IsUsableCarriedGun(slot, slotJar, slotGun))
                {
                    slotBest = Mathf.Max(slotBest, GunScore(slotGun));
                }
            }
            ItemJar? bestJar = null;
            ItemAsset? bestAsset = null;
            byte bestPage = 0;
            var bestScore = 0f;
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                var count = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < count; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar?.GetAsset() is ItemGunAsset gun
                        && IsUsableCarriedGun(page, jar, gun)
                        && GunScore(gun) > bestScore
                        && (slotBest <= 0f || IsClearlyWeakerGunScore(slotBest, GunScore(gun))))
                    {
                        bestScore = GunScore(gun);
                        bestJar = jar;
                        bestAsset = gun;
                        bestPage = page;
                    }
                }
            }
            if (bestJar == null || bestAsset == null)
            {
                return;
            }
            if (!TryMakeGunHoldable(bestPage, bestJar, bestAsset, now))
            {
                LogWeaponSlotBlocked(bestAsset, now);
                m_NextWeaponArrangeAt = now + 30f;
            }
        }
    }
}
