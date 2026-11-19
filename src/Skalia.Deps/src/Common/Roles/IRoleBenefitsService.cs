using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface IRoleBenefitsService
    {
        Task<int> GetHomeLimitAsync(UnturnedPlayer player);

        Task<VaultDimensions> GetVaultDimensionsAsync(UnturnedPlayer player);
    }

    public readonly struct VaultDimensions
    {
        public VaultDimensions(byte width, byte height)
        {
            Width = width;
            Height = height;
        }

        public byte Width { get; }

        public byte Height { get; }
    }
}
