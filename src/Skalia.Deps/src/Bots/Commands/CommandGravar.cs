using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    // 0.1.170: o gravador de movimentos dos jogadores reais.
    //   /gravar              mostra se esta gravando e onde
    //   /gravar marca <nota> escreve uma nota na gravacao de quem usa
    //   /gravar parar        para de gravar todo mundo
    //   /gravar ligar        volta a gravar
    //   /gravar bots ligar   0.2.0: grava tambem os bots (arquivos com _BOT_)
    //   /gravar bots parar   para de gravar os bots
    // 0.2.2x8: o console tambem pode ligar e parar (antes so jogador, e o
    // console respondia "Comando nao encontrado"); a marca continua de jogador.
    [Command("gravar")]
    public sealed class CommandGravar : UnturnedCommand
    {
        public CommandGravar(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        protected override async UniTask OnExecuteAsync()
        {
            await UniTask.SwitchToMainThread();
            var user = Context.Actor as UnturnedUser;
            var action = Context.Parameters.Length > 0
                ? Context.Parameters[0].ToString().ToLowerInvariant()
                : "";
            switch (action)
            {
                case "marca":
                case "nota":
                    if (user == null)
                    {
                        await PrintAsync("A marca e gravada na gravacao de um jogador: use /gravar marca no chat do jogo.");
                        return;
                    }
                    var note = Context.Parameters.Length > 1
                        ? string.Join(" ", Context.Parameters.Skip(1)).Trim()
                        : "(sem texto)";
                    await PrintAsync(SkaliaPlayerRecorder.Mark(user.SteamId.m_SteamID, note)
                        ? $"Marca gravada: {note}"
                        : "Voce nao esta sendo gravado agora (use /gravar ligar).");
                    return;
                case "parar":
                    SkaliaPlayerRecorder.SetEnabled(false);
                    await PrintAsync("Gravacao de movimentos parada.");
                    return;
                case "ligar":
                    SkaliaPlayerRecorder.SetEnabled(true);
                    await PrintAsync("Gravacao de movimentos ligada.");
                    return;
                case "bots":
                    var botAction = Context.Parameters.Length > 1
                        ? Context.Parameters[1].ToString().ToLowerInvariant()
                        : "";
                    if (botAction == "ligar")
                    {
                        SkaliaPlayerRecorder.SetBotsEnabled(true);
                        await PrintAsync(SkaliaPlayerRecorder.Enabled
                            ? "Gravacao dos bots ligada (arquivos com _BOT_ no nome, apagados depois de 7 dias)."
                            : "Gravacao dos bots ligada, mas a gravacao geral esta parada. Use /gravar ligar.");
                    }
                    else if (botAction == "parar")
                    {
                        SkaliaPlayerRecorder.SetBotsEnabled(false);
                        await PrintAsync("Gravacao dos bots parada.");
                    }
                    else
                    {
                        await PrintAsync(SkaliaPlayerRecorder.BotsEnabled
                            ? "Os bots estao sendo gravados. Use /gravar bots parar."
                            : "Os bots nao estao sendo gravados. Use /gravar bots ligar.");
                    }
                    return;
                default:
                    await PrintAsync(SkaliaPlayerRecorder.Enabled
                        ? $"Gravando {SkaliaPlayerRecorder.ActiveCount} jogador(es) em {SkaliaPlayerRecorder.RecordingsDirectory} ({SkaliaPlayerRecorder.FilesOpened} arquivo(s), {SkaliaPlayerRecorder.RowsWritten} linhas nesta sessao). Bots: {(SkaliaPlayerRecorder.BotsEnabled ? "gravando" : "nao")}. Use /gravar marca <nota>, /gravar parar, /gravar ligar ou /gravar bots ligar|parar."
                        : "Gravacao de movimentos parada. Use /gravar ligar.");
                    return;
            }
        }
    }
}
