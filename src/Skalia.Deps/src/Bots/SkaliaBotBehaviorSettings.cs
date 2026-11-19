using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SkaliaDeps
{
    // 0.1.172: switches for the newer behaviour, under serverBot:behavior in
    // config.yaml. The defaults live here: a missing key keeps them, and a
    // value that does not read as true/false or a number only logs a
    // warning. The values in use are logged once when the plugin loads.
    internal sealed class SkaliaBotBehaviorSettings
    {
        private const string Section = "serverBot:behavior:";

        // ~90% of swings at the head, the rest at the chest, small side and
        // distance changes. false: always the head, same distance.
        public bool MeleeAimVariation { get; private set; } = true;

        // Hurt in a horde: crouch and only fight what is close or a zombie
        // that has not noticed the bot. false: no caution at all.
        public bool HordeCaution { get; private set; } = true;

        // How far the caution goes crouched after a distracted zombie (4-25m).
        public float StealthApproachMeters { get; private set; } = 12f;

        // Stop by the item, look at it for half a second, take it, pause.
        // false: take it as soon as it is in reach.
        public bool LootLook { get; private set; } = true;

        // Turning that speeds up for big turns and eases into small ones.
        // false: the fixed movement:turnSpeedDegreesPerSecond.
        public bool HumanTurning { get; private set; } = true;

        // Straight line outdoors when the route zigzags.
        public bool RouteHops { get; private set; } = true;

        // Leave and do not loot within 40m of where another bot died in the
        // last 2 min.
        public bool FreshDeathAvoidance { get; private set; } = true;

        // 0.2.0: guns aim at the head at any distance (players: 72% of gun
        // hits on the head; the bots 21% aiming at the chest past 10m).
        // false: head up to 10m, chest beyond, as up to 0.1.175.
        public bool GunHeadAim { get; private set; } = true;

        // 0.2.0: an automatic gun keeps the trigger held while it is on the
        // zombie, firing at the gun's own rate (players 10-12 shots/s with a
        // PDW; one click per shot capped the bots at 5.5). Bursts farther out.
        // false: one click per shot.
        public bool GunAutoFire { get; private set; } = true;

        // 0.2.0: how much of each shot's recoil (the gun's own numbers, stance,
        // aim and attachments) is left on the aim before the bot pulls it back.
        // 0 = no recoil at all.
        public float GunRecoilScale { get; private set; } = 0.5f;

        // 0.2.0: prone to take an item off the floor and to put a bandage
        // on, crouched to eat and in some of the short stops, only with no
        // zombie after the bot within 25m (the players: prone or crouched
        // 15-60% of the time; the bots almost never). false: always standing
        // except the horde caution.
        public bool PlayerPosture { get; private set; } = true;

        // 0.2.0: on the way somewhere, like the players: short stops looking
        // round, glances to the side while walking (walking sideways), sprint
        // in bursts with a breath between them, a hop now and then on open
        // ground. Never chased, fighting or fleeing. false: as up to 0.1.175.
        public bool PlayerHabits { get; private set; } = true;

        // 0.2.0: extra short stops per minute on the way (the players stop
        // about 4 a minute counting loot; the bots already stop for loot).
        public float HabitPausesPerMinute { get; private set; } = 2f;

        // 0.2.1: a common zombie is fought with the melee weapon (74-97% of
        // the players' hits were within 3m); the gun is for a special zombie,
        // the Mega, 3+ zombies after the bot or no melee weapon.
        public bool MeleeForCommon { get; private set; } = true;

        // 0.2.1: the next area is picked by what the bot's gear lacks (the
        // loot tables of each place), and a military zombie is worth a fight
        // when a gun or its ammo is missing.
        public bool GoalLoot { get; private set; } = true;

        // 0.2.1: ready for the Mega, the bot travels to where it was born and
        // takes a spot 45-60m away with a line on it, like the players.
        public bool MegaSpawnHunt { get; private set; } = true;

        // 0.2.1: a bed found in the loot is placed inside a house and claimed;
        // the bot is born again on it and types /home in the chat.
        public bool BotBeds { get; private set; } = true;
        public bool BotHomeCommand { get; private set; } = true;

        // 0.2.1: just born, the bot goes straight to the area of its goal for
        // the first minute (no stops to look around).
        public bool RespawnRush { get; private set; } = true;

        // 0.2.2 (doc 9): close aim follows the zombie like a hand on the mouse;
        // small groups are fought, not fled; ladders go down the player way;
        // toys and torches are not weapons; the melee stays in the hand.
        public bool CloseAimTracking { get; private set; } = true;
        public bool FightSmallGroups { get; private set; } = true;
        public bool LadderDescend { get; private set; } = true;
        public bool UsefulWeaponsOnly { get; private set; } = true;
        public bool KeepMeleeInHand { get; private set; } = true;
        // 0.2.2: tryhard, the goal (best gear) decides the next area over
        // distance, and a better gun is worth more the better it is.
        public bool TryhardGoals { get; private set; } = true;
        // 0.2.2 tactical awareness: a look from a high point before going
        // into an area; common zombies left behind on the way; the mesh's
        // shortcuts up a ledge with a jump and through a low passage crouched.
        public bool AreaOverlook { get; private set; } = true;
        public bool LeaveZombiesBehind { get; private set; } = true;
        public bool JumpLinks { get; private set; } = true;
        // 0.2.2x (doc 15.2, item 9): the crouch passages made bad links
        // (7 bots stuck on them, 3 removed in the 0.2.2w test). Gone: the
        // key in the config is no longer read.
        public bool CrouchLinks { get; private set; }
        // 0.2.2c: the Mega is not lured instead of fled from (the bot keeps
        // its area and only stays out of the Mega's reach); the loot look
        // only for items in furniture; shipping containers left out.
        public bool MegaNoLure { get; private set; } = true;
        public bool LootLookFurnitureOnly { get; private set; } = true;
        public bool SkipContainers { get; private set; } = true;
        // 0.2.2t: the tryhard bot. Strong loot first (the areas whose spawn
        // tables hold military guns and armour), then the biggest city as the
        // hunting ground, swept street by street up to the Mega; no stops on
        // the way, cover before healing, the Mega no longer ends an area.
        // false: area choice and stops as in 0.2.2c.
        public bool Tryhard { get; private set; } = true;
        // 0.2.2t: name of the hunting ground (a location node of the map).
        // Empty: the area with the most item spawns (Seattle on Washington).
        public string HuntArea { get; private set; } = string.Empty;

        public static SkaliaBotBehaviorSettings Load(IConfiguration configuration, ILogger logger)
        {
            var settings = new SkaliaBotBehaviorSettings();
            settings.MeleeAimVariation = ReadBool(configuration, logger, "meleeAimVariation", settings.MeleeAimVariation);
            settings.HordeCaution = ReadBool(configuration, logger, "hordeCaution", settings.HordeCaution);
            settings.StealthApproachMeters = ReadFloat(configuration, logger, "stealthApproachMeters", settings.StealthApproachMeters, 4f, 25f);
            settings.LootLook = ReadBool(configuration, logger, "lootLook", settings.LootLook);
            settings.HumanTurning = ReadBool(configuration, logger, "humanTurning", settings.HumanTurning);
            settings.RouteHops = ReadBool(configuration, logger, "routeHops", settings.RouteHops);
            settings.FreshDeathAvoidance = ReadBool(configuration, logger, "freshDeathAvoidance", settings.FreshDeathAvoidance);
            settings.GunHeadAim = ReadBool(configuration, logger, "gunHeadAim", settings.GunHeadAim);
            settings.GunAutoFire = ReadBool(configuration, logger, "gunAutoFire", settings.GunAutoFire);
            settings.GunRecoilScale = ReadFloat(configuration, logger, "gunRecoilScale", settings.GunRecoilScale, 0f, 2f);
            settings.PlayerPosture = ReadBool(configuration, logger, "playerPosture", settings.PlayerPosture);
            settings.PlayerHabits = ReadBool(configuration, logger, "playerHabits", settings.PlayerHabits);
            settings.HabitPausesPerMinute = ReadFloat(configuration, logger, "habitPausesPerMinute", settings.HabitPausesPerMinute, 0f, 6f);
            settings.MeleeForCommon = ReadBool(configuration, logger, "meleeForCommon", settings.MeleeForCommon);
            settings.GoalLoot = ReadBool(configuration, logger, "goalLoot", settings.GoalLoot);
            settings.MegaSpawnHunt = ReadBool(configuration, logger, "megaSpawnHunt", settings.MegaSpawnHunt);
            settings.BotBeds = ReadBool(configuration, logger, "botBeds", settings.BotBeds);
            settings.BotHomeCommand = ReadBool(configuration, logger, "botHomeCommand", settings.BotHomeCommand);
            settings.RespawnRush = ReadBool(configuration, logger, "respawnRush", settings.RespawnRush);
            settings.CloseAimTracking = ReadBool(configuration, logger, "closeAimTracking", settings.CloseAimTracking);
            settings.FightSmallGroups = ReadBool(configuration, logger, "fightSmallGroups", settings.FightSmallGroups);
            settings.LadderDescend = ReadBool(configuration, logger, "ladderDescend", settings.LadderDescend);
            settings.UsefulWeaponsOnly = ReadBool(configuration, logger, "usefulWeaponsOnly", settings.UsefulWeaponsOnly);
            settings.KeepMeleeInHand = ReadBool(configuration, logger, "keepMeleeInHand", settings.KeepMeleeInHand);
            settings.TryhardGoals = ReadBool(configuration, logger, "tryhardGoals", settings.TryhardGoals);
            settings.AreaOverlook = ReadBool(configuration, logger, "areaOverlook", settings.AreaOverlook);
            settings.LeaveZombiesBehind = ReadBool(configuration, logger, "leaveZombiesBehind", settings.LeaveZombiesBehind);
            settings.JumpLinks = ReadBool(configuration, logger, "jumpLinks", settings.JumpLinks);
            settings.CrouchLinks = false;
            settings.MegaNoLure = ReadBool(configuration, logger, "megaNoLure", settings.MegaNoLure);
            settings.LootLookFurnitureOnly = ReadBool(configuration, logger, "lootLookFurnitureOnly", settings.LootLookFurnitureOnly);
            settings.SkipContainers = ReadBool(configuration, logger, "skipContainers", settings.SkipContainers);
            settings.Tryhard = ReadBool(configuration, logger, "tryhard", settings.Tryhard);
            settings.HuntArea = ReadRaw(configuration, logger, "huntArea") ?? string.Empty;
            logger.LogInformation(
                "Comportamento dos bots (0.2.2x13): tryhard {Tryhard}, area de caca {Hunt}.",
                settings.Tryhard,
                settings.HuntArea.Length == 0 ? "(Seattle, ou a maior cidade se o mapa nao tiver)" : settings.HuntArea);
            logger.LogInformation(
                "Comportamento dos bots (serverBot:behavior): golpe com variacao {Melee}, cautela {Caution} (ate {Stealth:0}m atras de zumbi distraido), "
                + "olhar o loot {Look}, giro humano {Turn}, atalhos ao ar livre {Hops}, evitar onde bot morreu ha pouco {Fresh}, "
                + "tiro na cabeca {HeadAim}, gatilho segurado na automatica {AutoFire}, recuo {Recoil:0.##}, "
                + "postura de jogador {Posture}, habitos no caminho {Habits} ({Pauses:0.#} paradas/min), "
                + "arma branca no zumbi comum {MeleeCommon}, loot com meta {GoalLoot}, Mega no ponto de nascimento {MegaSpawn}, "
                + "cama {Beds}, /home {Home}, direto ao renascer {Rush}, mira que acompanha de perto {Track}, "
                + "luta com grupo pequeno {SmallGroups}, desce escada {LadderDown}, so armas uteis {Useful}, arma branca na mao {KeepMelee}, tryhard {Tryhard}, "
                + "olhar a area antes de entrar {Overlook}, deixar zumbi para tras no caminho {LeaveBehind}, atalho de pulo {JumpLinks}, passagem agachado {CrouchLinks}, "
                + "nao puxar o Mega {MegaNoLure}, olhar so loot de movel {FurnitureLook}, fora dos containers {SkipContainers}.",
                settings.MeleeAimVariation,
                settings.HordeCaution,
                settings.StealthApproachMeters,
                settings.LootLook,
                settings.HumanTurning,
                settings.RouteHops,
                settings.FreshDeathAvoidance,
                settings.GunHeadAim,
                settings.GunAutoFire,
                settings.GunRecoilScale,
                settings.PlayerPosture,
                settings.PlayerHabits,
                settings.HabitPausesPerMinute,
                settings.MeleeForCommon,
                settings.GoalLoot,
                settings.MegaSpawnHunt,
                settings.BotBeds,
                settings.BotHomeCommand,
                settings.RespawnRush,
                settings.CloseAimTracking,
                settings.FightSmallGroups,
                settings.LadderDescend,
                settings.UsefulWeaponsOnly,
                settings.KeepMeleeInHand,
                settings.TryhardGoals,
                settings.AreaOverlook,
                settings.LeaveZombiesBehind,
                settings.JumpLinks,
                settings.CrouchLinks,
                settings.MegaNoLure,
                settings.LootLookFurnitureOnly,
                settings.SkipContainers);
            return settings;
        }

        private static string? ReadRaw(IConfiguration configuration, ILogger logger, string key)
        {
            try
            {
                var raw = configuration[Section + key];
                return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            }
            catch (Exception exception)
            {
                logger.LogWarning("Nao deu para ler serverBot:behavior:{Key} ({Error}); usando o padrao.", key, exception.Message);
                return null;
            }
        }

        private static bool ReadBool(IConfiguration configuration, ILogger logger, string key, bool fallback)
        {
            var raw = ReadRaw(configuration, logger, key);
            if (raw == null)
            {
                return fallback;
            }
            if (bool.TryParse(raw, out var value))
            {
                return value;
            }
            logger.LogWarning(
                "serverBot:behavior:{Key} = '{Value}' nao e true nem false; usando {Default}.",
                key,
                raw,
                fallback);
            return fallback;
        }

        private static float ReadFloat(IConfiguration configuration, ILogger logger, string key, float fallback, float minimum, float maximum)
        {
            var raw = ReadRaw(configuration, logger, key);
            if (raw == null)
            {
                return fallback;
            }
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && !float.IsNaN(value)
                && !float.IsInfinity(value))
            {
                var clamped = Math.Max(minimum, Math.Min(maximum, value));
                if (clamped != value)
                {
                    logger.LogWarning(
                        "serverBot:behavior:{Key} = {Value} fora de {Minimum}-{Maximum}; usando {Clamped}.",
                        key,
                        value,
                        minimum,
                        maximum,
                        clamped);
                }
                return clamped;
            }
            logger.LogWarning(
                "serverBot:behavior:{Key} = '{Value}' nao e numero; usando {Default}.",
                key,
                raw,
                fallback);
            return fallback;
        }
    }
}
