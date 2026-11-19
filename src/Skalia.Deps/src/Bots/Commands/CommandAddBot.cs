using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("addbot")]
    public sealed class CommandAddBot : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandAddBot(
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length < 2
                || !ulong.TryParse(Context.Parameters[0], out var steamId)
                || steamId == 0)
            {
                await PrintAsync("Uso: /addbot <steamid64> <nome>");
                return;
            }

            var name = string.Join(" ", Context.Parameters.Skip(1));
            var added = await m_BotService.AddBotAsync(steamId, name);
            await PrintAsync(added ? "Bot adicionado." : "Bot nao adicionado.");
        }
    }
}
