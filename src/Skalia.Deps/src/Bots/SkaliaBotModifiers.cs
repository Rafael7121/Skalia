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
            var fightDistance = Read(configuration, logger, "fightDistance", Minimum, Maximum);
            var looting = Read(configuration, logger, "looting", Minimum, Maximum);
            logger.LogInformation(
                "Modificadores dos bots: visao {Vision}, campo de visao {Fov}, audicao {Hearing}, reacao {Reaction}, velocidade da mira {AimSpeed}, precisao {Accuracy}, coragem {Courage}, cobertura {Cover} (1 = como gravado).",
                F(VisionRange), F(FieldOfView), F(Hearing), F(ReactionTime), F(AimSpeed), F(Accuracy), F(Courage), F(Cover));
            if (fightDistance != 1f || looting != 1f)
            {
                logger.LogWarning(
                    "Modificadores dos bots: fightDistance ({FightDistance}) e looting ({Looting}) nao tem base gravada e ficam sem efeito.",
                    F(fightDistance), F(looting));
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

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
