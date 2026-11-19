using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.API.Permissions;
using OpenMod.API.Users;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using Steamworks;

namespace SkaliaDeps.Commands
{
    public abstract class AdminPermissionCommandBase : UnturnedCommand
    {
        private const string AdminRoleId = "admin";
        private readonly IAdminFeatureAccessService m_AdminFeatureAccessService;
        private readonly IPermissionRoleStore m_RoleStore;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IUnturnedUserDirectory m_UserDirectory;
        private readonly IUserManager m_UserManager;

        protected AdminPermissionCommandBase(
            IAdminFeatureAccessService adminFeatureAccessService,
            IPermissionRoleStore roleStore,
            IStringLocalizer stringLocalizer,
            IUnturnedUserDirectory userDirectory,
            IUserManager userManager,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_AdminFeatureAccessService = adminFeatureAccessService;
            m_RoleStore = roleStore;
            m_StringLocalizer = stringLocalizer;
            m_UserDirectory = userDirectory;
            m_UserManager = userManager;
        }

        protected abstract bool GrantPermission { get; }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length == 0)
            {
                await PrintAsync(Format(GrantPermission
                    ? "messages:iam:grant_usage"
                    : "messages:iam:revoke_usage"));
                return;
            }

            var searchedName = string.Join(" ", Context.Parameters);
            string targetId;
            if (ulong.TryParse(searchedName, out var steamIdValue))
            {
                targetId = steamIdValue.ToString();
            }
            else
            {
                var match = PlayerNameMatcher.Find(searchedName, m_UserDirectory.GetOnlineUsers());
                if (match.IsAmbiguous)
                {
                    await PrintAsync(Format("messages:common:player_ambiguous", new
                    {
                        Player = searchedName,
                        Players = PlayerNameMatcher.FormatConflicts(match.Conflicts)
                    }));
                    return;
                }

                if (match.User == null)
                {
                    await PrintAsync(Format("messages:common:player_not_found", new { Player = searchedName }));
                    return;
                }

                targetId = match.User.SteamId.m_SteamID.ToString();
            }

            var targetActor = await m_UserManager.FindUserAsync(
                "player",
                targetId,
                UserSearchMode.FindById);
            if (targetActor == null)
            {
                await PrintAsync(Format("messages:common:player_not_found", new { Player = searchedName }));
                return;
            }

            var permissionChanged = GrantPermission
                ? await m_RoleStore.AddRoleToActorAsync(targetActor, AdminRoleId)
                : await m_RoleStore.RemoveRoleFromActorAsync(targetActor, AdminRoleId);

            if (GrantPermission && !permissionChanged)
            {
                await PrintAsync(Format("messages:iam:grant_failed", new
                {
                    Player = GetDisplayName(targetActor, targetId)
                }));
                return;
            }

            // Garante que nem uma entrada nativa antiga volte a exibir azul/martelo.
            await UniTask.SwitchToMainThread();
            var steamId = new CSteamID(ulong.Parse(targetId));
            if (SteamAdminlist.checkAdmin(steamId))
            {
                SteamAdminlist.unadmin(steamId);
            }

            var onlineTarget = m_UserDirectory.FindUser(steamId);
            if (onlineTarget != null)
            {
                await m_AdminFeatureAccessService.SyncAsync(onlineTarget.Player);
            }

            await PrintAsync(Format(GrantPermission
                ? "messages:iam:grant_complete"
                : "messages:iam:revoke_complete", new
            {
                Player = GetDisplayName(targetActor, targetId)
            }));
        }

        private static string GetDisplayName(IPermissionActor actor, string fallback)
        {
            return string.IsNullOrWhiteSpace(actor.DisplayName)
                ? fallback
                : actor.DisplayName;
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            return PrivateMessageFormatter.AppendNoticeIfPlayer(
                m_StringLocalizer,
                $"{m_StringLocalizer["prefix"].Value} {message}",
                Context.Actor);
        }
    }
}
