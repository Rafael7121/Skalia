using System.Net;
using SDG.NetTransport;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// Local transport used by the server-side bot. There is no remote socket:
    /// the server owns both ends of this connection.
    /// </summary>
    internal sealed class SkaliaBotTransportConnection : ITransportConnection
    {
        private readonly IPAddress m_Address;
        private readonly string m_AddressText;
        private readonly ushort m_Port;
        private readonly ulong m_SteamId;

        public SkaliaBotTransportConnection(
            IPAddress address,
            ushort port,
            ulong steamId)
        {
            m_Address = address;
            m_AddressText = address.ToString();
            m_Port = port;
            m_SteamId = steamId;
        }

        public void CloseConnection()
        {
        }

        public bool Equals(ITransportConnection other)
        {
            return ReferenceEquals(this, other);
        }

        public bool TryGetIPv4Address(out uint address)
        {
            address = Parser.getUInt32FromIP(m_AddressText);
            return true;
        }

        public bool TryGetPort(out ushort port)
        {
            port = m_Port;
            return true;
        }

        public IPAddress GetAddress()
        {
            return m_Address;
        }

        public string GetAddressString(bool withPort)
        {
            return withPort ? $"{m_AddressText}:{m_Port}" : m_AddressText;
        }

        public void Send(byte[] buffer, long size, ENetReliability sendType)
        {
            // Keep the locally simulated client synchronized with the authoritative
            // server movement, matching the behavior used by the Dummy transport.
            var reader = NetMessages.GetInvokableReader();
            reader.SetBufferSegment(buffer, (int)size);
            reader.Reset();

            reader.ReadEnum(out EClientMessage clientMessage);
            if (clientMessage != EClientMessage.InvokeMethod)
            {
                return;
            }

            reader.ReadBits(NetReflection.clientMethodsBitCount, out var methodIndex);
            if (methodIndex >= NetReflection.clientMethodsLength)
            {
                return;
            }

            var method = NetReflection.clientMethods[(int)methodIndex];
            if (method.declaringType == typeof(PlayerInput)
                && method.debugName.Contains("ReceiveSimulateMispredictedInputs"))
            {
                // No separate client-side PlayerInput exists. Looping this callback
                // into the server object corrupts the boundary between prediction
                // state and authoritative state.
                SkaliaBotMovementDiagnostics.RecordCorrection();
            }
        }

        public bool TryGetSteamId(out ulong steamId)
        {
            // This is visual platform metadata only. The local transport still
            // performs no Steam login or account authentication.
            steamId = m_SteamId;
            return steamId != 0;
        }
    }
}
