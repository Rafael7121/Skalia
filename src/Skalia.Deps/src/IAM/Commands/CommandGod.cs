using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("god")]
    [CommandDescription("Ativa ou desativa a invulnerabilidade de um jogador.")]
    [CommandSyntax("[jogador] [on|off]")]
    public sealed class CommandGod : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IUnturnedUserDirectory m_UserDirectory;

        public CommandGod(
            IStringLocalizer stringLocalizer,
            IUnturnedUserDirectory userDirectory,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_StringLocalizer = stringLocalizer;
            m_UserDirectory = userDirectory;
        }

        protected override async UniTask OnExecuteAsync()
        {
            var actor = Context.Actor as UnturnedUser;
            var parameters = Context.Parameters.ToList();
            var requestedState = TryTakeState(parameters);
            UnturnedUser? target = actor;

            if (parameters.Count == 0 && target == null)
            {
                await PrintAsync(Format("messages:administration:god_console_usage"));
                return;
            }

            if (parameters.Count > 0)
            {
                var searchedName = string.Join(" ", parameters);
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

                target = match.User;
            }

            if (target == null)
            {
                return;
            }

            await UniTask.SwitchToMainThread();
            var steamId = target.SteamId.m_SteamID;
            var enabled = requestedState.HasValue
                ? GodModeState.SetEnabled(steamId, requestedState.Value)
                : GodModeState.Toggle(steamId);
            GodModeState.ApplySpeed(target.Player.Player, enabled);

            if (enabled)
            {
                var life = target.Player.Player.life;
                if (life.IsAlive)
                {
                    life.askHeal(100, true, true);
                    life.serverModifyFood(100f - life.food);
                    life.serverModifyWater(100f - life.water);
                    life.serverModifyVirus(100f - life.virus);
                    life.serverModifyStamina(100f - life.stamina);
                    life.simulatedModifyOxygen(100f - life.oxygen);
                }
            }

            await PrintAsync(Format(enabled
                ? "messages:administration:god_enabled"
                : "messages:administration:god_disabled", new
            {
                Player = target.Player.SteamPlayer.playerID.characterName
            }));
        }

        private static bool? TryTakeState(IList<string> parameters)
        {
            if (parameters.Count == 0)
            {
                return null;
            }

            var lastIndex = parameters.Count - 1;
            var value = parameters[lastIndex];
            if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
            {
                parameters.RemoveAt(lastIndex);
                return true;
            }

            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            {
                parameters.RemoveAt(lastIndex);
                return false;
            }

            return null;
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
