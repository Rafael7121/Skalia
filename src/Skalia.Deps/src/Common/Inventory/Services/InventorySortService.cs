using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.API.Persistence;
using OpenMod.Unturned.Players;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class InventorySortService : IInventorySortService
    {
        public const ushort DefaultEffectId = 59111;
        public const short DefaultEffectKey = 5911;
        public const string ButtonName = "BifrostInventorySortButton";
        public const string SortMenuButtonName = "BifrostInventorySortMenuButton";
        public const string SortMenuOkButtonName = "BifrostInventorySortMenuOkButton";
        public const string SortMenuDismissButtonName = "BifrostInventorySortMenuDismissButton";
        public const string CombineButtonName = "BifrostInventoryCombineButton";
        public const string DirectionButtonName = "BifrostInventoryDirectionButton";
        public const string CloseButtonName = "BifrostInventoryCloseButton";
        public const string AllInButtonName = "BifrostInventoryAllInButton";
        public const string AllOutButtonName = "BifrostInventoryAllOutButton";
        public const string SettingsButtonName = "BifrostInventorySettingsButton";
        public const string SettingsSaveButtonName = "BifrostInventorySettingsSaveButton";
        public const string SettingsClearButtonName = "BifrostInventorySettingsClearButton";
        public const string SettingsDismissButtonName = "BifrostInventorySettingsDismissButton";
        public const string PrioritySelectButtonName = "BifrostInventoryPrioritySelectButton";
        public const string PrioritySelectDismissButtonName =
            "BifrostInventoryPrioritySelectDismissButton";
        public const string SameSortButtonName = "BifrostInventorySameSortButton";
        public const string StoragePrioritySelectButtonName =
            "BifrostStoragePrioritySelectButton";
        public const string StorageSortButtonName = "BifrostStorageSortButton";
        public const string StorageDirectionButtonName = "BifrostStorageDirectionButton";
        private const string PreferencesDataKey = "inventory.sort.preferences";
        private const int PriorityGroupCount = 5;
        private const int PriorityGeometryPermutationCount = 24;
        private const int CompactGeometryPermutationCount = 32;
        private const int TransferGeometryPermutationCount = 4;

        // Standby: mantenha sincronizado com SkaliaInventorySortBuilder.
        private static readonly bool IsSortDropdownEnabled = false;

        private readonly Dictionary<ulong, float> m_LastClicks = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> m_LastTransferClicks =
            new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, PlayerSortPreferences> m_Preferences =
            new Dictionary<ulong, PlayerSortPreferences>();
        private readonly Dictionary<ulong, List<int>> m_StagedPriorityGroups =
            new Dictionary<ulong, List<int>>();
        private readonly Dictionary<ulong, List<int>> m_StagedStoragePriorityGroups =
            new Dictionary<ulong, List<int>>();
        private readonly Dictionary<ulong, bool> m_StagedUseSameSortForStorage =
            new Dictionary<ulong, bool>();
        private readonly HashSet<ulong> m_HiddenUntilInventoryReopen = new HashSet<ulong>();
        private readonly HashSet<ulong> m_OpenSortMenus = new HashSet<ulong>();
        private readonly HashSet<ulong> m_OpenSettingsMenus = new HashSet<ulong>();
        private readonly HashSet<ulong> m_OpenPrioritySelectMenus = new HashSet<ulong>();
        private readonly HashSet<ulong> m_OpenStoragePrioritySelectMenus =
            new HashSet<ulong>();
        private readonly Dictionary<ulong, uint> m_PendingUiRequests =
            new Dictionary<ulong, uint>();
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<InventorySortService> m_Logger;
        private readonly IDataStore m_DataStore;
        private readonly SemaphoreSlim m_PreferencesSemaphore = new SemaphoreSlim(1, 1);
        // inventorySort:enabled liga a UI do pacote de interface da Workshop (3765696023)
        // (botoes e menus). O comando /sort e independente dela
        // (inventorySort:commandEnabled): ordena com as preferencias salvas
        // ou o padrao do servidor, sem UI nenhuma. Os bots digitam /sort
        // (SkaliaBotSortY) quando falta espaco.
        private readonly bool m_Enabled;
        private readonly bool m_CommandEnabled;

        // Lido pelos bots: o /sort existe neste servidor.
        public static bool CommandAvailable { get; private set; }
        private readonly bool m_ShowButton;
        private readonly ushort m_EffectId;
        private readonly short m_EffectKey;
        private readonly float m_CooldownSeconds;
        private readonly PackingOrigin m_DefaultPackingOrigin;
        private readonly PackingOrder m_PackingOrder;
        private readonly bool m_RotateItems;
        private readonly bool m_CombinePagesByDefault;
        private InventorySortPreferencesData m_PersistedPreferences =
            new InventorySortPreferencesData();
        private bool m_PreferencesLoaded;
        private bool m_Started;
        private bool m_MissingEffectLogged;

        public InventorySortService(
            IConfiguration configuration,
            IDataStore dataStore,
            IStringLocalizer stringLocalizer,
            ILogger<InventorySortService> logger)
        {
            m_DataStore = dataStore;
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
            m_Enabled = configuration.GetValue("inventorySort:enabled", true);
            m_ShowButton = configuration.GetValue("inventorySort:showButton", true);
            m_CommandEnabled = configuration.GetValue("inventorySort:commandEnabled", true);
            m_EffectId = (ushort)Math.Max(1, Math.Min(ushort.MaxValue,
                configuration.GetValue("inventorySort:effectId", (int)DefaultEffectId)));
            m_EffectKey = (short)Math.Max(short.MinValue + 1, Math.Min(short.MaxValue,
                configuration.GetValue("inventorySort:effectKey", (int)DefaultEffectKey)));
            m_CooldownSeconds = Math.Max(0.25f,
                configuration.GetValue("inventorySort:cooldownSeconds", 1f));
            m_DefaultPackingOrigin = ParseEnum(
                configuration.GetValue("inventorySort:packingOrigin", "topLeft"),
                PackingOrigin.TopLeft);
            m_PackingOrder = ParseEnum(
                configuration.GetValue("inventorySort:packingOrder", "compact"),
                PackingOrder.Compact);
            m_RotateItems = configuration.GetValue("inventorySort:rotateItems", true);
            m_CombinePagesByDefault = configuration.GetValue(
                "inventorySort:combinePagesByDefault",
                true);
        }

        public void Start()
        {
            CommandAvailable = m_CommandEnabled;
            if (m_CommandEnabled)
            {
                EnsurePreferencesLoadedAsync().Forget();
            }

            if (!m_Enabled || !m_ShowButton || m_Started)
            {
                return;
            }

            EffectManager.onEffectButtonClicked += OnEffectButtonClicked;
            InventoryGestureRequestPatch.InventoryVisibilityRequested +=
                OnInventoryVisibilityRequested;
            StorageVisibilityPatch.StorageVisibilityChanged +=
                OnStorageVisibilityChanged;
            m_Started = true;
            EnsurePreferencesLoadedAsync().Forget();
        }

        public void Stop()
        {
            CommandAvailable = false;
            if (!m_Started)
            {
                return;
            }

            EffectManager.onEffectButtonClicked -= OnEffectButtonClicked;
            InventoryGestureRequestPatch.InventoryVisibilityRequested -=
                OnInventoryVisibilityRequested;
            StorageVisibilityPatch.StorageVisibilityChanged -=
                OnStorageVisibilityChanged;
            try
            {
                ClearUiForOnlinePlayers();
            }
            finally
            {
                // Mesmo que uma conexao desapareca durante o unload, o singleton
                // nao pode conservar estado nem listeners para o proximo reload.
                m_LastClicks.Clear();
                m_LastTransferClicks.Clear();
                m_Preferences.Clear();
                m_StagedPriorityGroups.Clear();
                m_StagedStoragePriorityGroups.Clear();
                m_StagedUseSameSortForStorage.Clear();
                m_HiddenUntilInventoryReopen.Clear();
                m_OpenSortMenus.Clear();
                m_OpenSettingsMenus.Clear();
                m_OpenPrioritySelectMenus.Clear();
                m_OpenStoragePrioritySelectMenus.Clear();
                m_PendingUiRequests.Clear();
                m_Started = false;
            }
        }

        public async UniTask SyncUiForOnlinePlayersAsync()
        {
            if (!m_Enabled)
            {
                return;
            }

            await UniTask.SwitchToMainThread();
            foreach (SteamPlayer client in Provider.clients)
            {
                if (client.player != null)
                {
                    if (m_ShowButton &&
                        client.player.animator.gesture == EPlayerGesture.INVENTORY_START)
                    {
                        SendUi(client.player);
                    }
                    else
                    {
                        // Remove tambem uma instancia enviada por uma versao
                        // anterior quando o plugin e recarregado sem desconectar.
                        EffectManager.askEffectClearByID(
                            m_EffectId,
                            client.transportConnection);
                    }
                }
            }
        }

        public async UniTask SortAsync(UnturnedPlayer player)
        {
            if (!m_CommandEnabled)
            {
                return;
            }

            await EnsurePreferencesLoadedAsync();
            await UniTask.SwitchToMainThread();
            SortForPlayer(player.Player);
        }

        private void SendUi(Player player)
        {
            var asset = Assets.find(EAssetType.EFFECT, m_EffectId) as EffectAsset;
            if (asset == null)
            {
                if (!m_MissingEffectLogged)
                {
                    m_MissingEffectLogged = true;
                    m_Logger.LogWarning(
                        "Effect UI {EffectId} do organizador de inventario nao foi encontrado. Atualize o pacote de interface da Workshop (3765696023) no servidor.",
                        m_EffectId);
                }
                return;
            }

            // O assembly de referencia do OpenMod ainda expoe somente este nome
            // legado, embora ele continue sendo o RPC suportado pelo jogo atual.
#pragma warning disable CS0612
            EffectManager.sendUIEffect(
                m_EffectId,
                m_EffectKey,
                player.channel.owner.playerID.steamID,
                true);
#pragma warning restore CS0612
            SyncUiState(player);
        }

        private void OnEffectButtonClicked(Player player, string buttonName)
        {
            HandleEffectButtonClickedAsync(player, buttonName).Forget();
        }

        private async UniTaskVoid HandleEffectButtonClickedAsync(Player player, string buttonName)
        {
            if (!m_Enabled)
            {
                return;
            }

            await EnsurePreferencesLoadedAsync();
            await UniTask.SwitchToMainThread();

            if (string.Equals(buttonName, ButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                m_OpenSortMenus.Remove(steamId);
                if (m_OpenSettingsMenus.Contains(steamId))
                {
                    if (!await ApplySettingsDraftAsync(player, steamId))
                    {
                        return;
                    }
                }
                else
                {
                    SyncUiState(player);
                }

                SortForPlayer(player);
            }
            else if (IsSortDropdownEnabled &&
                     string.Equals(buttonName, SortMenuButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                if (!m_OpenSortMenus.Add(steamId))
                {
                    m_OpenSortMenus.Remove(steamId);
                }
                SyncUiState(player);
            }
            else if (string.Equals(buttonName, CombineButtonName, StringComparison.Ordinal))
            {
                PlayerSortPreferences preferences = GetPreferences(player);
                preferences.CombinePages = !preferences.CombinePages;
                SyncUiState(player);
            }
            else if (string.Equals(buttonName, DirectionButtonName, StringComparison.Ordinal))
            {
                PlayerSortPreferences preferences = GetPreferences(player);
                preferences.Origin = GetNextOrigin(preferences.Origin);
                SyncUiState(player);
            }
            else if (IsSortDropdownEnabled &&
                     (string.Equals(buttonName, SortMenuOkButtonName, StringComparison.Ordinal) ||
                      string.Equals(buttonName, SortMenuDismissButtonName, StringComparison.Ordinal)))
            {
                m_OpenSortMenus.Remove(GetSteamId(player));
                SyncUiState(player);
            }
            else if (string.Equals(buttonName, AllInButtonName, StringComparison.Ordinal))
            {
                TransferStorageForPlayer(player, true);
            }
            else if (string.Equals(buttonName, AllOutButtonName, StringComparison.Ordinal))
            {
                TransferStorageForPlayer(player, false);
            }
            else if (string.Equals(buttonName, StorageDirectionButtonName, StringComparison.Ordinal))
            {
                PlayerSortPreferences preferences = GetPreferences(player);
                if (!preferences.UseSameSortForStorage && player.inventory.isStoring)
                {
                    preferences.StorageOrigin = GetNextOrigin(preferences.StorageOrigin);
                    SyncUiState(player);
                }
            }
            else if (string.Equals(buttonName, StorageSortButtonName, StringComparison.Ordinal))
            {
                SortStorageForPlayer(player);
            }
            else if (string.Equals(buttonName, CloseButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                m_HiddenUntilInventoryReopen.Add(steamId);
                m_OpenSortMenus.Remove(steamId);
                m_OpenSettingsMenus.Remove(steamId);
                m_OpenPrioritySelectMenus.Remove(steamId);
                m_OpenStoragePrioritySelectMenus.Remove(steamId);
                ClearSettingsDraft(steamId);
                m_PendingUiRequests.Remove(steamId);
                ClearUi(player);
            }
            else if (string.Equals(buttonName, SettingsButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                if (!m_OpenSettingsMenus.Add(steamId))
                {
                    m_OpenSettingsMenus.Remove(steamId);
                    m_OpenPrioritySelectMenus.Remove(steamId);
                    m_OpenStoragePrioritySelectMenus.Remove(steamId);
                    ClearSettingsDraft(steamId);
                }
                else
                {
                    m_OpenPrioritySelectMenus.Remove(steamId);
                    m_OpenStoragePrioritySelectMenus.Remove(steamId);
                    PlayerSortPreferences preferences = GetPreferences(player);
                    m_StagedPriorityGroups[steamId] = preferences.PriorityGroups.ToList();
                    m_StagedStoragePriorityGroups[steamId] =
                        preferences.StoragePriorityGroups.ToList();
                    m_StagedUseSameSortForStorage[steamId] =
                        preferences.UseSameSortForStorage;
                }

                SyncUiState(player);
            }
            else if (string.Equals(buttonName, SettingsDismissButtonName, StringComparison.Ordinal))
            {
                CloseSettings(player);
            }
            else if (string.Equals(
                         buttonName,
                         PrioritySelectDismissButtonName,
                         StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                bool changed = m_OpenPrioritySelectMenus.Remove(steamId);
                changed |= m_OpenStoragePrioritySelectMenus.Remove(steamId);
                if (changed)
                {
                    SyncUiState(player);
                }
            }
            else if (string.Equals(buttonName, SettingsClearButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId))
                {
                    m_StagedPriorityGroups[steamId] = CreateDefaultPriorityGroups();
                    m_StagedStoragePriorityGroups[steamId] =
                        CreateDefaultPriorityGroups();
                    m_StagedUseSameSortForStorage[steamId] = true;
                    m_OpenPrioritySelectMenus.Remove(steamId);
                    m_OpenStoragePrioritySelectMenus.Remove(steamId);
                    SyncUiState(player);
                }
            }
            else if (string.Equals(buttonName, SameSortButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId) &&
                    m_StagedUseSameSortForStorage.TryGetValue(steamId, out bool useSame))
                {
                    m_StagedUseSameSortForStorage[steamId] = !useSame;
                    m_OpenPrioritySelectMenus.Remove(steamId);
                    m_OpenStoragePrioritySelectMenus.Remove(steamId);
                    SyncUiState(player);
                }
            }
            else if (string.Equals(buttonName, PrioritySelectButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId))
                {
                    m_OpenStoragePrioritySelectMenus.Remove(steamId);
                    if (!m_OpenPrioritySelectMenus.Add(steamId))
                    {
                        m_OpenPrioritySelectMenus.Remove(steamId);
                    }
                    SyncUiState(player);
                }
            }
            else if (string.Equals(
                         buttonName,
                         StoragePrioritySelectButtonName,
                         StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId) &&
                    m_StagedUseSameSortForStorage.TryGetValue(steamId, out bool useSame) &&
                    !useSame)
                {
                    m_OpenPrioritySelectMenus.Remove(steamId);
                    if (!m_OpenStoragePrioritySelectMenus.Add(steamId))
                    {
                        m_OpenStoragePrioritySelectMenus.Remove(steamId);
                    }
                    SyncUiState(player);
                }
            }
            else if (TryGetPriorityGroupButton(buttonName, out int group))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId) &&
                    m_OpenPrioritySelectMenus.Contains(steamId) &&
                    m_StagedPriorityGroups.TryGetValue(steamId, out List<int> staged))
                {
                    if (staged.Contains(group))
                    {
                        staged.Remove(group);
                    }
                    else if (staged.Count < PriorityGroupCount)
                    {
                        staged.Add(group);
                        if (staged.Count == PriorityGroupCount)
                        {
                            m_OpenPrioritySelectMenus.Remove(steamId);
                        }
                    }
                    SyncUiState(player);
                }
            }
            else if (TryGetStoragePriorityGroupButton(buttonName, out int storageGroup))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId) &&
                    m_OpenStoragePrioritySelectMenus.Contains(steamId) &&
                    m_StagedStoragePriorityGroups.TryGetValue(
                        steamId,
                        out List<int> stagedStorage))
                {
                    if (stagedStorage.Contains(storageGroup))
                    {
                        stagedStorage.Remove(storageGroup);
                    }
                    else if (stagedStorage.Count < PriorityGroupCount)
                    {
                        stagedStorage.Add(storageGroup);
                        if (stagedStorage.Count == PriorityGroupCount)
                        {
                            m_OpenStoragePrioritySelectMenus.Remove(steamId);
                        }
                    }
                    SyncUiState(player);
                }
            }
            else if (TryGetPriorityTagButton(buttonName, out int tagIndex))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId) &&
                    m_StagedPriorityGroups.TryGetValue(steamId, out List<int> staged) &&
                    tagIndex >= 0 && tagIndex < staged.Count)
                {
                    staged.RemoveAt(tagIndex);
                    SyncUiState(player);
                }
            }
            else if (TryGetStoragePriorityTagButton(buttonName, out int storageTagIndex))
            {
                ulong steamId = GetSteamId(player);
                if (m_OpenSettingsMenus.Contains(steamId) &&
                    m_StagedStoragePriorityGroups.TryGetValue(
                        steamId,
                        out List<int> stagedStorage) &&
                    storageTagIndex >= 0 && storageTagIndex < stagedStorage.Count)
                {
                    stagedStorage.RemoveAt(storageTagIndex);
                    SyncUiState(player);
                }
            }
            else if (string.Equals(buttonName, SettingsSaveButtonName, StringComparison.Ordinal))
            {
                ulong steamId = GetSteamId(player);
                await ApplySettingsDraftAsync(player, steamId);
            }
        }

        private async UniTask<bool> ApplySettingsDraftAsync(Player player, ulong steamId)
        {
            if (!m_StagedPriorityGroups.TryGetValue(steamId, out List<int> staged) ||
                staged.Count != PriorityGroupCount ||
                !m_StagedStoragePriorityGroups.TryGetValue(
                    steamId,
                    out List<int> stagedStorage) ||
                stagedStorage.Count != PriorityGroupCount ||
                !m_StagedUseSameSortForStorage.TryGetValue(
                    steamId,
                    out bool useSameSortForStorage))
            {
                Tell(player, "messages:inventory_sort:priority_incomplete");
                return false;
            }

            List<int> inventoryOrder = staged.ToList();
            List<int> storageOrder = stagedStorage.ToList();
            if (!await SavePriorityGroupsAsync(
                    steamId,
                    inventoryOrder,
                    storageOrder,
                    useSameSortForStorage))
            {
                await UniTask.SwitchToMainThread();
                Tell(player, "messages:inventory_sort:priority_save_failed");
                return false;
            }

            await UniTask.SwitchToMainThread();
            PlayerSortPreferences preferences = GetPreferences(player);
            preferences.PriorityGroups = inventoryOrder;
            preferences.StoragePriorityGroups = storageOrder;
            preferences.UseSameSortForStorage = useSameSortForStorage;
            CloseSettings(player);
            return true;
        }

        private void OnInventoryVisibilityRequested(
            PlayerAnimator animator,
            bool isInventoryOpen)
        {
            if (!m_Enabled || !m_ShowButton || animator?.player == null)
            {
                return;
            }

            ulong steamId = GetSteamId(animator.player);
            if (isInventoryOpen)
            {
                // Cada START bruto representa uma nova abertura no cliente. Assim o
                // X permanece oculto somente ate o inventario ser aberto novamente,
                // mesmo quando o jogo descarta o STOP por postura ou item equipado.
                m_HiddenUntilInventoryReopen.Remove(steamId);
                m_OpenSortMenus.Remove(steamId);
                m_OpenSettingsMenus.Remove(steamId);
                m_OpenPrioritySelectMenus.Remove(steamId);
                m_OpenStoragePrioritySelectMenus.Remove(steamId);
                ClearSettingsDraft(steamId);
                QueueUiAfterInventoryStart(animator.player);
            }
            else
            {
                ResetAndClearUi(animator.player);
            }
        }

        private void OnStorageVisibilityChanged(
            PlayerInventory inventory,
            bool isStorageOpen)
        {
            Player? player = Provider.clients
                .Select(client => client.player)
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    ReferenceEquals(candidate.inventory, inventory));
            if (player == null)
            {
                return;
            }

            if (isStorageOpen)
            {
                // Entrar no banco do motorista concede acesso ao trunk e chama
                // sendStorage antes de o inventario ser aberto. A abertura da UI
                // pertence exclusivamente ao evento INVENTORY_START.
                return;
            }
            else
            {
                ResetAndClearUi(player);
            }
        }

        private void QueueUiAfterInventoryStart(Player player)
        {
            ulong steamId = GetSteamId(player);
            uint requestId = m_PendingUiRequests.TryGetValue(steamId, out uint previous)
                ? previous + 1u
                : 1u;
            m_PendingUiRequests[steamId] = requestId;
            SendUiAfterInventoryStartAsync(player, steamId, requestId).Forget();
        }

        private async UniTaskVoid SendUiAfterInventoryStartAsync(
            Player player,
            ulong steamId,
            uint requestId)
        {
            await UniTask.Delay(20);
            await UniTask.SwitchToMainThread();

            if (!m_Started ||
                !m_PendingUiRequests.TryGetValue(steamId, out uint currentRequestId) ||
                currentRequestId != requestId)
            {
                return;
            }

            m_PendingUiRequests.Remove(steamId);
            if (!Provider.clients.Any(client => ReferenceEquals(client.player, player)))
            {
                return;
            }

            SendUi(player);
        }

        private void ResetAndClearUi(Player player)
        {
            ulong steamId = GetSteamId(player);
            m_OpenSortMenus.Remove(steamId);
            m_OpenSettingsMenus.Remove(steamId);
            m_OpenPrioritySelectMenus.Remove(steamId);
            m_OpenStoragePrioritySelectMenus.Remove(steamId);
            ClearSettingsDraft(steamId);
            m_PendingUiRequests.Remove(steamId);
            ClearUi(player);
        }

        private void SyncUiState(Player player)
        {
            PlayerSortPreferences preferences = GetPreferences(player);
            ulong steamId = GetSteamId(player);
            SetVisibility(player, "CombineIcon", preferences.CombinePages);
            SetVisibility(player, "ExcludeIcon", !preferences.CombinePages);
            SetVisibility(player, "DirectionTopLeftIcon", preferences.Origin == PackingOrigin.TopLeft);
            SetVisibility(player, "DirectionTopRightIcon", preferences.Origin == PackingOrigin.TopRight);
            SetVisibility(player, "DirectionBottomRightIcon", preferences.Origin == PackingOrigin.BottomRight);
            SetVisibility(player, "DirectionBottomLeftIcon", preferences.Origin == PackingOrigin.BottomLeft);
            SetStorageDirectionIconVisibility(
                player,
                "TopLeft",
                preferences.StorageOrigin == PackingOrigin.TopLeft);
            SetStorageDirectionIconVisibility(
                player,
                "TopRight",
                preferences.StorageOrigin == PackingOrigin.TopRight);
            SetStorageDirectionIconVisibility(
                player,
                "BottomRight",
                preferences.StorageOrigin == PackingOrigin.BottomRight);
            SetStorageDirectionIconVisibility(
                player,
                "BottomLeft",
                preferences.StorageOrigin == PackingOrigin.BottomLeft);
            if (IsSortDropdownEnabled)
            {
                bool isSortMenuOpen = m_OpenSortMenus.Contains(steamId);
                SetVisibility(player, "SortDropdownControl", isSortMenuOpen);
                EffectManager.sendUIEffectVisibility(
                    m_EffectKey,
                    player.channel.owner.transportConnection,
                    true,
                    SortMenuDismissButtonName,
                    isSortMenuOpen);
            }

            bool isSettingsOpen = m_OpenSettingsMenus.Contains(steamId);
            bool isPrioritySelectOpen = isSettingsOpen &&
                m_OpenPrioritySelectMenus.Contains(steamId);
            bool isStoragePrioritySelectOpen = isSettingsOpen &&
                m_OpenStoragePrioritySelectMenus.Contains(steamId);
            bool useSameSortForStorage = isSettingsOpen &&
                m_StagedUseSameSortForStorage.TryGetValue(steamId, out bool stagedUseSame)
                    ? stagedUseSame
                    : preferences.UseSameSortForStorage;
            SetVisibility(player, "SettingsDropdownControl", isSettingsOpen);
            SetVisibility(player, "PrioritySelectMenuControl", isPrioritySelectOpen);
            SetVisibility(player, "PrioritySelectChevronDown", !isPrioritySelectOpen);
            SetVisibility(player, "PrioritySelectChevronUp", isPrioritySelectOpen);
            SetVisibility(
                player,
                "StoragePrioritySelectMenuControl",
                isStoragePrioritySelectOpen);
            SetVisibility(
                player,
                "StoragePrioritySelectChevronDown",
                !isStoragePrioritySelectOpen);
            SetVisibility(
                player,
                "StoragePrioritySelectChevronUp",
                isStoragePrioritySelectOpen);
            SetVisibility(player, "SameSortChecked", useSameSortForStorage);
            SetVisibility(player, "SameSortUnchecked", !useSameSortForStorage);
            SetVisibility(player, "SettingsSharedBackground", useSameSortForStorage);
            SetVisibility(player, "SettingsSeparateBackground", !useSameSortForStorage);
            SetVisibility(player, "SettingsSharedFooterControl", useSameSortForStorage);
            SetVisibility(player, "SettingsSeparateFooterControl", !useSameSortForStorage);
            SetVisibility(player, "StoragePrioritySection", !useSameSortForStorage);
            EffectManager.sendUIEffectVisibility(
                m_EffectKey,
                player.channel.owner.transportConnection,
                true,
                SettingsDismissButtonName,
                isSettingsOpen);
            EffectManager.sendUIEffectVisibility(
                m_EffectKey,
                player.channel.owner.transportConnection,
                true,
                PrioritySelectDismissButtonName,
                isPrioritySelectOpen || isStoragePrioritySelectOpen);
            SetVisibility(player, "SettingsIcon", !isSettingsOpen);
            SetVisibility(player, "SettingsActiveIcon", isSettingsOpen);

            IReadOnlyList<int> displayedGroups = isSettingsOpen &&
                m_StagedPriorityGroups.TryGetValue(steamId, out List<int> staged)
                    ? staged
                    : preferences.PriorityGroups;
            for (int index = 0; index < PriorityGroupCount; index++)
            {
                bool hasTag = index < displayedGroups.Count;
                int tagNumber = index + 1;
                SetVisibility(player, $"PriorityTag{tagNumber}Control", hasTag);
                SetVisibility(player, $"PriorityArrow{tagNumber}", hasTag && index > 0);
                for (int group = 1; group <= PriorityGroupCount; group++)
                {
                    SetVisibility(
                        player,
                        $"PriorityTag{tagNumber}Group{group}Icon",
                        hasTag && displayedGroups[index] == group);
                }
            }

            IReadOnlyList<int> displayedStorageGroups = isSettingsOpen &&
                m_StagedStoragePriorityGroups.TryGetValue(
                    steamId,
                    out List<int> stagedStorage)
                    ? stagedStorage
                    : preferences.StoragePriorityGroups;
            for (int index = 0; index < PriorityGroupCount; index++)
            {
                bool hasTag = index < displayedStorageGroups.Count;
                int tagNumber = index + 1;
                SetVisibility(player, $"StoragePriorityTag{tagNumber}Control", hasTag);
                SetVisibility(
                    player,
                    $"StoragePriorityArrow{tagNumber}",
                    hasTag && index > 0);
                for (int group = 1; group <= PriorityGroupCount; group++)
                {
                    SetVisibility(
                        player,
                        $"StoragePriorityTag{tagNumber}Group{group}Icon",
                        hasTag && displayedStorageGroups[index] == group);
                }
            }

            for (int group = 1; group <= PriorityGroupCount; group++)
            {
                SetVisibility(
                    player,
                    $"PriorityOptionGroup{group}Check",
                    isPrioritySelectOpen && displayedGroups.Contains(group));
                SetVisibility(
                    player,
                    $"StoragePriorityOptionGroup{group}Check",
                    isStoragePrioritySelectOpen && displayedStorageGroups.Contains(group));
            }

            SetVisibility(
                player,
                "SettingsSharedSaveControl",
                isSettingsOpen && useSameSortForStorage &&
                displayedGroups.Count == PriorityGroupCount);
            SetVisibility(
                player,
                "SettingsSeparateSaveControl",
                isSettingsOpen && !useSameSortForStorage &&
                displayedGroups.Count == PriorityGroupCount &&
                displayedStorageGroups.Count == PriorityGroupCount);
            SetStorageControlsVisibility(
                player,
                player.inventory.isStoring &&
                player.inventory.items[PlayerInventory.STORAGE].height > 0);
        }

        private void SetStorageControlsVisibility(Player player, bool visible)
        {
            string[] controlNames =
            {
                "AllInSlot",
                "NarrowAllInSlot",
                "WideAllOutControl",
                "NarrowAllOutControl",
                "StorageCloseControl",
                "NarrowStorageCloseControl"
            };
            foreach (string controlName in controlNames)
            {
                EffectManager.sendUIEffectVisibility(
                    m_EffectKey,
                    player.channel.owner.transportConnection,
                    true,
                    controlName,
                    visible);
            }

            string[] regularCloseControlNames =
            {
                "CloseControl",
                "NarrowCloseControl"
            };
            foreach (string controlName in regularCloseControlNames)
            {
                EffectManager.sendUIEffectVisibility(
                    m_EffectKey,
                    player.channel.owner.transportConnection,
                    true,
                    controlName,
                    !visible);
            }

            bool showIndependentStorageSort = visible &&
                !GetPreferences(player).UseSameSortForStorage;
            string[] independentStorageControls =
            {
                "WideStorageDirectionControl",
                "NarrowStorageDirectionControl",
                "WideStorageSortControl",
                "NarrowStorageSortControl"
            };
            foreach (string controlName in independentStorageControls)
            {
                EffectManager.sendUIEffectVisibility(
                    m_EffectKey,
                    player.channel.owner.transportConnection,
                    true,
                    controlName,
                    showIndependentStorageSort);
            }
        }

        private void SetVisibility(Player player, string childName, bool visible)
        {
            EffectManager.sendUIEffectVisibility(
                m_EffectKey,
                player.channel.owner.transportConnection,
                true,
                childName,
                visible);
            EffectManager.sendUIEffectVisibility(
                m_EffectKey,
                player.channel.owner.transportConnection,
                true,
                "Narrow" + childName,
                visible);
        }

        private void SetStorageDirectionIconVisibility(
            Player player,
            string direction,
            bool visible)
        {
            string[] prefixes = { "Wide", "Narrow" };
            foreach (string prefix in prefixes)
            {
                EffectManager.sendUIEffectVisibility(
                    m_EffectKey,
                    player.channel.owner.transportConnection,
                    true,
                    prefix + "StorageDirection" + direction + "Icon",
                    visible);
            }
        }

        private PlayerSortPreferences GetPreferences(Player player)
        {
            ulong steamId = GetSteamId(player);
            if (!m_Preferences.TryGetValue(steamId, out PlayerSortPreferences preferences))
            {
                preferences = new PlayerSortPreferences(
                    m_CombinePagesByDefault,
                    m_DefaultPackingOrigin,
                    GetPersistedPriorityGroups(steamId),
                    GetPersistedStoragePriorityGroups(steamId),
                    GetPersistedUseSameSortForStorage(steamId));
                m_Preferences.Add(steamId, preferences);
            }

            return preferences;
        }

        private void CloseSettings(Player player)
        {
            ulong steamId = GetSteamId(player);
            m_OpenSettingsMenus.Remove(steamId);
            m_OpenPrioritySelectMenus.Remove(steamId);
            m_OpenStoragePrioritySelectMenus.Remove(steamId);
            ClearSettingsDraft(steamId);
            SyncUiState(player);
        }

        private void ClearSettingsDraft(ulong steamId)
        {
            m_StagedPriorityGroups.Remove(steamId);
            m_StagedStoragePriorityGroups.Remove(steamId);
            m_StagedUseSameSortForStorage.Remove(steamId);
        }

        private static bool TryGetPriorityGroupButton(string buttonName, out int group)
        {
            const string prefix = "BifrostInventoryPriorityGroup";
            const string suffix = "Button";
            if (buttonName.StartsWith(prefix, StringComparison.Ordinal) &&
                buttonName.EndsWith(suffix, StringComparison.Ordinal) &&
                int.TryParse(
                    buttonName.Substring(prefix.Length, buttonName.Length - prefix.Length - suffix.Length),
                    out group) &&
                group >= 1 && group <= PriorityGroupCount)
            {
                return true;
            }

            group = 0;
            return false;
        }

        private static bool TryGetPriorityTagButton(string buttonName, out int index)
        {
            const string prefix = "BifrostInventoryPriorityTag";
            const string suffix = "Button";
            if (buttonName.StartsWith(prefix, StringComparison.Ordinal) &&
                buttonName.EndsWith(suffix, StringComparison.Ordinal) &&
                int.TryParse(
                    buttonName.Substring(prefix.Length, buttonName.Length - prefix.Length - suffix.Length),
                    out int tag) &&
                tag >= 1 && tag <= PriorityGroupCount)
            {
                index = tag - 1;
                return true;
            }

            index = -1;
            return false;
        }

        private static bool TryGetStoragePriorityGroupButton(
            string buttonName,
            out int group)
        {
            const string prefix = "BifrostStoragePriorityGroup";
            const string suffix = "Button";
            if (buttonName.StartsWith(prefix, StringComparison.Ordinal) &&
                buttonName.EndsWith(suffix, StringComparison.Ordinal) &&
                int.TryParse(
                    buttonName.Substring(
                        prefix.Length,
                        buttonName.Length - prefix.Length - suffix.Length),
                    out group) &&
                group >= 1 && group <= PriorityGroupCount)
            {
                return true;
            }

            group = 0;
            return false;
        }

        private static bool TryGetStoragePriorityTagButton(
            string buttonName,
            out int index)
        {
            const string prefix = "BifrostStoragePriorityTag";
            const string suffix = "Button";
            if (buttonName.StartsWith(prefix, StringComparison.Ordinal) &&
                buttonName.EndsWith(suffix, StringComparison.Ordinal) &&
                int.TryParse(
                    buttonName.Substring(
                        prefix.Length,
                        buttonName.Length - prefix.Length - suffix.Length),
                    out int tag) &&
                tag >= 1 && tag <= PriorityGroupCount)
            {
                index = tag - 1;
                return true;
            }

            index = -1;
            return false;
        }

        private static ulong GetSteamId(Player player)
        {
            return player.channel.owner.playerID.steamID.m_SteamID;
        }

        private async UniTask EnsurePreferencesLoadedAsync()
        {
            await m_PreferencesSemaphore.WaitAsync();
            try
            {
                if (m_PreferencesLoaded)
                {
                    return;
                }

                if (await m_DataStore.ExistsAsync(PreferencesDataKey))
                {
                    m_PersistedPreferences =
                        await m_DataStore.LoadAsync<InventorySortPreferencesData>(PreferencesDataKey) ??
                        new InventorySortPreferencesData();
                }
                else
                {
                    m_PersistedPreferences = new InventorySortPreferencesData();
                    await m_DataStore.SaveAsync(PreferencesDataKey, m_PersistedPreferences);
                }

                m_PersistedPreferences.PriorityGroups ??=
                    new Dictionary<ulong, List<int>>();
                m_PersistedPreferences.StoragePriorityGroups ??=
                    new Dictionary<ulong, List<int>>();
                m_PersistedPreferences.UseSameSortForStorage ??=
                    new Dictionary<ulong, bool>();
                foreach (ulong steamId in m_PersistedPreferences.PriorityGroups.Keys.ToArray())
                {
                    m_PersistedPreferences.PriorityGroups[steamId] = NormalizePriorityGroups(
                        m_PersistedPreferences.PriorityGroups[steamId]);
                }
                foreach (ulong steamId in
                         m_PersistedPreferences.StoragePriorityGroups.Keys.ToArray())
                {
                    m_PersistedPreferences.StoragePriorityGroups[steamId] =
                        NormalizePriorityGroups(
                            m_PersistedPreferences.StoragePriorityGroups[steamId]);
                }

                m_PreferencesLoaded = true;
                foreach (var pair in m_Preferences)
                {
                    pair.Value.PriorityGroups = GetPersistedPriorityGroups(pair.Key);
                    pair.Value.StoragePriorityGroups =
                        GetPersistedStoragePriorityGroups(pair.Key);
                    pair.Value.UseSameSortForStorage =
                        GetPersistedUseSameSortForStorage(pair.Key);
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao carregar as prioridades persistentes do organizador de inventario.");
                m_PersistedPreferences = new InventorySortPreferencesData();
                m_PreferencesLoaded = true;
            }
            finally
            {
                m_PreferencesSemaphore.Release();
            }
        }

        private async UniTask<bool> SavePriorityGroupsAsync(
            ulong steamId,
            IReadOnlyCollection<int> groups,
            IReadOnlyCollection<int> storageGroups,
            bool useSameSortForStorage)
        {
            await m_PreferencesSemaphore.WaitAsync();
            List<int>? previous = null;
            List<int>? previousStorage = null;
            bool hadPrevious = false;
            bool hadPreviousStorage = false;
            bool hadPreviousUseSame = false;
            bool previousUseSame = true;
            try
            {
                List<int> normalized = NormalizePriorityGroups(groups);
                List<int> normalizedStorage = NormalizePriorityGroups(storageGroups);
                hadPrevious = m_PersistedPreferences.PriorityGroups.TryGetValue(
                    steamId,
                    out previous);
                hadPreviousStorage =
                    m_PersistedPreferences.StoragePriorityGroups.TryGetValue(
                        steamId,
                        out previousStorage);
                hadPreviousUseSame =
                    m_PersistedPreferences.UseSameSortForStorage.TryGetValue(
                        steamId,
                        out previousUseSame);
                m_PersistedPreferences.PriorityGroups[steamId] = normalized;
                m_PersistedPreferences.StoragePriorityGroups[steamId] = normalizedStorage;
                m_PersistedPreferences.UseSameSortForStorage[steamId] =
                    useSameSortForStorage;
                await m_DataStore.SaveAsync(PreferencesDataKey, m_PersistedPreferences);
                return true;
            }
            catch (Exception exception)
            {
                if (hadPrevious && previous != null)
                {
                    m_PersistedPreferences.PriorityGroups[steamId] = previous;
                }
                else
                {
                    m_PersistedPreferences.PriorityGroups.Remove(steamId);
                }

                if (hadPreviousStorage && previousStorage != null)
                {
                    m_PersistedPreferences.StoragePriorityGroups[steamId] = previousStorage;
                }
                else
                {
                    m_PersistedPreferences.StoragePriorityGroups.Remove(steamId);
                }

                if (hadPreviousUseSame)
                {
                    m_PersistedPreferences.UseSameSortForStorage[steamId] = previousUseSame;
                }
                else
                {
                    m_PersistedPreferences.UseSameSortForStorage.Remove(steamId);
                }

                m_Logger.LogError(
                    exception,
                    "Falha ao salvar prioridades do organizador para {SteamId}.",
                    steamId);
                return false;
            }
            finally
            {
                m_PreferencesSemaphore.Release();
            }
        }

        private List<int> GetPersistedPriorityGroups(ulong steamId)
        {
            return m_PreferencesLoaded &&
                   m_PersistedPreferences.PriorityGroups.TryGetValue(steamId, out List<int> saved)
                ? NormalizePriorityGroups(saved)
                : CreateDefaultPriorityGroups();
        }

        private List<int> GetPersistedStoragePriorityGroups(ulong steamId)
        {
            return m_PreferencesLoaded &&
                   m_PersistedPreferences.StoragePriorityGroups.TryGetValue(
                       steamId,
                       out List<int> saved)
                ? NormalizePriorityGroups(saved)
                : CreateDefaultPriorityGroups();
        }

        private bool GetPersistedUseSameSortForStorage(ulong steamId)
        {
            return !m_PreferencesLoaded ||
                   !m_PersistedPreferences.UseSameSortForStorage.TryGetValue(
                       steamId,
                       out bool useSame) ||
                   useSame;
        }

        private static List<int> NormalizePriorityGroups(IEnumerable<int>? groups)
        {
            var normalized = (groups ?? Enumerable.Empty<int>())
                .Where(group => group >= 1 && group <= PriorityGroupCount)
                .Distinct()
                .Take(PriorityGroupCount)
                .ToList();
            foreach (int group in CreateDefaultPriorityGroups())
            {
                if (!normalized.Contains(group))
                {
                    normalized.Add(group);
                }
            }

            return normalized;
        }

        private static List<int> CreateDefaultPriorityGroups()
        {
            return new List<int> { 1, 2, 3, 4, 5 };
        }

        private static Item? GetEquippedPersonalItem(Player player)
        {
            if (player.equipment.equippedPage < PlayerInventory.SLOTS ||
                player.equipment.equippedPage >= PlayerInventory.STORAGE)
            {
                return null;
            }

            byte index = player.inventory.getIndex(
                player.equipment.equippedPage,
                player.equipment.equipped_x,
                player.equipment.equipped_y);
            return index == byte.MaxValue
                ? null
                : player.inventory.getItem(player.equipment.equippedPage, index)?.item;
        }

        private static bool IsEquippedPlacement(Player player, OriginalPlacement item)
        {
            return player.equipment.equippedPage == item.Page &&
                   player.equipment.equipped_x == item.X &&
                   player.equipment.equipped_y == item.Y;
        }

        private static PackingOrigin GetNextOrigin(PackingOrigin origin)
        {
            switch (origin)
            {
                case PackingOrigin.TopLeft:
                    return PackingOrigin.TopRight;
                case PackingOrigin.TopRight:
                    return PackingOrigin.BottomRight;
                case PackingOrigin.BottomRight:
                    return PackingOrigin.BottomLeft;
                default:
                    return PackingOrigin.TopLeft;
            }
        }

        private void ClearUiForOnlinePlayers()
        {
            foreach (SteamPlayer client in Provider.clients)
            {
                if (client.player != null)
                {
                    ClearUi(client.player);
                }
            }
        }

        private void ClearUi(Player player)
        {
            EffectManager.askEffectClearByID(
                m_EffectId,
                player.channel.owner.transportConnection);
        }

        private void SortForPlayer(Player player)
        {
            try
            {
                var steamId = GetSteamId(player);
                if (IsSortOnCooldown(steamId))
                {
                    return;
                }

                if (player.life.isDead)
                {
                    Tell(player, "messages:inventory_sort:must_be_alive");
                    return;
                }

                m_LastClicks[steamId] = Time.realtimeSinceStartup;

                PlayerSortPreferences preferences = GetPreferences(player);
                var result = SortInventory(
                    player,
                    preferences,
                    true,
                    preferences.UseSameSortForStorage && player.inventory.isStoring);
                if (result.MovedItems == 0)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Falha ao ordenar o inventario de {PlayerId}.",
                    player.channel.owner.playerID.steamID.m_SteamID);
                Tell(player, "messages:inventory_sort:failed");
            }
        }

        private void SortStorageForPlayer(Player player)
        {
            try
            {
                ulong steamId = GetSteamId(player);
                PlayerSortPreferences preferences = GetPreferences(player);
                Items storage = player.inventory.items[PlayerInventory.STORAGE];
                if (preferences.UseSameSortForStorage ||
                    !player.inventory.isStoring ||
                    storage == null || storage.width == 0 || storage.height == 0 ||
                    player.life.isDead || IsSortOnCooldown(steamId))
                {
                    return;
                }

                m_LastClicks[steamId] = Time.realtimeSinceStartup;
                var storagePreferences = new PlayerSortPreferences(
                    false,
                    preferences.StorageOrigin,
                    preferences.StoragePriorityGroups,
                    preferences.StoragePriorityGroups,
                    false);
                SortInventory(player, storagePreferences, false, true);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao ordenar o storage de {PlayerId}.",
                    player.channel.owner.playerID.steamID.m_SteamID);
                Tell(player, "messages:inventory_sort:failed");
            }
        }

        private void TransferStorageForPlayer(Player player, bool intoStorage)
        {
            try
            {
                ulong steamId = GetSteamId(player);
                if (IsOnCooldown(m_LastTransferClicks, steamId) || player.life.isDead)
                {
                    return;
                }

                PlayerInventory inventory = player.inventory;
                Items storage = inventory.items[PlayerInventory.STORAGE];
                if (!inventory.isStoring || storage == null ||
                    storage.width == 0 || storage.height == 0)
                {
                    SyncUiState(player);
                    return;
                }

                m_LastTransferClicks[steamId] = Time.realtimeSinceStartup;
                PlayerSortPreferences preferences = GetPreferences(player);
                Stopwatch stopwatch = Stopwatch.StartNew();
                int movedItems = intoStorage
                    ? TransferAllIntoStorage(player, storage, preferences.Origin)
                    : TransferAllOutOfStorage(player, storage, preferences.Origin);
                stopwatch.Stop();

                if (stopwatch.ElapsedMilliseconds >= 50)
                {
                    m_Logger.LogWarning(
                        "{Transfer} moveu {ItemCount} itens para {PlayerId} em {ElapsedMilliseconds} ms.",
                        intoStorage ? "All In" : "All Out",
                        movedItems,
                        steamId,
                        stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    m_Logger.LogDebug(
                        "{Transfer} moveu {ItemCount} itens para {PlayerId} em {ElapsedMilliseconds} ms.",
                        intoStorage ? "All In" : "All Out",
                        movedItems,
                        steamId,
                        stopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao transferir todos os itens do storage de {PlayerId}.",
                    player.channel.owner.playerID.steamID.m_SteamID);
            }
        }

        private int TransferAllIntoStorage(
            Player player,
            Items storage,
            PackingOrigin origin)
        {
            PlayerInventory inventory = player.inventory;
            var existingStorageItems = storage.items
                .Select(jar => new OriginalPlacement(
                    jar.item,
                    storage.page,
                    jar.x,
                    jar.y,
                    jar.rot))
                .ToList();
            var candidates = new List<OriginalPlacement>();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                Items source = inventory.items[page];
                candidates.AddRange(source.items
                    .Select(jar => new OriginalPlacement(
                        jar.item,
                        source.page,
                        jar.x,
                        jar.y,
                        jar.rot))
                    .Where(item => !IsEquippedPlacement(player, item)));
            }

            if (candidates.Count == 0 ||
                !TryCreateAllInPlan(
                    storage,
                    existingStorageItems,
                    candidates,
                    origin,
                    out List<OriginalPlacement> accepted,
                    out List<ItemPlacement> storagePlacements))
            {
                return 0;
            }

            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                RemoveAcceptedItems(inventory.items[page], accepted);
            }

            for (int index = storage.getItemCount() - 1; index >= 0; index--)
            {
                storage.removeItem((byte)index);
            }

            foreach (ItemPlacement placement in storagePlacements)
            {
                storage.addItem(
                    placement.X,
                    placement.Y,
                    placement.Rotation,
                    placement.Item);
            }

            return accepted.Count;
        }

        private bool TryCreateAllInPlan(
            Items storage,
            IReadOnlyList<OriginalPlacement> existingItems,
            IReadOnlyList<OriginalPlacement> candidates,
            PackingOrigin origin,
            out List<OriginalPlacement> accepted,
            out List<ItemPlacement> placements)
        {
            accepted = new List<OriginalPlacement>();
            placements = new List<ItemPlacement>();
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateTransferCandidateOrders(candidates, TransferGeometryPermutationCount))
            {
                TryCreateFixedAllInPlan(
                    storage,
                    existingItems,
                    candidateOrder,
                    origin,
                    out List<OriginalPlacement> fixedAccepted,
                    out List<ItemPlacement> fixedPlacements);
                SelectBetterTransferPlan(
                    ref accepted,
                    ref placements,
                    fixedAccepted,
                    fixedPlacements);
                if (accepted.Count == candidates.Count)
                {
                    return true;
                }

                TryCreateRepackedAllInPlan(
                    storage,
                    existingItems,
                    candidateOrder,
                    origin,
                    out List<OriginalPlacement> repackedAccepted,
                    out List<ItemPlacement> repackedPlacements);
                SelectBetterTransferPlan(
                    ref accepted,
                    ref placements,
                    repackedAccepted,
                    repackedPlacements);
                if (accepted.Count == candidates.Count)
                {
                    return true;
                }
            }

            return accepted.Count > 0;
        }

        private void TryCreateRepackedAllInPlan(
            Items storage,
            IReadOnlyList<OriginalPlacement> existingItems,
            IReadOnlyList<OriginalPlacement> candidates,
            PackingOrigin origin,
            out List<OriginalPlacement> accepted,
            out List<ItemPlacement> placements)
        {
            accepted = new List<OriginalPlacement>();
            if (!TryPackForTransfer(
                    storage,
                    existingItems,
                    Array.Empty<OriginalPlacement>(),
                    origin,
                    out placements))
            {
                placements = new List<ItemPlacement>();
                return;
            }

            var remaining = candidates.ToList();
            bool madeProgress;
            do
            {
                madeProgress = false;
                for (int index = 0; index < remaining.Count;)
                {
                    OriginalPlacement candidate = remaining[index];
                    var attempt = new List<OriginalPlacement>(
                        existingItems.Count + accepted.Count + 1);
                    attempt.AddRange(existingItems);
                    attempt.AddRange(accepted);
                    attempt.Add(candidate);
                    if (TryPackForTransfer(
                            storage,
                            attempt,
                            Array.Empty<OriginalPlacement>(),
                            origin,
                            out List<ItemPlacement> attemptPlacements))
                    {
                        accepted.Add(candidate);
                        placements = attemptPlacements;
                        remaining.RemoveAt(index);
                        madeProgress = true;
                        continue;
                    }

                    index++;
                }
            }
            while (madeProgress && remaining.Count > 0);
        }

        private void TryCreateFixedAllInPlan(
            Items storage,
            IReadOnlyList<OriginalPlacement> existingItems,
            IReadOnlyList<OriginalPlacement> candidates,
            PackingOrigin origin,
            out List<OriginalPlacement> accepted,
            out List<ItemPlacement> placements)
        {
            var packed = new Items(storage.page);
            packed.resize(storage.width, storage.height);
            accepted = new List<OriginalPlacement>();
            placements = new List<ItemPlacement>(existingItems.Count + candidates.Count);

            foreach (OriginalPlacement existing in existingItems)
            {
                if (!TryAddExactPlacement(packed, existing, false, placements))
                {
                    accepted.Clear();
                    placements.Clear();
                    return;
                }
            }

            foreach (OriginalPlacement candidate in candidates)
            {
                if (!TryFindPlacement(
                        packed,
                        candidate,
                        origin,
                        out byte x,
                        out byte y,
                        out byte rotation))
                {
                    continue;
                }

                packed.addItem(x, y, rotation, candidate.Item);
                placements.Add(new ItemPlacement(
                    candidate.Item,
                    storage.page,
                    x,
                    y,
                    rotation));
                accepted.Add(candidate);
            }
        }

        private int TransferAllOutOfStorage(
            Player player,
            Items storage,
            PackingOrigin origin)
        {
            PlayerInventory inventory = player.inventory;
            List<Items> sources = GetPersonalInventoryPages(inventory, origin);
            var originals = sources
                .SelectMany(source => source.items.Select(jar =>
                    new OriginalPlacement(
                        jar.item,
                        source.page,
                        jar.x,
                        jar.y,
                        jar.rot)))
                .ToList();
            var candidates = storage.items
                .Select(jar => new OriginalPlacement(
                    jar.item,
                    storage.page,
                    jar.x,
                    jar.y,
                    jar.rot))
                .ToList();
            List<OriginalPlacement> pinnedItems = originals
                .Where(item => IsEquippedPlacement(player, item))
                .ToList();
            if (candidates.Count == 0 ||
                !TryCreateAllOutPlan(
                    sources,
                    originals,
                    pinnedItems,
                    candidates,
                    origin,
                    out List<OriginalPlacement> accepted,
                    out List<ItemPlacement> personalPlacements))
            {
                return 0;
            }

            foreach (Items source in sources)
            {
                for (int index = source.getItemCount() - 1; index >= 0; index--)
                {
                    Item item = source.getItem((byte)index).item;
                    if (!pinnedItems.Any(pinned => ReferenceEquals(pinned.Item, item)))
                    {
                        source.removeItem((byte)index);
                    }
                }
            }

            RemoveAcceptedItems(storage, accepted);
            foreach (ItemPlacement placement in personalPlacements.Where(item => !item.IsPinned))
            {
                inventory.items[placement.Page].addItem(
                    placement.X,
                    placement.Y,
                    placement.Rotation,
                    placement.Item);
            }

            return accepted.Count;
        }

        private bool TryCreateAllOutPlan(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> originals,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            IReadOnlyList<OriginalPlacement> candidates,
            PackingOrigin origin,
            out List<OriginalPlacement> accepted,
            out List<ItemPlacement> placements)
        {
            List<OriginalPlacement> movableItems = originals
                .Where(item => !pinnedItems.Any(pinned =>
                    ReferenceEquals(pinned.Item, item.Item)))
                .ToList();

            accepted = new List<OriginalPlacement>();
            placements = new List<ItemPlacement>();
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateTransferCandidateOrders(candidates, TransferGeometryPermutationCount))
            {
                TryCreateFixedAllOutPlan(
                    sources,
                    originals,
                    pinnedItems,
                    candidateOrder,
                    origin,
                    out List<OriginalPlacement> fixedAccepted,
                    out List<ItemPlacement> fixedPlacements);
                SelectBetterTransferPlan(
                    ref accepted,
                    ref placements,
                    fixedAccepted,
                    fixedPlacements);
                if (accepted.Count == candidates.Count)
                {
                    return true;
                }

                TryCreateRepackedAllOutPlan(
                    sources,
                    pinnedItems,
                    movableItems,
                    candidateOrder,
                    origin,
                    out List<OriginalPlacement> repackedAccepted,
                    out List<ItemPlacement> repackedPlacements);
                SelectBetterTransferPlan(
                    ref accepted,
                    ref placements,
                    repackedAccepted,
                    repackedPlacements);
                if (accepted.Count == candidates.Count)
                {
                    return true;
                }
            }

            return accepted.Count > 0;
        }

        private void TryCreateRepackedAllOutPlan(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            IReadOnlyList<OriginalPlacement> movableItems,
            IReadOnlyList<OriginalPlacement> candidates,
            PackingOrigin origin,
            out List<OriginalPlacement> accepted,
            out List<ItemPlacement> placements)
        {
            accepted = new List<OriginalPlacement>();
            if (!TryPackAcrossPagesForTransfer(
                    sources,
                    movableItems,
                    pinnedItems,
                    origin,
                    out placements))
            {
                placements = new List<ItemPlacement>();
                return;
            }

            var remaining = candidates.ToList();
            bool madeProgress;
            do
            {
                madeProgress = false;
                for (int index = 0; index < remaining.Count;)
                {
                    OriginalPlacement candidate = remaining[index];
                    var attempt = new List<OriginalPlacement>(
                        movableItems.Count + accepted.Count + 1);
                    attempt.AddRange(movableItems);
                    attempt.AddRange(accepted);
                    attempt.Add(candidate);
                    if (TryPackAcrossPagesForTransfer(
                            sources,
                            attempt,
                            pinnedItems,
                            origin,
                            out List<ItemPlacement> attemptPlacements))
                    {
                        accepted.Add(candidate);
                        placements = attemptPlacements;
                        remaining.RemoveAt(index);
                        madeProgress = true;
                        continue;
                    }

                    index++;
                }
            }
            while (madeProgress && remaining.Count > 0);
        }

        private void TryCreateFixedAllOutPlan(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> originals,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            IReadOnlyList<OriginalPlacement> candidates,
            PackingOrigin origin,
            out List<OriginalPlacement> accepted,
            out List<ItemPlacement> placements)
        {
            List<Items> packedPages = sources.Select(source =>
            {
                var packed = new Items(source.page);
                packed.resize(source.width, source.height);
                return packed;
            }).ToList();
            accepted = new List<OriginalPlacement>();
            placements = new List<ItemPlacement>(originals.Count + candidates.Count);

            foreach (OriginalPlacement original in originals)
            {
                Items? packed = packedPages.FirstOrDefault(page => page.page == original.Page);
                if (packed == null ||
                    !TryAddExactPlacement(
                        packed,
                        original,
                        pinnedItems.Any(pinned => ReferenceEquals(pinned.Item, original.Item)),
                        placements))
                {
                    accepted.Clear();
                    placements.Clear();
                    return;
                }
            }

            foreach (OriginalPlacement candidate in candidates)
            {
                foreach (Items packed in packedPages)
                {
                    if (!TryFindPlacement(
                            packed,
                            candidate,
                            origin,
                            out byte x,
                            out byte y,
                            out byte rotation))
                    {
                        continue;
                    }

                    packed.addItem(x, y, rotation, candidate.Item);
                    placements.Add(new ItemPlacement(
                        candidate.Item,
                        packed.page,
                        x,
                        y,
                        rotation));
                    accepted.Add(candidate);
                    break;
                }
            }
        }

        private static List<Items> GetPersonalInventoryPages(
            PlayerInventory inventory,
            PackingOrigin origin)
        {
            var sources = new List<Items>();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                Items source = inventory.items[page];
                if (source != null && source.width > 0 && source.height > 0)
                {
                    sources.Add(source);
                }
            }

            if (origin == PackingOrigin.BottomLeft || origin == PackingOrigin.BottomRight)
            {
                sources.Reverse();
            }

            return sources;
        }

        private static void RemoveAcceptedItems(
            Items source,
            IReadOnlyCollection<OriginalPlacement> accepted)
        {
            for (int index = source.getItemCount() - 1; index >= 0; index--)
            {
                Item item = source.getItem((byte)index).item;
                if (accepted.Any(candidate => ReferenceEquals(candidate.Item, item)))
                {
                    source.removeItem((byte)index);
                }
            }
        }

        private static int CompareAcceptedPlans(
            IReadOnlyCollection<OriginalPlacement> left,
            IReadOnlyCollection<OriginalPlacement> right)
        {
            int leftArea = left.Sum(item => item.Area);
            int rightArea = right.Sum(item => item.Area);
            int areaComparison = leftArea.CompareTo(rightArea);
            return areaComparison != 0
                ? areaComparison
                : left.Count.CompareTo(right.Count);
        }

        private static void SelectBetterTransferPlan(
            ref List<OriginalPlacement> bestAccepted,
            ref List<ItemPlacement> bestPlacements,
            List<OriginalPlacement> candidateAccepted,
            List<ItemPlacement> candidatePlacements)
        {
            if (CompareAcceptedPlans(candidateAccepted, bestAccepted) > 0)
            {
                bestAccepted = candidateAccepted;
                bestPlacements = candidatePlacements;
            }
        }

        private static IEnumerable<List<OriginalPlacement>> CreateTransferCandidateOrders(
            IEnumerable<OriginalPlacement> candidates,
            int geometryPermutationCount = CompactGeometryPermutationCount)
        {
            List<OriginalPlacement> items = candidates.ToList();

            // Maior area primeiro tende a ocupar mais celulas rapidamente.
            yield return items
                .OrderByDescending(item => item.Area)
                .ThenByDescending(item => item.LongestSide)
                .ThenBy(item => item.Item.id)
                .ToList();

            // Menores primeiro explora buracos que o plano acima pode bloquear.
            yield return items
                .OrderBy(item => item.Area)
                .ThenBy(item => item.LongestSide)
                .ThenBy(item => item.Item.id)
                .ToList();

            // Formatos compridos primeiro reduzem corredores inutilizaveis.
            yield return items
                .OrderByDescending(item => item.LongestSide)
                .ThenByDescending(item => item.Area)
                .ThenBy(item => item.Item.id)
                .ToList();

            // Agrupamento por categoria pode produzir uma geometria diferente.
            yield return items
                .OrderBy(item => item.Type)
                .ThenByDescending(item => item.Area)
                .ThenBy(item => item.Item.id)
                .ToList();

            // A ordem inversa cobre casos em que itens equivalentes disputam
            // o mesmo canto e somente um deles libera a combinacao seguinte.
            yield return items
                .OrderByDescending(item => item.Area)
                .ThenByDescending(item => item.LongestSide)
                .ThenBy(item => item.Item.id)
                .Reverse()
                .ToList();

            // Multiplos desempates deterministas exploram geometrias que as
            // ordenacoes acima nao alcancam, sem tornar a transferencia aleatoria.
            for (int seed = 0; seed < geometryPermutationCount; seed++)
            {
                int currentSeed = seed;
                yield return items
                    .OrderBy(item => GetTransferPermutationKey(item, currentSeed))
                    .ToList();
            }
        }

        private static int GetTransferPermutationKey(OriginalPlacement item, int seed)
        {
            unchecked
            {
                uint value = item.Item.id;
                value = value * 16777619u ^ item.Page;
                value = value * 16777619u ^ item.X;
                value = value * 16777619u ^ item.Y;
                value = value * 16777619u ^ item.Rotation;
                value ^= (uint)(seed + 1) * 0x9E3779B9u;
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                return (int)value;
            }
        }

        private bool IsSortOnCooldown(ulong steamId)
        {
            return IsOnCooldown(m_LastClicks, steamId);
        }

        private bool IsOnCooldown(Dictionary<ulong, float> clicks, ulong steamId)
        {
            if (!clicks.TryGetValue(steamId, out float lastClick))
            {
                return false;
            }

            if (Time.realtimeSinceStartup - lastClick < m_CooldownSeconds)
            {
                return true;
            }

            clicks.Remove(steamId);
            return false;
        }

        private SortResult SortInventory(
            Player player,
            PlayerSortPreferences preferences,
            bool sortPersonalInventory,
            bool includeStorage)
        {
            var inventory = player.inventory;
            var plans = new List<PagePlan>();

            if (sortPersonalInventory && preferences.CombinePages)
            {
                plans = CreateCombinedPlans(player, inventory, preferences);
            }
            else if (sortPersonalInventory && m_PackingOrder == PackingOrder.Type)
            {
                plans = CreateExcludedTypePlans(inventory, preferences);
            }
            else if (sortPersonalInventory)
            {
                // Primary e Secondary sao slots equipaveis. STORAGE e AREA pertencem
                // a interfaces externas. Somente Hands, Backpack, Vest, Shirt e Pants
                // fazem parte do inventario pessoal organizavel.
                for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
                {
                    var plan = CreatePagePlan(player, inventory.items[page], preferences);
                    if (plan != null)
                    {
                        plans.Add(plan);
                    }
                }
            }

            // Quando um storage esta aberto, o Sort tambem compacta essa pagina
            // visualmente, mas nunca mistura seu conteudo com as paginas pessoais.
            if (includeStorage && inventory.isStoring)
            {
                PagePlan? storagePlan = CreatePagePlan(
                    player,
                    inventory.items[PlayerInventory.STORAGE],
                    preferences);
                if (storagePlan != null)
                {
                    plans.Add(storagePlan);
                }
            }

            if (plans.Count == 0)
            {
                return new SortResult(0, 0);
            }

            Item? equippedItem = GetEquippedPersonalItem(player);

            PagePlan? equippedPlan = null;
            ItemPlacement? equippedPlacement = null;
            if (equippedItem != null)
            {
                foreach (var plan in plans)
                {
                    var placement = plan.Placements.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.Item, equippedItem));
                    if (placement != null)
                    {
                        if (placement.IsPinned)
                        {
                            break;
                        }

                        equippedPlan = plan;
                        equippedPlacement = placement;
                        player.equipment.dequip();
                        break;
                    }
                }
            }

            var movedItems = 0;
            foreach (var plan in plans)
            {
                var pageItems = inventory.items[plan.Page];
                for (var index = pageItems.getItemCount() - 1; index >= 0; index--)
                {
                    Item item = pageItems.getItem((byte)index).item;
                    bool isPinned = plan.Placements.Any(placement =>
                        placement.IsPinned && ReferenceEquals(placement.Item, item));
                    if (isPinned)
                    {
                        continue;
                    }

                    pageItems.removeItem((byte)index);
                }

                movedItems += plan.MovedItems;
            }

            // No modo combinado, esvazia primeiro todos os itens moveis para
            // evitar que um item transferido seja removido novamente quando a
            // pagina de destino for processada.
            foreach (var plan in plans)
            {
                var pageItems = inventory.items[plan.Page];
                foreach (var placement in plan.Placements.Where(placement => !placement.IsPinned))
                {
                    pageItems.addItem(placement.X, placement.Y, placement.Rotation, placement.Item);
                }
            }

            if (equippedPlan != null && equippedPlacement != null)
            {
                player.equipment.ServerEquip(
                    equippedPlan.Page,
                    equippedPlacement.X,
                    equippedPlacement.Y);
            }

            return new SortResult(movedItems, plans.Count);
        }

        private List<PagePlan> CreateExcludedTypePlans(
            PlayerInventory inventory,
            PlayerSortPreferences preferences)
        {
            var sources = new List<Items>();
            var itemsByPage = new List<List<OriginalPlacement>>();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                Items source = inventory.items[page];
                if (source == null || source.width == 0 || source.height == 0)
                {
                    continue;
                }

                sources.Add(source);
                itemsByPage.Add(source.items
                    .Select(jar => new OriginalPlacement(
                        jar.item,
                        source.page,
                        jar.x,
                        jar.y,
                        jar.rot))
                    .ToList());
            }

            if (preferences.Origin == PackingOrigin.BottomLeft ||
                preferences.Origin == PackingOrigin.BottomRight)
            {
                sources.Reverse();
                itemsByPage.Reverse();
            }

            List<OriginalPlacement> allItems = itemsByPage
                .SelectMany(items => items)
                .ToList();
            if (allItems.Count == 0)
            {
                return new List<PagePlan>();
            }

            // Primeiro tenta a distribuicao ideal: as primeiras N prioridades
            // vao para a pagina que ja possuia N itens. Assim Exclude participa
            // da ordem global sem transformar Hands em um inventario combinado.
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateSortCandidateOrders(
                         allItems,
                         PackingOrder.Type,
                         preferences.PriorityGroups))
            {
                var idealBatches = new List<List<OriginalPlacement>>();
                int offset = 0;
                foreach (List<OriginalPlacement> originalPageItems in itemsByPage)
                {
                    int itemCount = originalPageItems.Count;
                    idealBatches.Add(candidateOrder
                        .Skip(offset)
                        .Take(itemCount)
                        .ToList());
                    offset += itemCount;
                }

                if (TryCreateExcludedPlans(
                        sources,
                        idealBatches,
                        preferences,
                        out List<PagePlan> idealPlans))
                {
                    return idealPlans;
                }
            }

            // Quando formatos diferentes impedem a divisao ideal, faz trocas
            // pareadas que sempre melhoram a prioridade global e so confirma uma
            // troca se ambas as paginas continuarem comportando a mesma contagem.
            List<List<OriginalPlacement>> swappedBatches = itemsByPage
                .Select(items => items.ToList())
                .ToList();
            ImproveExcludedPriorityBySwapping(sources, swappedBatches, preferences);
            return TryCreateExcludedPlans(
                sources,
                swappedBatches,
                preferences,
                out List<PagePlan> swappedPlans)
                ? swappedPlans
                : new List<PagePlan>();
        }

        private void ImproveExcludedPriorityBySwapping(
            IReadOnlyList<Items> sources,
            IList<List<OriginalPlacement>> batches,
            PlayerSortPreferences preferences)
        {
            bool improved;
            do
            {
                improved = false;
                for (int earlierPage = 0;
                     earlierPage < batches.Count - 1 && !improved;
                     earlierPage++)
                {
                    for (int laterPage = earlierPage + 1;
                         laterPage < batches.Count && !improved;
                         laterPage++)
                    {
                        List<OriginalPlacement> earlierCandidates = batches[earlierPage]
                            .OrderByDescending(item => GetTypeGroupRank(
                                item.Type,
                                preferences.PriorityGroups))
                            .ThenBy(item => item.Area)
                            .ToList();
                        List<OriginalPlacement> laterCandidates = batches[laterPage]
                            .OrderBy(item => GetTypeGroupRank(
                                item.Type,
                                preferences.PriorityGroups))
                            .ThenByDescending(item => item.Area)
                            .ToList();

                        foreach (OriginalPlacement earlierItem in earlierCandidates)
                        {
                            int earlierRank = GetTypeGroupRank(
                                earlierItem.Type,
                                preferences.PriorityGroups);
                            foreach (OriginalPlacement laterItem in laterCandidates)
                            {
                                int laterRank = GetTypeGroupRank(
                                    laterItem.Type,
                                    preferences.PriorityGroups);
                                if (laterRank >= earlierRank)
                                {
                                    break;
                                }

                                var proposedEarlier = batches[earlierPage]
                                    .Where(item => !ReferenceEquals(item, earlierItem))
                                    .Append(laterItem)
                                    .ToList();
                                var proposedLater = batches[laterPage]
                                    .Where(item => !ReferenceEquals(item, laterItem))
                                    .Append(earlierItem)
                                    .ToList();
                                if (!TryPackAssignedPage(
                                        sources[earlierPage],
                                        proposedEarlier,
                                        preferences,
                                        out _) ||
                                    !TryPackAssignedPage(
                                        sources[laterPage],
                                        proposedLater,
                                        preferences,
                                        out _))
                                {
                                    continue;
                                }

                                batches[earlierPage] = proposedEarlier;
                                batches[laterPage] = proposedLater;
                                improved = true;
                                break;
                            }

                            if (improved)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            while (improved);
        }

        private bool TryCreateExcludedPlans(
            IReadOnlyList<Items> sources,
            IReadOnlyList<List<OriginalPlacement>> batches,
            PlayerSortPreferences preferences,
            out List<PagePlan> plans)
        {
            plans = new List<PagePlan>();
            for (int index = 0; index < sources.Count; index++)
            {
                Items source = sources[index];
                if (!TryPackAssignedPage(
                        source,
                        batches[index],
                        preferences,
                        out List<ItemPlacement> placements))
                {
                    plans = new List<PagePlan>();
                    return false;
                }

                int movedItems = placements.Count(placement =>
                {
                    OriginalPlacement original = batches
                        .SelectMany(items => items)
                        .First(item => ReferenceEquals(item.Item, placement.Item));
                    return original.Page != placement.Page ||
                           original.X != placement.X ||
                           original.Y != placement.Y ||
                           original.Rotation != placement.Rotation;
                });
                if (movedItems > 0)
                {
                    plans.Add(new PagePlan(source.page, placements, movedItems));
                }
            }

            return true;
        }

        private bool TryPackAssignedPage(
            Items source,
            IReadOnlyCollection<OriginalPlacement> assignedItems,
            PlayerSortPreferences preferences,
            out List<ItemPlacement> placements)
        {
            var noPinnedItems = new List<OriginalPlacement>();
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateSortCandidateOrders(
                         assignedItems,
                         PackingOrder.Type,
                         preferences.PriorityGroups))
            {
                if (TryPackByPriorityBoundaries(
                        source,
                        candidateOrder,
                        noPinnedItems,
                        preferences.Origin,
                        preferences.PriorityGroups,
                        out placements))
                {
                    return true;
                }
            }

            foreach (List<OriginalPlacement> candidateOrder in
                     CreateSortCandidateOrders(
                         assignedItems,
                         PackingOrder.Type,
                         preferences.PriorityGroups))
            {
                if (TryPack(
                        source,
                        candidateOrder,
                        noPinnedItems,
                        preferences.Origin,
                        out placements))
                {
                    return true;
                }
            }

            // A pagina ja continha a mesma quantidade de itens antes do sort,
            // portanto um layout seguro sempre deve ser procurado mesmo quando
            // a separacao geometrica perfeita entre os grupos nao cabe. Essas
            // ordens sao somente o ultimo recurso: a pontuacao por prioridade
            // continua sendo determinada pela ordem das tentativas acima.
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateTransferCandidateOrders(assignedItems.ToList()))
            {
                if (TryPack(
                        source,
                        candidateOrder,
                        noPinnedItems,
                        preferences.Origin,
                        out placements))
                {
                    return true;
                }
            }

            placements = new List<ItemPlacement>();
            return false;
        }

        private List<PagePlan> CreateCombinedPlans(
            Player player,
            PlayerInventory inventory,
            PlayerSortPreferences preferences)
        {
            PackingOrigin origin = preferences.Origin;
            var sources = new List<Items>();
            var originals = new List<OriginalPlacement>();
            for (byte page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
            {
                Items source = inventory.items[page];
                if (source == null || source.width == 0 || source.height == 0)
                {
                    continue;
                }

                sources.Add(source);
                originals.AddRange(source.items.Select(jar =>
                    new OriginalPlacement(jar.item, source.page, jar.x, jar.y, jar.rot)));
            }

            if (originals.Count == 0)
            {
                return new List<PagePlan>();
            }

            // A direcao vertical tambem define qual armazenamento recebe itens
            // primeiro no modo combine. Para baixo, comeca em Pants e sobe ate
            // Hands; para cima, comeca em Hands e desce ate Pants.
            if (origin == PackingOrigin.BottomLeft || origin == PackingOrigin.BottomRight)
            {
                sources.Reverse();
            }

            // O item equipado tambem participa da ordenacao. SortInventory o
            // desequipa somente durante a troca e o equipa novamente no destino.
            var pinnedItems = new List<OriginalPlacement>();
            List<OriginalPlacement> movableItems = originals;

            List<ItemPlacement>? placements = null;
            CombinedLayoutScore? bestScore = null;

            void ConsiderCandidate(List<ItemPlacement> candidatePlacements)
            {
                CombinedLayoutScore candidateScore = GetCombinedLayoutScore(
                    sources,
                    originals,
                    candidatePlacements,
                    origin,
                    preferences.PriorityGroups);
                if (bestScore == null || candidateScore.CompareTo(bestScore.Value) < 0)
                {
                    placements = candidatePlacements;
                    bestScore = candidateScore;
                }
            }

            // Combine prioriza compactacao real. Todas as estrategias validas sao
            // avaliadas antes da escolha: vence a que deixa o menor intervalo vazio
            // na direcao selecionada; prioridade de grupo desempata depois.
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateSortCandidateOrders(
                         movableItems,
                         m_PackingOrder,
                         preferences.PriorityGroups))
            {
                bool packed = m_PackingOrder == PackingOrder.Type
                    ? TryPackAcrossPagesByPriorityBoundaries(
                        sources,
                        candidateOrder,
                        pinnedItems,
                        origin,
                        preferences.PriorityGroups,
                        out List<ItemPlacement> candidatePlacements)
                    : TryPackAcrossPages(
                        sources,
                        candidateOrder,
                        pinnedItems,
                        origin,
                        out candidatePlacements);
                if (packed)
                {
                    ConsiderCandidate(candidatePlacements);
                }
            }

            if (m_PackingOrder == PackingOrder.Type)
            {
                // O limite por pagina ainda participa como candidato, mas nao pode
                // vencer um layout que combine melhor os itens.
                foreach (List<OriginalPlacement> candidateOrder in
                         CreateSortCandidateOrders(
                             movableItems,
                             m_PackingOrder,
                             preferences.PriorityGroups))
                {
                    if (!TryPackAcrossPagesByPriorityPageFloor(
                            sources,
                            candidateOrder,
                            pinnedItems,
                            origin,
                            preferences.PriorityGroups,
                            out List<ItemPlacement> candidatePlacements))
                    {
                        continue;
                    }

                    ConsiderCandidate(candidatePlacements);
                }
            }

            if (m_PackingOrder == PackingOrder.Type)
            {
                // Esta e a estrategia normal do Combine: todo item tenta primeiro
                // a pagina mais proxima da origem, inclusive para preencher buracos
                // deixados por itens maiores de grupos anteriores.
                foreach (List<OriginalPlacement> candidateOrder in
                         CreateSortCandidateOrders(
                             movableItems,
                             m_PackingOrder,
                             preferences.PriorityGroups))
                {
                    if (!TryPackAcrossPages(
                            sources,
                            candidateOrder,
                            pinnedItems,
                            origin,
                            out List<ItemPlacement> candidatePlacements))
                    {
                        continue;
                    }

                    ConsiderCandidate(candidatePlacements);
                }
            }

            // Ordens puramente geometricas podem quebrar a prioridade, mas sao
            // obrigatorias no Combine quando produzem uma ocupacao melhor.
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateTransferCandidateOrders(movableItems))
            {
                if (!TryPackAcrossPages(
                        sources,
                        candidateOrder,
                        pinnedItems,
                        origin,
                        out List<ItemPlacement> candidatePlacements))
                {
                    continue;
                }

                ConsiderCandidate(candidatePlacements);
            }

            if (placements == null)
            {
                return new List<PagePlan>();
            }

            int movedItems = placements.Count(placement =>
            {
                OriginalPlacement original = originals.First(item =>
                    ReferenceEquals(item.Item, placement.Item));
                return original.Page != placement.Page || original.X != placement.X ||
                       original.Y != placement.Y || original.Rotation != placement.Rotation;
            });
            if (movedItems == 0)
            {
                return new List<PagePlan>();
            }

            return sources
                .Select(source => new PagePlan(
                    source.page,
                    placements.Where(placement => placement.Page == source.page).ToList(),
                    placements.Count(placement =>
                    {
                        if (placement.Page != source.page)
                        {
                            return false;
                        }

                        OriginalPlacement original = originals.First(item =>
                            ReferenceEquals(item.Item, placement.Item));
                        return original.Page != placement.Page || original.X != placement.X ||
                               original.Y != placement.Y || original.Rotation != placement.Rotation;
                    })))
                .ToList();
        }

        private static CombinedLayoutScore GetCombinedLayoutScore(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> originals,
            IReadOnlyList<ItemPlacement> placements,
            PackingOrigin origin,
            IReadOnlyList<int> priorityGroups)
        {
            var pageOffsets = new Dictionary<byte, long>();
            long currentOffset = 0;
            foreach (Items source in sources)
            {
                pageOffsets[source.page] = currentOffset;
                currentOffset += source.width * source.height;
            }

            bool fromBottom = origin == PackingOrigin.BottomLeft ||
                              origin == PackingOrigin.BottomRight;
            bool fromRight = origin == PackingOrigin.TopRight ||
                             origin == PackingOrigin.BottomRight;
            long farthestOccupiedCell = -1;
            long compactnessCost = 0;
            long priorityCost = 0;

            foreach (ItemPlacement placement in placements)
            {
                Items? source = sources.FirstOrDefault(page => page.page == placement.Page);
                OriginalPlacement? original = originals.FirstOrDefault(item =>
                    ReferenceEquals(item.Item, placement.Item));
                if (source == null || original == null ||
                    !pageOffsets.TryGetValue(placement.Page, out long pageOffset))
                {
                    continue;
                }

                int itemWidth = placement.Rotation % 2 == 0
                    ? original.SizeX
                    : original.SizeY;
                int itemHeight = placement.Rotation % 2 == 0
                    ? original.SizeY
                    : original.SizeX;
                int groupRank = GetTypeGroupRank(original.Type, priorityGroups);
                int priorityWeight = Math.Max(1, PriorityGroupCount - groupRank);

                for (int itemY = placement.Y;
                     itemY < placement.Y + itemHeight;
                     itemY++)
                {
                    int scanY = fromBottom ? source.height - 1 - itemY : itemY;
                    for (int itemX = placement.X;
                         itemX < placement.X + itemWidth;
                         itemX++)
                    {
                        int scanX = fromRight ? source.width - 1 - itemX : itemX;
                        long cellIndex = pageOffset + (scanY * source.width) + scanX;
                        farthestOccupiedCell = Math.Max(farthestOccupiedCell, cellIndex);
                        compactnessCost += cellIndex;
                        priorityCost += cellIndex * priorityWeight;
                    }
                }
            }

            return new CombinedLayoutScore(
                farthestOccupiedCell,
                compactnessCost,
                priorityCost);
        }

        private PagePlan? CreatePagePlan(
            Player player,
            Items source,
            PlayerSortPreferences preferences)
        {
            PackingOrigin origin = preferences.Origin;
            if (source == null || source.getItemCount() == 0 || source.width == 0 || source.height == 0)
            {
                return null;
            }

            var originals = source.items
                .Select(jar => new OriginalPlacement(jar.item, source.page, jar.x, jar.y, jar.rot))
                .ToList();

            // Mantem a mesma regra do modo combinado: prioridade inclui o item
            // equipado e ele e restaurado na nova coordenada ao fim da operacao.
            var pinnedItems = new List<OriginalPlacement>();
            List<OriginalPlacement> movableItems = originals;
            List<ItemPlacement>? placements = null;
            foreach (List<OriginalPlacement> candidateOrder in
                     CreateSortCandidateOrders(
                         movableItems,
                         m_PackingOrder,
                         preferences.PriorityGroups))
            {
                bool packed = m_PackingOrder == PackingOrder.Type
                    ? TryPackByPriorityBoundaries(
                        source,
                        candidateOrder,
                        pinnedItems,
                        origin,
                        preferences.PriorityGroups,
                        out List<ItemPlacement> candidatePlacements)
                    : TryPack(
                        source,
                        candidateOrder,
                        pinnedItems,
                        origin,
                        out candidatePlacements);
                if (packed)
                {
                    placements = candidatePlacements;
                    break;
                }
            }

            if (placements == null && m_PackingOrder == PackingOrder.Type)
            {
                foreach (List<OriginalPlacement> candidateOrder in
                         CreateSortCandidateOrders(
                             movableItems,
                             m_PackingOrder,
                             preferences.PriorityGroups))
                {
                    if (!TryPack(
                            source,
                            candidateOrder,
                            pinnedItems,
                            origin,
                            out List<ItemPlacement> candidatePlacements))
                    {
                        continue;
                    }

                    placements = candidatePlacements;
                    break;
                }
            }

            if (placements == null && m_PackingOrder == PackingOrder.Type)
            {
                foreach (List<OriginalPlacement> candidateOrder in
                         CreateTransferCandidateOrders(movableItems))
                {
                    if (!TryPack(
                            source,
                            candidateOrder,
                            pinnedItems,
                            origin,
                            out List<ItemPlacement> candidatePlacements))
                    {
                        continue;
                    }

                    placements = candidatePlacements;
                    break;
                }
            }

            if (placements == null)
            {
                // Nenhum item e removido quando nao ha um layout seguro.
                return null;
            }

            var movedItems = placements.Count(placement =>
            {
                var original = originals.First(item => ReferenceEquals(item.Item, placement.Item));
                return original.X != placement.X || original.Y != placement.Y ||
                       original.Rotation != placement.Rotation;
            });

            return movedItems == 0 ? null : new PagePlan(source.page, placements, movedItems);
        }

        private List<OriginalPlacement> OrderItems(
            IEnumerable<OriginalPlacement> items,
            PackingOrder order,
            IReadOnlyList<int>? priorityGroups = null)
        {
            switch (order)
            {
                case PackingOrder.Type:
                    return items
                        .OrderBy(item => GetTypeGroupRank(item.Type, priorityGroups))
                        .ThenByDescending(item => item.Area)
                        .ThenByDescending(item => item.LongestSide)
                        .ThenBy(item => item.Type)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Item.id)
                        .ToList();

                case PackingOrder.Name:
                    return items
                        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(item => item.Area)
                        .ThenBy(item => item.Item.id)
                        .ToList();

                case PackingOrder.Original:
                    return items
                        .OrderBy(item => item.Y)
                        .ThenBy(item => item.X)
                        .ToList();

                default:
                    // Itens maiores primeiro reduzem buracos e mantem o conjunto
                    // o mais proximo possivel da origem configurada.
                    return items
                        .OrderByDescending(item => item.Area)
                        .ThenByDescending(item => item.LongestSide)
                        .ThenBy(item => item.Type)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Item.id)
                        .ToList();
            }
        }

        private IEnumerable<List<OriginalPlacement>> CreateSortCandidateOrders(
            IReadOnlyCollection<OriginalPlacement> items,
            PackingOrder order,
            IReadOnlyList<int> priorityGroups)
        {
            yield return OrderItems(items, order, priorityGroups);

            if (order == PackingOrder.Type)
            {
                // Mantem os grupos como chave principal enquanto explora formas
                // diferentes de encaixar os retangulos dentro de cada grupo.
                yield return items
                    .OrderBy(item => GetTypeGroupRank(item.Type, priorityGroups))
                    .ThenByDescending(item => item.LongestSide)
                    .ThenByDescending(item => item.Area)
                    .ThenBy(item => item.Type)
                    .ThenBy(item => item.Item.id)
                    .ToList();
                yield return items
                    .OrderBy(item => GetTypeGroupRank(item.Type, priorityGroups))
                    .ThenBy(item => item.Area)
                    .ThenBy(item => item.LongestSide)
                    .ThenBy(item => item.Type)
                    .ThenBy(item => item.Item.id)
                    .ToList();
                yield return items
                    .OrderBy(item => GetTypeGroupRank(item.Type, priorityGroups))
                    .ThenBy(item => item.Y)
                    .ThenBy(item => item.X)
                    .ThenBy(item => item.Item.id)
                    .ToList();

                // Formatos iguais podem disputar o unico encaixe que libera o
                // restante da pagina. Variar deterministicamente apenas dentro
                // de cada grupo preserva a prioridade solicitada e cobre esses
                // casos sem comportamento aleatorio entre cliques.
                for (int seed = 0; seed < PriorityGeometryPermutationCount; seed++)
                {
                    int currentSeed = seed;
                    yield return items
                        .OrderBy(item => GetTypeGroupRank(item.Type, priorityGroups))
                        .ThenBy(item => GetTransferPermutationKey(item, currentSeed))
                        .ToList();
                }

                // Em type a prioridade e a direcao sao obrigatorias. Se nenhum
                // candidato respeitando ambas couber, o inventario fica intacto.
                yield break;
            }

            // Nos demais modos, a compactacao global continua sendo a ultima
            // alternativa segura quando a ordem solicitada nao cabe.
            yield return OrderItems(items, PackingOrder.Compact);
        }

        private static int GetTypeGroupRank(
            int nativeType,
            IReadOnlyList<int>? priorityGroups)
        {
            int group = GetTypeGroup(nativeType);
            if (priorityGroups == null)
            {
                return group;
            }

            for (int index = 0; index < priorityGroups.Count; index++)
            {
                if (priorityGroups[index] == group)
                {
                    return index;
                }
            }

            return PriorityGroupCount + nativeType;
        }

        private static int GetTypeGroup(int nativeType)
        {
            switch ((EItemType)nativeType)
            {
                case EItemType.GUN:
                case EItemType.MELEE:
                case EItemType.THROWABLE:
                case EItemType.DETONATOR:
                case EItemType.CHARGE:
                    return 1;

                case EItemType.SIGHT:
                case EItemType.TACTICAL:
                case EItemType.GRIP:
                case EItemType.BARREL:
                case EItemType.MAGAZINE:
                case EItemType.OPTIC:
                    return 2;

                case EItemType.FOOD:
                case EItemType.WATER:
                case EItemType.MEDICAL:
                case EItemType.SUPPLY:
                case EItemType.REFILL:
                case EItemType.FISHER:
                case EItemType.CLOUD:
                case EItemType.MAP:
                case EItemType.FILTER:
                case EItemType.COMPASS:
                    return 3;

                case EItemType.BARRICADE:
                case EItemType.STORAGE:
                case EItemType.BEACON:
                case EItemType.FARM:
                case EItemType.TRAP:
                case EItemType.STRUCTURE:
                case EItemType.GROWER:
                case EItemType.TANK:
                case EItemType.GENERATOR:
                case EItemType.LIBRARY:
                case EItemType.SENTRY:
                case EItemType.OIL_PUMP:
                    return 4;

                default:
                    return 5;
            }
        }

        private bool TryPack(
            Items source,
            IReadOnlyList<OriginalPlacement> orderedItems,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            out List<ItemPlacement> placements)
        {
            var packed = new Items(source.page);
            packed.resize(source.width, source.height);
            placements = new List<ItemPlacement>(orderedItems.Count + pinnedItems.Count);

            foreach (OriginalPlacement pinned in pinnedItems)
            {
                if (!TryPinOriginalPlacement(packed, pinned, placements))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            foreach (var original in orderedItems)
            {
                if (!TryFindPlacement(packed, original, origin, out var x, out var y, out var rotation))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }

                packed.addItem(x, y, rotation, original.Item);
                placements.Add(new ItemPlacement(original.Item, source.page, x, y, rotation));
            }

            return true;
        }

        private bool TryPackByPriorityBoundaries(
            Items source,
            IReadOnlyList<OriginalPlacement> orderedItems,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            IReadOnlyList<int> priorityGroups,
            out List<ItemPlacement> placements)
        {
            var packed = new Items(source.page);
            packed.resize(source.width, source.height);
            placements = new List<ItemPlacement>(orderedItems.Count + pinnedItems.Count);

            foreach (OriginalPlacement pinned in pinnedItems)
            {
                if (!TryPinOriginalPlacement(packed, pinned, placements))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            int currentGroupRank = -1;
            int minimumScanIndex = 0;
            int currentGroupLastScanIndex = 0;
            foreach (OriginalPlacement original in orderedItems)
            {
                int groupRank = GetTypeGroupRank(original.Type, priorityGroups);
                if (currentGroupRank >= 0 && groupRank != currentGroupRank)
                {
                    minimumScanIndex = currentGroupLastScanIndex + 1;
                    currentGroupLastScanIndex = minimumScanIndex;
                }

                currentGroupRank = groupRank;
                if (!TryFindPlacement(
                        packed,
                        original,
                        origin,
                        minimumScanIndex,
                        out byte x,
                        out byte y,
                        out byte rotation,
                        out int scanIndex))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }

                currentGroupLastScanIndex = Math.Max(
                    currentGroupLastScanIndex,
                    scanIndex);
                packed.addItem(x, y, rotation, original.Item);
                placements.Add(new ItemPlacement(
                    original.Item,
                    source.page,
                    x,
                    y,
                    rotation));
            }

            return true;
        }

        private bool TryPackForTransfer(
            Items source,
            IReadOnlyList<OriginalPlacement> items,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            out List<ItemPlacement> placements)
        {
            foreach (List<OriginalPlacement> order in CreateTransferCandidateOrders(
                         items,
                         TransferGeometryPermutationCount))
            {
                if (TryPack(source, order, pinnedItems, origin, out placements))
                {
                    return true;
                }
            }

            placements = new List<ItemPlacement>();
            return false;
        }

        private bool TryPackAcrossPages(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> orderedItems,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            out List<ItemPlacement> placements)
        {
            var packedPages = sources.Select(source =>
            {
                var packed = new Items(source.page);
                packed.resize(source.width, source.height);
                return packed;
            }).ToList();
            placements = new List<ItemPlacement>(orderedItems.Count + pinnedItems.Count);

            foreach (OriginalPlacement pinned in pinnedItems)
            {
                Items? packed = packedPages.FirstOrDefault(page => page.page == pinned.Page);
                if (packed == null || !TryPinOriginalPlacement(packed, pinned, placements))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            foreach (OriginalPlacement original in orderedItems)
            {
                bool placed = false;
                foreach (Items packed in packedPages)
                {
                    if (!TryFindPlacement(
                            packed,
                            original,
                            origin,
                            out byte x,
                            out byte y,
                            out byte rotation))
                    {
                        continue;
                    }

                    packed.addItem(x, y, rotation, original.Item);
                    placements.Add(new ItemPlacement(original.Item, packed.page, x, y, rotation));
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            return true;
        }

        private bool TryPackAcrossPagesByPriorityBoundaries(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> orderedItems,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            IReadOnlyList<int> priorityGroups,
            out List<ItemPlacement> placements)
        {
            var packedPages = sources.Select(source =>
            {
                var packed = new Items(source.page);
                packed.resize(source.width, source.height);
                return packed;
            }).ToList();
            placements = new List<ItemPlacement>(orderedItems.Count + pinnedItems.Count);

            foreach (OriginalPlacement pinned in pinnedItems)
            {
                Items? packed = packedPages.FirstOrDefault(page => page.page == pinned.Page);
                if (packed == null || !TryPinOriginalPlacement(packed, pinned, placements))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            int currentGroupRank = -1;
            int minimumPageIndex = 0;
            int minimumScanIndex = 0;
            int currentGroupLastPageIndex = 0;
            int currentGroupLastScanIndex = 0;
            foreach (OriginalPlacement original in orderedItems)
            {
                int groupRank = GetTypeGroupRank(original.Type, priorityGroups);
                if (currentGroupRank >= 0 && groupRank != currentGroupRank)
                {
                    minimumPageIndex = currentGroupLastPageIndex;
                    minimumScanIndex = currentGroupLastScanIndex + 1;
                    currentGroupLastPageIndex = minimumPageIndex;
                    currentGroupLastScanIndex = minimumScanIndex;
                }

                currentGroupRank = groupRank;
                bool placed = false;
                for (int pageIndex = minimumPageIndex;
                     pageIndex < packedPages.Count;
                     pageIndex++)
                {
                    Items packed = packedPages[pageIndex];
                    int pageMinimumScanIndex = pageIndex == minimumPageIndex
                        ? minimumScanIndex
                        : 0;
                    if (!TryFindPlacement(
                            packed,
                            original,
                            origin,
                            pageMinimumScanIndex,
                            out byte x,
                            out byte y,
                            out byte rotation,
                            out int scanIndex))
                    {
                        continue;
                    }

                    packed.addItem(x, y, rotation, original.Item);
                    placements.Add(new ItemPlacement(
                        original.Item,
                        packed.page,
                        x,
                        y,
                        rotation));
                    if (pageIndex > currentGroupLastPageIndex)
                    {
                        currentGroupLastPageIndex = pageIndex;
                        currentGroupLastScanIndex = scanIndex;
                    }
                    else if (pageIndex == currentGroupLastPageIndex)
                    {
                        currentGroupLastScanIndex = Math.Max(
                            currentGroupLastScanIndex,
                            scanIndex);
                    }

                    placed = true;
                    break;
                }

                if (!placed)
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            return true;
        }

        private bool TryPackAcrossPagesByPriorityPageFloor(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> orderedItems,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            IReadOnlyList<int> priorityGroups,
            out List<ItemPlacement> placements)
        {
            var packedPages = sources.Select(source =>
            {
                var packed = new Items(source.page);
                packed.resize(source.width, source.height);
                return packed;
            }).ToList();
            placements = new List<ItemPlacement>(orderedItems.Count + pinnedItems.Count);

            foreach (OriginalPlacement pinned in pinnedItems)
            {
                Items? packed = packedPages.FirstOrDefault(page => page.page == pinned.Page);
                if (packed == null || !TryPinOriginalPlacement(packed, pinned, placements))
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            int currentGroupRank = -1;
            int minimumPageIndex = 0;
            int currentGroupLastPageIndex = 0;
            foreach (OriginalPlacement original in orderedItems)
            {
                int groupRank = GetTypeGroupRank(original.Type, priorityGroups);
                if (currentGroupRank >= 0 && groupRank != currentGroupRank)
                {
                    minimumPageIndex = currentGroupLastPageIndex;
                    currentGroupLastPageIndex = minimumPageIndex;
                }

                currentGroupRank = groupRank;
                bool placed = false;
                for (int pageIndex = minimumPageIndex;
                     pageIndex < packedPages.Count;
                     pageIndex++)
                {
                    Items packed = packedPages[pageIndex];
                    if (!TryFindPlacement(
                            packed,
                            original,
                            origin,
                            out byte x,
                            out byte y,
                            out byte rotation))
                    {
                        continue;
                    }

                    packed.addItem(x, y, rotation, original.Item);
                    placements.Add(new ItemPlacement(
                        original.Item,
                        packed.page,
                        x,
                        y,
                        rotation));
                    currentGroupLastPageIndex = Math.Max(
                        currentGroupLastPageIndex,
                        pageIndex);
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    placements = new List<ItemPlacement>();
                    return false;
                }
            }

            return true;
        }

        private bool TryPackAcrossPagesForTransfer(
            IReadOnlyList<Items> sources,
            IReadOnlyList<OriginalPlacement> items,
            IReadOnlyList<OriginalPlacement> pinnedItems,
            PackingOrigin origin,
            out List<ItemPlacement> placements)
        {
            foreach (List<OriginalPlacement> order in CreateTransferCandidateOrders(
                         items,
                         TransferGeometryPermutationCount))
            {
                if (TryPackAcrossPages(sources, order, pinnedItems, origin, out placements))
                {
                    return true;
                }
            }

            placements = new List<ItemPlacement>();
            return false;
        }

        private static bool TryPinOriginalPlacement(
            Items packed,
            OriginalPlacement original,
            ICollection<ItemPlacement> placements)
        {
            return TryAddExactPlacement(packed, original, true, placements);
        }

        private static bool TryAddExactPlacement(
            Items packed,
            OriginalPlacement original,
            bool isPinned,
            ICollection<ItemPlacement> placements)
        {
            if (packed.page != original.Page ||
                !packed.checkSpaceEmpty(
                    original.X,
                    original.Y,
                    original.SizeX,
                    original.SizeY,
                    original.Rotation))
            {
                return false;
            }

            packed.addItem(
                original.X,
                original.Y,
                original.Rotation,
                original.Item);
            placements.Add(new ItemPlacement(
                original.Item,
                original.Page,
                original.X,
                original.Y,
                original.Rotation,
                isPinned));
            return true;
        }

        private bool TryFindPlacement(
            Items packed,
            OriginalPlacement item,
            PackingOrigin origin,
            out byte x,
            out byte y,
            out byte rotation)
        {
            return TryFindPlacement(
                packed,
                item,
                origin,
                0,
                out x,
                out y,
                out rotation,
                out _);
        }

        private bool TryFindPlacement(
            Items packed,
            OriginalPlacement item,
            PackingOrigin origin,
            int minimumScanIndex,
            out byte x,
            out byte y,
            out byte rotation,
            out int scanIndex)
        {
            var fromBottom = origin == PackingOrigin.BottomLeft ||
                             origin == PackingOrigin.BottomRight;
            var fromRight = origin == PackingOrigin.TopRight ||
                            origin == PackingOrigin.BottomRight;

            for (var yIndex = 0; yIndex < packed.height; yIndex++)
            {
                var candidateY = fromBottom ? packed.height - 1 - yIndex : yIndex;
                for (var xIndex = 0; xIndex < packed.width; xIndex++)
                {
                    var candidateX = fromRight ? packed.width - 1 - xIndex : xIndex;
                    var rotationCount = m_RotateItems && item.SizeX != item.SizeY ? 2 : 1;
                    for (var rotationIndex = 0; rotationIndex < rotationCount; rotationIndex++)
                    {
                        var candidateRotation = m_RotateItems
                            ? (byte)rotationIndex
                            : (byte)(item.Rotation % 2);
                        GetPlacementScanRange(
                            packed,
                            item,
                            (byte)candidateX,
                            (byte)candidateY,
                            candidateRotation,
                            fromBottom,
                            fromRight,
                            out int firstCandidateScanIndex,
                            out int lastCandidateScanIndex);
                        if (firstCandidateScanIndex < minimumScanIndex)
                        {
                            continue;
                        }

                        if (packed.checkSpaceEmpty(
                                (byte)candidateX,
                                (byte)candidateY,
                                item.SizeX,
                                item.SizeY,
                                candidateRotation))
                        {
                            x = (byte)candidateX;
                            y = (byte)candidateY;
                            rotation = candidateRotation;
                            scanIndex = lastCandidateScanIndex;
                            return true;
                        }
                    }
                }
            }

            x = byte.MaxValue;
            y = byte.MaxValue;
            rotation = 0;
            scanIndex = int.MaxValue;
            return false;
        }

        private static void GetPlacementScanRange(
            Items packed,
            OriginalPlacement item,
            byte x,
            byte y,
            byte rotation,
            bool fromBottom,
            bool fromRight,
            out int firstScanIndex,
            out int lastScanIndex)
        {
            int itemWidth = rotation % 2 == 0 ? item.SizeX : item.SizeY;
            int itemHeight = rotation % 2 == 0 ? item.SizeY : item.SizeX;
            firstScanIndex = int.MaxValue;
            lastScanIndex = int.MinValue;
            for (int itemY = y; itemY < y + itemHeight; itemY++)
            {
                int scanY = fromBottom ? packed.height - 1 - itemY : itemY;
                for (int itemX = x; itemX < x + itemWidth; itemX++)
                {
                    int scanX = fromRight ? packed.width - 1 - itemX : itemX;
                    int cellScanIndex = (scanY * packed.width) + scanX;
                    firstScanIndex = Math.Min(firstScanIndex, cellScanIndex);
                    lastScanIndex = Math.Max(lastScanIndex, cellScanIndex);
                }
            }
        }

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
            where TEnum : struct
        {
            return Enum.TryParse(value, true, out TEnum parsed) ? parsed : fallback;
        }

        private void Tell(Player player, string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            ChatManager.serverSendMessage(
                $"{m_StringLocalizer["prefix"].Value} {message}",
                Color.white,
                toPlayer: player.channel.owner,
                iconURL: Provider.configData.Browser.Icon,
                useRichTextFormatting: true);
        }

        private sealed class OriginalPlacement
        {
            public OriginalPlacement(Item item, byte page, byte x, byte y, byte rotation)
            {
                Item = item;
                Page = page;
                X = x;
                Y = y;
                Rotation = rotation;
                var asset = item.GetAsset();
                Type = asset == null ? int.MaxValue : (int)asset.type;
                SizeX = asset?.size_x ?? 1;
                SizeY = asset?.size_y ?? 1;
                Area = asset == null ? 1 : asset.size_x * asset.size_y;
                LongestSide = asset == null ? 1 : Math.Max(asset.size_x, asset.size_y);
                Name = asset?.itemName ?? string.Empty;
            }

            public Item Item { get; }
            public byte Page { get; }
            public byte X { get; }
            public byte Y { get; }
            public byte Rotation { get; }
            public byte SizeX { get; }
            public byte SizeY { get; }
            public int Type { get; }
            public int Area { get; }
            public int LongestSide { get; }
            public string Name { get; }
        }

        private sealed class ItemPlacement
        {
            public ItemPlacement(
                Item item,
                byte page,
                byte x,
                byte y,
                byte rotation,
                bool isPinned = false)
            {
                Item = item;
                Page = page;
                X = x;
                Y = y;
                Rotation = rotation;
                IsPinned = isPinned;
            }

            public Item Item { get; }
            public byte Page { get; }
            public byte X { get; }
            public byte Y { get; }
            public byte Rotation { get; }
            public bool IsPinned { get; }
        }

        private sealed class PagePlan
        {
            public PagePlan(byte page, List<ItemPlacement> placements, int movedItems)
            {
                Page = page;
                Placements = placements;
                MovedItems = movedItems;
            }

            public byte Page { get; }
            public List<ItemPlacement> Placements { get; }
            public int MovedItems { get; }
        }

        private readonly struct SortResult
        {
            public SortResult(int movedItems, int sortedPages)
            {
                MovedItems = movedItems;
                SortedPages = sortedPages;
            }

            public int MovedItems { get; }
            public int SortedPages { get; }
        }

        private readonly struct CombinedLayoutScore : IComparable<CombinedLayoutScore>
        {
            public CombinedLayoutScore(
                long farthestOccupiedCell,
                long compactnessCost,
                long priorityCost)
            {
                FarthestOccupiedCell = farthestOccupiedCell;
                CompactnessCost = compactnessCost;
                PriorityCost = priorityCost;
            }

            private long FarthestOccupiedCell { get; }
            private long CompactnessCost { get; }
            private long PriorityCost { get; }

            public int CompareTo(CombinedLayoutScore other)
            {
                int comparison = FarthestOccupiedCell.CompareTo(other.FarthestOccupiedCell);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = CompactnessCost.CompareTo(other.CompactnessCost);
                return comparison != 0
                    ? comparison
                    : PriorityCost.CompareTo(other.PriorityCost);
            }
        }

        private sealed class PlayerSortPreferences
        {
            public PlayerSortPreferences(
                bool combinePages,
                PackingOrigin origin,
                List<int> priorityGroups,
                List<int> storagePriorityGroups,
                bool useSameSortForStorage)
            {
                CombinePages = combinePages;
                Origin = origin;
                PriorityGroups = priorityGroups;
                StorageOrigin = origin;
                StoragePriorityGroups = storagePriorityGroups;
                UseSameSortForStorage = useSameSortForStorage;
            }

            public bool CombinePages { get; set; }
            public PackingOrigin Origin { get; set; }
            public List<int> PriorityGroups { get; set; }
            public PackingOrigin StorageOrigin { get; set; }
            public List<int> StoragePriorityGroups { get; set; }
            public bool UseSameSortForStorage { get; set; }
        }

        private enum PackingOrigin
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private enum PackingOrder
        {
            Compact,
            Type,
            Name,
            Original
        }
    }
}
