using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("kickbots")]
    public sealed class CommandKickBots : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandKickBots(
            ISkaliaBotService botService,
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                return;
            }

            var count = await m_BotService.KickAllAsync();
            await PrintAsync(Format("messages:administration:hidden_action_complete", new
            {
                Count = count
            }));
        }

        private string Format(string key, object arguments)
        {
            return $"{m_StringLocalizer["prefix"].Value} "
                + m_StringLocalizer[key, arguments].Value;
        }
    }
}
