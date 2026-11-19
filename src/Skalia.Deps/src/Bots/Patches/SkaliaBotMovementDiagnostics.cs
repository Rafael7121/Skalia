using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    /// <summary>
    /// Temporary, read-only instrumentation for following the fake client's
    /// walking packet through the native server simulation.
    /// </summary>
    internal static class SkaliaBotMovementDiagnostics
    {
        internal readonly struct SimulationState
        {
            public SimulationState(
                bool active,
                DiagnosticContext context,
                PacketSample packet,
                Vector3 position,
                Vector3 velocity,
                bool grounded,
                bool controllerEnabled,
                EPlayerStance stance,
                float speed,
                int updateCount)
            {
                Active = active;
                Context = context;
                Packet = packet;
                Position = position;
                Velocity = velocity;
                Grounded = grounded;
                ControllerEnabled = controllerEnabled;
                Stance = stance;
                Speed = speed;
                UpdateCount = updateCount;
            }

            public bool Active { get; }
            internal DiagnosticContext Context { get; }
            public PacketSample Packet { get; }
            public Vector3 Position { get; }
            public Vector3 Velocity { get; }
            public bool Grounded { get; }
            public bool ControllerEnabled { get; }
            public EPlayerStance Stance { get; }
            public float Speed { get; }
            public int UpdateCount { get; }
        }

        internal readonly struct PacketSample
        {
            public PacketSample(
                WalkingPlayerInputPacket packet,
                uint clientFrame,
                uint serverFrameAtEnqueue,
                byte analog,
                Vector3 clientPosition,
                int queueCount)
            {
                Packet = packet;
                ClientFrame = clientFrame;
                ServerFrameAtEnqueue = serverFrameAtEnqueue;
                Analog = analog;
                ClientPosition = clientPosition;
                QueueCount = queueCount;
            }

            public WalkingPlayerInputPacket Packet { get; }
            public uint ClientFrame { get; }
            public uint ServerFrameAtEnqueue { get; }
            public byte Analog { get; }
            public Vector3 ClientPosition { get; }
            public int QueueCount { get; }
        }

        internal sealed class DiagnosticContext
        {
            public DiagnosticContext(Player player, ILogger logger)
            {
                Player = player;
                Logger = logger;
            }

            public Player Player { get; }
            public ILogger Logger { get; }
            public bool Enabled { get; set; } = true;
            public Queue<PacketSample> PendingPackets { get; } = new Queue<PacketSample>();
            public float NextLogTime { get; set; }
            public uint EnqueuedCount { get; set; }
            public uint ProcessedCount { get; set; }
            public uint MovedCount { get; set; }
            public uint CorrectionCount { get; set; }
        }

        private static readonly Dictionary<Player, DiagnosticContext> Contexts =
            new Dictionary<Player, DiagnosticContext>();

        internal static void Register(Player player, ILogger logger)
        {
            Contexts[player] = new DiagnosticContext(player, logger);
        }

        internal static void Unregister(Player player)
        {
            Contexts.Remove(player);
        }

        internal static void SetEnabled(Player player, bool enabled)
        {
            if (!Contexts.TryGetValue(player, out var context))
            {
                return;
            }

            context.Enabled = enabled;
            context.PendingPackets.Clear();
            if (enabled)
            {
                context.NextLogTime = 0f;
            }
        }

        internal static void ResetPending(Player player)
        {
            if (Contexts.TryGetValue(player, out var context))
            {
                context.PendingPackets.Clear();
            }
        }

        internal static void RecordEnqueued(
            Player player,
            WalkingPlayerInputPacket packet,
            int queueCount)
        {
            if (!Contexts.TryGetValue(player, out var context) || !context.Enabled)
            {
                return;
            }

            context.PendingPackets.Enqueue(new PacketSample(
                packet,
                packet.clientSimulationFrameNumber,
                player.input.simulation,
                packet.analog,
                packet.clientPosition,
                queueCount));
            context.EnqueuedCount++;
        }

        internal static SimulationState Begin(PlayerMovement movement)
        {
            var context = Contexts.Values.FirstOrDefault(value =>
                value.Enabled && ReferenceEquals(value.Player.movement, movement));
            if (context == null || context.PendingPackets.Count == 0)
            {
                return default;
            }

            var packet = context.PendingPackets.Dequeue();
            return new SimulationState(
                true,
                context,
                packet,
                context.Player.transform.position,
                movement.velocity,
                movement.isGrounded,
                movement.controller != null && movement.controller.enabled,
                context.Player.stance.stance,
                movement.speed,
                movement.updates?.Count ?? -1);
        }

        internal static void Complete(
            PlayerMovement movement,
            uint serverSimulationFrame,
            int inputX,
            int inputY,
            in SimulationState before)
        {
            if (!before.Active || !before.Context.Enabled)
            {
                return;
            }

            var context = before.Context;
            var player = context.Player;
            context.ProcessedCount++;
            var afterPosition = player.transform.position;
            var displacement = afterPosition - before.Position;
            if (displacement.sqrMagnitude > 0.000001f)
            {
                context.MovedCount++;
            }

            // There is no separate Unity client to predict and acknowledge this
            // movement. Complete the fake client's acknowledgement with the exact
            // result of the native authoritative simulation. PlayerInput compares
            // this same packet immediately after this method returns.
            before.Packet.Packet.clientPosition = afterPosition;

            var now = Time.realtimeSinceStartup;
            if (now < context.NextLogTime)
            {
                return;
            }

            context.NextLogTime = now + 1f;
            var predictionError = before.Packet.ClientPosition - afterPosition;
            context.Logger.LogDebug(
                "Diagnostico movimento nativo: cliente frame {ClientFrame}; servidor ao enfileirar/processar "
                + "{ServerFrameAtEnqueue}/{ServerFrame}; analog 0x{Analog:X2} -> entrada ({InputX},{InputY}); "
                + "posicao {Before}->{After}; deslocamento {Displacement:0.####}m; previsao cliente {ClientPosition}; "
                + "erro {PredictionError:0.####}m; velocidade {VelocityBefore}->{VelocityAfter}; velocidade-base "
                + "{Speed:0.##}; grounded {GroundedBefore}->{GroundedAfter}; controller {Controller}; postura "
                + "{StanceBefore}->{StanceAfter}; estados {UpdatesBefore}->{UpdatesAfter}; totais "
                + "enfileirados/processados/moveram/correcoes {Enqueued}/{Processed}/{Moved}/{Corrections}.",
                before.Packet.ClientFrame,
                before.Packet.ServerFrameAtEnqueue,
                serverSimulationFrame,
                before.Packet.Analog,
                inputX,
                inputY,
                FormatPosition(before.Position),
                FormatPosition(afterPosition),
                displacement.magnitude,
                FormatPosition(before.Packet.ClientPosition),
                predictionError.magnitude,
                FormatPosition(before.Velocity),
                FormatPosition(movement.velocity),
                before.Speed,
                before.Grounded,
                movement.isGrounded,
                before.ControllerEnabled,
                before.Stance,
                player.stance.stance,
                before.UpdateCount,
                movement.updates?.Count ?? -1,
                context.EnqueuedCount,
                context.ProcessedCount,
                context.MovedCount,
                context.CorrectionCount);
        }

        internal static void RecordCorrection()
        {
            foreach (var context in Contexts.Values)
            {
                if (context.Enabled)
                {
                    context.CorrectionCount++;
                }
            }
        }

        private static string FormatPosition(Vector3 value)
        {
            return $"({value.x:0.0}, {value.y:0.0}, {value.z:0.0})";
        }
    }

    [HarmonyPatch(
        typeof(PlayerMovement),
        nameof(PlayerMovement.simulate),
        new[]
        {
            typeof(uint), typeof(int), typeof(int), typeof(int), typeof(float),
            typeof(float), typeof(bool), typeof(bool), typeof(float)
        })]
    internal static class SkaliaBotNativeWalkingSimulationPatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerMovement __instance, out SkaliaBotMovementDiagnostics.SimulationState __state)
        {
            __state = SkaliaBotMovementDiagnostics.Begin(__instance);
        }

        [HarmonyPostfix]
        private static void Postfix(
            PlayerMovement __instance,
            uint simulation,
            int input_x,
            int input_y,
            SkaliaBotMovementDiagnostics.SimulationState __state)
        {
            SkaliaBotMovementDiagnostics.Complete(
                __instance,
                simulation,
                input_x,
                input_y,
                in __state);
        }
    }
}
