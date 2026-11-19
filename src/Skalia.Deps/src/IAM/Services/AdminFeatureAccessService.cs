using System;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.API.Permissions;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Users;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class AdminFeatureAccessService : IAdminFeatureAccessService
    {
        private const string GlobalAdminPermission = "*";
        private const string AdminRoleId = "admin";
        private readonly IPermissionChecker m_PermissionChecker;
        private readonly IPermissionRoleStore m_RoleStore;
        private readonly IUnturnedUserDirectory m_UserDirectory;
        private readonly ILogger<AdminFeatureAccessService> m_Logger;

        public AdminFeatureAccessService(
            IPermissionChecker permissionChecker,
            IPermissionRoleStore roleStore,
            IUnturnedUserDirectory userDirectory,
            ILogger<AdminFeatureAccessService> logger)
        {
            m_PermissionChecker = permissionChecker;
            m_RoleStore = roleStore;
            m_UserDirectory = userDirectory;
            m_Logger = logger;
        }

        public async Task SyncAsync(UnturnedPlayer player)
        {
            var user = m_UserDirectory.GetUser(player.Player);
            var hasGlobalAdminPermission = false;
            var hasGlobalAdminDenial = false;
            var roles = await m_RoleStore.GetRolesAsync(user, true);
            var hasAdminRole = roles.Any(role =>
                string.Equals(role.Id, AdminRoleId, StringComparison.OrdinalIgnoreCase));

            // Nao usa CheckPermissionAsync porque ele exige que a permissao consultada
            // esteja registrada. As stores retornam permissoes diretas e herdadas.
            foreach (var permissionStore in m_PermissionChecker.PermissionStores)
            {
                var deniedPermissions = await permissionStore.GetDeniedPermissionsAsync(user);
                hasGlobalAdminDenial |= deniedPermissions.Any(permission =>
                    string.Equals(permission, GlobalAdminPermission, StringComparison.OrdinalIgnoreCase));

                var grantedPermissions = await permissionStore.GetGrantedPermissionsAsync(user);
                hasGlobalAdminPermission |= grantedPermissions.Any(permission =>
                    string.Equals(permission, GlobalAdminPermission, StringComparison.OrdinalIgnoreCase));
            }

            await SetEnabledAsync(
                player,
                hasAdminRole || (hasGlobalAdminPermission && !hasGlobalAdminDenial));
        }

        public async Task SyncOnlinePlayersAsync()
        {
            foreach (var user in m_UserDirectory.GetOnlineUsers().ToArray())
            {
                try
                {
                    await SyncAsync(user.Player);
                }
                catch (Exception exception)
                {
                    m_Logger.LogError(
                        exception,
                        "Nao foi possivel sincronizar os recursos administrativos de {Player} ({SteamId}).",
                        user.DisplayName,
                        user.SteamId);
                }
            }
        }

        public async Task SetEnabledAsync(UnturnedPlayer player, bool enabled)
        {
            await UniTask.SwitchToMainThread();
            var look = player.Player.look;
            look.sendFreecamAllowed(enabled);
            look.sendWorkzoneAllowed(enabled);
            look.sendSpecStatsAllowed(enabled);
        }

        public async Task RevokeAdvantagesAsync(UnturnedPlayer player)
        {
            await UniTask.SwitchToMainThread();

            var steamId = player.SteamId.m_SteamID;
            GodModeState.SetEnabled(steamId, false);
            GodModeState.ApplySpeed(player.Player, false);

            var look = player.Player.look;
            look.sendFreecamAllowed(false);
            look.sendWorkzoneAllowed(false);
            look.sendSpecStatsAllowed(false);
        }
    }
}
