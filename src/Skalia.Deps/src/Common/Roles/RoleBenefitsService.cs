using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenMod.API.Ioc;
using OpenMod.API.Permissions;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Users;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class RoleBenefitsService : IRoleBenefitsService
    {
        private readonly IUnturnedUserDirectory m_UserDirectory;
        private readonly IPermissionRoleStore m_RoleStore;
        private readonly RoleBenefit m_DefaultBenefit;
        private readonly IReadOnlyDictionary<string, RoleBenefit> m_RoleBenefits;

        public RoleBenefitsService(
            IUnturnedUserDirectory userDirectory,
            IPermissionRoleStore roleStore,
            IConfiguration configuration)
        {
            m_UserDirectory = userDirectory;
            m_RoleStore = roleStore;
            m_DefaultBenefit = ReadBenefit(configuration.GetSection("roleBenefits:default"),
                new RoleBenefit(2, 6, 6));

            var benefits = new Dictionary<string, RoleBenefit>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in configuration.GetSection("roleBenefits:roles").GetChildren())
            {
                benefits[role.Key] = ReadBenefit(role, m_DefaultBenefit);
            }

            m_RoleBenefits = benefits;
        }

        public async Task<int> GetHomeLimitAsync(UnturnedPlayer player)
        {
            var benefit = await GetBenefitAsync(player);
            return benefit.HomeLimit;
        }

        public async Task<VaultDimensions> GetVaultDimensionsAsync(UnturnedPlayer player)
        {
            var benefit = await GetBenefitAsync(player);
            return new VaultDimensions(benefit.VaultWidth, benefit.VaultHeight);
        }

        private async Task<RoleBenefit> GetBenefitAsync(UnturnedPlayer player)
        {
            var user = m_UserDirectory.GetUser(player.Player);
            var roles = await m_RoleStore.GetRolesAsync(user, true);
            var benefit = m_DefaultBenefit;

            foreach (var role in roles.OrderBy(candidate => candidate.Priority))
            {
                if (m_RoleBenefits.TryGetValue(role.Id, out var configured))
                {
                    benefit = new RoleBenefit(
                        Math.Max(benefit.HomeLimit, configured.HomeLimit),
                        Math.Max(benefit.VaultWidth, configured.VaultWidth),
                        Math.Max(benefit.VaultHeight, configured.VaultHeight));
                }
            }

            return benefit;
        }

        private static RoleBenefit ReadBenefit(IConfiguration section, RoleBenefit fallback)
        {
            return new RoleBenefit(
                Clamp(section.GetValue("homes", fallback.HomeLimit), 1, 10),
                (byte)Clamp(section.GetValue("vaultWidth", fallback.VaultWidth), 1, 15),
                (byte)Clamp(section.GetValue("vaultHeight", fallback.VaultHeight), 1, 15));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private readonly struct RoleBenefit
        {
            public RoleBenefit(int homeLimit, byte vaultWidth, byte vaultHeight)
            {
                HomeLimit = homeLimit;
                VaultWidth = vaultWidth;
                VaultHeight = vaultHeight;
            }

            public int HomeLimit { get; }
            public byte VaultWidth { get; }
            public byte VaultHeight { get; }
        }
    }
}
