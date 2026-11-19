using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Users.Events;

namespace SkaliaDeps
{
    public sealed class ChatNamePrefixListener : IEventListener<UnturnedUserConnectingEvent>
    {
        private readonly string m_NamePrefix;

        public ChatNamePrefixListener(IConfiguration configuration)
        {
            m_NamePrefix = configuration["chat:namePrefix"] ?? string.Empty;
        }

        public Task HandleEventAsync(object? sender, UnturnedUserConnectingEvent @event)
        {
            if (string.IsNullOrEmpty(m_NamePrefix) ||
                @event.User.SteamPending.transportConnection?.GetType().FullName ==
                "SkaliaDeps.SkaliaBotTransportConnection")
            {
                return Task.CompletedTask;
            }

            string displayName = @event.User.DisplayName;
            if (!displayName.StartsWith(m_NamePrefix, StringComparison.Ordinal))
            {
                @event.User.SteamPending.playerID.characterName = m_NamePrefix + displayName;
            }

            return Task.CompletedTask;
        }
    }
}
