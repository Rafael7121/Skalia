using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SkaliaDeps
{
    // 0.1.0-y17.34 (the tester, 21/11, the launcher's difficulty: "Vai ser
    // padrao (o que ja e hoje) dificil e tryhard"). serverBot:modifiers:
    // multipliers of the bases taken from the players' recordings, read at
    // load. 0.1.0-y17.35: any decimal within the launcher's sliders (0.25 to
    // 1.75, fieldOfView 0.5 to 1.5), clamped outside them; missing or not a
    // number, 1. 1 (or missing) is the recorded behaviour, untouched:
    // every use multiplies or divides by exactly 1.
    // - visionRange: how far a man is seen (PvpMaxMeters 250 and the gun's
    //   range; past 1 only the 250m cap grows, never past the gun's range)
    //   and the zombies' (VisualZombieDetectionRadiusMeters 50).
    // - fieldOfView: the screen's half angle (PvpViewHalfDegrees 60, at
    //   most 180).
    // - hearing: a shot heard up to HearShotMeters 100 (never past the
    //   gun's own sound) and one aimed at it within HearAimedMeters 20.
    // - reactionTime: the players' reaction times (to a hit, to a man seen,
    //   before the hand moves, turning to a shot). Smaller is quicker.
    // - aimSpeed: the players' times of the move onto a man, its
    //   corrections and the lag following him. Bigger is quicker.
    // - accuracy: the aim's spread (the drawn error). Bigger is tighter.
    // - courage: the health it runs or heals at (40, 65 bleeding, 35 in a
    //   fight), divided by it.
    // - cover: the crouched and prone shares while shooting, and how far a
    //   spot of cover is looked for (10m watching, 6m in the fight).
    // 0.1.0-y17.36 (the tester, 21/11: "de ideia de mais uns 15 - 30
    // modificadores"), 13 more:
    // - shotOrigin: the error turning to a shot from off the screen
    //   (25/35/47/85/127 degrees), divided, and the share that found a
    //   shooter off the screen (4 of 7), times it (at most all).
    // - openFireDelay: from a man seen to the first round (0-7s). Smaller
    //   is sooner. From y17.36 reactionTime no longer scales it.
    // - burstLength, burstPause: the trigger held (0.08-1.1s) and the time
    //   between pulls (0.12-1.5s).
    // - strafing: the share of the fight's rows on A, D or back, by range
    //   (at most all).
    // - repositioning: the rounds fired from one spot (median 9), divided.
    // - sprinting: the players' sprint runs times it, the walks between
    //   them divided.
    // - persistence: the fight remembered (5 min) and the watch of where he
    //   was (30/45/60s), times it.
    // - chase: the share that went straight to where he was (2 of 12),
    //   times it (at most the 7 of 12 that moved).
    // - healSpeed: the wait before the bandage at low health (0-1.6s),
    //   divided.
    // - hordeCourage: up to 4 zombies fought (5 tryhard, the kite), 6 for
    //   the big group, times it; the 50 health for it divided.
    // - meleeHeadshots: the blade on the head (96%), times it (at most all).
    // - swingSpeed: the players' 0.44s between blows (p25) and the 0.08s
    //   after the swing, divided; never under the swing's own length.
    // fightDistance and looting have no recorded base to scale (the bot
    // fights a man wherever its gun reaches him, and a man seen always
    // comes before loot); they are read for the log only.
    internal static class SkaliaBotModifiers
    {
        public const float Minimum = 0.25f;
        public const float Maximum = 1.75f;
        public const float FieldOfViewMinimum = 0.5f;
        public const float FieldOfViewMaximum = 1.5f;

        public static float VisionRange { get; private set; } = 1f;
        public static float FieldOfView { get; private set; } = 1f;
        public static float Hearing { get; private set; } = 1f;
        public static float ReactionTime { get; private set; } = 1f;
        public static float AimSpeed { get; private set; } = 1f;
        public static float Accuracy { get; private set; } = 1f;
        public static float Courage { get; private set; } = 1f;
        public static float Cover { get; private set; } = 1f;
        public static float ShotOrigin { get; private set; } = 1f;
        public static float OpenFireDelay { get; private set; } = 1f;
        public static float BurstLength { get; private set; } = 1f;
        public static float BurstPause { get; private set; } = 1f;
        public static float Strafing { get; private set; } = 1f;
        public static float Repositioning { get; private set; } = 1f;
        public static float Sprinting { get; private set; } = 1f;
        public static float Persistence { get; private set; } = 1f;
        public static float Chase { get; private set; } = 1f;
        public static float HealSpeed { get; private set; } = 1f;
        public static float HordeCourage { get; private set; } = 1f;
        public static float MeleeHeadshots { get; private set; } = 1f;
        public static float SwingSpeed { get; private set; } = 1f;

        public static void Load(IConfiguration configuration, ILogger logger)
        {
            VisionRange = Read(configuration, logger, "visionRange", Minimum, Maximum);
            FieldOfView = Read(configuration, logger, "fieldOfView", FieldOfViewMinimum, FieldOfViewMaximum);
            Hearing = Read(configuration, logger, "hearing", Minimum, Maximum);
            ReactionTime = Read(configuration, logger, "reactionTime", Minimum, Maximum);
            AimSpeed = Read(configuration, logger, "aimSpeed", Minimum, Maximum);
            Accuracy = Read(configuration, logger, "accuracy", Minimum, Maximum);
            Courage = Read(configuration, logger, "courage", Minimum, Maximum);
            Cover = Read(configuration, logger, "cover", Minimum, Maximum);
            ShotOrigin = Read(configuration, logger, "shotOrigin", Minimum, Maximum);
            OpenFireDelay = Read(configuration, logger, "openFireDelay", Minimum, Maximum);
            BurstLength = Read(configuration, logger, "burstLength", Minimum, Maximum);
            BurstPause = Read(configuration, logger, "burstPause", Minimum, Maximum);
            Strafing = Read(configuration, logger, "strafing", Minimum, Maximum);
            Repositioning = Read(configuration, logger, "repositioning", Minimum, Maximum);
            Sprinting = Read(configuration, logger, "sprinting", Minimum, Maximum);
            Persistence = Read(configuration, logger, "persistence", Minimum, Maximum);
            Chase = Read(configuration, logger, "chase", Minimum, Maximum);
            HealSpeed = Read(configuration, logger, "healSpeed", Minimum, Maximum);
            HordeCourage = Read(configuration, logger, "hordeCourage", Minimum, Maximum);
            MeleeHeadshots = Read(configuration, logger, "meleeHeadshots", Minimum, Maximum);
            SwingSpeed = Read(configuration, logger, "swingSpeed", Minimum, Maximum);
            var unscaled = new[] { "nightVision", "headshots", "recoilControl", "flanking", "fleeWhenHurt" };
            var fightDistance = Read(configuration, logger, "fightDistance", Minimum, Maximum);
            var looting = Read(configuration, logger, "looting", Minimum, Maximum);
            logger.LogInformation(
                "Modificadores dos bots: visao {Vision}, campo de visao {Fov}, audicao {Hearing}, reacao {Reaction}, velocidade da mira {AimSpeed}, precisao {Accuracy}, coragem {Courage}, cobertura {Cover} (1 = como gravado).",
                F(VisionRange), F(FieldOfView), F(Hearing), F(ReactionTime), F(AimSpeed), F(Accuracy), F(Courage), F(Cover));
            logger.LogInformation(
                "Modificadores dos bots: origem do tiro {ShotOrigin}, demora para abrir fogo {OpenFire}, rajada {Burst}, pausa {Pause}, passos de lado {Strafing}, reposicionar {Reposition}, correr {Sprinting}, persistencia {Persistence}, perseguir {Chase}, curar {Heal}, coragem com horda {Horde}, golpe na cabeca {MeleeHead}, golpes {Swing}.",
                F(ShotOrigin), F(OpenFireDelay), F(BurstLength), F(BurstPause), F(Strafing), F(Repositioning), F(Sprinting),
                F(Persistence), F(Chase), F(HealSpeed), F(HordeCourage), F(MeleeHeadshots), F(SwingSpeed));
            if (fightDistance != 1f || looting != 1f)
            {
                logger.LogWarning(
                    "Modificadores dos bots: fightDistance ({FightDistance}) e looting ({Looting}) nao tem base gravada e ficam sem efeito.",
                    F(fightDistance), F(looting));
            }
            foreach (var key in unscaled)
            {
                var value = Read(configuration, logger, key, Minimum, Maximum);
                if (value != 1f)
                {
                    logger.LogWarning("Modificadores dos bots: {Key} ({Value}) nao tem base gravada e fica sem efeito.", key, F(value));
                }
            }
        }

        private static float Read(IConfiguration configuration, ILogger logger, string key, float minimum, float maximum)
        {
            var text = configuration["serverBot:modifiers:" + key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return 1f;
            }
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || float.IsNaN(value) || float.IsInfinity(value))
            {
                logger.LogWarning("Modificadores dos bots: {Key} = '{Text}' nao e um numero; usando 1.", key, text);
                return 1f;
            }
            if (value < minimum || value > maximum)
            {
                var clamped = value < minimum ? minimum : maximum;
                logger.LogWarning("Modificadores dos bots: {Key} = {Value} fora de {Min}-{Max}; usando {Clamped}.",
                    key, F(value), F(minimum), F(maximum), F(clamped));
                return clamped;
            }
            return value;
        }

        // At most all of it; at 1 the share itself.
        public static float Share(float share, float modifier)
        {
            return modifier == 1f ? share : System.Math.Min(1f, share * modifier);
        }

        public static int Count(int count, float modifier)
        {
            return modifier == 1f ? count : System.Math.Max(1, (int)System.Math.Round(count * modifier));
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
