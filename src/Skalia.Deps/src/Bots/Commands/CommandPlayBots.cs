using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Core.Console;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("playbots")]
    [CommandActor(typeof(ConsoleActor))]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandPlayBots : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandPlayBots(
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

            var count = await m_BotService.PlayAsync("all");
            await PrintAsync($"{count} bot(s) jogando.");
        }
    }
}
