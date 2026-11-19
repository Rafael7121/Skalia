using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
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
using OpenMod.Unturned.Players.Interacting.Events;
using SDG.Unturned;
using Steamworks;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class HomeService : IHomeService, IDisposable
    {
        private const string HomeSlotsDataKey = "home.slots";
        private readonly object m_Sync = new object();
        private readonly Dictionary<ulong, HomeTeleport> m_ActiveTeleports = new Dictionary<ulong, HomeTeleport>();
        private readonly CancellationTokenSource m_Shutdown = new CancellationTokenSource();
        private readonly SemaphoreSlim m_DataSemaphore = new SemaphoreSlim(1, 1);
        private readonly IDataStore m_DataStore;
        private readonly ITpaService m_TpaService;
        private readonly IRoleBenefitsService m_RoleBenefitsService;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<HomeService> m_Logger;
        private readonly int m_TeleportDelaySeconds;
        private HomeSlotsData m_SlotsData = new HomeSlotsData();
        private bool m_SlotsLoaded;
        private bool m_Disposed;

        public HomeService(
            ITpaService tpaService,
            IDataStore dataStore,
            IRoleBenefitsService roleBenefitsService,
            IConfiguration configuration,
            IStringLocalizer stringLocalizer,
            ILogger<HomeService> logger)
        {
            m_TpaService = tpaService;
            m_DataStore = dataStore;
            m_RoleBenefitsService = roleBenefitsService;
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
            m_TeleportDelaySeconds = Clamp(configuration.GetValue("home:teleportDelaySeconds", 5), 0, 60);
        }

        public async Task TeleportAsync(UnturnedPlayer player, string? selector)
        {
            await UniTask.SwitchToMainThread();
            if (!await TryEnsureSlotsLoadedAsync(player))
            {
                return;
            }

            if (!player.IsAlive)
            {
                await TellAsync(player, "messages:home:must_be_alive");
                return;
            }

            var combatSeconds = m_TpaService.GetCombatSecondsRemaining(player);
            if (combatSeconds > 0)
            {
                await TellAsync(player, "messages:home:in_combat", new { Seconds = combatSeconds });
                return;
            }

            var beds = GetOwnedStationaryBeds(player.SteamId);
            var playerId = player.SteamId.m_SteamID;
            var maxBeds = await m_RoleBenefitsService.GetHomeLimitAsync(player);
            bool slotsChanged;
            HomeSlotAssignment assignment;
            lock (m_Sync)
            {
                slotsChanged = ReconcileSlotsUnsafe(playerId, beds, maxBeds);
                assignment = m_SlotsData.Players[playerId].Clone();
            }

            if (slotsChanged)
            {
                await SaveSlotsAsync();
            }

            var availableHomes = GetAvailableHomes(beds, assignment);
            m_Logger.LogInformation(
                "Pedido de Home: jogador {PlayerId}, seletor {Selector}, camas {BedCount}, homes {Homes}.",
                playerId,
                selector ?? "auto",
                beds.Count,
                string.Join(",", assignment.GetHomes()));

            if (availableHomes.Count == 0)
            {
                await TellAsync(player, "messages:home:no_beds");
                return;
            }

            if (string.IsNullOrWhiteSpace(selector) && availableHomes.Count > 1)
            {
                await TellAsync(player, "messages:home:choose", new { Count = availableHomes.Count });
                return;
            }

            SlottedBed? selectedHome;
            if (string.IsNullOrWhiteSpace(selector))
            {
                selectedHome = availableHomes[0];
            }
            else
            {
                var matches = FindBedMatches(
                    availableHomes.Select(home =>
                        new NamedBed(home.Bed.InstanceId, home.Slot, home.Name)).ToList(),
                    selector);
                if (matches.Count > 1)
                {
                    await TellAsync(player, "messages:home:target_ambiguous", new
                    {
                        Home = selector,
                        Homes = string.Join(", ", matches.Select(home => home.Name))
                    });
                    return;
                }

                selectedHome = matches.Count == 1
                    ? availableHomes.First(home =>
                        home.Bed.InstanceId == matches[0].InstanceId)
                    : null;
            }

            if (selectedHome == null)
            {
                await TellAsync(player, "messages:home:target_not_found", new
                {
                    Home = selector ?? string.Empty,
                    Homes = string.Join(", ", availableHomes.Select(home => home.Name))
                });
                return;
            }

            HomeTeleport teleport;
            HomeTeleport? replacedTeleport = null;
            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                if (m_ActiveTeleports.TryGetValue(playerId, out replacedTeleport))
                {
                    m_ActiveTeleports.Remove(playerId);
                    CancelAndDispose(replacedTeleport.Cancellation);
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(m_Shutdown.Token);
                teleport = new HomeTeleport(
                    player,
                    selectedHome.Bed.InstanceId,
                    selectedHome.Name,
                    cancellation);
                m_ActiveTeleports.Add(playerId, teleport);
            }

            if (replacedTeleport != null)
            {
                await TellAsync(player, "messages:home:replaced");
            }

            await TellAsync(player, "messages:home:warmup", new
            {
                Name = selectedHome.Name,
                Seconds = m_TeleportDelaySeconds
            });

            await CompleteTeleportAsync(teleport);
        }

        public async Task RenameBedAsync(
            UnturnedPlayer player,
            string selector,
            string newName)
        {
            await UniTask.SwitchToMainThread();
            if (!await TryEnsureSlotsLoadedAsync(player))
            {
                return;
            }

            newName = newName.Trim();
            if (newName.Length < 2 || newName.Length > 40 ||
                newName.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                await TellAsync(player, "messages:rename_bed:invalid_name");
                return;
            }

            var playerId = player.SteamId.m_SteamID;
            var beds = GetOwnedStationaryBeds(player.SteamId);
            var maxBeds = await m_RoleBenefitsService.GetHomeLimitAsync(player);
            List<NamedBed> namedBeds;
            lock (m_Sync)
            {
                ReconcileSlotsUnsafe(playerId, beds, maxBeds);
                var assignment = m_SlotsData.Players[playerId];
                namedBeds = GetNamedBeds(beds, assignment);
            }

            if (namedBeds.Count == 0)
            {
                await TellAsync(player, "messages:home:no_beds");
                return;
            }

            var matches = FindBedMatches(namedBeds, selector);
            if (matches.Count == 0)
            {
                await TellAsync(player, "messages:rename_bed:not_found", new
                {
                    Bed = selector,
                    Beds = string.Join(", ", namedBeds.Select(bed => bed.Name))
                });
                return;
            }

            if (matches.Count > 1)
            {
                await TellAsync(player, "messages:rename_bed:ambiguous", new
                {
                    Bed = selector,
                    Beds = string.Join(", ", matches.Select(bed => bed.Name))
                });
                return;
            }

            var normalizedNewName = NormalizeBedName(newName);
            if (namedBeds.Any(bed =>
                    bed.InstanceId != matches[0].InstanceId &&
                    NormalizeBedName(bed.Name) == normalizedNewName))
            {
                await TellAsync(player, "messages:rename_bed:duplicate", new { Name = newName });
                return;
            }

            var oldName = matches[0].Name;
            lock (m_Sync)
            {
                m_SlotsData.Players[playerId].GetNames()[matches[0].InstanceId] = newName;
            }

            await SaveSlotsAsync();
            await TellAsync(player, "messages:rename_bed:success", new
            {
                OldName = oldName,
                NewName = newName
            });
        }

        public async Task ListBedsAsync(UnturnedPlayer player)
        {
            await UniTask.SwitchToMainThread();
            if (!await TryEnsureSlotsLoadedAsync(player))
            {
                return;
            }

            var playerId = player.SteamId.m_SteamID;
            var beds = GetOwnedStationaryBeds(player.SteamId);
            var maxBeds = await m_RoleBenefitsService.GetHomeLimitAsync(player);
            List<NamedBed> namedBeds;
            bool slotsChanged;
            lock (m_Sync)
            {
                slotsChanged = ReconcileSlotsUnsafe(playerId, beds, maxBeds);
                namedBeds = GetNamedBeds(beds, m_SlotsData.Players[playerId]);
            }

            if (slotsChanged)
            {
                await SaveSlotsAsync();
            }

            if (namedBeds.Count == 0)
            {
                await TellAsync(player, "messages:home:no_beds");
                return;
            }

            await TellAsync(
                player,
                namedBeds.Count >= maxBeds
                    ? "messages:homes:header_limit"
                    : "messages:homes:header",
                new
            {
                Count = namedBeds.Count,
                Limit = maxBeds
            });
            for (var index = 0; index < namedBeds.Count; index++)
            {
                await TellAsync(
                    player,
                    namedBeds.Count >= maxBeds
                        ? "messages:homes:item_limit"
                        : "messages:homes:item",
                    new
                {
                    Slot = index + 1,
                    Name = namedBeds[index].Name
                });
            }
        }

        public async Task<string> GetBedNameAsync(ulong ownerSteamId, uint bedInstanceId)
        {
            await EnsureSlotsLoadedAsync();
            lock (m_Sync)
            {
                if (!m_SlotsData.Players.TryGetValue(ownerSteamId, out var assignment))
                {
                    return "cama";
                }

                if (assignment.GetNames().TryGetValue(bedInstanceId, out var customName) &&
                    !string.IsNullOrWhiteSpace(customName))
                {
                    return customName;
                }

                var slot = assignment.GetHomes().IndexOf(bedInstanceId);
                return slot >= 0 ? $"home {slot + 1}" : "cama";
            }
        }

        public async Task HandleBedClaimingAsync(UnturnedPlayerBedClaimingEvent @event)
        {
            await UniTask.SwitchToMainThread();
            if (!await TryEnsureSlotsLoadedAsync(@event.Player))
            {
                @event.IsCancelled = true;
                return;
            }

            if (@event.Bed.owner == @event.Player.SteamId)
            {
                return;
            }

            if (IsVehicleBed(@event.Bed))
            {
                @event.IsCancelled = true;
                await TellAsync(@event.Player, "messages:home:vehicle_bed");
                return;
            }

            var ownedBeds = GetOwnedStationaryBeds(@event.Player.SteamId);
            var maxBeds = await m_RoleBenefitsService.GetHomeLimitAsync(@event.Player);
            if (ownedBeds.Count >= maxBeds)
            {
                @event.IsCancelled = true;
                await TellAsync(@event.Player, "messages:home:bed_limit", new { Count = maxBeds });
                return;
            }

            var claimedBed = FindStationaryBed(@event.Bed);
            if (claimedBed == null)
            {
                @event.IsCancelled = true;
                m_Logger.LogWarning("Nao foi possivel localizar a instancia da cama reivindicada por {PlayerId}.",
                    @event.Player.SteamId.m_SteamID);
                await TellAsync(@event.Player, "messages:home:claim_failed");
                return;
            }

            var playerId = @event.Player.SteamId.m_SteamID;
            int assignedSlot;
            lock (m_Sync)
            {
                ReconcileSlotsUnsafe(playerId, ownedBeds, maxBeds);
                var assignment = m_SlotsData.Players[playerId];
                var homes = assignment.GetHomes();
                homes.Add(claimedBed.InstanceId);
                assignment.SyncLegacySlots();
                assignedSlot = homes.Count;
            }

            await SaveSlotsAsync();
            m_Logger.LogInformation("Cama {BedInstanceId} registrada como home {Slot} para {PlayerId}.",
                claimedBed.InstanceId, assignedSlot, playerId);

            await TellAsync(@event.Player, "messages:home:bed_claimed", new
            {
                Slot = assignedSlot,
                Name = $"home {assignedSlot}",
                Count = maxBeds
            });
        }

        public async Task CancelForCombatAsync(ulong firstPlayerId, ulong secondPlayerId)
        {
            var cancelled = new List<HomeTeleport>();
            lock (m_Sync)
            {
                foreach (var playerId in new[] { firstPlayerId, secondPlayerId }.Distinct())
                {
                    if (!m_ActiveTeleports.TryGetValue(playerId, out var teleport))
                    {
                        continue;
                    }

                    m_ActiveTeleports.Remove(playerId);
                    CancelAndDispose(teleport.Cancellation);
                    cancelled.Add(teleport);
                }
            }

            foreach (var teleport in cancelled)
            {
                await TellAsync(teleport.Player, "messages:home:combat_cancelled");
            }
        }

        public async Task HandleDeathAsync(UnturnedPlayer player)
        {
            HomeTeleport? teleport;
            lock (m_Sync)
            {
                if (!m_ActiveTeleports.TryGetValue(player.SteamId.m_SteamID, out teleport))
                {
                    teleport = null;
                }
                else
                {
                    m_ActiveTeleports.Remove(player.SteamId.m_SteamID);
                    CancelAndDispose(teleport.Cancellation);
                }
            }

            if (teleport != null)
            {
                await TellAsync(player, "messages:home:cancelled_dead");
            }
        }

        public void HandleDisconnect(UnturnedPlayer player)
        {
            lock (m_Sync)
            {
                if (!m_ActiveTeleports.TryGetValue(player.SteamId.m_SteamID, out var teleport))
                {
                    return;
                }

                m_ActiveTeleports.Remove(player.SteamId.m_SteamID);
                CancelAndDispose(teleport.Cancellation);
            }
        }

        public void Dispose()
        {
            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Disposed = true;
                m_Shutdown.Cancel();
                foreach (var teleport in m_ActiveTeleports.Values)
                {
                    CancelAndDispose(teleport.Cancellation);
                }

                m_ActiveTeleports.Clear();
            }

            m_Shutdown.Dispose();
        }

        private async Task CompleteTeleportAsync(HomeTeleport teleport)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(m_TeleportDelaySeconds), teleport.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await UniTask.SwitchToMainThread();

            lock (m_Sync)
            {
                if (!m_ActiveTeleports.TryGetValue(teleport.PlayerId, out var active) ||
                    !ReferenceEquals(active, teleport))
                {
                    return;
                }

                m_ActiveTeleports.Remove(teleport.PlayerId);
            }

            teleport.Cancellation.Dispose();

            if (!teleport.Player.IsAlive)
            {
                await TellAsync(teleport.Player, "messages:home:cancelled_dead");
                return;
            }

            if (m_TpaService.GetCombatSecondsRemaining(teleport.Player) > 0)
            {
                await TellAsync(teleport.Player, "messages:home:combat_cancelled");
                return;
            }

            var bed = GetOwnedStationaryBeds(teleport.Player.SteamId)
                .FirstOrDefault(candidate => candidate.InstanceId == teleport.BedInstanceId);
            if (bed == null)
            {
                await TellAsync(teleport.Player, "messages:home:bed_missing");
                return;
            }

            var position = bed.Bed.transform.position;
            position.y += 0.5f;
            var yaw = bed.Bed.transform.eulerAngles.y + 90f;
            var teleported = false;
            try
            {
                teleported = teleport.Player.Player.teleportToLocation(position, yaw);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Falha ao teleportar {PlayerId} para a cama {BedInstanceId}.",
                    teleport.PlayerId, teleport.BedInstanceId);
            }

            m_Logger.LogInformation(
                "Resultado do Home: jogador {PlayerId}, cama {BedInstanceId}, sucesso {Success}, posicao {Position}.",
                teleport.PlayerId, teleport.BedInstanceId, teleported, position);

            await TellAsync(teleport.Player,
                teleported ? "messages:home:success" : "messages:home:failed",
                new { Name = teleport.BedName });
        }

        private async Task EnsureSlotsLoadedAsync()
        {
            lock (m_Sync)
            {
                if (m_SlotsLoaded)
                {
                    return;
                }
            }

            await m_DataSemaphore.WaitAsync();
            try
            {
                lock (m_Sync)
                {
                    if (m_SlotsLoaded)
                    {
                        return;
                    }
                }

                HomeSlotsData loaded;
                if (await m_DataStore.ExistsAsync(HomeSlotsDataKey))
                {
                    loaded = await m_DataStore.LoadAsync<HomeSlotsData>(HomeSlotsDataKey) ??
                             new HomeSlotsData();
                }
                else
                {
                    loaded = new HomeSlotsData();
                    await m_DataStore.SaveAsync(HomeSlotsDataKey, loaded);
                    m_Logger.LogInformation("Arquivo de slots de Home inicializado pela primeira vez.");
                }

                lock (m_Sync)
                {
                    m_SlotsData = loaded;
                    if (m_SlotsData.Players == null)
                    {
                        m_SlotsData.Players = new Dictionary<ulong, HomeSlotAssignment>();
                    }

                    m_SlotsLoaded = true;
                }
            }
            finally
            {
                m_DataSemaphore.Release();
            }
        }

        private async Task<bool> TryEnsureSlotsLoadedAsync(UnturnedPlayer player)
        {
            try
            {
                await EnsureSlotsLoadedAsync();
                return true;
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Falha ao carregar os slots de Home; os dados nao serao sobrescritos.");
                await TellAsync(player, "messages:home:data_failed");
                return false;
            }
        }

        private async Task SaveSlotsAsync()
        {
            HomeSlotsData snapshot;
            lock (m_Sync)
            {
                snapshot = new HomeSlotsData
                {
                    Players = m_SlotsData.Players.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Clone())
                };
            }

            await m_DataSemaphore.WaitAsync();
            try
            {
                await m_DataStore.SaveAsync(HomeSlotsDataKey, snapshot);
            }
            finally
            {
                m_DataSemaphore.Release();
            }
        }

        private bool ReconcileSlotsUnsafe(
            ulong playerId,
            IReadOnlyCollection<OwnedBed> beds,
            int maxBeds)
        {
            if (!m_SlotsData.Players.TryGetValue(playerId, out var assignment))
            {
                assignment = new HomeSlotAssignment();
                m_SlotsData.Players[playerId] = assignment;
            }

            var homes = assignment.GetHomes();
            var originalHomes = homes.ToArray();
            var validIds = new HashSet<uint>(beds.Select(bed => bed.InstanceId));
            var seen = new HashSet<uint>();
            homes.RemoveAll(home => !validIds.Contains(home) || !seen.Add(home));

            foreach (var bed in beds.OrderBy(candidate => candidate.InstanceId))
            {
                if (homes.Count >= maxBeds)
                {
                    break;
                }

                if (!seen.Contains(bed.InstanceId))
                {
                    homes.Add(bed.InstanceId);
                    seen.Add(bed.InstanceId);
                }
            }

            if (homes.Count > maxBeds)
            {
                homes.RemoveRange(maxBeds, homes.Count - maxBeds);
            }

            assignment.SyncLegacySlots();
            return !originalHomes.SequenceEqual(homes);
        }

        private static List<SlottedBed> GetAvailableHomes(
            IReadOnlyCollection<OwnedBed> beds,
            HomeSlotAssignment assignment)
        {
            var byInstanceId = beds.ToDictionary(bed => bed.InstanceId);
            var result = new List<SlottedBed>();
            var homes = assignment.GetHomes();
            var names = assignment.GetNames();
            for (var index = 0; index < homes.Count; index++)
            {
                if (byInstanceId.TryGetValue(homes[index], out var home))
                {
                    var name = names.TryGetValue(homes[index], out var customName) &&
                               !string.IsNullOrWhiteSpace(customName)
                        ? customName
                        : $"home {index + 1}";
                    result.Add(new SlottedBed(index + 1, name, home));
                }
            }

            return result;
        }

        private static List<NamedBed> GetNamedBeds(
            IReadOnlyCollection<OwnedBed> beds,
            HomeSlotAssignment assignment)
        {
            var byInstanceId = beds.ToDictionary(bed => bed.InstanceId);
            var homes = assignment.GetHomes();
            var names = assignment.GetNames();
            var result = new List<NamedBed>();
            for (var index = 0; index < homes.Count; index++)
            {
                if (!byInstanceId.ContainsKey(homes[index]))
                {
                    continue;
                }

                var isCustom = names.TryGetValue(homes[index], out var customName) &&
                               !string.IsNullOrWhiteSpace(customName);
                var name = isCustom ? customName! : $"home {index + 1}";
                result.Add(new NamedBed(homes[index], index + 1, name));
            }

            return result;
        }

        private static List<NamedBed> FindBedMatches(
            IReadOnlyCollection<NamedBed> beds,
            string selector)
        {
            var query = NormalizeBedName(selector);
            if (query.Length == 0)
            {
                return new List<NamedBed>();
            }

            var scored = beds
                .Select(bed => new
                {
                    Bed = bed,
                    Score = new[]
                    {
                        NormalizeBedName(bed.Name),
                        $"home{bed.Slot}",
                        $"cama{bed.Slot}",
                        bed.Slot.ToString(CultureInfo.InvariantCulture)
                    }.Min(candidate => GetBedMatchScore(candidate, query))
                })
                .Where(match => match.Score < int.MaxValue)
                .ToList();
            if (scored.Count == 0)
            {
                return new List<NamedBed>();
            }

            var bestScore = scored.Min(match => match.Score);
            return scored.Where(match => match.Score == bestScore)
                .Select(match => match.Bed)
                .ToList();
        }

        private static int GetBedMatchScore(string candidate, string query)
        {
            if (candidate == query) return 0;
            if (candidate.StartsWith(query, StringComparison.Ordinal)) return 1;
            if (candidate.Contains(query)) return 2;
            var distance = GetEditDistance(candidate, query);
            return distance <= Math.Max(1, query.Length / 3) ? 3 + distance : int.MaxValue;
        }

        private static string NormalizeBedName(string value)
        {
            var normalized = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) !=
                    UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }

        private static int GetEditDistance(string left, string right)
        {
            var previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
            {
                var current = new int[right.Length + 1];
                current[0] = leftIndex;
                for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                {
                    var substitution = previous[rightIndex - 1] +
                        (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                    current[rightIndex] = Math.Min(
                        Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                        substitution);
                }

                previous = current;
            }

            return previous[right.Length];
        }

        private static OwnedBed? FindStationaryBed(InteractableBed target)
        {
            var regions = BarricadeManager.regions;
            for (var x = 0; x < regions.GetLength(0); x++)
            {
                for (var y = 0; y < regions.GetLength(1); y++)
                {
                    foreach (var drop in regions[x, y].drops)
                    {
                        if (drop.interactable is InteractableBed bed && ReferenceEquals(bed, target))
                        {
                            return new OwnedBed(drop.instanceID, bed);
                        }
                    }
                }
            }

            return null;
        }

        private static List<OwnedBed> GetOwnedStationaryBeds(CSteamID owner)
        {
            var result = new List<OwnedBed>();
            var regions = BarricadeManager.regions;
            for (var x = 0; x < regions.GetLength(0); x++)
            {
                for (var y = 0; y < regions.GetLength(1); y++)
                {
                    AddOwnedBeds(regions[x, y], owner, result);
                }
            }

            return result.OrderBy(bed => bed.InstanceId).ToList();
        }

        private static void AddOwnedBeds(BarricadeRegion region, CSteamID owner, ICollection<OwnedBed> result)
        {
            foreach (var drop in region.drops)
            {
                if (drop.interactable is InteractableBed bed && bed.owner == owner)
                {
                    result.Add(new OwnedBed(drop.instanceID, bed));
                }
            }
        }

        private static bool IsVehicleBed(InteractableBed bed)
        {
            foreach (var region in BarricadeManager.vehicleRegions)
            {
                if (region.drops.Any(drop => ReferenceEquals(drop.interactable, bed)))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task TellAsync(UnturnedPlayer player, string key, object? arguments = null)
        {
            try
            {
                var message = arguments == null
                    ? m_StringLocalizer[key].Value
                    : m_StringLocalizer[key, arguments].Value;
                await player.PrintMessageAsync(
                    $"{m_StringLocalizer["prefix"].Value} {message}");
            }
            catch (Exception exception)
            {
                m_Logger.LogDebug(exception, "Nao foi possivel enviar uma mensagem Home ao jogador {PlayerId}.",
                    player.SteamId.m_SteamID);
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static void CancelAndDispose(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            cancellation.Dispose();
        }

        private sealed class OwnedBed
        {
            public OwnedBed(uint instanceId, InteractableBed bed)
            {
                InstanceId = instanceId;
                Bed = bed;
            }

            public uint InstanceId { get; }

            public InteractableBed Bed { get; }
        }

        private sealed class SlottedBed
        {
            public SlottedBed(int slot, string name, OwnedBed bed)
            {
                Slot = slot;
                Name = name;
                Bed = bed;
            }

            public int Slot { get; }

            public string Name { get; }

            public OwnedBed Bed { get; }
        }

        private sealed class NamedBed
        {
            public NamedBed(uint instanceId, int slot, string name)
            {
                InstanceId = instanceId;
                Slot = slot;
                Name = name;
            }

            public uint InstanceId { get; }

            public int Slot { get; }

            public string Name { get; }
        }

        private sealed class HomeTeleport
        {
            public HomeTeleport(
                UnturnedPlayer player,
                uint bedInstanceId,
                string bedName,
                CancellationTokenSource cancellation)
            {
                Player = player;
                BedInstanceId = bedInstanceId;
                BedName = bedName;
                Cancellation = cancellation;
            }

            public UnturnedPlayer Player { get; }

            public ulong PlayerId => Player.SteamId.m_SteamID;

            public uint BedInstanceId { get; }

            public string BedName { get; }

            public CancellationTokenSource Cancellation { get; }
        }
    }
}
