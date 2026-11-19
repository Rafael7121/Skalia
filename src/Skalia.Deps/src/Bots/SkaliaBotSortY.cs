using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.10 (test of 18/11, 18:37:49): AlemaoGamer had no room for
    // a Sabertooth and dropped its Cobra for it; the pickup failed and it
    // fought the tester with a knife. the tester (18/11): "faca o sort funcionar
    // pelo comando ... assim ele organiza o inventario". The server's /sort
    // (InventorySortService) works without the Workshop interface pack UI now,
    // and the bot types it in the chat like a player when an item it
    // wants, or a gun it has to put away, does not fit as the bag is but
    // would fit with the bag in order (CanPackBagWith). It waits a moment
    // for the sort and tries again; only then something is dropped.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float SortCooldownSeconds = 30f;
        private const float SortSettleSeconds = 1f;
        private float m_NextSortAt = float.NegativeInfinity;
        private float m_SortTypedAt = float.NegativeInfinity;
        private readonly Dictionary<int, bool> m_SortRoomCache = new Dictionary<int, bool>();
        private float m_SortRoomCacheAt = float.NegativeInfinity;

        private bool IsSortSettling(float now)
        {
            return now - m_SortTypedAt < SortSettleSeconds;
        }

        // Room a /sort would open for this item (cached for 1s: the loot
        // scan asks for many items at once).
        private bool CanSortOpenRoomFor(ItemAsset asset)
        {
            if (!InventorySortService.CommandAvailable)
            {
                return false;
            }
            var now = Time.realtimeSinceStartup;
            if (now - m_SortRoomCacheAt > 1f)
            {
                m_SortRoomCache.Clear();
                m_SortRoomCacheAt = now;
            }
            var key = asset.size_x << 8 | asset.size_y;
            if (!m_SortRoomCache.TryGetValue(key, out var fits))
            {
                fits = CanPackBagWith(asset);
                m_SortRoomCache[key] = fits;
            }
            return fits;
        }

        // True when /sort was typed now (the caller tries again after
        // SortSettleSeconds).
        private bool TryTypeSort(ItemAsset forItem, string why, float now)
        {
            if (!InventorySortService.CommandAvailable
                || now < m_NextSortAt
                || m_Player.life.isDead
                || m_TreatmentPhase != 0
                || m_Player.equipment.isBusy
                || !CanPackBagWith(forItem))
            {
                return false;
            }
            m_NextSortAt = now + SortCooldownSeconds;
            m_SortTypedAt = now;
            m_SortRoomCache.Clear();
            // The sort moves the bag's items: a gun known empty at a spot
            // may be elsewhere now.
            ClearKnownEmptyGun();
            BotLog(LogLevel.Information,
                "Bot digitou /sort para abrir espaco: {Why}.",
                why);
            var context = BotInvocationContext();
            ChatManager.ReceiveChatRequest(in context, (byte)EChatMode.GLOBAL, "/sort");
            return true;
        }
    }
}
