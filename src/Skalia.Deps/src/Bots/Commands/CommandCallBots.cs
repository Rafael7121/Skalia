using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("callbots")]
    public sealed class CommandCallBots : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandCallBots(
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

            var count = await m_BotService.CallAsync("all");
            await PrintAsync($"{count} bot(s) chamado(s).");
        }
    }
}
