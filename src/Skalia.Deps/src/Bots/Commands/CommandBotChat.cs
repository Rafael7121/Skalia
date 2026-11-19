using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("chat")]
    public sealed class CommandBotChat : UnturnedCommand
    {
        private readonly ISkaliaBotService m_BotService;

        public CommandBotChat(
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length < 2)
            {
                await PrintAsync("Uso: /chat <nome do bot> <mensagem>");
                return;
            }

            var botName = Context.Parameters[0];
            var message = string.Join(" ", Context.Parameters.Skip(1)).Trim();
            if (message.Length >= 2 && message[0] == '"' && message[message.Length - 1] == '"')
            {
                message = message.Substring(1, message.Length - 2);
            }

            var sent = await m_BotService.SendChatAsync(botName, message);
            await PrintAsync(sent ? "Mensagem enviada." : "Bot conectado nao encontrado.");
        }
    }
}
