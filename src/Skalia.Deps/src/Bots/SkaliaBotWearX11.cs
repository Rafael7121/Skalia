using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x11 (the tester): the best clothes, by protection then room, whatever
    // the shape. Military Bottom (4x3) is narrower than Firefighter Bottom
    // (5x2): the game drops what lies outside the new shape when the page
    // shrinks. Before the swap the bag pages are repacked the way the
    // plugin's Sort does (biggest first), with the page being changed
    // limited to what both shapes share, planned on a grid and applied only
    // when everything fits.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private enum WearRepack
        {
            Done,
            Impossible,
            Later
        }

        private readonly Dictionary<ushort, float> m_WearRepackFailedUntil = new Dictionary<ushort, float>();

        private WearRepack TryRepackForWear(byte wearPage, ItemBagAsset newBag, ItemBagAsset oldBag, ItemJar garment, float now, out byte garmentPage, out ItemJar garmentJar)
        {
            garmentPage = 0;
            garmentJar = garment;
            if (m_WearRepackFailedUntil.TryGetValue(newBag.id, out var until) && now < until)
            {
                return WearRepack.Later;
            }
            var inventory = m_Player.inventory;
            var equipment = m_Player.equipment;
            var grids = new bool[PlayerInventory.STORAGE][,];
            var moving = new List<BagPlacement>();
            BagPlacement? garmentEntry = null;
            for (byte p = PlayerInventory.SLOTS; p < PlayerInventory.STORAGE; p++)
            {
                var items = inventory.items[p];
                if (items == null || items.width == 0 || items.height == 0)
                {
                    continue;
                }
                var width = p == wearPage ? Mathf.Min(items.width, newBag.width) : items.width;
                var height = p == wearPage ? Mathf.Min(items.height, newBag.height) : items.height;
                grids[p] = new bool[width, height];
                var count = items.getItemCount();
                for (byte index = 0; index < count; index++)
                {
                    var jar = items.getItem(index);
                    if (jar == null)
                    {
                        continue;
                    }
                    var entry = new BagPlacement { Page = p, Index = index, Item = jar.item, SizeX = jar.size_x, SizeY = jar.size_y };
                    if (ReferenceEquals(jar, garment))
                    {
                        garmentEntry = entry;
                        continue;
                    }
                    if (equipment.equippedPage == p && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y)
                    {
                        // The item in hand stays put; inside the shared shape only.
                        if (!MarkCells(grids[p], jar.x, jar.y, (jar.rot & 1) == 0 ? jar.size_x : jar.size_y, (jar.rot & 1) == 0 ? jar.size_y : jar.size_x))
                        {
                            return Fail(newBag, now, "o item na mao esta fora do formato novo");
                        }
                        continue;
                    }
                    moving.Add(entry);
                }
            }
            if (garmentEntry == null)
            {
                return WearRepack.Later;
            }
            // The garment goes first, outside the page it replaces.
            var wearGrid = grids[wearPage];
            grids[wearPage] = null!;
            var garmentPlan = garmentEntry.Value;
            var placed = TryPlace(grids, garmentPlan.SizeX, garmentPlan.SizeY, out garmentPlan.NewPage, out garmentPlan.NewX, out garmentPlan.NewY, out garmentPlan.NewRot);
            grids[wearPage] = wearGrid;
            if (!placed)
            {
                return Fail(newBag, now, "sem lugar para a peca nova fora do bolso trocado");
            }
            moving.Sort((left, right) => (right.SizeX * right.SizeY).CompareTo(left.SizeX * left.SizeY));
            for (var i = 0; i < moving.Count; i++)
            {
                var entry = moving[i];
                if (!TryPlace(grids, entry.SizeX, entry.SizeY, out entry.NewPage, out entry.NewX, out entry.NewY, out entry.NewRot))
                {
                    return Fail(newBag, now, "os itens nao cabem nem reorganizando");
                }
                moving[i] = entry;
            }
            moving.Add(garmentPlan);
            var removal = new List<BagPlacement>(moving);
            removal.Sort((left, right) => left.Page != right.Page
                ? left.Page.CompareTo(right.Page)
                : right.Index.CompareTo(left.Index));
            foreach (var entry in removal)
            {
                inventory.items[entry.Page].removeItem(entry.Index);
            }
            foreach (var entry in moving)
            {
                inventory.items[entry.NewPage].addItem(entry.NewX, entry.NewY, entry.NewRot, entry.Item);
            }
            ClearKnownEmptyGun();
            var garmentItems = inventory.items[garmentPlan.NewPage];
            var garmentCount = garmentItems.getItemCount();
            for (byte index = 0; index < garmentCount; index++)
            {
                var jar = garmentItems.getItem(index);
                if (jar != null && ReferenceEquals(jar.item, garmentPlan.Item))
                {
                    garmentPage = garmentPlan.NewPage;
                    garmentJar = jar;
                    BotLog(LogLevel.Information,
                        "Bot reorganizou o inventario ({Count} itens) para vestir {New} ({NewSize}) no lugar de {Old} ({OldSize}) sem derrubar nada.",
                        moving.Count,
                        newBag.itemName,
                        newBag.width + "x" + newBag.height,
                        oldBag.itemName,
                        oldBag.width + "x" + oldBag.height);
                    return WearRepack.Done;
                }
            }
            return WearRepack.Later;
        }

        private WearRepack Fail(ItemBagAsset newBag, float now, string why)
        {
            m_WearRepackFailedUntil[newBag.id] = now + 30f;
            BotLog(LogLevel.Information,
                "Nao vestiu {New} agora: {Why}; tenta de novo em 30s.",
                newBag.itemName,
                why);
            return WearRepack.Impossible;
        }
    }
}
