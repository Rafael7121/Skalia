using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y (the tester, 16/11: "use as gravações ... para deixar eles menos
    // robotizados"): how the players of the night of 08/11 (Onça, Zebraum,
    // Natanick, Xane, zombie) moved and pulled the trigger while shooting
    // at each other. 554 recording rows (10 a second) with the trigger down
    // and the gun within 8 degrees of another player, 43 exchanges, 18
    // players hit. The first 0.2.2y stood still, upright or crouched, and
    // clicked once every 0.18s from the first hit to the end.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // Distance bands of the rows: <15m, 15-40, 40-80, 80-150, 150+.
        private static int PvpBand(float meters)
        {
            return meters < 15f ? 0 : meters < 40f ? 1 : meters < 80f ? 2 : meters < 150f ? 3 : 4;
        }

        // Posture while shooting, by band (stand, crouch, prone shares).
        // <15: 71/21/7, 15-40: 91/8/0, 40-80: 54/12/32, 80-150: 56/22/20,
        // 150+: 68/31/0.
        private static readonly float[,] s_PvpPostureShare =
        {
            { 0.72f, 0.21f, 0.07f },
            { 0.92f, 0.08f, 0f },
            { 0.56f, 0.12f, 0.32f },
            { 0.58f, 0.22f, 0.20f },
            { 0.69f, 0.31f, 0f },
        };

        // Share of the rows with the player moving while shooting, by band:
        // 78% close (mostly backing off to a side), 25%, 55% (A and D),
        // 27%, 30%.
        private static readonly float[] s_PvpMoveShare = { 0.78f, 0.25f, 0.55f, 0.27f, 0.30f };

        // Close in (<15m) 71% of the rows had the aim up; past 15m it was up
        // in 95-100% of the rows out to 150m.
        private const float PvpCloseAimShare = 0.71f;

        // How long A or D stayed down before the other one (83 runs):
        // p25 0.2s, median 0.3, p75 0.6, p90 1.0. The still stretches are
        // drawn from the same times.
        private static readonly float[] s_PvpStepQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
        private static readonly float[] s_PvpStepSeconds = { 0.1f, 0.2f, 0.3f, 0.6f, 1.0f, 1.4f };

        // Trigger down (150 pulls): p25 0.1s, median 0.2, p75 0.4, p90 0.8.
        // Between pulls (107): p25 0.2s, median 0.3, p75 0.6, p90 1.1.
        private static readonly float[] s_PvpHoldSeconds = { 0.08f, 0.1f, 0.2f, 0.4f, 0.8f, 1.1f };
        private static readonly float[] s_PvpGapSeconds = { 0.12f, 0.2f, 0.3f, 0.6f, 1.1f, 1.5f };

        // Hit while not looking at the attacker: the players who turned and
        // shot back faced him 0.8, 0.9, 1.5 and 2.1s after the first round.
        private static readonly float[] s_PvpReactQuantiles = { 0f, 0.33f, 0.66f, 1f };
        private static readonly float[] s_PvpReactSeconds = { 0.8f, 0.9f, 1.5f, 2.1f };

        // 0.1.0-y17.3: hit with the shooter on the screen, within 60m
        // (rec/pvp16.py): first round back 0.0, 0.0, 0.2, 0.2, 0.3, 0.4,
        // 0.5s after (7 of 9; the other 2 ran).
        private static readonly float[] s_PvpHitSeenQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_PvpHitSeenShotSeconds = { 0f, 0.1f, 0.2f, 0.35f, 0.5f };

        // The screen: the game's default field of view is 90 degrees up and
        // down (OptionsSettings: 60 + 40 x 0.75), 121 across at 16:9.
        // 0.1.0-y17.34: times serverBot:modifiers:fieldOfView.
        private static float PvpViewHalfDegrees => Mathf.Min(180f, 60f * SkaliaBotModifiers.FieldOfView);

        // Hit with the shooter off the screen (17 first hits): with him
        // within 42 degrees all 10 found him (to 0-8 degrees); with him at
        // 62-151 degrees 4 of 7 found him, after 0.8, 1.8, 2.6 and 2.7s,
        // and the other 3 never faced him (best 38-74 degrees off) and ran.
        private const float PvpFoundOffScreenShare = 4f / 7f;
        private static readonly float[] s_PvpOffScreenReactSeconds = { 0.8f, 1.8f, 2.6f, 2.7f };

        // 0.1.0-y14 (the tester, 17/11: "os tiros que eu dei nao foi pra matar.
        // isso nao e numero pra base. a realidade e os primeiros a chance de
        // acerto e 80% pra mais" / "ate uns 50m vai, mais que isso so se
        // tiver luneta"). He plays the tests to watch the bots, so his
        // recorded rounds (and the 08/11 table of 0.2.2y and y13 built on
        // them) are not how a player fighting to kill aims. The spread is
        // now the one that lands 80% of the rounds on the body (0.8 x 1.85m,
        // aimed at the chest) at that range, out to 50m; past 50m without a
        // scope the aim is as good in degrees as at 50m, so the chance falls
        // with the body getting smaller; with a scope it stays 80%.
        private bool m_PvpAware;
        private float m_PvpAwareSince;

        // 0.1.0-y15 (the tester, 17/11: "coloque 90% de acerto entao e 10% de
        // erro"): 90% instead of 80%.
        private const float PvpFirstHitChance = 0.9f;
        // 0.1.0-y15.1 (the tester, 17/11: "requisito da luneta e apenas para
        // 100m +"): the 90% holds without a scope out to 100m.
        private const float PvpNoScopeMeters = 100f;
        private const float PvpBodyHalfWidth = 0.4f;
        private const float PvpBodyHalfHeight = 0.925f;

        private float PvpAimSigmaDegrees(float meters)
        {
            var range = Mathf.Max(meters, 1f);
            if (range > PvpNoScopeMeters && !HasPvpScope())
            {
                range = PvpNoScopeMeters;
            }
            return PvpSigmaForHitChance(range, PvpFirstHitChance);
        }

        // The spread (degrees, each axis) that puts that share of the
        // rounds on the body at that range.
        private static float PvpSigmaForHitChance(float meters, float chance)
        {
            var w = Mathf.Atan(PvpBodyHalfWidth / meters) * Mathf.Rad2Deg;
            var h = Mathf.Atan(PvpBodyHalfHeight / meters) * Mathf.Rad2Deg;
            var lo = 0.001f;
            var hi = 30f;
            for (var i = 0; i < 40; i++)
            {
                var mid = 0.5f * (lo + hi);
                if (PvpBodyHitChance(w, h, mid) > chance)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            return 0.5f * (lo + hi);
        }

        private static float PvpBodyHitChance(float halfWidth, float halfHeight, float sigma)
        {
            var k = 1f / (sigma * Mathf.Sqrt(2f));
            return Erf(halfWidth * k) * Erf(halfHeight * k);
        }

        // Abramowitz-Stegun 7.1.26, within 1.5e-7.
        private static float Erf(float x)
        {
            var sign = x < 0f ? -1f : 1f;
            x = Mathf.Abs(x);
            var t = 1f / (1f + 0.3275911f * x);
            var y = 1f - ((((1.061405429f * t - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * Mathf.Exp(-x * x);
            return sign * y;
        }

        // A scope (a sight with a zoom: 4x to 20x in the game's sights; the
        // iron sights and the red dots have none). The asset keeps it either
        // as the factor or as the field of view, both in 2-60.
        private bool HasPvpScope()
        {
            if (!(m_Player.equipment.useable is UseableGun gun))
            {
                return false;
            }
            try
            {
                var sight = gun.thirdAttachments?.sightAsset;
                if (sight == null)
                {
                    return false;
                }
                var zoom = sight.zoom;
                return zoom >= 2f && zoom < 60f;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static float Gaussian()
        {
            var u1 = Mathf.Max(1e-6f, Random.value);
            var u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        private bool IsInPvpView(Vector3 point)
        {
            var to = point - m_Player.look.aim.position;
            return Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, DirectionToYaw(to))) <= PvpViewHalfDegrees;
        }

        // Hit: does the bot know where it came from? On the screen yes; off
        // it, as often as the players found their shooter. Unfound, it goes
        // on as before and fights him only once it sees him on the screen
        // (or he hits it again and it is found that time).
        private bool RollPvpNotice(Player attacker, out float[]? reactSeconds, out string text)
        {
            if (IsInPvpView(attacker.transform.position + Vector3.up * 1.3f))
            {
                reactSeconds = s_PvpReactSeconds;
                text = "vi de onde veio";
                return true;
            }
            if (Random.value < PvpFoundOffScreenShare)
            {
                reactSeconds = s_PvpOffScreenReactSeconds;
                text = "veio de fora da tela, achei de onde";
                return true;
            }
            reactSeconds = null;
            text = "veio de fora da tela, nao sei de onde";
            return false;
        }

        private float m_PvpReactAt;
        private int m_PvpPosture;
        private bool m_PvpHipFire;
        private int m_PvpBandAtEngage;
        private float m_PvpStepUntil;
        private byte m_PvpStepAnalog = 0x11;
        private int m_PvpLastSide = 1;
        private bool m_PvpTriggerHeld;
        private float m_PvpTriggerUntil;

        // The time from the hit to facing him: the drawn reaction.
        // 0.1.0-y17.19: it held only the trigger; the head went to him at
        // once through the look at where he was. Now the hand sets off so
        // that its move ends then (SkaliaBotPvpHandY).
        private void SetPvpReactionFrom(float[] seconds, Player attacker, float now)
        {
            var react = DrawFromQuantiles(s_PvpReactQuantiles, seconds) * SkaliaBotModifiers.ReactionTime;
            m_PvpReactAt = StartPvpHandReactionToFace(react, now);
        }

        private float m_PvpStanceDrawnAt = float.NegativeInfinity;

        private void DrawPvpStance(float meters, float now)
        {
            var band = PvpBand(meters);
            // Back on the same man a moment later at the same range: the
            // same way of shooting, not a new one each time he shows.
            if (now - m_PvpStanceDrawnAt < 10f && band == m_PvpBandAtEngage)
            {
                m_PvpStanceDrawnAt = now;
                m_PvpStepUntil = now;
                m_PvpStepAnalog = 0x11;
                return;
            }
            m_PvpStanceDrawnAt = now;
            m_PvpBandAtEngage = band;
            var roll = Random.value;
            m_PvpPosture = roll < s_PvpPostureShare[band, 0] ? 0
                : roll < s_PvpPostureShare[band, 0] + s_PvpPostureShare[band, 1] ? 1
                : 2;
            // 0.1.0-y17.34: the crouched and prone shares times
            // serverBot:modifiers:cover (up to all of it), standing the rest.
            // At 1 the draw above stands as it is.
            if (SkaliaBotModifiers.Cover != 1f)
            {
                var crouch = s_PvpPostureShare[band, 1] * SkaliaBotModifiers.Cover;
                var prone = s_PvpPostureShare[band, 2] * SkaliaBotModifiers.Cover;
                var low = crouch + prone;
                if (low > 1f)
                {
                    crouch /= low;
                    prone /= low;
                }
                var stand = 1f - crouch - prone;
                m_PvpPosture = roll < stand ? 0 : roll < stand + crouch ? 1 : 2;
            }
            m_PvpHipFire = band == 0 && Random.value > PvpCloseAimShare;
            m_PvpStepUntil = now;
            m_PvpStepAnalog = 0x11;
            m_PvpLastSide = Random.value < 0.5f ? -1 : 1;
        }

        // Down low and the grass, a wall or a car hides him: up one step
        // (prone to crouched, crouched to standing), or the bot would go
        // down, lose him, get up, see him and go down again.
        private float m_PvpRaisedAt = float.NegativeInfinity;

        private void RaisePvpPostureIfHidden(bool visible, float now)
        {
            // One step a second: hidden a moment by a tree he crossed, the
            // bot should not end up standing.
            if (visible || m_PvpPosture == 0 || now - m_PvpRaisedAt < 1f)
            {
                return;
            }
            var stance = m_Player.stance.stance;
            if ((m_PvpPosture == 2 && stance == EPlayerStance.PRONE)
                || (m_PvpPosture == 1 && stance == EPlayerStance.CROUCH))
            {
                m_PvpPosture--;
                m_PvpRaisedAt = now;
            }
        }

        // The legs while shooting: stretches of A or D (turned each time),
        // close in backing off to a side, or standing, in the band's share.
        // Never prone, never into a wall, off a ledge or into water.
        private void UpdatePvpSteps(float meters, float now)
        {
            if (m_PvpPosture == 2 || m_Player.stance.stance == EPlayerStance.PRONE)
            {
                m_PvpStepAnalog = 0x11;
                return;
            }
            if (now >= m_PvpStepUntil)
            {
                m_PvpStepUntil = now + DrawFromQuantiles(s_PvpStepQuantiles, s_PvpStepSeconds);
                var band = PvpBand(meters);
                if (Random.value >= s_PvpMoveShare[band])
                {
                    m_PvpStepAnalog = 0x11;
                }
                else
                {
                    m_PvpLastSide = -m_PvpLastSide;
                    // Close in: of the moving rows, 59% back and to a side,
                    // 18% to a side, 18% forward, 5% straight back.
                    var back = 1;
                    var side = m_PvpLastSide;
                    if (band == 0)
                    {
                        var roll = Random.value;
                        if (roll < 0.59f)
                        {
                            back = 0;
                        }
                        else if (roll < 0.77f)
                        {
                        }
                        else if (roll < 0.95f)
                        {
                            back = 2;
                            side = 0;
                        }
                        else
                        {
                            back = 0;
                            side = 0;
                        }
                    }
                    m_PvpStepAnalog = (byte)(((side + 1) << 4) | back);
                }
            }
            if (m_PvpStepAnalog != 0x11 && !IsPvpStepClear(m_PvpStepAnalog))
            {
                m_PvpStepAnalog = 0x11;
            }
        }

        private bool IsPvpStepClear(byte analog)
        {
            var side = ((analog >> 4) & 0x0F) - 1;
            var ahead = (analog & 0x0F) - 1;
            var rotation = Quaternion.Euler(0f, m_CurrentYaw, 0f);
            var direction = rotation * Vector3.right * side + rotation * Vector3.forward * ahead;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
            {
                return true;
            }
            direction.Normalize();
            var position = m_Player.transform.position;
            if (IsPathBlocked(position, direction, 1.5f))
            {
                return false;
            }
            var probe = position + direction * 1.2f + Vector3.up * 1f;
            if (!Physics.Raycast(probe, Vector3.down, out var ground, 2.5f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            return !WaterUtility.isPointUnderwater(ground.point + Vector3.up * 0.3f);
        }

        // The trigger let go, wherever the fight stands.
        private void ReleasePvpTrigger(WalkingPlayerInputPacket packet, float now)
        {
            if (!m_PvpTriggerHeld)
            {
                return;
            }
            packet.primaryAttack = EAttackInputFlags.Stop;
            m_PvpTriggerHeld = false;
            m_NextAttackTime = Mathf.Max(m_NextAttackTime, now + DrawFromQuantiles(s_PvpStepQuantiles, s_PvpGapSeconds));
        }

        // Hit and the shooter not found (the tester, 16/11: going on as before
        // "da pra melhorar"). What the players hit on 08/11 did in the 8s
        // after the first round: most sprinted (60-97% of the time), 8-10m
        // in 2s, 14-25m in 4s, 30-50m in 8s, several into a building (a roof
        // over them 26-71% of the time), hopping (0.54 jumps a second
        // running; between jumps p25 0.7s, median 0.9, p75 1.7) and turning
        // as they ran, and fought whoever showed on the screen. The bot
        // sprints to the nearest cover (the healing cover search, 4-12m),
        // or 10m to open ground with none, hopping, and fights the shooter
        // the moment it sees him.
        private const string PvpEscapeName = "tiro sem origem";
        private const float PvpEscapeSeconds = 8f;
        private static readonly float[] s_PvpHopQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_PvpHopSeconds = { 0.4f, 0.7f, 0.9f, 1.7f, 2.5f };

        private bool m_PvpEscapeRequested;
        private bool m_PvpEscapeActive;
        private float m_PvpEscapeUntil;
        private float m_PvpNextHopAt;

        private bool IsPvpEscaping()
        {
            return m_PvpEscapeActive
                && m_ObjectiveType == ObjectiveType.Flee
                && m_FleeingFromPlayerName == PvpEscapeName;
        }

        private void StartPvpEscape(float now)
        {
            if (IsPvpEscaping()
                || m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || IsLadderInProgress()
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.movement.getVehicle() != null
                || m_CachedNearestZombieDistance <= PvpZombieFirstMeters)
            {
                return;
            }
            var position = m_Player.transform.position;
            // 0.1.0-y17: cover from the side it believes the shot came from
            // (SkaliaBotShotSectorY); the nearest cover of any kind or open
            // ground only with none.
            var what = "cobertura";
            var covered = TryFindPvpSectorCover(position, now, out var spot, out var roof, out var hiddenFrom);
            if (covered)
            {
                what = $"cobertura do setor {DescribeShotSector(now)} (escondido de {hiddenFrom} de 5 pontos{(roof ? ", com teto" : string.Empty)})";
            }
            else
            {
                covered = TryFindCoverSpot(position, now, out spot);
                if (!covered && !TryFindPvpOpenSpot(position, out spot))
                {
                    BotLog(LogLevel.Information, "PvP: tiro sem origem e nenhum lugar para correr; seguindo.");
                    return;
                }
                what = covered ? "cobertura qualquer (nenhuma tapa o setor)" : "outro lugar";
            }
            m_FleeingFromPlayerName = PvpEscapeName;
            SetObjective(ObjectiveType.Flee, spot, now + PvpEscapeSeconds);
            m_PvpEscapeActive = true;
            m_PvpEscapeUntil = now + PvpEscapeSeconds;
            m_PvpNextHopAt = now + DrawFromQuantiles(s_PvpHopQuantiles, s_PvpHopSeconds);
            BotLog(LogLevel.Information,
                "PvP: tiro sem origem; correndo {Distance:0}m para {What} em {Spot}.",
                (spot - position).GetHorizontalMagnitude(),
                what,
                FormatPosition(spot));
        }

        private bool TryFindPvpOpenSpot(Vector3 position, out Vector3 spot)
        {
            spot = position;
            var first = Random.Range(0, 8);
            for (var k = 0; k < 8; k++)
            {
                var direction = Quaternion.Euler(0f, (first + k) * 45f, 0f) * Vector3.forward;
                if (!TryGetWalkableGround(position + direction * 10f, out var grounded)
                    || Mathf.Abs(grounded.y - position.y) > 1.5f
                    || WaterUtility.isPointUnderwater(grounded + Vector3.up * 0.5f)
                    || !SkaliaBotUnityNavmesh.TrySnap(grounded, 1.5f, out var onMesh)
                    || (onMesh - grounded).GetHorizontalMagnitude() > 1.5f)
                {
                    continue;
                }
                spot = onMesh;
                return true;
            }
            return false;
        }

        // Called each tick of the fight code: the hops on the way, the end
        // of the run, and the stop when the shooter shows on the screen.
        private void UpdatePvpEscape(float now)
        {
            if (m_PvpEscapeRequested)
            {
                m_PvpEscapeRequested = false;
                StartPvpEscape(now);
            }
            if (!m_PvpEscapeActive)
            {
                return;
            }
            if (!IsPvpEscaping() || now >= m_PvpEscapeUntil)
            {
                m_PvpEscapeActive = false;
                // 0.1.0-y17.4: where it got to, against the sector
                // (SkaliaBotPvpMemoryY).
                if (!m_PvpAware)
                {
                    LogPvpSectorCoverReached(now);
                }
                return;
            }
            if (m_PvpAware)
            {
                // He showed: the run ends and the fight starts.
                ClearObjective();
                m_PvpEscapeActive = false;
                return;
            }
            if (now >= m_PvpNextHopAt)
            {
                m_PvpNextHopAt = now + DrawFromQuantiles(s_PvpHopQuantiles, s_PvpHopSeconds);
                if (m_IsSprinting && m_Player.movement.isGrounded)
                {
                    m_NavigationJumpUntil = now + 0.3f;
                }
            }
        }
    }
}
