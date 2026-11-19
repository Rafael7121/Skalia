using System;
using System.Collections.Generic;
using System.Threading;

namespace SkaliaDeps
{
    internal sealed class CallVoteSession
    {
        public CallVoteSession(CallVoteType type, TimeSpan duration)
        {
            Type = type;
            Voters = new HashSet<ulong>();
            AnnouncedPlayers = new HashSet<ulong>();
            Cancellation = new CancellationTokenSource();
            ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        }

        public CallVoteType Type { get; }

        public HashSet<ulong> Voters { get; }

        public HashSet<ulong> AnnouncedPlayers { get; }

        public CancellationTokenSource Cancellation { get; }

        public DateTimeOffset ExpiresAt { get; }

        public void Cancel()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }
}
