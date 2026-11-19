using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("homes")]
    [CommandDescription("Lista suas camas ativas e seus nomes.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandHomes : UnturnedCommand
    {
        private readonly IHomeService m_HomeService;

        public CommandHomes(
            IHomeService homeService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_HomeService = homeService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                return;
            }

            await m_HomeService.ListBedsAsync(((UnturnedUser)Context.Actor).Player);
        }
    }
}
