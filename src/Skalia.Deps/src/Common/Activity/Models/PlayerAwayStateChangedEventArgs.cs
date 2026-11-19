using System;

namespace SkaliaDeps
{
    public sealed class PlayerAwayStateChangedEventArgs : EventArgs
    {
        public PlayerAwayStateChangedEventArgs(ulong playerId, string playerName, bool isAway)
        {
            PlayerId = playerId;
            PlayerName = playerName;
            IsAway = isAway;
        }

        public ulong PlayerId { get; }

        public string PlayerName { get; }

        public bool IsAway { get; }
    }
}
