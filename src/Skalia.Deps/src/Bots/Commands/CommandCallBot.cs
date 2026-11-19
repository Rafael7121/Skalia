using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("callbot")]
    public sealed class CommandCallBot : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandCallBot(
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length < 1)
            {
                await PrintAsync("Uso: /callbot <nome do bot>  (todos: /callbots)");
                return;
            }

            var selector = string.Join(" ", Context.Parameters.Select(value => value.ToString()));
            if (string.Equals(selector.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                await PrintAsync("Uso: /callbot <nome do bot>  (todos: /callbots)");
                return;
            }
            var count = await m_BotService.CallAsync(selector);
            await PrintAsync($"{count} bot(s) chamado(s).");
        }
    }
}
