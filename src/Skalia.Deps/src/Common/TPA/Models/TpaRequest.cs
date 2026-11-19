using System;
using System.Threading;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    internal enum TpaRequestState
    {
        Pending,
        WarmingUp
    }

    internal sealed class TpaRequest
    {
        public TpaRequest(
            UnturnedPlayer sender,
            UnturnedPlayer receiver,
            CancellationTokenSource delayCancellation)
        {
            Id = Guid.NewGuid();
            Sender = sender;
            Receiver = receiver;
            SenderId = sender.SteamId.m_SteamID;
            ReceiverId = receiver.SteamId.m_SteamID;
            DelayCancellation = delayCancellation;
        }

        public Guid Id { get; }

        public UnturnedPlayer Sender { get; }

        public UnturnedPlayer Receiver { get; }

        public ulong SenderId { get; }

        public ulong ReceiverId { get; }

        public TpaRequestState State { get; set; }

        public CancellationTokenSource DelayCancellation { get; set; }
    }
}
