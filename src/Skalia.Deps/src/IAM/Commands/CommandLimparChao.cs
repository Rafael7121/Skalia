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
    [Command("clearg")]
    [CommandDescription("Remove todos os itens derrubados por jogadores no chão.")]
    public sealed class CommandLimparChao : UnturnedCommand
    {
        private readonly IGroundCleanupService m_GroundCleanupService;
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandLimparChao(
            IGroundCleanupService groundCleanupService,
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_GroundCleanupService = groundCleanupService;
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                await PrintAsync(FormatPrivate("messages:cleanup:ground_usage"));
                return;
            }

            await UniTask.SwitchToMainThread();
            var removedItems = m_GroundCleanupService.RemoveDroppedItems();
            Broadcast("messages:cleanup:ground_global", new
            {
                Count = removedItems
            });

            if (!(Context.Actor is UnturnedUser))
            {
                await PrintAsync(Format("messages:cleanup:ground_complete", new
                {
                    Count = removedItems
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
