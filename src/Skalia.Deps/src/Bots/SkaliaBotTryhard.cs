using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Framework.Devkit;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2t (doc 11): the tryhard bot. the tester, after the 0.2.2 test: "ele
    // ainda ta muito fraco". The always-on fixes (light, radiation, acid,
    // bandages, ammo, strip, attachments, loot pile) and the tryhard-only
    // rules (serverBot:behavior:tryhard: where to go, the Mega, no stops,
    // cover) live here; the hooks in the other files only call in.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private static bool Tryhard => s_Settings.Tryhard;

        // ---- 11.10 headlamp and night vision ----
        // ItemGlassesAsset.getState returns { 1 } for any glasses with a
        // vision mode: they are born switched ON and nothing turned them off.
        // Tadeu walked the 0.2.2 night with a Headlamp lit. A headlamp gives
        // the position away: always off. Night vision: on at night only.
        private float m_NextVisionCheckAt;

        private void UpdateVisionLight(float now)
        {
            if (now < m_NextVisionCheckAt)
            {
                return;
            }
            m_NextVisionCheckAt = now + 1f;
            SwitchOffGunLight();
            var clothing = m_Player.clothing;
            var asset = clothing.glassesAsset;
            if (asset == null
                || asset.vision == ELightingVision.NONE
                || clothing.glassesState == null
                || clothing.glassesState.Length != 1
                || !m_Player.equipment.hasVision)
            {
                return;
            }
            var wantOn = asset.vision != ELightingVision.HEADLAMP && !LightingManager.isDaytime;
            var isOn = clothing.glassesState[0] != 0;
            if (wantOn == isOn)
            {
                return;
            }
            m_Player.equipment.ReceiveToggleVisionRequest();
            BotLog(LogLevel.Information,
                "Luz: {Item} {State} ({Why}).",
                asset.itemName,
                wantOn ? "ligado" : "desligado",
                asset.vision == ELightingVision.HEADLAMP
                    ? "headlamp revela a posicao, fica sempre apagado"
                    : wantOn ? "visao noturna so de noite" : "de dia a visao noturna fica desligada");
        }

        // ---- 11.15 radiation ----
        // The plugin knew nothing of deadzones. Washington has one (the
        // alien crash, 69m radius): a destination, a loot point or an item
        // inside one is refused.
        private static bool IsInRadiation(Vector3 point, float margin = 0f)
        {
            if (!Level.isLoaded)
            {
                return false;
            }
            try
            {
                var manager = VolumeManager<DeadzoneVolume, DeadzoneVolumeManager>.Get();
                if (manager == null)
                {
                    return false;
                }
                if (manager.IsNavmeshCenterInsideAnyVolume(point))
                {
                    return true;
                }
                if (margin > 0f)
                {
                    return manager.IsNavmeshCenterInsideAnyVolume(point + Vector3.forward * margin)
                        || manager.IsNavmeshCenterInsideAnyVolume(point + Vector3.back * margin)
                        || manager.IsNavmeshCenterInsideAnyVolume(point + Vector3.left * margin)
                        || manager.IsNavmeshCenterInsideAnyVolume(point + Vector3.right * margin);
                }
            }
            catch (System.Exception)
            {
                // A map without the devkit volumes.
            }
            return false;
        }

        // ---- 11.11 acid puddles ----
        // Tadeu, Clearwater 00:55:36-00:55:49 of the 0.2.2 test: out of the
        // puddle 5 times in 13s, health 100 -> 50. Only loot items were
        // checked against acid; the patrol sent the bot back to the same
        // loot point by the puddle. The game's own bubbles (position and
        // radius) are read now and count as a hazard everywhere
        // IsHazardousPosition is asked: patrol point, loot, door, flee and
        // combat step.
        private static FieldInfo? s_TemperatureBubblesField;
        private static bool s_TemperatureBubblesFieldTried;
        private static List<TemperatureBubble>? s_TemperatureBubbles;
        private static int s_TemperatureBubblesFrame = -1;

        private static List<TemperatureBubble>? GetTemperatureBubbles()
        {
            if (s_TemperatureBubblesFrame == Time.frameCount)
            {
                return s_TemperatureBubbles;
            }
            s_TemperatureBubblesFrame = Time.frameCount;
            s_TemperatureBubbles = null;
            if (!Level.isLoaded)
            {
                return null;
            }
            if (!s_TemperatureBubblesFieldTried)
            {
                s_TemperatureBubblesFieldTried = true;
                s_TemperatureBubblesField = typeof(TemperatureManager).GetField(
                    "bubbles",
                    BindingFlags.NonPublic | BindingFlags.Static);
            }
            try
            {
                s_TemperatureBubbles = s_TemperatureBubblesField?.GetValue(null) as List<TemperatureBubble>;
            }
            catch (System.Exception)
            {
                s_TemperatureBubbles = null;
            }
            return s_TemperatureBubbles;
        }

        private static bool TryGetAcidBubbleNear(Vector3 point, float margin, out Vector3 center, out float radius)
        {
            center = Vector3.zero;
            radius = 0f;
            var bubbles = GetTemperatureBubbles();
            if (bubbles == null)
            {
                return false;
            }
            for (var index = 0; index < bubbles.Count; index++)
            {
                var bubble = bubbles[index];
                if (bubble == null
                    || bubble.origin == null
                    || bubble.temperature != EPlayerTemperature.ACID)
                {
                    continue;
                }
                var bubbleRadius = Mathf.Sqrt(bubble.sqrRadius);
                var bubbleCenter = bubble.origin.position;
                if (Mathf.Abs(bubbleCenter.y - point.y) > bubbleRadius + 2.5f
                    || (bubbleCenter - point).GetHorizontalMagnitude() > bubbleRadius + margin)
                {
                    continue;
                }
                center = bubbleCenter;
                radius = bubbleRadius;
                return true;
            }
            return false;
        }

        private static bool IsNearAcidBubble(Vector3 point, float margin)
        {
            return TryGetAcidBubbleNear(point, margin, out _, out _);
        }

        // Outside a puddle and about to walk into it: go round. The check
        // that refuses points only helps when a point is being chosen; a
        // route already under way crossed the puddle anyway.
        private Vector3 m_AcidGuardLastPosition;
        private float m_NextAcidGuardAt;
        private float m_NextAcidGuardLogAt;

        private void UpdateAcidAheadGuard(Vector3 position, float now)
        {
            var move = position - m_AcidGuardLastPosition;
            m_AcidGuardLastPosition = position;
            move.y = 0f;
            if (now < m_NextAcidGuardAt
                || move.sqrMagnitude < 0.0004f
                || move.sqrMagnitude > 9f
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || m_ObjectiveType == ObjectiveType.Ladder
                || m_ObjectiveType == ObjectiveType.None)
            {
                return;
            }
            // Inside already: stepping out is TryStepOutOfAcid's job.
            if (IsNearAcidBubble(position, 0.2f))
            {
                return;
            }
            var direction = move.normalized;
            if (!TryGetAcidBubbleNear(position + direction * 1.5f, 0.4f, out var center, out var radius)
                && !TryGetAcidBubbleNear(position + direction * 3f, 0.4f, out center, out radius))
            {
                return;
            }
            m_NextAcidGuardAt = now + 1f;
            var toCenter = center - position;
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude < 0.01f)
            {
                return;
            }
            var side = new Vector3(-toCenter.z, 0f, toCenter.x).normalized;
            var found = false;
            var best = position;
            var bestScore = float.NegativeInfinity;
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var beside = center + side * (sign * (radius + 3f));
                beside.y = position.y;
                if (!SkaliaBotUnityNavmesh.TrySnap(beside, 2f, out var point)
                    || Mathf.Abs(point.y - position.y) > 2f
                    || !SkaliaBotUnityNavmesh.IsBodyClear(position, point)
                    || IsHazardousPosition(point, now, 0.5f))
                {
                    continue;
                }
                // The forced movement stops 4m short of a flee point.
                var leg = point - position;
                var beyond = point + leg.normalized * 4f;
                if (SkaliaBotUnityNavmesh.TrySnap(beyond, 2f, out var farther)
                    && Mathf.Abs(farther.y - position.y) <= 2f
                    && !IsHazardousPosition(farther, now, 0.5f))
                {
                    point = farther;
                }
                var score = -(point - m_ObjectivePosition).GetHorizontalMagnitude();
                if (score > bestScore)
                {
                    bestScore = score;
                    best = point;
                    found = true;
                }
            }
            if (!found)
            {
                if (now >= m_NextAcidGuardLogAt)
                {
                    m_NextAcidGuardLogAt = now + 10f;
                    BotLog(LogLevel.Information,
                        "Poca de acido a frente em {Center} (raio {Radius:0.0}m) sem ponto livre para contornar; seguindo como estava.",
                        FormatPosition(center),
                        radius);
                }
                return;
            }
            if (m_ObjectiveType == ObjectiveType.Loot)
            {
                IgnoreLoot(m_LootInstanceId, now + AcidLootIgnoreSeconds);
            }
            m_StatWindowAcidSteps++;
            BotLog(LogLevel.Information,
                "Poca de acido a frente em {Center} (raio {Radius:0.0}m, objetivo {Objective}); contornando por {Destination} em vez de pisar nela.",
                FormatPosition(center),
                radius,
                m_ObjectiveType,
                FormatPosition(best));
            m_FleeingFromPlayerName = AcidFleeName;
            SetObjective(ObjectiveType.Flee, best, now + 3f);
        }
    }
}
