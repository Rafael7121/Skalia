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
    [Command("clearv")]
    [CommandDescription("Remove todos os veículos do mapa.")]
    public sealed class CommandLimparVeiculos : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandLimparVeiculos(
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                await PrintAsync(FormatPrivate("messages:cleanup:vehicles_usage"));
                return;
            }

            await UniTask.SwitchToMainThread();
            var removedVehicles = VehicleManager.vehicles.Count;
            VehicleManager.askVehicleDestroyAll();
            Broadcast("messages:cleanup:vehicles_global", new
            {
                Count = removedVehicles
            });

            if (!(Context.Actor is UnturnedUser))
            {
                await PrintAsync(Format("messages:cleanup:vehicles_complete", new
                {
                    Count = removedVehicles
                }));
            }
        }

        private void Broadcast(string key, object arguments)
        {
            ChatManager.serverSendMessage(
                Format(key, arguments),
                ServerChatPalette.BroadcastText,
                useRichTextFormatting: true,
                iconURL: Provider.configData.Browser.Icon);
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            return $"{m_StringLocalizer["prefix"].Value} {message}";
        }

        private string FormatPrivate(string key, object? arguments = null)
        {
            return PrivateMessageFormatter.AppendNoticeIfPlayer(
                m_StringLocalizer,
                Format(key, arguments),
                Context.Actor);
        }
    }
}
