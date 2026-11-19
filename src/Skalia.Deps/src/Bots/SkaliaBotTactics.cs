using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Framework.Landscapes;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2 tactical awareness (doc, phase table): high points over an area,
    // a short look from one before going in, common zombies left behind on
    // the way like the players do, and the shortcuts of the mesh (a jump up
    // a ledge, a low passage crouched; SkaliaBotShortcutLinks).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- high points over an area ----
        // Rings round the area's centre; on each, the top surface (a roof, a
        // rock, a hill) and the ground under it, both on the mesh. A spot
        // must see the centre or most of the area. The list depends only on
        // the map, so it is kept per area for every bot; which spot a bot
        // takes depends on where it comes from.
        private sealed class AreaVantage
        {
            public Vector3 Spot;
            public float Height;
            public int Visible;
        }

        private static readonly Dictionary<string, List<AreaVantage>> s_AreaVantages =
            new Dictionary<string, List<AreaVantage>>();
        private static readonly float[] s_VantageRings = { 22f, 32f, 45f };
        private const int VantageDirections = 12;
        private const float VantageEyeMeters = 1.3f;
        private const float VantageMinimumHeight = 1.5f;
        private const int VantageGoodView = 4;
        private const int MaxVantageAreas = 400;

        private static List<AreaVantage> GetAreaVantages(string areaKey, Vector3 center)
        {
            if (s_AreaVantages.TryGetValue(areaKey, out var cached))
            {
                return cached;
            }
            if (s_AreaVantages.Count >= MaxVantageAreas)
            {
                s_AreaVantages.Clear();
            }
            var list = new List<AreaVantage>();
            s_AreaVantages[areaKey] = list;
            if (!SkaliaBotUnityNavmesh.MapFilled)
            {
                return list;
            }
            var mask = RayMasks.BLOCK_COLLISION | RayMasks.GROUND | RayMasks.GROUND2;
            var targets = new Vector3[5];
            targets[0] = center + Vector3.up * 1f;
            for (var i = 0; i < 4; i++)
            {
                targets[i + 1] = center + Quaternion.Euler(0f, i * 90f + 45f, 0f) * Vector3.forward * 12f + Vector3.up * 1f;
            }
            foreach (var ring in s_VantageRings)
            {
                for (var step = 0; step < VantageDirections; step++)
                {
                    var direction = Quaternion.Euler(0f, step * (360f / VantageDirections), 0f) * Vector3.forward;
                    var column = center + direction * ring;
                    // The top surface: a roof or a rock; and the ground
                    // under a roof.
                    var hasTop = Physics.Raycast(
                        new Vector3(column.x, center.y + 40f, column.z),
                        Vector3.down,
                        out var top,
                        80f,
                        mask,
                        QueryTriggerInteraction.Ignore);
                    if (hasTop)
                    {
                        TryAddVantage(list, top.point, 1f, center, targets);
                    }
                    if (Landscape.getWorldHeight(column, out var groundY)
                        && (!hasTop || top.point.y - groundY > 1.5f))
                    {
                        TryAddVantage(list, new Vector3(column.x, groundY, column.z), 1.5f, center, targets);
                    }
                }
            }
            return list;
        }

        private static void TryAddVantage(List<AreaVantage> list, Vector3 point, float snap, Vector3 center, Vector3[] targets)
        {
            if (!SkaliaBotUnityNavmesh.TrySnap(point, snap, out var spot)
                || Mathf.Abs(spot.y - point.y) > 1f
                || WaterUtility.isPointUnderwater(spot + Vector3.up * 0.5f))
            {
                return;
            }
            foreach (var other in list)
            {
                if ((other.Spot - spot).sqrMagnitude < 9f)
                {
                    return;
                }
            }
            var eye = spot + Vector3.up * VantageEyeMeters;
            var visible = 0;
            var seesCenter = false;
            for (var i = 0; i < targets.Length; i++)
            {
                if (!Physics.Linecast(eye, targets[i], RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    visible++;
                    seesCenter |= i == 0;
                }
            }
            var height = spot.y - center.y;
            if (!(seesCenter || visible >= 3)
                || (height < VantageMinimumHeight && visible < VantageGoodView))
            {
                return;
            }
            list.Add(new AreaVantage { Spot = spot, Height = height, Visible = visible });
        }

        // ---- observe before entering ----
        // The players stop at the edge or up on something, crouched, look
        // the place over and only then go in; they skip it when it is full.
        // Tryhard: a short look (3.5-6s), and a gun goes in whatever it saw.
        private const float OverlookMinimumAreaMeters = 30f;
        private const float OverlookMaxDetourMeters = 35f;
        private const float OverlookMaxSpotMeters = 200f;
        private const float OverlookArriveMeters = 2f;
        private const float OverlookSeenMeters = 30f;
        private const int OverlookCrowdZombies = 6;
        private const float OverlookSkipSeconds = 300f;
        private const float OverlookRememberSeconds = 1200f;
        private const int OverlookMaxResets = 3;

        // 0 none, 1 going to the spot, 2 watching.
        private int m_OverlookPhase;
        private Vector3 m_OverlookSpot;
        private Vector3 m_OverlookCenter;
        private string m_OverlookAreaKey = string.Empty;
        private string m_OverlookAreaName = string.Empty;
        private float m_OverlookStartedAt;
        private float m_OverlookUntil;
        private float m_OverlookWatchUntil;
        private float m_OverlookNextLookAt;
        private float m_OverlookLookYaw;
        private float m_OverlookLookPitch;
        private int m_OverlookResets;
        private int m_OverlookSeenMax;
        private int m_OverlookSpecialSeen;
        private readonly Dictionary<string, float> m_OverlookedAreas = new Dictionary<string, float>();
        private int m_StatWindowOverlooks;
        private int m_StatWindowOverlookSkips;

        private bool IsOverlookWatching(float now)
        {
            return m_OverlookPhase == 2
                && m_ObjectiveType == ObjectiveType.None
                && now < m_OverlookWatchUntil;
        }

        private void ResetTacticsForLife()
        {
            m_OverlookPhase = 0;
            m_OverlookResets = 0;
            ResetShortcutLeg();
        }

        // Called where an area was just accepted and the Explore objective
        // set to its centre.
        private void TryStartAreaOverlook(Vector3 position, float now, Vector3 center, string areaKey, string areaName)
        {
            m_OverlookPhase = 0;
            // 0.2.2t: the tryhard looks as it arrives (34s lost at Olympia).
            if (!s_Settings.AreaOverlook
                || Tryhard
                || string.IsNullOrEmpty(areaKey)
                || areaName == "exploracao local"
                || !SkaliaBotUnityNavmesh.MapFilled
                || m_Player.stance.stance == EPlayerStance.SWIM
                || IsLowHealth()
                || (center - position).GetHorizontalMagnitude() < OverlookMinimumAreaMeters)
            {
                return;
            }
            if (m_OverlookedAreas.TryGetValue(areaKey, out var until) && now < until)
            {
                return;
            }
            if (m_OverlookedAreas.Count > 200)
            {
                m_OverlookedAreas.Clear();
            }
            var direct = (center - position).GetHorizontalMagnitude();
            var candidates = new List<KeyValuePair<float, Vector3>>();
            foreach (var vantage in GetAreaVantages(areaKey, center))
            {
                var toSpot = (vantage.Spot - position).GetHorizontalMagnitude();
                var detour = toSpot + (center - vantage.Spot).GetHorizontalMagnitude() - direct;
                if (detour > OverlookMaxDetourMeters
                    || toSpot > OverlookMaxSpotMeters
                    || toSpot < 4f
                    || IsHazardousPosition(vantage.Spot, now, 3f)
                    || IsInHordeMemory(vantage.Spot, HordeAvoidMeters))
                {
                    continue;
                }
                var score = Mathf.Clamp(vantage.Height, 0f, 15f)
                    + vantage.Visible * 1.5f
                    - detour / 10f;
                candidates.Add(new KeyValuePair<float, Vector3>(score, vantage.Spot));
            }
            if (candidates.Count == 0)
            {
                return;
            }
            candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (var i = 0; i < candidates.Count && i < 3; i++)
            {
                var spot = candidates[i].Value;
                if (!SkaliaBotUnityNavmesh.IsReachable(position, spot))
                {
                    continue;
                }
                m_OverlookedAreas[areaKey] = now + OverlookRememberSeconds;
                m_OverlookPhase = 1;
                m_OverlookSpot = spot;
                m_OverlookCenter = center;
                m_OverlookAreaKey = areaKey;
                m_OverlookAreaName = areaName;
                m_OverlookStartedAt = now;
                m_OverlookResets = 0;
                m_OverlookUntil = now + 25f + (spot - position).GetHorizontalMagnitude() / 3f;
                m_OverlookSeenMax = 0;
                m_OverlookSpecialSeen = 0;
                SetObjective(ObjectiveType.Explore, spot, m_OverlookUntil + 5f);
                BotLog(LogLevel.Information,
                    "Antes de entrar em {Area}: indo ao ponto alto {Spot} ({Height:0.#}m acima do centro, a {Distance:0}m do centro) para olhar a area.",
                    areaName,
                    FormatPosition(spot),
                    spot.y - center.y,
                    (center - spot).GetHorizontalMagnitude());
                return;
            }
        }

        // First thing in the exploration behavior. True while the overlook
        // owns the bot.
        private bool TickAreaOverlook(Vector3 position, float now)
        {
            if (m_OverlookPhase == 0)
            {
                return false;
            }
            if (!m_HasInterestArea
                || m_InterestAreaKey != m_OverlookAreaKey
                || m_ForceLocalExplorationOnNextSelection)
            {
                m_OverlookPhase = 0;
                return false;
            }
            if (m_OverlookPhase == 1)
            {
                var headingThere = m_ObjectiveType == ObjectiveType.Explore
                    && (m_ObjectivePosition - m_OverlookSpot).GetHorizontalMagnitude() < 1f;
                if (!headingThere && m_ObjectiveType != ObjectiveType.None)
                {
                    // Something else set a task of its own: the look is off.
                    m_OverlookPhase = 0;
                    return false;
                }
                if (now >= m_OverlookUntil)
                {
                    EnterOverlookedArea(position, now, "nao chegou ao ponto a tempo");
                    return true;
                }
                if (!headingThere)
                {
                    // A fight or an item took the objective; back on the way,
                    // a few times at most (a route that keeps failing ends it).
                    if (++m_OverlookResets > OverlookMaxResets)
                    {
                        EnterOverlookedArea(position, now, "caminho ao ponto interrompido demais");
                        return true;
                    }
                    SetObjective(ObjectiveType.Explore, m_OverlookSpot, m_OverlookUntil + 5f);
                    return true;
                }
                var offset = m_OverlookSpot - position;
                if (offset.GetHorizontalMagnitude() > OverlookArriveMeters || Mathf.Abs(offset.y) > 1.5f)
                {
                    return true;
                }
                ClearObjective();
                m_OverlookPhase = 2;
                m_OverlookWatchUntil = now + Random.Range(3.5f, 6f);
                m_OverlookUntil = m_OverlookWatchUntil + 20f;
                m_OverlookNextLookAt = 0f;
                m_StatWindowOverlooks++;
                return true;
            }
            // Watching.
            if (m_ObjectiveType != ObjectiveType.None)
            {
                // A fight or an item came first; what was seen still counts.
                m_OverlookPhase = 0;
                return false;
            }
            CountOverlookZombies();
            if (now < m_OverlookWatchUntil && now < m_OverlookUntil)
            {
                return true;
            }
            if (m_OverlookSeenMax >= OverlookCrowdZombies && !HasLoadedRangedWeapon())
            {
                m_OverlookPhase = 0;
                m_StatWindowOverlookSkips++;
                m_ExplorationDestinationBlacklist[m_OverlookAreaKey] = now + OverlookSkipSeconds;
                RememberHordeSpot(m_OverlookCenter, m_OverlookSeenMax, now);
                if (s_AreaClaims.TryGetValue(m_OverlookAreaKey, out var claim) && claim.Key == m_Player)
                {
                    s_AreaClaims.Remove(m_OverlookAreaKey);
                }
                m_HasInterestArea = false;
                m_AreaLootPoints.Clear();
                m_PatrolTargetSet = false;
                m_NextExplorationSelectionTime = 0f;
                BotLog(LogLevel.Information,
                    "Olhou {Area} de cima: {Count} zumbis perto do centro e sem arma de fogo; deixando a area por {Seconds:0}s.",
                    m_OverlookAreaName,
                    m_OverlookSeenMax,
                    OverlookSkipSeconds);
                return false;
            }
            EnterOverlookedArea(position, now, "olhou a area");
            return true;
        }

        private void EnterOverlookedArea(Vector3 position, float now, string why)
        {
            var spent = now - m_OverlookStartedAt;
            m_OverlookPhase = 0;
            // The area's clock starts now, not when the look began.
            m_InterestAreaExpiresAt += spent;
            StartInterestAreaBookkeeping(now);
            SetObjective(ObjectiveType.Explore, m_OverlookCenter, now + m_ObjectiveTimeoutSeconds);
            BotLog(LogLevel.Information,
                "Entrando em {Area} ({Why}, {Seconds:0}s): {Count} zumbi(s) vistos perto do centro, {Special} especial(is).",
                m_OverlookAreaName,
                why,
                spent,
                m_OverlookSeenMax,
                m_OverlookSpecialSeen);
        }

        private void CountOverlookZombies()
        {
            var count = 0;
            var special = 0;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie == null
                    || zombie.isDead
                    || !zombie.gameObject.activeInHierarchy
                    || (zombie.transform.position - m_OverlookCenter).GetHorizontalMagnitude() > OverlookSeenMeters)
                {
                    continue;
                }
                count++;
                if (!IsCommonZombie(zombie))
                {
                    special++;
                }
            }
            m_OverlookSeenMax = Mathf.Max(m_OverlookSeenMax, count);
            m_OverlookSpecialSeen = Mathf.Max(m_OverlookSpecialSeen, special);
        }

        // The view while watching: towards the centre, sweeping 55 degrees
        // to each side, a little down when up high.
        private bool TryGetOverlookLook(float now, out float yaw, out float pitch)
        {
            yaw = 0f;
            pitch = 90f;
            if (!IsOverlookWatching(now))
            {
                return false;
            }
            if (now >= m_OverlookNextLookAt)
            {
                m_OverlookNextLookAt = now + Random.Range(0.7f, 1.4f);
                var eye = m_Player.look.aim.position;
                var toCenter = m_OverlookCenter + Vector3.up * 1f - eye;
                var horizontal = new Vector3(toCenter.x, 0f, toCenter.z);
                var centerYaw = horizontal.sqrMagnitude > 0.01f ? DirectionToYaw(horizontal) : m_CurrentYaw;
                m_OverlookLookYaw = centerYaw + Random.Range(-55f, 55f);
                m_OverlookLookPitch = Mathf.Clamp(
                    90f - Mathf.Atan2(toCenter.y, Mathf.Max(1f, horizontal.magnitude)) * Mathf.Rad2Deg + Random.Range(-4f, 4f),
                    70f,
                    125f);
            }
            yaw = m_OverlookLookYaw;
            pitch = m_OverlookLookPitch;
            return true;
        }

        // ---- leave common zombies behind on the way ----
        // A zombie walks at 5.5 m/s (4.5 with Slow_Movement), a sprinter at
        // 6.5, a crawler at 3; a sprinting player at 7 (Zombie.updateStates,
        // PlayerMovement). On the way to an area the players run and the
        // zombie falls behind: they fight in 11-13% of the time one follows
        // them. A bot fought every zombie that hunted it, wherever it was going.
        // Past the reach where a hit on the bot forces a fight (2.5m), and
        // more for the one already fought, so the two never take turns.
        private const float LeaveBehindCloseMeters = 3f;
        private const float LeaveBehindTargetMeters = 4f;
        private const float LeaveBehindMinimumStamina = 25f;
        private const float TransitTaskMeters = 35f;
        private float m_NextLeaveBehindLogAt;
        private int m_StatWindowLeftBehind;

        private bool IsInTransit(Vector3 position, float now)
        {
            if (m_OverlookPhase == 2)
            {
                return false;
            }
            if (m_TravelActive || m_ObjectiveType == ObjectiveType.Travel)
            {
                return true;
            }
            Vector3 task;
            if (m_ObjectiveType == ObjectiveType.Explore || m_ObjectiveType == ObjectiveType.Patrol)
            {
                task = m_ObjectivePosition;
            }
            else if ((m_ObjectiveType == ObjectiveType.Combat || m_ObjectiveType == ObjectiveType.None)
                     && now - m_LastTaskAt < 15f)
            {
                task = m_LastTaskPosition;
            }
            else
            {
                return false;
            }
            if ((task - position).GetHorizontalMagnitude() < TransitTaskMeters)
            {
                return false;
            }
            return !m_HasInterestArea || !IsInsideInterestArea(position, 0f);
        }

        private bool ShouldLeaveHunterBehind(Zombie zombie, Vector3 position)
        {
            if (!s_Settings.LeaveZombiesBehind
                || !IsCommonZombie(zombie)
                || zombie.speciality == EZombieSpeciality.SPRINTER
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.life.stamina < LeaveBehindMinimumStamina)
            {
                return false;
            }
            // 0.2.2c: only a fight not yet begun is skipped. Leaving the one
            // being fought (4-5m away between swings) made the bots hit once
            // and walk off (6 times in the 0.2.2 test).
            if (zombie == m_TargetZombie || m_ObjectiveType == ObjectiveType.Combat)
            {
                return false;
            }
            var now = m_BehaviorNow;
            if (!IsInTransit(position, now))
            {
                return false;
            }
            var horizontal = (zombie.transform.position - position).GetHorizontalMagnitude();
            if (horizontal <= (zombie == m_TargetZombie ? LeaveBehindTargetMeters : LeaveBehindCloseMeters)
                || IsGoalZombie(zombie, horizontal))
            {
                return false;
            }
            if (now >= m_NextLeaveBehindLogAt)
            {
                m_NextLeaveBehindLogAt = now + 15f;
                m_StatWindowLeftBehind++;
                BotLog(LogLevel.Information,
                    "Deixando para tras zumbi comum a {Distance:0.#}m que segue o bot no caminho ({Objective}); correndo, folego {Stamina}.",
                    horizontal,
                    m_ObjectiveType,
                    m_Player.life.stamina);
            }
            return true;
        }

        // ---- the mesh's shortcuts: up a ledge with a jump, low crouched ----
        private float m_NextShortcutScanAt;
        private int m_ShortcutLegId = -1;
        private SkaliaBotUnityNavmesh.ShortcutKind m_ShortcutLegKind;
        private float m_ShortcutLegSince;
        // The same link seen again within 3s (a route asked again at the
        // same ledge) keeps its clock: thrashing at it still counts.
        private int m_ShortcutTrackId = -1;
        private float m_ShortcutTrackSeenAt = float.NegativeInfinity;
        private float m_ShortcutCrouchUntil;
        private int m_StatWindowShortcutsTaken;
        private int m_StatWindowShortcutsFailed;
        private const float ShortcutJumpFailSeconds = 6f;
        private const float ShortcutCrouchFailSeconds = 12f;
        private const float ShortcutCrouchEarlyMeters = 2.5f;

        // 0.2.2u: a route cut for no progress on a shortcut (the leg being
        // walked, or the one starting at the corner ahead) removes the
        // shortcut at once: the mesh cut of BlockStuckSpot does not touch
        // links, and Tadeu asked for the same route through the Research
        // wall 15 times in 3.5 min.
        private bool TryDropFailedShortcut(Vector3 position)
        {
            if (SkaliaBotUnityNavmesh.ShortcutCount == 0
                || m_PathIndex >= m_PathPoints.Count)
            {
                return false;
            }
            var corner = m_PathPoints[m_PathIndex];
            var previous = m_PathIndex > 0 ? m_PathPoints[m_PathIndex - 1] : position;
            if (!SkaliaBotUnityNavmesh.TryGetShortcutLeg(previous, corner, 1.5f, out var id, out var kind, out var from, out var to)
                && !SkaliaBotUnityNavmesh.TryGetShortcutLeg(position, corner, 2f, out id, out kind, out from, out to)
                && !(m_PathIndex + 1 < m_PathPoints.Count
                     && (corner - position).GetHorizontalMagnitude() <= 2.5f
                     && SkaliaBotUnityNavmesh.TryGetShortcutLeg(corner, m_PathPoints[m_PathIndex + 1], 0.7f, out id, out kind, out from, out to)))
            {
                return false;
            }
            m_StatWindowShortcutsFailed++;
            // 0.2.2w (doc 14.3, item 6): one failure no longer removes it for
            // everyone: thigas walked the shortcut XX got stuck on. It goes
            // when 2 bots fail it, or one bot fails it twice.
            if (!s_ShortcutFailures.TryGetValue(id, out var failures))
            {
                failures = new List<string>();
                s_ShortcutFailures[id] = failures;
            }
            failures.Add(m_Player.channel.owner.playerID.characterName);
            var distinct = new HashSet<string>(failures).Count;
            var remove = !Tryhard || distinct >= 2 || failures.Count >= 3;
            BotLog(LogLevel.Information,
                "Atalho da malha ({Kind}) de {From} para {To} travou o bot em {Position} (postura {Stance}, correndo {Sprint}, arma {Weapon}); {Outcome}.",
                kind == SkaliaBotUnityNavmesh.ShortcutKind.Crouch ? "agachado" : "pulo",
                FormatPosition(from),
                FormatPosition(to),
                FormatPosition(position),
                m_Player.stance.stance,
                m_IsSprinting,
                m_Player.equipment.asset != null ? m_Player.equipment.asset.itemName : "nenhuma",
                remove
                    ? "removido para todos os bots (" + failures.Count + " falha(s), " + distinct + " bot(s))"
                    : "fica no mapa para os outros bots; este evita o trecho por " + (OrbitBadSpotSeconds / 60f).ToString("0") + " min");
            if (remove)
            {
                SkaliaBotUnityNavmesh.RemoveShortcut(id);
                s_ShortcutFailures.Remove(id);
            }
            else
            {
                // Only this bot keeps off it: its routes through here are refused.
                MarkOrbitBadSpot(position, m_BehaviorNow);
            }
            ResetShortcutLeg();
            m_ShortcutTrackId = -1;
            return true;
        }

        private static readonly Dictionary<int, List<string>> s_ShortcutFailures = new Dictionary<int, List<string>>();

        private void ResetShortcutLeg()
        {
            m_ShortcutLegId = -1;
            m_ShortcutLegKind = SkaliaBotUnityNavmesh.ShortcutKind.None;
            m_ShortcutCrouchUntil = 0f;
        }

        private bool IsShortcutCrouching(float now)
        {
            return now < m_ShortcutCrouchUntil;
        }

        private bool IsOnShortcutLeg()
        {
            return m_ShortcutLegId >= 0 && m_ShortcutLegKind != SkaliaBotUnityNavmesh.ShortcutKind.None;
        }

        // A few cells of the mesh round each bot are searched for shortcuts,
        // a piece at a time.
        private void TickShortcutScan(Vector3 position, float now)
        {
            if (now < m_NextShortcutScanAt
                || (!s_Settings.JumpLinks && !s_Settings.CrouchLinks)
                || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return;
            }
            m_NextShortcutScanAt = now + 0.5f;
            SkaliaBotUnityNavmesh.ScanShortcutsNear(position);
        }

        // In GetNavigationTarget, on a new-mesh route, after the corner ahead
        // was chosen.
        private void UpdateShortcutLeg(Vector3 position, float now)
        {
            if (m_PathIndex >= m_PathPoints.Count
                || SkaliaBotUnityNavmesh.ShortcutCount == 0)
            {
                ResetShortcutLeg();
                return;
            }
            var corner = m_PathPoints[m_PathIndex];
            // Crossing: the corner just passed and the one ahead are the two
            // ends of a shortcut (a route starting on one: the bot by it).
            var previous = m_PathIndex > 0 ? m_PathPoints[m_PathIndex - 1] : position;
            if (SkaliaBotUnityNavmesh.TryGetShortcutLeg(previous, corner, m_PathIndex > 0 ? 0.7f : 1.5f, out var id, out var kind, out var from, out var to)
                && (from - position).GetHorizontalMagnitude() <= 3f)
            {
                // 0.2.2u: 8s (was 3): the tryhard cuts a stalled route in 3s
                // and the next one took about 3s more, so the clock restarted
                // every time and a false link was never removed.
                if (id != m_ShortcutTrackId || now - m_ShortcutTrackSeenAt > 8f)
                {
                    m_ShortcutTrackId = id;
                    m_ShortcutLegSince = now;
                    m_StatWindowShortcutsTaken++;
                }
                m_ShortcutTrackSeenAt = now;
                m_ShortcutLegId = id;
                m_ShortcutLegKind = kind;
                if (kind == SkaliaBotUnityNavmesh.ShortcutKind.Crouch)
                {
                    m_ShortcutCrouchUntil = now + 0.6f;
                }
                else if (to.y - position.y > 0.45f
                         && (from - position).GetHorizontalMagnitude() <= 1.3f
                         && m_Player.movement.isGrounded
                         && now >= m_NextNavigationJumpAt)
                {
                    // Up the ledge: one jump at its foot (going down is a step off).
                    m_NavigationJumpUntil = now + 0.3f;
                    m_NextNavigationJumpAt = now + 0.9f;
                }
                var limit = kind == SkaliaBotUnityNavmesh.ShortcutKind.Crouch
                    ? ShortcutCrouchFailSeconds
                    : ShortcutJumpFailSeconds;
                if (now - m_ShortcutLegSince > limit)
                {
                    m_StatWindowShortcutsFailed++;
                    BotLog(LogLevel.Information,
                        "Atalho da malha ({Kind}) de {From} para {To} nao passou em {Seconds:0}s; removido para todos os bots.",
                        kind == SkaliaBotUnityNavmesh.ShortcutKind.Crouch ? "agachado" : "pulo",
                        FormatPosition(from),
                        FormatPosition(to),
                        now - m_ShortcutLegSince);
                    SkaliaBotUnityNavmesh.RemoveShortcut(id);
                    ResetShortcutLeg();
                    m_ShortcutTrackId = -1;
                    ClearPath();
                }
                return;
            }
            m_ShortcutLegId = -1;
            m_ShortcutLegKind = SkaliaBotUnityNavmesh.ShortcutKind.None;
            // Crouch a little before a low passage, not at its mouth.
            if (m_PathIndex + 1 < m_PathPoints.Count
                && (corner - position).GetHorizontalMagnitude() <= ShortcutCrouchEarlyMeters
                && SkaliaBotUnityNavmesh.TryGetShortcutLeg(corner, m_PathPoints[m_PathIndex + 1], 0.7f, out _, out var nextKind, out _, out _)
                && nextKind == SkaliaBotUnityNavmesh.ShortcutKind.Crouch)
            {
                m_ShortcutCrouchUntil = now + 0.6f;
            }
        }
    }
}
