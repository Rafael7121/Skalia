using System;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;

namespace SkaliaDeps.Commands
{
    // Liga e desliga as rotas dos bots pela malha nova, para comparar com a
    // malha dos zumbis no mesmo servidor.
    [Command("malhanova")]
    public sealed class CommandMalhaNova : UnturnedCommand
    {
        public CommandMalhaNova(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        protected override async UniTask OnExecuteAsync()
        {
            var argument = Context.Parameters.Length > 0 ? Context.Parameters[0].ToLowerInvariant() : string.Empty;
            if (argument == "on" || argument == "ligar")
            {
                SkaliaBotUnityNavmesh.Enabled = true;
            }
            else if (argument == "off" || argument == "desligar")
            {
                SkaliaBotUnityNavmesh.Enabled = false;
            }
            else if (argument.Length > 0)
            {
                await PrintAsync("Uso: /malhanova [on|off]");
                return;
            }
            await PrintAsync(string.Format(
                "Malha nova {0}: {1} pedacos prontos, {2} na fila.",
                SkaliaBotUnityNavmesh.Enabled ? "ligada" : "desligada",
                SkaliaBotUnityNavmesh.ReadyChunks,
                SkaliaBotUnityNavmesh.QueuedChunks));
        }
    }
}
