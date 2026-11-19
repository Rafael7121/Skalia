using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps.Commands
{
    [Command("navid")]
    [CommandDescription("Mostra o navmesh e a regiao de spawn na posicao atual.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandNavId : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandNavId(
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            var user = (UnturnedUser)Context.Actor;

            await UniTask.SwitchToMainThread();
            Vector3 position = user.Player.Player.transform.position;
            bool foundNavigation = LevelNavigation.tryGetNavigation(
                position,
                out byte navigation);
            bool foundSpawnRegion = LevelNavigation.tryGetBounds(
                position,
                out byte spawnRegion);

            if (!foundNavigation && !foundSpawnRegion)
            {
                await PrintAsync(Format(
                    "messages:administration:navid_not_found",
                    new
                    {
                        Map = Provider.map,
                        X = Mathf.RoundToInt(position.x),
                        Y = Mathf.RoundToInt(position.y),
                        Z = Mathf.RoundToInt(position.z)
                    }));
                return;
            }

            int spawnpointCount = 0;
            int maxZombies = 0;
            if (foundSpawnRegion)
            {
                if (LevelZombies.zombies != null
                    && spawnRegion < LevelZombies.zombies.Length)
                {
                    spawnpointCount =
                        LevelZombies.zombies[spawnRegion].Count;
                }

                if (LevelNavigation.flagData != null
                    && spawnRegion < LevelNavigation.flagData.Count)
                {
                    maxZombies =
                        LevelNavigation.flagData[spawnRegion].maxZombies;
                }
            }

            await PrintAsync(Format(
                "messages:administration:navid_result",
                new
                {
                    Map = Provider.map,
                    Navigation = foundNavigation
                        ? navigation.ToString()
                        : "-",
                    SpawnRegion = foundSpawnRegion
                        ? spawnRegion.ToString()
                        : "-",
                    X = Mathf.RoundToInt(position.x),
                    Y = Mathf.RoundToInt(position.y),
                    Z = Mathf.RoundToInt(position.z),
                    Spawnpoints = spawnpointCount,
                    MaxZombies = maxZombies
                }));
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            return PrivateMessageFormatter.AppendNotice(
                m_StringLocalizer,
                $"{m_StringLocalizer["prefix"].Value} {message}");
        }
    }
}
