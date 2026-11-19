using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("sort")]
    [CommandDescription("Organiza o inventario pessoal conforme a configuracao do servidor.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandSort : UnturnedCommand
    {
        private readonly IInventorySortService m_InventorySortService;

        public CommandSort(
            IInventorySortService inventorySortService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_InventorySortService = inventorySortService;
        }

        protected override UniTask OnExecuteAsync()
        {
            var user = (UnturnedUser)Context.Actor;
            return m_InventorySortService.SortAsync(user.Player);
        }
    }
}
