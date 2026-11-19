using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OpenMod.API.Eventing;
using OpenMod.API.Permissions;
using OpenMod.API.Users;
using OpenMod.Unturned.Players.Chat.Events;
using UnityEngine;

namespace SkaliaDeps
{
    public sealed class ChatRoleColorListener : IEventListener<UnturnedPlayerChattingEvent>
    {
        private readonly IPermissionRoleStore m_RoleStore;
        private readonly IUserManager m_UserManager;
        private readonly IReadOnlyDictionary<string, Color> m_RoleColors;

        public ChatRoleColorListener(
            IPermissionRoleStore roleStore,
            IUserManager userManager,
            IConfiguration configuration)
        {
            m_RoleStore = roleStore;
            m_UserManager = userManager;
            m_RoleColors = ReadRoleColors(configuration);
        }

        public async Task HandleEventAsync(object? sender, UnturnedPlayerChattingEvent @event)
        {
            var user = await m_UserManager.FindUserAsync(
                "player",
                @event.Player.SteamId.m_SteamID.ToString(),
                UserSearchMode.FindById);

            if (user == null)
            {
                return;
            }

            var roles = await m_RoleStore.GetRolesAsync(user, true);
            var coloredRole = roles
                .Where(role => m_RoleColors.ContainsKey(role.Id))
                .OrderByDescending(role => role.Priority)
                .FirstOrDefault();

            if (coloredRole != null)
            {
                @event.Color = m_RoleColors[coloredRole.Id];
            }
        }

        private static IReadOnlyDictionary<string, Color> ReadRoleColors(IConfiguration configuration)
        {
            var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in configuration.GetSection("chat:roleColors").GetChildren())
            {
                if (TryParseColor(entry.Value, out var color))
                {
                    colors[entry.Key] = color;
                }
            }

            if (colors.Count == 0)
            {
                colors["default"] = Color.white;
            }

            return colors;
        }

        private static bool TryParseColor(string? value, out Color color)
        {
            if (!string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value, out color))
            {
                return true;
            }

            color = default;
            return false;
        }
    }
}
