using System;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using UnityEngine;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("passagem")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandPassagem : UnturnedCommand
    {
        private static readonly Dictionary<ulong, Vector3> s_PendingStart = new Dictionary<ulong, Vector3>();

        public CommandPassagem(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        protected override async UniTask OnExecuteAsync()
        {
            await UniTask.SwitchToMainThread();
            var user = (UnturnedUser)Context.Actor;
            var steamId = user.SteamId.m_SteamID;
            var position = user.Player.Player.transform.position;
            var action = Context.Parameters.Length > 0
                ? Context.Parameters[0].ToString().ToLowerInvariant()
                : "";
            switch (action)
            {
                case "a":
                    s_PendingStart[steamId] = position;
                    await PrintAsync($"Ponta A marcada em {Format(position)} (regiao {AreaText(position)}). Va ate a outra ponta e use /passagem b.");
                    return;
                case "b":
                    if (!s_PendingStart.TryGetValue(steamId, out var start))
                    {
                        await PrintAsync("Marque a primeira ponta com /passagem a.");
                        return;
                    }
                    s_PendingStart.Remove(steamId);
                    var number = SkaliaBotPassageLinks.Add(start, position);
                    await PrintAsync($"Passagem #{number} salva: A {Format(start)} (regiao {AreaText(start)}) <-> B {Format(position)} (regiao {AreaText(position)}), {Vector3.Distance(start, position):0.#}m.");
                    if (SkaliaBotPassageLinks.GetArea(start) == SkaliaBotPassageLinks.GetArea(position))
                    {
                        await PrintAsync("Aviso: as duas pontas ja estao na mesma regiao da malha; o bot ja chega de uma na outra sem a passagem.");
                    }
                    return;
                case "lista":
                    var links = SkaliaBotPassageLinks.Links;
                    if (links.Count == 0)
                    {
                        await PrintAsync("Nenhuma passagem marcada neste mapa.");
                        return;
                    }
                    for (var index = 0; index < links.Count; index++)
                    {
                        var link = links[index];
                        var distance = Mathf.Min(Vector3.Distance(position, link.A), Vector3.Distance(position, link.B));
                        await PrintAsync($"#{index + 1}: A {Format(link.A)} <-> B {Format(link.B)} (a {distance:0}m de voce)");
                    }
                    return;
                case "remover":
                    if (Context.Parameters.Length < 2
                        || !int.TryParse(Context.Parameters[1].ToString(), out var removeNumber)
                        || !SkaliaBotPassageLinks.RemoveAt(removeNumber - 1))
                    {
                        await PrintAsync("Uso: /passagem remover <numero da /passagem lista>");
                        return;
                    }
                    await PrintAsync($"Passagem #{removeNumber} removida.");
                    return;
                case "cancelar":
                    s_PendingStart.Remove(steamId);
                    await PrintAsync("Marcacao cancelada.");
                    return;
                default:
                    await PrintAsync("Uso: /passagem a (primeira ponta) | /passagem b (segunda ponta) | /passagem lista | /passagem remover <n> | /passagem cancelar");
                    await PrintAsync($"Sua posicao: {Format(position)}, regiao da malha {AreaText(position)}.");
                    return;
            }
        }

        private static string AreaText(Vector3 point)
        {
            var area = SkaliaBotPassageLinks.GetArea(point);
            if (!area.HasValue)
            {
                return "nenhuma";
            }
            var text = area.Value.ToString(CultureInfo.InvariantCulture);
            var mesh = SkaliaBotPassageLinks.GetNearestMeshPoint(point);
            if (mesh.HasValue)
            {
                var below = point.y - mesh.Value.y;
                var away = new Vector2(mesh.Value.x - point.x, mesh.Value.z - point.z).magnitude;
                if (Mathf.Abs(below) > 1f || away > 1.5f)
                {
                    text += string.Format(CultureInfo.InvariantCulture,
                        ", fora da malha: ponto mais proximo {0:0.0}m {1} e {2:0.0}m ao lado",
                        Mathf.Abs(below), below > 0f ? "abaixo" : "acima", away);
                }
            }
            return text;
        }

        private static string Format(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", value.x, value.y, value.z);
        }
    }
}
