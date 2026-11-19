using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.API.Persistence;
using OpenMod.Unturned.Players;
using SDG.Unturned;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class SpawnLoadoutService : ISpawnLoadoutService, IDisposable
    {
        // OpenMod data-store keys accept only alphanumeric characters and dots.
        private const string StateDataKey = "spawn.loadouts.state";
        private const int MaximumQuantityPerEntry = 100;
        private readonly IConfiguration m_Configuration;
        private readonly IDataStore m_DataStore;
        private readonly ILogger<SpawnLoadoutService> m_Logger;
        private readonly SemaphoreSlim m_StateSemaphore = new SemaphoreSlim(1, 1);
        private readonly object m_RandomSync = new object();
        private readonly Random m_Random = new Random();
        private SpawnLoadoutStateData m_State = new SpawnLoadoutStateData();
        private bool m_StateLoaded;
        private bool m_Disposed;

        public SpawnLoadoutService(
            IConfiguration configuration,
            IDataStore dataStore,
            ILogger<SpawnLoadoutService> logger)
        {
            m_Configuration = configuration;
            m_DataStore = dataStore;
            m_Logger = logger;
        }

        public async Task HandleSpawnAsync(UnturnedPlayer player)
        {
            if (!m_Configuration.GetValue("spawnLoadouts:enabled", true))
            {
                return;
            }

            var mapName = GetMapName();
            var mapSection = FindMapSection(mapName);
            if (mapSection == null)
            {
                return;
            }

            var equipment = ReadEntries(mapSection.GetSection("equipment"));
            var inventoryItems = ReadEntries(mapSection.GetSection("items"));
            var randomEquipment = ReadRandomEntries(mapSection.GetSection("randomEquipment"));
            var randomInventoryItems = ReadRandomEntries(mapSection.GetSection("randomItems"));
            if (equipment.Count == 0 && inventoryItems.Count == 0
                && randomEquipment.Count == 0 && randomInventoryItems.Count == 0)
            {
                return;
            }

            if (!await TryMarkGrantedAsync(mapName, player.SteamId.m_SteamID, true))
            {
                return;
            }

            await UniTask.SwitchToMainThread();
            if (!player.IsAlive)
            {
                await TryMarkGrantedAsync(mapName, player.SteamId.m_SteamID, false);
                return;
            }

            var granted = 0;
            foreach (var entry in equipment)
            {
                granted += GiveEquipment(player, entry);
            }

            foreach (var entries in randomEquipment)
            {
                granted += GiveEquipment(player, ChooseRandom(entries));
            }

            foreach (var entry in inventoryItems)
            {
                granted += GiveInventoryItems(player, entry);
            }

            foreach (var entries in randomInventoryItems)
            {
                granted += GiveInventoryItems(player, ChooseRandom(entries));
            }

            player.Player.inventory.save();
            m_Logger.LogInformation(
                "Loadout de nascimento aplicado a {Player} no mapa {Map}: {Count} item(ns).",
                player.SteamPlayer.playerID.characterName,
                mapName,
                granted);
        }

        public async Task HandleDeathAsync(UnturnedPlayer player)
        {
            if (!m_Configuration.GetValue("spawnLoadouts:enabled", true))
            {
                return;
            }

            var mapName = GetMapName();
            if (FindMapSection(mapName) == null)
            {
                return;
            }

            await TryMarkGrantedAsync(mapName, player.SteamId.m_SteamID, false);
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_StateSemaphore.Dispose();
        }

        private IConfigurationSection? FindMapSection(string mapName)
        {
            var mapsSection = m_Configuration.GetSection("spawnLoadouts:maps");
            var exactMap = mapsSection.GetChildren().FirstOrDefault(section =>
                string.Equals(section.Key, mapName, StringComparison.OrdinalIgnoreCase));
            return exactMap ?? mapsSection.GetChildren().FirstOrDefault(section =>
                string.Equals(section.Key, "default", StringComparison.OrdinalIgnoreCase));
        }

        private List<LoadoutEntry> ReadEntries(IConfigurationSection section)
        {
            var entries = new List<LoadoutEntry>();
            foreach (var itemSection in section.GetChildren())
            {
                var configuredId = itemSection.GetValue("id", 0);
                if (configuredId <= 0 || configuredId > ushort.MaxValue)
                {
                    m_Logger.LogWarning(
                        "Item ignorado em {Path}: ID {Id} invalido.",
                        itemSection.Path,
                        configuredId);
                    continue;
                }

                var quantity = Clamp(
                    itemSection.GetValue("quantity", 1),
                    1,
                    MaximumQuantityPerEntry);
                var quality = Clamp(itemSection.GetValue("quality", 100), 0, 100);
                entries.Add(new LoadoutEntry((ushort)configuredId, quantity, (byte)quality));
            }

            return entries;
        }

        private List<List<LoadoutEntry>> ReadRandomEntries(IConfigurationSection section)
        {
            var groups = new List<List<LoadoutEntry>>();
            foreach (var groupSection in section.GetChildren())
            {
                var quantity = Clamp(groupSection.GetValue("quantity", 1), 1, MaximumQuantityPerEntry);
                var quality = (byte)Clamp(groupSection.GetValue("quality", 100), 0, 100);
                var choices = new List<LoadoutEntry>();
                foreach (var choiceSection in groupSection.GetSection("choices").GetChildren())
                {
                    if (!int.TryParse(choiceSection.Value, out var configuredId)
                        || configuredId <= 0 || configuredId > ushort.MaxValue)
                    {
                        m_Logger.LogWarning(
                            "Escolha aleatoria ignorada em {Path}: ID {Id} invalido.",
                            choiceSection.Path,
                            choiceSection.Value);
                        continue;
                    }

                    choices.Add(new LoadoutEntry((ushort)configuredId, quantity, quality));
                }

                if (choices.Count > 0)
                {
                    groups.Add(choices);
                }
            }

            return groups;
        }

        private LoadoutEntry ChooseRandom(IReadOnlyList<LoadoutEntry> entries)
        {
            lock (m_RandomSync)
            {
                return entries[m_Random.Next(entries.Count)];
            }
        }

        private int GiveEquipment(UnturnedPlayer player, LoadoutEntry entry)
        {
            var asset = Assets.find(EAssetType.ITEM, entry.Id) as ItemAsset;
            if (asset == null || asset.isPro)
            {
                LogInvalidAsset(entry.Id);
                return 0;
            }

            for (var index = 0; index < entry.Quantity; index++)
            {
                var item = new Item(entry.Id, EItemOrigin.ADMIN, entry.Quality);
                if (!TryWearClothing(player, asset, item))
                {
                    player.Player.inventory.forceAddItemAuto(
                        item,
                        autoEquipWeapon: true,
                        autoEquipUseable: true,
                        autoEquipClothing: false,
                        playEffect: false);
                }
            }

            return entry.Quantity;
        }

        private int GiveInventoryItems(UnturnedPlayer player, LoadoutEntry entry)
        {
            var asset = Assets.find(EAssetType.ITEM, entry.Id) as ItemAsset;
            if (asset == null || asset.isPro)
            {
                LogInvalidAsset(entry.Id);
                return 0;
            }

            for (var index = 0; index < entry.Quantity; index++)
            {
                player.Player.inventory.forceAddItemAuto(
                    new Item(entry.Id, EItemOrigin.ADMIN, entry.Quality),
                    autoEquipWeapon: false,
                    autoEquipUseable: false,
                    autoEquipClothing: false,
                    playEffect: false);
            }

            return entry.Quantity;
        }

        private static bool TryWearClothing(UnturnedPlayer player, ItemAsset asset, Item item)
        {
            var clothing = player.Player.clothing;
            switch (asset.type)
            {
                case EItemType.SHIRT:
                    clothing.askWearShirt(item.id, item.quality, item.state, false);
                    return true;
                case EItemType.PANTS:
                    clothing.askWearPants(item.id, item.quality, item.state, false);
                    return true;
                case EItemType.HAT:
                    clothing.askWearHat(item.id, item.quality, item.state, false);
                    return true;
                case EItemType.BACKPACK:
                    clothing.askWearBackpack(item.id, item.quality, item.state, false);
                    return true;
                case EItemType.VEST:
                    clothing.askWearVest(item.id, item.quality, item.state, false);
                    return true;
                case EItemType.MASK:
                    clothing.askWearMask(item.id, item.quality, item.state, false);
                    return true;
                case EItemType.GLASSES:
                    clothing.askWearGlasses(item.id, item.quality, item.state, false);
                    return true;
                default:
                    return false;
            }
        }

        private async Task<bool> TryMarkGrantedAsync(string mapName, ulong playerId, bool granted)
        {
            await m_StateSemaphore.WaitAsync();
            try
            {
                await EnsureStateLoadedAsync();
                var mapKey = mapName.Trim().ToLowerInvariant();
                if (!m_State.GrantedLives.TryGetValue(mapKey, out var players))
                {
                    players = new Dictionary<ulong, bool>();
                    m_State.GrantedLives[mapKey] = players;
                }

                if (players.TryGetValue(playerId, out var currentValue) && currentValue == granted)
                {
                    return false;
                }

                players[playerId] = granted;
                await m_DataStore.SaveAsync(StateDataKey, m_State);
                return true;
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao atualizar o controle de loadout de {Player} no mapa {Map}.",
                    playerId,
                    mapName);
                return false;
            }
            finally
            {
                m_StateSemaphore.Release();
            }
        }

        private async Task EnsureStateLoadedAsync()
        {
            if (m_StateLoaded)
            {
                return;
            }

            if (await m_DataStore.ExistsAsync(StateDataKey))
            {
                m_State = await m_DataStore.LoadAsync<SpawnLoadoutStateData>(StateDataKey) ??
                          new SpawnLoadoutStateData();
            }
            else
            {
                m_State = new SpawnLoadoutStateData();
                await m_DataStore.SaveAsync(StateDataKey, m_State);
            }

            m_State.GrantedLives ??= new Dictionary<string, Dictionary<ulong, bool>>();
            m_StateLoaded = true;
        }

        private void LogInvalidAsset(ushort id)
        {
            m_Logger.LogWarning("Item {Id} do loadout nao existe ou nao pode ser entregue.", id);
        }

        private static string GetMapName()
        {
            return Level.info?.name ?? Provider.map ?? string.Empty;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private sealed class LoadoutEntry
        {
            public LoadoutEntry(ushort id, int quantity, byte quality)
            {
                Id = id;
                Quantity = quantity;
                Quality = quality;
            }

            public ushort Id { get; }

            public int Quantity { get; }

            public byte Quality { get; }
        }
    }
}
