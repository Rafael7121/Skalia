using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Core.Console;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("stopbot")]
    [CommandActor(typeof(ConsoleActor))]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandStopBot : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandStopBot(
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length < 1)
            {
                await PrintAsync("Uso: /stopbot <nome do bot>  (todos: /stopbots)");
                return;
            }

            var selector = string.Join(" ", Context.Parameters.Select(value => value.ToString())).Trim();
            if (string.Equals(selector, "all", StringComparison.OrdinalIgnoreCase))
            {
                await PrintAsync("Uso: /stopbot <nome do bot>  (todos: /stopbots)");
                return;
            }

            var count = await m_BotService.SetStandbyAsync(selector);
            await PrintAsync($"{count} bot(s) em standby.");
        }
    }
}
