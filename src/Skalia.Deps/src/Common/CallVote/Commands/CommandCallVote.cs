using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("cv")]
    [CommandAlias("callvote")]
    [CommandDescription("Participa de uma votação ativa no servidor.")]
    [CommandSyntax("<d|wn>")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandCallVote : UnturnedCommand
    {
        private readonly ICallVoteService m_CallVoteService;
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandCallVote(
            ICallVoteService callVoteService,
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_CallVoteService = callVoteService;
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 1)
            {
                await PrintAsync(Format("messages:call_vote:usage"));
                return;
            }

            CallVoteType type;
            if (string.Equals(Context.Parameters[0], "d", StringComparison.OrdinalIgnoreCase))
            {
                type = CallVoteType.SkipNight;
            }
            else if (string.Equals(Context.Parameters[0], "wn", StringComparison.OrdinalIgnoreCase))
            {
                type = CallVoteType.WeatherNone;
            }
            else
            {
                await PrintAsync(Format("messages:call_vote:usage"));
                return;
            }

            var user = (UnturnedUser)Context.Actor;
            await m_CallVoteService.CastVoteAsync(user.Player, type);
        }

        private string Format(string key)
        {
            return $"<color=#58C7FA>[Votação]</color> {m_StringLocalizer[key].Value}";
        }
    }
}
