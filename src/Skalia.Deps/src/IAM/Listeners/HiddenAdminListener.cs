using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Server.Events;
using SDG.Unturned;
using Steamworks;

namespace SkaliaDeps
{
    public sealed class HiddenAdminListener : IEventListener<UnturnedServerHostedEvent>
    {
        private readonly IConfiguration m_Configuration;
        private readonly ILogger<HiddenAdminListener> m_Logger;

        public HiddenAdminListener(
            IConfiguration configuration,
            ILogger<HiddenAdminListener> logger)
        {
            m_Configuration = configuration;
            m_Logger = logger;
        }

        public Task HandleEventAsync(object? sender, UnturnedServerHostedEvent @event)
        {
            foreach (var entry in m_Configuration
                         .GetSection("administration:hiddenOpenModAdmins")
                         .GetChildren())
            {
                if (!ulong.TryParse(entry.Value, out var steamIdValue))
                {
                    m_Logger.LogWarning(
                        "SteamID invalido em administration:hiddenOpenModAdmins: {SteamId}.",
                        entry.Value);
                    continue;
                }

                var steamId = new CSteamID(steamIdValue);
                if (!SteamAdminlist.checkAdmin(steamId))
                {
                    continue;
                }

                SteamAdminlist.unadmin(steamId);
                m_Logger.LogInformation(
                    "Admin nativo removido de {SteamId}; as permissoes administrativas do OpenMod foram preservadas.",
                    steamIdValue);
            }

            return Task.CompletedTask;
        }
    }
}
