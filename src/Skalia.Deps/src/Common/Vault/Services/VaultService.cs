using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.API.Persistence;
using OpenMod.Unturned.Players;
using SDG.Unturned;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class VaultService : IVaultService, IDisposable
    {
        private readonly object m_Sync = new object();
        private readonly Dictionary<ulong, VaultSession> m_Sessions = new Dictionary<ulong, VaultSession>();
        private readonly IDataStore m_DataStore;
        private readonly IRoleBenefitsService m_RoleBenefitsService;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<VaultService> m_Logger;
        private bool m_Disposed;

        public VaultService(
            IDataStore dataStore,
            IRoleBenefitsService roleBenefitsService,
            IStringLocalizer stringLocalizer,
            ILogger<VaultService> logger)
        {
            m_DataStore = dataStore;
            m_RoleBenefitsService = roleBenefitsService;
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
        }

        public async Task OpenAsync(UnturnedPlayer player)
        {
            var dimensions = await m_RoleBenefitsService.GetVaultDimensionsAsync(player);
            await UniTask.SwitchToMainThread();

            if (!player.IsAlive)
            {
                await TellAsync(player, "messages:vault:must_be_alive");
                return;
            }

            var playerId = player.SteamId.m_SteamID;
            VaultSession? session;
            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Sessions.TryGetValue(playerId, out session);
            }

            if (session == null)
            {
                VaultData data;
                try
                {
                    data = await LoadVaultAsync(playerId, dimensions);
                }
                catch (Exception exception)
                {
                    m_Logger.LogError(exception, "Falha ao carregar o Vault de {PlayerId}.", playerId);
                    await TellAsync(player, "messages:vault:load_failed");
                    return;
                }

                await UniTask.SwitchToMainThread();
                try
                {
                    var items = CreateItems(data, dimensions);
                    session = new VaultSession(
                        playerId,
                        player,
                        items,
                        data.Clone(),
                        dimensions);
                    AttachChangeHandlers(session);
                    lock (m_Sync)
                    {
                        if (m_Disposed)
                        {
                            return;
                        }

                        if (m_Sessions.TryGetValue(playerId, out var existing))
                        {
                            session = existing;
                            session.Player = player;
                        }
                        else
                        {
                            m_Sessions.Add(playerId, session);
                        }
                    }
                }
                catch (Exception exception)
                {
                    m_Logger.LogError(exception, "Os dados do Vault de {PlayerId} sao invalidos.", playerId);
                    await TellAsync(player, "messages:vault:load_failed");
                    return;
                }
            }
            else
            {
                session.Player = player;
            }

            if (session.IsFaulted)
            {
                await TellAsync(player, "messages:vault:load_failed");
                return;
            }

            try
            {
                var inventory = player.Player.inventory;
                if (session.IsOpen && inventory.isStoring)
                {
                    return;
                }

                OpenVaultStorage(inventory, session.Items);
                session.IsOpen = true;
                m_Logger.LogInformation("Vault {Width}x{Height} aberto por {PlayerId} com {ItemCount} item(ns).",
                    session.Dimensions.Width,
                    session.Dimensions.Height,
                    playerId,
                    session.Items.getItemCount());
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Falha ao abrir o Vault de {PlayerId}.", playerId);
                await TellAsync(player, "messages:vault:open_failed");
            }
        }

        public async Task SaveIfOpenAsync(UnturnedPlayer player)
        {
            VaultSession? session;
            lock (m_Sync)
            {
                m_Sessions.TryGetValue(player.SteamId.m_SteamID, out session);
            }

            if (session == null || session.IsFaulted || !session.IsOpen)
            {
                return;
            }

            session.IsOpen = false;
            await UniTask.SwitchToMainThread();

            await PersistAsync(session);
            await UniTask.SwitchToMainThread();
            player.Player.inventory.save();
        }

        public async Task SaveAndCloseSessionAsync(UnturnedPlayer player)
        {
            VaultSession? session;
            lock (m_Sync)
            {
                if (!m_Sessions.TryGetValue(player.SteamId.m_SteamID, out session))
                {
                    return;
                }
            }

            await UniTask.SwitchToMainThread();
            await PersistAsync(session);
            await UniTask.SwitchToMainThread();
            player.Player.inventory.save();

            lock (m_Sync)
            {
                if (m_Sessions.TryGetValue(player.SteamId.m_SteamID, out var active) &&
                    ReferenceEquals(active, session))
                {
                    m_Sessions.Remove(player.SteamId.m_SteamID);
                }
            }

            session.Player = null;
            session.IsOpen = false;
            m_Logger.LogInformation("Vault salvo ao desconectar o jogador {PlayerId}.", player.SteamId.m_SteamID);
        }

        public void Dispose()
        {
            List<VaultSession> sessions;
            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Disposed = true;
                sessions = m_Sessions.Values.ToList();
                m_Sessions.Clear();
            }

            if (sessions.Count > 0)
            {
                m_Logger.LogWarning(
                    "VaultService descartado sem ShutdownAsync; {Count} sessao(oes) serao apenas liberadas para evitar acesso ao Unity fora da game thread.",
                    sessions.Count);
            }

            foreach (var session in sessions)
            {
                session.SaveSemaphore.Dispose();
            }
        }

        public async Task ShutdownAsync()
        {
            await UniTask.SwitchToMainThread();

            List<VaultSession> sessions;
            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Disposed = true;
                sessions = m_Sessions.Values.ToList();
                m_Sessions.Clear();
            }

            // Items e PlayerInventory pertencem ao Unity. Capture e feche todas as
            // sessoes antes do primeiro await que possa continuar em outra thread.
            var snapshots = new Dictionary<VaultSession, VaultData>();
            foreach (var session in sessions)
            {
                snapshots.Add(session, Capture(session));
                var wasOpen = session.IsOpen;
                session.IsOpen = false;

                if (session.Player != null)
                {
                    var inventory = session.Player.Player.inventory;
                    if (wasOpen && inventory.isStoring)
                    {
                        inventory.closeStorageAndNotifyClient();
                    }

                    inventory.save();
                    session.Player = null;
                }
            }

            foreach (var session in sessions)
            {
                try
                {
                    await PersistSnapshotAsync(session, snapshots[session]);
                }
                catch (Exception exception)
                {
                    m_Logger.LogError(exception,
                        "Falha ao salvar o Vault de {PlayerId} durante o descarregamento.",
                        session.PlayerId);
                }
                finally
                {
                    session.SaveSemaphore.Dispose();
                }
            }
        }

        private void AttachChangeHandlers(VaultSession session)
        {
            session.Items.onItemAdded += (page, index, jar) => OnVaultChanged(session);
            session.Items.onItemRemoved += (page, index, jar) => OnVaultChanged(session);
            session.Items.onItemUpdated += (page, index, jar) => OnVaultChanged(session);
            session.Items.onStateUpdated += () => OnVaultChanged(session);
            session.Items.onItemsResized += (page, width, height) => OnVaultChanged(session);
        }

        private void OnVaultChanged(VaultSession session)
        {
            if (session.IsPersisting || session.IsFaulted || m_Disposed)
            {
                return;
            }

            try
            {
                PersistAsync(session).GetAwaiter().GetResult();
                session.Player?.Player.inventory.save();
            }
            catch (Exception exception)
            {
                session.IsFaulted = true;
                m_Logger.LogError(exception, "Falha ao persistir uma alteracao do Vault de {PlayerId}.",
                    session.PlayerId);
                if (session.Player != null)
                {
                    TellAsync(session.Player, "messages:vault:save_failed").GetAwaiter().GetResult();
                    session.IsOpen = false;
                    session.Player.Player.inventory.closeStorageAndNotifyClient();
                }
            }
        }

        private async Task PersistAsync(VaultSession session)
        {
            var snapshot = Capture(session);
            await PersistSnapshotAsync(session, snapshot);
        }

        private async Task PersistSnapshotAsync(VaultSession session, VaultData snapshot)
        {
            await session.SaveSemaphore.WaitAsync();
            try
            {
                session.IsPersisting = true;
                var primaryKey = GetPrimaryKey(session.PlayerId);
                var backupKey = GetBackupKey(session.PlayerId);

                await m_DataStore.SaveAsync(backupKey, session.LastPersisted.Clone());
                await m_DataStore.SaveAsync(primaryKey, snapshot);
                await m_DataStore.SaveAsync(backupKey, snapshot.Clone());
                session.LastPersisted = snapshot.Clone();
            }
            finally
            {
                session.IsPersisting = false;
                session.SaveSemaphore.Release();
            }
        }

        private async Task<VaultData> LoadVaultAsync(ulong playerId, VaultDimensions dimensions)
        {
            var primaryKey = GetPrimaryKey(playerId);
            if (!await m_DataStore.ExistsAsync(primaryKey))
            {
                return NewVaultData(dimensions);
            }

            try
            {
                var primary = await m_DataStore.LoadAsync<VaultData>(primaryKey);
                return Normalize(
                    primary ?? throw new InvalidDataException("Vault principal vazio."),
                    dimensions);
            }
            catch (Exception primaryException)
            {
                var backupKey = GetBackupKey(playerId);
                if (!await m_DataStore.ExistsAsync(backupKey))
                {
                    throw new InvalidDataException("Vault principal invalido e backup inexistente.", primaryException);
                }

                var backup = await m_DataStore.LoadAsync<VaultData>(backupKey);
                if (backup == null)
                {
                    throw new InvalidDataException("Vault principal e backup invalidos.", primaryException);
                }

                var recovered = Normalize(backup, dimensions);
                await m_DataStore.SaveAsync(primaryKey, recovered);
                m_Logger.LogWarning(primaryException, "Vault de {PlayerId} recuperado pelo backup.", playerId);
                return recovered;
            }
        }

        private static Items CreateItems(VaultData data, VaultDimensions dimensions)
        {
            var items = new Items(PlayerInventory.STORAGE);
            items.resize(dimensions.Width, dimensions.Height);

            foreach (var stored in data.Items)
            {
                var asset = Assets.find(EAssetType.ITEM, stored.ItemId) as ItemAsset;
                if (asset == null)
                {
                    throw new InvalidDataException($"Item inexistente no Vault: {stored.ItemId}.");
                }

                var state = string.IsNullOrEmpty(stored.StateBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(stored.StateBase64);
                if (!items.checkSpaceEmpty(stored.X, stored.Y, asset.size_x, asset.size_y, stored.Rotation))
                {
                    throw new InvalidDataException($"Item {stored.ItemId} ocupa uma posicao invalida no Vault.");
                }

                items.loadItem(stored.X, stored.Y, stored.Rotation,
                    new Item(stored.ItemId, stored.Amount, stored.Quality, state));
            }

            return items;
        }

        private static VaultData Capture(VaultSession session)
        {
            var data = NewVaultData(session.Dimensions);
            foreach (var jar in session.Items.items)
            {
                data.Items.Add(new VaultItemData
                {
                    X = jar.x,
                    Y = jar.y,
                    Rotation = jar.rot,
                    ItemId = jar.item.id,
                    Amount = jar.item.amount,
                    Quality = jar.item.quality,
                    StateBase64 = Convert.ToBase64String(jar.item.state ?? Array.Empty<byte>())
                });
            }

            return data;
        }

        private static void OpenVaultStorage(PlayerInventory inventory, Items items)
        {
            if (inventory.isStoring)
            {
                inventory.closeStorage();
            }

            inventory.isStoring = true;
            inventory.isStorageTrunk = false;
            inventory.storage = null;
            inventory.updateItems(PlayerInventory.STORAGE, items);
            inventory.sendStorage();
        }

        private static VaultData Normalize(VaultData data, VaultDimensions dimensions)
        {
            data.Width = dimensions.Width;
            data.Height = dimensions.Height;
            if (data.Items == null)
            {
                throw new InvalidDataException("Lista de itens ausente no Vault.");
            }

            return data;
        }

        private static VaultData NewVaultData(VaultDimensions dimensions)
        {
            return new VaultData
            {
                Width = dimensions.Width,
                Height = dimensions.Height
            };
        }

        private async Task TellAsync(UnturnedPlayer player, string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            await player.PrintMessageAsync(
                $"{m_StringLocalizer["prefix"].Value} {message}");
        }

        private static string GetPrimaryKey(ulong playerId) => $"vault.{playerId}";

        private static string GetBackupKey(ulong playerId) => $"vault.{playerId}.backup";

        private sealed class VaultSession
        {
            public VaultSession(
                ulong playerId,
                UnturnedPlayer player,
                Items items,
                VaultData lastPersisted,
                VaultDimensions dimensions)
            {
                PlayerId = playerId;
                Player = player;
                Items = items;
                LastPersisted = lastPersisted;
                Dimensions = dimensions;
            }

            public VaultDimensions Dimensions { get; }

            public ulong PlayerId { get; }

            public UnturnedPlayer? Player { get; set; }

            public Items Items { get; }

            public VaultData LastPersisted { get; set; }

            public SemaphoreSlim SaveSemaphore { get; } = new SemaphoreSlim(1, 1);

            public bool IsPersisting { get; set; }

            public bool IsFaulted { get; set; }

            public bool IsOpen { get; set; }
        }
    }
}
