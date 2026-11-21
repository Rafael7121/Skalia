using System;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API;
using OpenMod.API.Plugins;
using OpenMod.Core.Permissions;
using OpenMod.Unturned.Plugins;
using SDG.Unturned;

[assembly: PluginMetadata("Skalia.Deps", DisplayName = "SKALIA DEPS")]
[assembly: RegisterPermission("help.full", Description = "Permite visualizar a ajuda completa, incluindo comandos administrativos e cheats.")]

namespace SkaliaDeps
{
    public sealed class SkaliaAddonPlugin : OpenModUnturnedPlugin
    {
        private readonly ILogger<SkaliaAddonPlugin> m_Logger;
        private readonly Harmony m_Harmony;
        private readonly AutomaticAnnouncementService m_AutomaticAnnouncementService;
        private readonly IAdminFeatureAccessService m_AdminFeatureAccessService;
        private readonly IGroundCleanupService m_GroundCleanupService;
        private readonly IInventorySortService m_InventorySortService;
        private readonly IExperienceBarService m_ExperienceBarService;
        private readonly ICallVoteService m_CallVoteService;
        private readonly IPlayerActivityService m_PlayerActivityService;
        private readonly IVaultService m_VaultService;
        private readonly ServerCapacityService m_ServerCapacityService;
        private readonly float m_GodSpeedMultiplier;
        private readonly ISkaliaBotService m_BotService;
        private readonly bool m_AutoCallBots;

        public SkaliaAddonPlugin(
            ILogger<SkaliaAddonPlugin> logger,
            IConfiguration configuration,
            IStringLocalizer stringLocalizer,
            IAdminFeatureAccessService adminFeatureAccessService,
            IGroundCleanupService groundCleanupService,
            IInventorySortService inventorySortService,
            IExperienceBarService experienceBarService,
            ICallVoteService callVoteService,
            IPlayerActivityService playerActivityService,
            IVaultService vaultService,
            IRuntime runtime,
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_Logger = logger;
            m_BotService = botService;
            m_AutoCallBots = configuration.GetValue("serverBot:autoCallOnStart", false);
            m_Harmony = new Harmony("Skalia.Deps");
            m_AutomaticAnnouncementService = new AutomaticAnnouncementService(
                configuration,
                groundCleanupService,
                stringLocalizer,
                logger);
            m_AdminFeatureAccessService = adminFeatureAccessService;
            m_GroundCleanupService = groundCleanupService;
            m_InventorySortService = inventorySortService;
            m_ExperienceBarService = experienceBarService;
            m_CallVoteService = callVoteService;
            m_PlayerActivityService = playerActivityService;
            m_VaultService = vaultService;
            m_ServerCapacityService = new ServerCapacityService(
                configuration,
                logger,
                runtime,
                WorkingDirectory);
            m_GodSpeedMultiplier = configuration.GetValue(
                "administration:godSpeedMultiplier", 1.5f);
        }

        protected override UniTask OnLoadAsync()
        {
            GodModeState.Clear();
            GodModeState.ConfigureSpeedMultiplier(m_GodSpeedMultiplier);
            ChatNamePrefixService.Configure(Configuration);
            // 0.1.0-y17.31: automaticReload:enabled. Sem Start o servico nao
            // age (QueueAfterShot sai sem s_Shutdown).
            if (Configuration.GetValue("automaticReload:enabled", true))
            {
                AutomaticReloadService.Start(m_Logger);
            }
            else
            {
                m_Logger.LogInformation("Recarga automatica desativada (automaticReload:enabled = false).");
            }
            m_AutomaticAnnouncementService.Start();
            m_GroundCleanupService.Start();
            m_InventorySortService.Start();
            m_ExperienceBarService.Start();
            m_PlayerActivityService.Start();
            m_ServerCapacityService.Start();
            m_CallVoteService.Start();
            GuaranteedAlicepackRewardService.Configure(
                Configuration,
                m_Logger);
            ZombieSpawnScalingService.Configure(
                Configuration,
                m_Logger);
            SkaliaLootAmount.Configure(Configuration, m_Logger);
            m_Harmony.PatchAll(typeof(SkaliaAddonPlugin).Assembly);
            Level.onPostLevelLoaded += OnPostLevelLoaded;
            m_BotService.Start();
            SkaliaPlayerRecorder.Start(m_Logger);
            SkaliaLauncherBridge.Start(m_Logger);
            // O loop de frames do Unturned ainda nao avanca enquanto o OpenMod
            // aguarda OnLoadAsync. Esta sincronizacao precisa ser adiada sem ser
            // aguardada aqui, ou o servidor entra em deadlock durante a partida.
            SyncOnlinePlayersAfterLoadAsync().Forget();
            m_Logger.LogInformation("Skalia Deps carregado com TPA, Home, Vault, organizador de inventario, comandos administrativos, recarga automatica, mensagens globais, permissoes e cores de chat.");
            return UniTask.CompletedTask;
        }

        // SKALIA: o launcher registra os bots em bots.json; ao carregar o mapa
        // todos entram sozinhos, sem precisar de /callbots all.
        private async UniTask AutoCallBotsAsync()
        {
            try
            {
                await UniTask.Delay(TimeSpan.FromSeconds(5));
                await UniTask.SwitchToMainThread();
                var count = await m_BotService.CallAsync("all");
                m_Logger.LogInformation("SKALIA: {Count} bot(s) chamados automaticamente.", count);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "SKALIA: falha chamando os bots automaticamente.");
            }
        }

        private async UniTask SyncOnlinePlayersAfterLoadAsync()
        {
            try
            {
                // No reload do OpenMod os jogadores conectados nao disparam
                // novamente o evento de conexao. Aguarda o proximo frame para
                // sincronizar permissoes e o estado atual da UI.
                await UniTask.DelayFrame(1);
                await m_AdminFeatureAccessService.SyncOnlinePlayersAsync();
                await m_InventorySortService.SyncUiForOnlinePlayersAsync();
                await m_ExperienceBarService.SyncOnlinePlayersAsync();
                if (Level.isLoaded)
                {
                    SkaliaLauncherBridge.MarkReady(m_Logger);
                    if (m_AutoCallBots)
                    {
                        AutoCallBotsAsync().Forget();
                    }
                    ValidateNaturalItemSpawnRemap();
                    SkaliaLootAmount.Start();
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Nao foi possivel sincronizar os jogadores apos carregar o Skalia Deps.");
            }
        }

        protected override async UniTask OnUnloadAsync()
        {
            // O OpenMod pode iniciar o dispose fora da thread do Unity durante
            // `openmod reload`. Limpar efeitos e acessar Provider exige a game thread.
            await UniTask.SwitchToMainThread();
            SkaliaDeps.Commands.CommandZumbis.StopAll();
            SkaliaPlayerRecorder.Stop();
            SkaliaLootAmount.Stop();
            SkaliaLauncherBridge.Stop();
            await m_BotService.StopAsync();
            await UniTask.SwitchToMainThread();
            await m_VaultService.ShutdownAsync();
            await UniTask.SwitchToMainThread();
            GodModeState.Clear();
            m_AutomaticAnnouncementService.Stop();
            m_GroundCleanupService.Stop();
            m_InventorySortService.Stop();
            await m_ExperienceBarService.StopAsync();
            m_CallVoteService.Stop();
            m_PlayerActivityService.Stop();
            m_ServerCapacityService.Stop();
            AutomaticReloadService.Stop();
            GuaranteedAlicepackRewardService.Clear();
            ZombieSpawnScalingService.Clear();
            ChatNamePrefixService.Clear();
            Level.onPostLevelLoaded -= OnPostLevelLoaded;
            m_Harmony.UnpatchAll("Skalia.Deps");
            m_Logger.LogInformation("Skalia Deps descarregado.");
        }

        private void OnPostLevelLoaded(int level)
        {
            SkaliaLauncherBridge.MarkReady(m_Logger);
            if (m_AutoCallBots)
            {
                AutoCallBotsAsync().Forget();
            }
            ZombieSpawnScalingService.ApplyToLoadedLevel();
            ValidateNaturalItemSpawnRemap();
            SkaliaLootAmount.Start();
        }

        private void ValidateNaturalItemSpawnRemap()
        {
            var grenade = Assets.find(
                EAssetType.ITEM,
                NaturalItemSpawnRemapPatch.StackableFragmentationGrenadeId)
                as ItemAsset;
            var explosives = Assets.find(
                EAssetType.ITEM,
                NaturalItemSpawnRemapPatch.StackableRawExplosivesId)
                as ItemAsset;

            if (grenade == null || explosives == null)
            {
                m_Logger.LogError(
                    "Remapeamento de loot stackable inativo no mapa {Map}: assets ausentes (granada={GrenadeLoaded}, explosivos={ExplosivesLoaded}). O loot vanilla sera preservado.",
                    Provider.map,
                    grenade != null,
                    explosives != null);
                return;
            }

            m_Logger.LogInformation(
                "Remapeamento de loot stackable ativo no mapa {Map}: {VanillaGrenade}->{StackableGrenade} e {VanillaExplosives}->{StackableExplosives}.",
                Provider.map,
                NaturalItemSpawnRemapPatch.VanillaFragmentationGrenadeId,
                NaturalItemSpawnRemapPatch.StackableFragmentationGrenadeId,
                NaturalItemSpawnRemapPatch.VanillaRawExplosivesId,
                NaturalItemSpawnRemapPatch.StackableRawExplosivesId);
        }
    }
}

namespace SkaliaDeps
{
    [HarmonyPatch(typeof(PlayerLife), "doDamage")]
    internal static class BotDamageNotificationPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First + 1)]
        private static void Prefix(PlayerLife __instance, byte amount, EDeathCause newCause, Steamworks.CSteamID newKiller)
        {
            SkaliaBotIdleSimulation.NotifyDamage(__instance, newCause, amount);
            SkaliaBotIdleSimulation.NotifyPlayerAttack(__instance, newCause, amount, newKiller);
        }
    }
}
