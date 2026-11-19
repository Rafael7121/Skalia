using System;
using System.Linq;
using System.Reflection;
using OpenMod.Unturned.Players;
using SDG.Unturned;

namespace SkaliaDeps
{
    internal static class VehiclePassengerTeleporter
    {
        private const byte NoSeat = byte.MaxValue;

        private static readonly FieldInfo? SendEnterVehicleField = typeof(VehicleManager).GetField(
            "SendEnterVehicle",
            BindingFlags.NonPublic | BindingFlags.Static);

        public static InteractableVehicle? GetVehicle(UnturnedPlayer player)
        {
            return player.Player.movement.getVehicle();
        }

        public static bool TryGetAvailableSeat(InteractableVehicle vehicle, out byte seat)
        {
            seat = NoSeat;
            if (vehicle == null || vehicle.isExploded || !vehicle.isExitable)
            {
                return false;
            }

            var passengers = vehicle.passengers;
            if (passengers == null || passengers.Length == 0)
            {
                return false;
            }

            // Prioriza o banco do motorista e depois procura entre os passageiros.
            for (var index = 0; index < passengers.Length && index < byte.MaxValue; index++)
            {
                var passenger = passengers[index];
                if (passenger != null && passenger.player == null)
                {
                    seat = (byte)index;
                    return true;
                }
            }

            return false;
        }

        public static bool TryPlaceInAvailableSeat(
            UnturnedPlayer player,
            InteractableVehicle vehicle,
            byte seat)
        {
            if (!IsSeatStillAvailable(vehicle, seat))
            {
                return false;
            }

            var movement = player.Player.movement;
            var currentVehicle = movement.getVehicle();
            if (currentVehicle == vehicle)
            {
                return true;
            }

            var dispatcher = SendEnterVehicleField?.GetValue(null);
            if (dispatcher == null)
            {
                throw new InvalidOperationException("O dispatcher SendEnterVehicle do Unturned não está disponível.");
            }

            var connections = Provider.GatherRemoteClientConnections();
            var invokeAndLoopback = dispatcher.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == "InvokeAndLoopback")
                .Where(method => method.GetParameters().Length == 5)
                .FirstOrDefault(method =>
                    method.GetParameters()[1].ParameterType.IsAssignableFrom(connections.GetType()));

            if (invokeAndLoopback == null)
            {
                throw new MissingMethodException(
                    dispatcher.GetType().FullName,
                    "InvokeAndLoopback");
            }

            if (currentVehicle != null && !movement.forceRemoveFromVehicle())
            {
                return false;
            }

            // Revalida depois de remover o jogador do veículo anterior. Toda esta
            // operação ocorre na thread principal, mas o loopback ainda pode processar
            // uma atualização pendente de assentos.
            if (!IsSeatStillAvailable(vehicle, seat))
            {
                return false;
            }

            var reliabilityType = invokeAndLoopback.GetParameters()[0].ParameterType;
            var reliable = Enum.ToObject(reliabilityType, 0);
            invokeAndLoopback.Invoke(dispatcher, new object[]
            {
                reliable,
                connections,
                vehicle.instanceID,
                seat,
                player.SteamId
            });

            return true;
        }

        private static bool IsSeatStillAvailable(InteractableVehicle vehicle, byte seat)
        {
            if (vehicle == null || vehicle.isExploded || !vehicle.isExitable)
            {
                return false;
            }

            var passengers = vehicle.passengers;
            return passengers != null &&
                   seat < passengers.Length &&
                   passengers[seat] != null &&
                   passengers[seat].player == null;
        }
    }
}
