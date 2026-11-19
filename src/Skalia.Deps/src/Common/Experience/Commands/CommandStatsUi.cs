using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("statsui")]
    [CommandDescription("Mostra ou oculta o HUD de status.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandStatsUi : UnturnedCommand
    {
        private readonly IExperienceBarService m_ExperienceBarService;

        public CommandStatsUi(
            IExperienceBarService experienceBarService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_ExperienceBarService = experienceBarService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                return;
            }

            bool visible = await m_ExperienceBarService.ToggleUiAsync(
                ((UnturnedUser)Context.Actor).Player);
            await PrintAsync(visible
                ? "HUD de status ativado. Use /statsui para ocultar."
                : "HUD de status ocultado. Use /statsui para exibir novamente.");
        }
    }
}
