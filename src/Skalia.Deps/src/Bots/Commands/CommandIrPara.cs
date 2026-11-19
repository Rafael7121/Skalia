using System;
using System.Globalization;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using UnityEngine;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: ferramenta para ir ate os
    // pontos que os logs dos bots apontam.
    [Command("irpara")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandIrPara : UnturnedCommand
    {
        public CommandIrPara(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        protected override async UniTask OnExecuteAsync()
        {
            var text = string.Join(" ", Context.Parameters).Replace('/', ' ').Replace(';', ' ');
            var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                await PrintAsync("Uso: /irpara x y z (ex.: /irpara -986 44 -763)");
                return;
            }
            await UniTask.SwitchToMainThread();
            var player = ((UnturnedUser)Context.Actor).Player.Player;
            // Um pouco acima do ponto, para nao nascer dentro do chao.
            player.teleportToLocationUnsafe(new Vector3(x, y + 0.5f, z), player.look.yaw);
            await PrintAsync($"Indo para ({x:0.#}, {y:0.#}, {z:0.#}).");
        }
    }
}
