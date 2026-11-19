using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Core.Console;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("stopbots")]
    [CommandActor(typeof(ConsoleActor))]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandStopBots : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandStopBots(
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                return;
            }

            var count = await m_BotService.SetStandbyAllAsync();
            await PrintAsync($"{count} bot(s) em standby.");
        }
    }
}
