using System;
using OpenMod.API.Ioc;
using SDG.Unturned;

namespace SkaliaDeps
{
    [Service]
    public interface IPlayerActivityService
    {
        event EventHandler<PlayerAwayStateChangedEventArgs>? AwayStateChanged;

        void Start();

        void Stop();

        void RegisterPlayer(Player player);

        void UnregisterPlayer(ulong playerId);

        void MarkActivity(ulong playerId);

        bool IsAway(ulong playerId);
    }
}
