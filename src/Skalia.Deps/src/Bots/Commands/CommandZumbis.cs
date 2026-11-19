using System;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    // Limpa zumbis em volta de quem usa, para marcar passagens com calma.
    [Command("zumbis")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandZumbis : UnturnedCommand
    {
        private const float DefaultRadius = 100f;
        private const float MaxRadius = 2000f;

        // Players with the continuous clear on, and its radius.
        private static readonly Dictionary<ulong, float> s_Paused = new Dictionary<ulong, float>();
        private static int s_LoopGeneration;

        public CommandZumbis(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        protected override async UniTask OnExecuteAsync()
        {
            await UniTask.SwitchToMainThread();
            var user = (UnturnedUser)Context.Actor;
            var steamId = user.SteamId.m_SteamID;
            var action = Context.Parameters.Length > 0
                ? Context.Parameters[0].ToString().ToLowerInvariant()
                : "";
            var radius = DefaultRadius;
            if (Context.Parameters.Length > 1
                && float.TryParse(Context.Parameters[1].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0f)
            {
                radius = Mathf.Min(parsed, MaxRadius);
            }
            switch (action)
            {
                case "limpar":
                    var killed = KillZombiesNear(user.Player.Player.transform.position, radius);
                    await PrintAsync($"{killed} zumbi(s) eliminado(s) num raio de {radius:0}m.");
                    return;
                case "pausar":
                    if (s_Paused.Remove(steamId))
                    {
                        await PrintAsync("Limpeza continua desligada. Os zumbis voltam a nascer normalmente.");
                        return;
                    }
                    s_Paused[steamId] = radius;
                    var first = KillZombiesNear(user.Player.Player.transform.position, radius);
                    await PrintAsync($"Limpeza continua ligada num raio de {radius:0}m ({first} eliminado(s) agora). Use /zumbis pausar de novo para desligar.");
                    if (s_Paused.Count == 1)
                    {
                        RunPauseLoop(++s_LoopGeneration).Forget();
                    }
                    return;
                default:
                    await PrintAsync("Uso: /zumbis limpar [raio] | /zumbis pausar [raio] (liga/desliga). Raio padrao 100m.");
                    return;
            }
        }

        // Called on plugin unload so a loop from an old copy of the DLL stops.
        internal static void StopAll()
        {
            s_Paused.Clear();
            s_LoopGeneration++;
        }

        private static async UniTaskVoid RunPauseLoop(int generation)
        {
            while (generation == s_LoopGeneration && s_Paused.Count > 0)
            {
                await UniTask.Delay(1000);
                await UniTask.SwitchToMainThread();
                if (generation != s_LoopGeneration)
                {
                    return;
                }
                foreach (var entry in new List<KeyValuePair<ulong, float>>(s_Paused))
                {
                    var player = PlayerTool.getPlayer(new CSteamID(entry.Key));
                    if (player == null)
                    {
                        s_Paused.Remove(entry.Key);
                        continue;
                    }
                    KillZombiesNear(player.transform.position, entry.Value);
                }
            }
        }

        private static int KillZombiesNear(Vector3 position, float radius)
        {
            if (ZombieManager.regions == null)
            {
                return 0;
            }
            var radiusSqr = radius * radius;
            var targets = new List<Zombie>();
            foreach (var region in ZombieManager.regions)
            {
                if (region?.zombies == null)
                {
                    continue;
                }
                foreach (var zombie in region.zombies)
                {
                    if (zombie != null
                        && !zombie.isDead
                        && (zombie.transform.position - position).sqrMagnitude <= radiusSqr)
                    {
                        targets.Add(zombie);
                    }
                }
            }
            var killed = 0;
            foreach (var zombie in targets)
            {
                DamageTool.damageZombie(DamageZombieParameters.makeInstakill(zombie), out _, out _);
                if (zombie.isDead)
                {
                    killed++;
                }
            }
            return killed;
        }
    }
}
