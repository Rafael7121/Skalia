using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // 0.1.170: records how the real players on the server move, so the bots
    // can be tuned on a person playing rather than on guesses. One CSV per
    // player per session in Servers/<server>/SkaliaRecordings, a row every
    // 0.08s (one input packet) while something changes and once a second
    // while standing still. Bots only with /gravar bots ligar (0.2.0). /gravar marca <texto>
    // puts a note in the file; /gravar parar and /gravar ligar switch it.
    // 0.1.171 (the tester: "o por que das coisas"): each row also says what was
    // around: the zombies by distance and side of the aim and which ones
    // follow the player, what stands in front at knee, waist and head
    // height, a roof overhead, what the crosshair is on, the nearest item
    // in sight, day or night, food and water; and as events every hit and
    // kill on a zombie, every hit taken, and every door opened or closed.
    internal static class SkaliaPlayerRecorder
    {
        private const float RowSeconds = 0.08f;
        private const float IdleRowSeconds = 1f;
        private const float FlushSeconds = 2f;
        private const float ZombieMeters = 40f;
        private const string Header =
            "t,x,y,z,yaw,pitch,postura,no_chao,frente,lado,vertical,pulo,correr,agachar,deitar,"
            + "ataque1,ataque2,arma,vida,folego,comida,agua,sangrando,dia,"
            + "zumbi_m,zumbi_alvo,zumbis_15m,zumbis_seguindo,zumbis,frente_obst,teto,mira,item_perto,evento";
        private const float ItemSeconds = 0.5f;
        private const float ItemMeters = 8f;
        private const float DoorSeconds = 0.25f;
        private const float DoorMeters = 2.5f;
        private const float AimMeters = 60f;
        private static readonly float[] s_FrontHeights = { 0.4f, 1.0f, 1.6f };
        private static readonly Collider[] s_DoorColliders = new Collider[64];
        private static readonly List<KeyValuePair<float, Zombie>> s_Zombies = new List<KeyValuePair<float, Zombie>>();

        private sealed class Recording
        {
            public StreamWriter Writer = null!;
            public string File = "";
            public float StartedAt;
            public float LastRowAt = float.NegativeInfinity;
            public float LastFlushAt;
            public Vector3 LastPosition;
            public float LastYaw;
            public float LastPitch;
            public bool HasLast;
            public bool WasAlive = true;
            public bool Jump;
            public bool Sprint;
            public bool Crouch;
            public bool Prone;
            public bool Primary;
            public bool Secondary;
            public readonly StringBuilder Events = new StringBuilder();
            public string ItemNear = "";
            public float NextItemAt;
            public float NextDoorAt;
            public readonly Dictionary<int, bool> Doors = new Dictionary<int, bool>();
            public readonly List<Zombie> HitZombies = new List<Zombie>();
            // 0.2.0: rounds fired and reloads, read off the held gun's ammo.
            public bool IsBot;
            public long AmmoKey = -1;
            public int LastAmmo = -1;
            public int Shots;
        }

        // 0.2.0: the bots are recorded too, in the same format, only while
        // /gravar bots ligar is on (about 4.3 MB an hour per bot). Their
        // files carry _BOT_ in the name and go after BotFileDays days.
        // x19: on from the start (the tester: no typing it every test);
        // /gravar bots parar still turns it off until the next load.
        private const int BotFileDays = 7;
        public static bool BotsEnabled { get; private set; } = true;

        private static readonly Dictionary<ulong, Recording> s_Recordings = new Dictionary<ulong, Recording>();
        private static readonly List<ulong> s_Gone = new List<ulong>();
        private static int s_Generation;
        private static bool s_Running;
        private static ILogger? s_Logger;
        private static float s_NextErrorLogAt;
        // 0.1.171: also shown by /gravar. (Two more fields also keep the
        // Field table off 2047 rows: at exactly 2047 OpenMod's hotloader
        // misread the assembly name and the plugin did not load.)
        private static long s_RowsWritten;
        private static int s_FilesOpened;

        public static long RowsWritten => s_RowsWritten;

        public static int FilesOpened => s_FilesOpened;

        public static bool Enabled { get; private set; } = true;

        public static int ActiveCount => s_Recordings.Count;

        public static string RecordingsDirectory
        {
            get
            {
                try
                {
                    return ReadWrite.PATH + ServerSavedata.transformPath("/SkaliaRecordings");
                }
                catch
                {
                    return Path.Combine(ReadWrite.PATH, "SkaliaRecordings");
                }
            }
        }

        public static void Start(ILogger logger)
        {
            if (s_Running)
            {
                return;
            }
            s_Running = true;
            s_Logger = logger;
            ItemManager.onTakeItemRequested += OnTakeItemRequested;
            DamageTool.damageZombieRequested += OnDamageZombieRequested;
            DamageTool.damagePlayerRequested += OnDamagePlayerRequested;
            InteractableDoor.OnDoorChanged_Global += OnDoorChanged;
            UseableConsumeable.onConsumePerformed += OnConsumePerformed;
            DeleteOldBotFiles();
            logger.LogInformation("Gravador de movimentos ligado; bots: {Bots}.", BotsEnabled ? "gravando" : "nao");
            RunAsync(++s_Generation).Forget();
        }

        // Called on plugin unload so the loop of an old copy of the DLL stops.
        public static void Stop()
        {
            if (!s_Running)
            {
                return;
            }
            s_Running = false;
            s_Generation++;
            ItemManager.onTakeItemRequested -= OnTakeItemRequested;
            DamageTool.damageZombieRequested -= OnDamageZombieRequested;
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;
            InteractableDoor.OnDoorChanged_Global -= OnDoorChanged;
            UseableConsumeable.onConsumePerformed -= OnConsumePerformed;
            CloseAll();
        }

        public static void SetBotsEnabled(bool enabled)
        {
            BotsEnabled = enabled;
            if (enabled)
            {
                return;
            }
            s_Gone.Clear();
            foreach (var pair in s_Recordings)
            {
                if (pair.Value.IsBot)
                {
                    s_Gone.Add(pair.Key);
                }
            }
            foreach (var steamId in s_Gone)
            {
                var recording = s_Recordings[steamId];
                AddEvent(recording, "gravacao de bots desligada");
                try
                {
                    WriteRow(null, recording, Time.realtimeSinceStartup, true);
                }
                catch
                {
                    // Closing anyway.
                }
                Close(recording);
                s_Recordings.Remove(steamId);
            }
            s_Gone.Clear();
        }

        private static void DeleteOldBotFiles()
        {
            try
            {
                var directory = RecordingsDirectory;
                if (!Directory.Exists(directory))
                {
                    return;
                }
                var limit = System.DateTime.Now.AddDays(-BotFileDays);
                var deleted = 0;
                foreach (var file in Directory.GetFiles(directory, "*_BOT_*.csv"))
                {
                    if (File.GetLastWriteTime(file) < limit)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                if (deleted > 0)
                {
                    s_Logger?.LogInformation(
                        "Gravador de movimentos: {Count} gravacao(oes) de bot com mais de {Days} dias apagada(s).",
                        deleted,
                        BotFileDays);
                }
            }
            catch (System.Exception exception)
            {
                s_Logger?.LogWarning("Gravador de movimentos: nao deu para apagar gravacoes antigas de bot ({Error}).", exception.Message);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (!enabled)
            {
                CloseAll();
            }
        }

        public static bool Mark(ulong steamId, string text)
        {
            if (!s_Recordings.TryGetValue(steamId, out var recording))
            {
                return false;
            }
            AddEvent(recording, "marca: " + text);
            return true;
        }

        private static async UniTaskVoid RunAsync(int generation)
        {
            while (generation == s_Generation)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                if (generation != s_Generation)
                {
                    return;
                }
                if (!Enabled || !Provider.isServer || !Level.isLoaded)
                {
                    continue;
                }
                try
                {
                    Tick();
                }
                catch (System.Exception exception)
                {
                    if (Time.realtimeSinceStartup >= s_NextErrorLogAt)
                    {
                        s_NextErrorLogAt = Time.realtimeSinceStartup + 60f;
                        s_Logger?.LogWarning(exception, "Gravador de movimentos: erro ao gravar.");
                    }
                }
            }
        }

        private static void Tick()
        {
            var now = Time.realtimeSinceStartup;
            s_Gone.Clear();
            foreach (var steamId in s_Recordings.Keys)
            {
                s_Gone.Add(steamId);
            }
            foreach (var client in Provider.clients)
            {
                var isBot = client != null && client.transportConnection is SkaliaBotTransportConnection;
                if (client == null
                    || client.player == null
                    || (isBot && !BotsEnabled))
                {
                    continue;
                }
                var steamId = client.playerID.steamID.m_SteamID;
                s_Gone.Remove(steamId);
                if (!s_Recordings.TryGetValue(steamId, out var recording))
                {
                    recording = Open(client, now, isBot);
                    if (recording == null)
                    {
                        continue;
                    }
                    s_Recordings[steamId] = recording;
                }
                Sample(client.player, recording, now);
            }
            foreach (var steamId in s_Gone)
            {
                if (s_Recordings.TryGetValue(steamId, out var recording))
                {
                    AddEvent(recording, "saiu do servidor");
                    WriteRow(null, recording, now, true);
                    Close(recording);
                    s_Recordings.Remove(steamId);
                }
            }
        }

        private static Recording? Open(SteamPlayer client, float now, bool isBot)
        {
            var directory = RecordingsDirectory;
            System.IO.Directory.CreateDirectory(directory);
            var name = new StringBuilder();
            foreach (var character in client.playerID.characterName ?? "jogador")
            {
                name.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            }
            var file = Path.Combine(
                directory,
                System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
                + (isBot ? "_BOT_" : "_") + name + "_" + client.playerID.steamID.m_SteamID + ".csv");
            var recording = new Recording
            {
                Writer = new StreamWriter(file, false, new UTF8Encoding(false)),
                File = file,
                StartedAt = now,
                LastFlushAt = now,
                IsBot = isBot
            };
            recording.Writer.WriteLine(Header);
            s_FilesOpened++;
            AddEvent(recording, "inicio, mapa " + Provider.map);
            s_Logger?.LogInformation(
                "Gravador de movimentos: gravando {Name} em {File}.",
                client.playerID.characterName,
                file);
            return recording;
        }

        private static void Sample(Player player, Recording recording, float now)
        {
            var alive = player.life != null && player.life.IsAlive;
            if (!alive)
            {
                if (recording.WasAlive)
                {
                    recording.WasAlive = false;
                    AddEvent(recording, "morreu");
                    WriteRow(player, recording, now, true);
                }
                recording.HasLast = false;
                return;
            }
            if (!recording.WasAlive)
            {
                recording.WasAlive = true;
                AddEvent(recording, "nasceu");
            }

            // Input packets arrive every 0.08s and these hold the last one:
            // reading every frame and keeping any "on" until the row is
            // written misses no jump or shot.
            var keys = player.input != null ? player.input.keys : null;
            if (keys != null && keys.Length > 5)
            {
                recording.Jump |= keys[0];
                recording.Crouch |= keys[3];
                recording.Prone |= keys[4];
                recording.Sprint |= keys[5];
            }
            if (player.input != null)
            {
                recording.Primary |= (player.input.pendingPrimaryAttackInput & EAttackInputFlags.Start) != 0;
                recording.Secondary |= (player.input.pendingSecondaryAttackInput & EAttackInputFlags.Start) != 0;
            }

            CheckKills(recording);
            TrackAmmo(player, recording);
            if (now >= recording.NextDoorAt)
            {
                recording.NextDoorAt = now + DoorSeconds;
                CheckDoors(player, recording);
            }
            if (now >= recording.NextItemAt)
            {
                recording.NextItemAt = now + ItemSeconds;
                recording.ItemNear = FindNearestItem(player);
            }

            if (now - recording.LastRowAt < RowSeconds)
            {
                return;
            }
            var position = player.transform.position;
            var changed = !recording.HasLast
                || recording.Events.Length > 0
                || recording.Shots > 0
                || (position - recording.LastPosition).sqrMagnitude > 0.0004f
                || Mathf.Abs(Mathf.DeltaAngle(player.look.yaw, recording.LastYaw)) > 0.5f
                || Mathf.Abs(Mathf.DeltaAngle(player.look.pitch, recording.LastPitch)) > 0.5f
                || recording.Jump
                || recording.Primary
                || recording.Secondary;
            if (!changed && now - recording.LastRowAt < IdleRowSeconds)
            {
                return;
            }
            WriteRow(player, recording, now, false);
        }

        private static void WriteRow(Player? player, Recording recording, float now, bool force)
        {
            if (recording.Shots > 0)
            {
                AddEvent(recording, "atirou " + recording.Shots.ToString(CultureInfo.InvariantCulture));
                recording.Shots = 0;
            }
            var line = new StringBuilder(160);
            var t = now - recording.StartedAt;
            line.Append(t.ToString("0.00", CultureInfo.InvariantCulture));
            if (player != null && player.life != null && player.life.IsAlive)
            {
                var position = player.transform.position;
                var yaw = player.look.yaw;
                var pitch = player.look.pitch;
                var forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                var right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
                var dt = Mathf.Max(0.01f, now - recording.LastRowAt);
                var velocity = recording.HasLast ? (position - recording.LastPosition) / dt : Vector3.zero;
                var flat = new Vector3(velocity.x, 0f, velocity.z);
                FindNearestZombie(player, position, out var zombieMeters, out var zombieTargets);
                Append(line, position.x, "0.00");
                Append(line, position.y, "0.00");
                Append(line, position.z, "0.00");
                Append(line, yaw, "0.0");
                Append(line, pitch, "0.0");
                line.Append(',').Append(player.stance != null ? player.stance.stance.ToString() : "");
                line.Append(',').Append(player.movement != null && player.movement.isGrounded ? '1' : '0');
                Append(line, Vector3.Dot(flat, forward), "0.00");
                Append(line, Vector3.Dot(flat, right), "0.00");
                Append(line, velocity.y, "0.00");
                line.Append(',').Append(recording.Jump ? '1' : '0');
                line.Append(',').Append(recording.Sprint ? '1' : '0');
                line.Append(',').Append(recording.Crouch ? '1' : '0');
                line.Append(',').Append(recording.Prone ? '1' : '0');
                line.Append(',').Append(recording.Primary ? '1' : '0');
                line.Append(',').Append(recording.Secondary ? '1' : '0');
                line.Append(',').Append(Clean(player.equipment != null && player.equipment.asset != null
                    ? player.equipment.asset.itemName
                    : ""));
                line.Append(',').Append(player.life.health.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(player.life.stamina.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(player.life.food.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(player.life.water.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(player.life.isBleeding ? '1' : '0');
                line.Append(',').Append(LightingManager.isDaytime ? '1' : '0');
                Append(line, zombieMeters, "0.0");
                line.Append(',').Append(zombieTargets ? '1' : '0');
                var zombies = DescribeZombies(player, position, yaw, out var within15, out var following);
                line.Append(',').Append(within15.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(following.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(zombies);
                line.Append(',').Append(DescribeFront(player, position, flat, forward));
                line.Append(',').Append(HasRoof(position) ? '1' : '0');
                line.Append(',').Append(DescribeAim(player));
                line.Append(',').Append(Clean(recording.ItemNear));
                recording.LastPosition = position;
                recording.LastYaw = yaw;
                recording.LastPitch = pitch;
                recording.HasLast = true;
            }
            else
            {
                line.Append(',', 32);
            }
            line.Append(',').Append(Clean(recording.Events.ToString()));
            recording.Writer.WriteLine(line.ToString());
            s_RowsWritten++;
            recording.Events.Clear();
            recording.LastRowAt = now;
            recording.Jump = false;
            recording.Sprint = false;
            recording.Crouch = false;
            recording.Prone = false;
            recording.Primary = false;
            recording.Secondary = false;
            if (force || now - recording.LastFlushAt >= FlushSeconds)
            {
                recording.LastFlushAt = now;
                recording.Writer.Flush();
            }
        }

        private static void FindNearestZombie(Player player, Vector3 position, out float meters, out bool targetsPlayer)
        {
            meters = -1f;
            targetsPlayer = false;
            if (ZombieManager.regions == null
                || player.movement == null
                || player.movement.bound >= ZombieManager.regions.Length)
            {
                return;
            }
            var region = ZombieManager.regions[player.movement.bound];
            if (region == null || region.zombies == null)
            {
                return;
            }
            var best = ZombieMeters;
            foreach (var zombie in region.zombies)
            {
                if (zombie == null || zombie.isDead)
                {
                    continue;
                }
                var distance = (zombie.transform.position - position).magnitude;
                if (distance < best)
                {
                    best = distance;
                    meters = distance;
                    targetsPlayer = zombie.player == player;
                }
            }
        }

        // Up to 4 zombies within 40m, nearest first: distance, side of the
        // aim in degrees (+ right, - left, 180 behind), A when it is after
        // this player, height when 2m or more off, kind when not a normal one.
        private static string DescribeZombies(Player player, Vector3 position, float yaw, out int within15, out int following)
        {
            within15 = 0;
            following = 0;
            s_Zombies.Clear();
            if (ZombieManager.regions == null
                || player.movement == null
                || player.movement.bound >= ZombieManager.regions.Length)
            {
                return "";
            }
            var region = ZombieManager.regions[player.movement.bound];
            if (region == null || region.zombies == null)
            {
                return "";
            }
            foreach (var zombie in region.zombies)
            {
                if (zombie == null || zombie.isDead)
                {
                    continue;
                }
                var distance = (zombie.transform.position - position).magnitude;
                if (distance > ZombieMeters)
                {
                    continue;
                }
                if (distance <= 15f)
                {
                    within15++;
                }
                if (zombie.player == player)
                {
                    following++;
                }
                s_Zombies.Add(new KeyValuePair<float, Zombie>(distance, zombie));
            }
            s_Zombies.Sort((a, b) => a.Key.CompareTo(b.Key));
            var text = new StringBuilder();
            for (var i = 0; i < s_Zombies.Count && i < 4; i++)
            {
                var zombie = s_Zombies[i].Value;
                var offset = zombie.transform.position - position;
                var bearing = Mathf.DeltaAngle(yaw, Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg);
                if (text.Length > 0)
                {
                    text.Append(' ');
                }
                text.Append(s_Zombies[i].Key.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append("m@")
                    .Append(Mathf.RoundToInt(bearing).ToString(CultureInfo.InvariantCulture));
                if (zombie.player == player)
                {
                    text.Append('A');
                }
                if (Mathf.Abs(offset.y) >= 2f)
                {
                    text.Append('^').Append(offset.y.ToString("0", CultureInfo.InvariantCulture));
                }
                if (zombie.speciality != EZombieSpeciality.NORMAL)
                {
                    text.Append('/').Append(zombie.speciality.ToString());
                }
            }
            return text.ToString();
        }

        // What stands in the way, in the direction the body moves (or faces
        // when still), at knee, waist and head height, up to 4m:
        // "1.2/1.2/-" is a waist-high fence 1.2m ahead (a hop), "1.2/1.2/1.2"
        // a wall, then the name of the nearest thing hit.
        private static string DescribeFront(Player player, Vector3 position, Vector3 flatVelocity, Vector3 forward)
        {
            var direction = flatVelocity.sqrMagnitude > 0.25f ? flatVelocity.normalized : forward;
            var text = new StringBuilder();
            var nearest = float.PositiveInfinity;
            string nearestName = "";
            for (var i = 0; i < s_FrontHeights.Length; i++)
            {
                if (i > 0)
                {
                    text.Append('/');
                }
                if (Physics.Raycast(
                        position + Vector3.up * s_FrontHeights[i],
                        direction,
                        out var hit,
                        4f,
                        RayMasks.BLOCK_COLLISION,
                        QueryTriggerInteraction.Ignore))
                {
                    text.Append(hit.distance.ToString("0.0", CultureInfo.InvariantCulture));
                    if (hit.distance < nearest)
                    {
                        nearest = hit.distance;
                        nearestName = DescribeCollider(hit.collider);
                    }
                }
                else
                {
                    text.Append('-');
                }
            }
            if (nearestName.Length > 0)
            {
                text.Append(' ').Append(nearestName);
            }
            return text.ToString();
        }

        private static bool HasRoof(Vector3 position)
        {
            return Physics.Raycast(
                position + Vector3.up * 1.9f,
                Vector3.up,
                20f,
                RayMasks.BLOCK_COLLISION,
                QueryTriggerInteraction.Ignore);
        }

        // What the crosshair is on, up to 60m.
        private static string DescribeAim(Player player)
        {
            if (player.look == null || player.look.aim == null)
            {
                return "";
            }
            var aim = player.look.aim;
            if (!Physics.Raycast(
                    aim.position,
                    aim.forward,
                    out var hit,
                    AimMeters,
                    RayMasks.DAMAGE_SERVER | RayMasks.ITEM | RayMasks.BLOCK_COLLISION,
                    QueryTriggerInteraction.Ignore))
            {
                return "nada";
            }
            var distance = hit.distance.ToString("0.0", CultureInfo.InvariantCulture) + "m";
            var zombie = DamageTool.getZombie(hit.transform);
            if (zombie != null)
            {
                return "zumbi " + distance;
            }
            var other = DamageTool.getPlayer(hit.transform);
            if (other != null && other != player)
            {
                return "jogador " + distance;
            }
            return DescribeCollider(hit.collider) + " " + distance;
        }

        private static string DescribeCollider(Collider collider)
        {
            if (collider == null)
            {
                return "";
            }
            var layer = collider.gameObject.layer;
            var kind = layer == LayerMasks.GROUND || layer == LayerMasks.GROUND2
                ? "chao"
                : layer == LayerMasks.BARRICADE
                    ? "barricada"
                    : layer == LayerMasks.STRUCTURE
                        ? "estrutura"
                        : layer == LayerMasks.VEHICLE
                            ? "veiculo"
                            : layer == LayerMasks.ITEM
                                ? "item"
                                : layer == LayerMasks.RESOURCE
                                    ? "arvore/pedra"
                                    : "objeto";
            if (kind == "chao")
            {
                return kind;
            }
            var name = collider.transform.root != null ? collider.transform.root.name : collider.name;
            return Clean(kind + ":" + name).Replace(' ', '_');
        }

        // The nearest item on the ground within 8m that the player can see:
        // name, distance, side of the aim, height.
        private static string FindNearestItem(Player player)
        {
            if (ItemManager.regions == null
                || player.look == null
                || player.look.aim == null
                || !Regions.tryGetCoordinate(player.transform.position, out var cx, out var cy))
            {
                return "";
            }
            var position = player.transform.position;
            var eye = player.look.aim.position;
            ItemData? best = null;
            var bestDistance = ItemMeters;
            for (var x = cx - 1; x <= cx + 1; x++)
            {
                for (var y = cy - 1; y <= cy + 1; y++)
                {
                    if (x < 0 || y < 0 || !Regions.checkSafe(x, y))
                    {
                        continue;
                    }
                    var region = ItemManager.regions[x, y];
                    if (region == null || region.items == null)
                    {
                        continue;
                    }
                    foreach (var item in region.items)
                    {
                        var distance = (item.point - position).magnitude;
                        if (distance >= bestDistance
                            || Physics.Linecast(
                                eye,
                                item.point + Vector3.up * 0.15f,
                                RayMasks.BLOCK_COLLISION,
                                QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }
                        best = item;
                        bestDistance = distance;
                    }
                }
            }
            if (best == null)
            {
                return "";
            }
            var offset = best.point - position;
            var bearing = Mathf.DeltaAngle(player.look.yaw, Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg);
            var asset = best.item != null ? best.item.GetAsset() : null;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:0.0}m@{2:0} alt {3:0.0}",
                asset != null ? asset.itemName : "item",
                bestDistance,
                bearing,
                offset.y);
        }

        // Map doors (and gates, lockers) are polled: there is no event for
        // them. Player-built doors come from OnDoorChanged.
        private static void CheckDoors(Player player, Recording recording)
        {
            var count = Physics.OverlapSphereNonAlloc(
                player.transform.position + Vector3.up,
                DoorMeters,
                s_DoorColliders,
                RayMasks.BLOCK_COLLISION,
                QueryTriggerInteraction.Collide);
            if (recording.Doors.Count > 300)
            {
                recording.Doors.Clear();
            }
            for (var i = 0; i < count; i++)
            {
                var collider = s_DoorColliders[i];
                if (collider == null)
                {
                    continue;
                }
                var state = collider.GetComponentInParent<InteractableObjectBinaryState>();
                if (state == null)
                {
                    continue;
                }
                var id = state.GetInstanceID();
                var used = state.isUsed;
                if (recording.Doors.TryGetValue(id, out var before) && before != used)
                {
                    AddEvent(recording, (used ? "abriu/ligou " : "fechou/desligou ") + Clean(state.transform.name).Replace(' ', '_'));
                }
                recording.Doors[id] = used;
            }
        }

        private static void OnDoorChanged(InteractableDoor door)
        {
            if (door == null)
            {
                return;
            }
            Recording? closest = null;
            var closestDistance = 4f;
            foreach (var client in Provider.clients)
            {
                if (client?.player == null
                    || !s_Recordings.TryGetValue(client.playerID.steamID.m_SteamID, out var recording))
                {
                    continue;
                }
                var distance = (client.player.transform.position - door.transform.position).magnitude;
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = recording;
                }
            }
            if (closest != null)
            {
                AddEvent(closest, (door.isOpen ? "abriu porta construida a " : "fechou porta construida a ")
                    + closestDistance.ToString("0.0", CultureInfo.InvariantCulture) + "m");
            }
        }

        private static void OnDamageZombieRequested(ref DamageZombieParameters parameters, ref bool shouldAllow)
        {
            var zombie = parameters.zombie;
            if (zombie == null
                || zombie.isDead
                || !(parameters.instigator is Player player)
                || player.channel == null
                || player.channel.owner == null
                || !s_Recordings.TryGetValue(player.channel.owner.playerID.steamID.m_SteamID, out var recording))
            {
                return;
            }
            var distance = (zombie.transform.position - player.transform.position).magnitude;
            AddEvent(recording, string.Format(
                CultureInfo.InvariantCulture,
                "acertou zumbi{0} {1} a {2:0.0}m dano ~{3:0} (vida dele {4:0}/{5:0})",
                zombie.speciality != EZombieSpeciality.NORMAL ? "/" + zombie.speciality : "",
                parameters.limb,
                distance,
                parameters.damage * parameters.times,
                zombie.GetHealth(),
                zombie.GetMaxHealth()));
            if (!recording.HitZombies.Contains(zombie))
            {
                recording.HitZombies.Add(zombie);
            }
        }

        private static void CheckKills(Recording recording)
        {
            for (var i = recording.HitZombies.Count - 1; i >= 0; i--)
            {
                var zombie = recording.HitZombies[i];
                if (zombie == null)
                {
                    recording.HitZombies.RemoveAt(i);
                }
                else if (zombie.isDead)
                {
                    AddEvent(recording, "matou zumbi" + (zombie.speciality != EZombieSpeciality.NORMAL ? "/" + zombie.speciality : ""));
                    recording.HitZombies.RemoveAt(i);
                }
            }
            if (recording.HitZombies.Count > 20)
            {
                recording.HitZombies.RemoveAt(0);
            }
        }

        private static void OnDamagePlayerRequested(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            var player = parameters.player;
            if (player == null
                || player.channel == null
                || player.channel.owner == null
                || !s_Recordings.TryGetValue(player.channel.owner.playerID.steamID.m_SteamID, out var recording))
            {
                return;
            }
            AddEvent(recording, string.Format(
                CultureInfo.InvariantCulture,
                "levou {0} {1} ~{2:0}",
                parameters.cause,
                parameters.limb,
                parameters.damage * parameters.times));
        }

        private static void OnTakeItemRequested(
            Player player,
            byte x,
            byte y,
            uint instanceID,
            byte to_x,
            byte to_y,
            byte to_rot,
            byte to_page,
            ItemData itemData,
            ref bool shouldAllow)
        {
            if (player == null
                || itemData == null
                || player.channel == null
                || player.channel.owner == null
                || !s_Recordings.TryGetValue(player.channel.owner.playerID.steamID.m_SteamID, out var recording))
            {
                return;
            }
            var offset = itemData.point - player.transform.position;
            var flat = new Vector2(offset.x, offset.z).magnitude;
            var asset = itemData.item != null ? itemData.item.GetAsset() : null;
            AddEvent(recording, string.Format(
                CultureInfo.InvariantCulture,
                "pegou {0} a {1:0.0}m (altura {2:0.0}m)",
                asset != null ? asset.itemName : "item " + itemData.item?.id.ToString(CultureInfo.InvariantCulture),
                flat,
                offset.y));
        }

        // 0.2.0: the held gun's rounds: fewer is a shot, more (same gun in
        // the same place) is a reload. A different gun starts over.
        private static void TrackAmmo(Player player, Recording recording)
        {
            var equipment = player.equipment;
            if (equipment == null
                || !(equipment.asset is ItemGunAsset gun)
                || equipment.state == null
                || equipment.state.Length <= 10)
            {
                recording.AmmoKey = -1;
                recording.LastAmmo = -1;
                return;
            }
            var key = ((long)gun.id << 24) | ((long)equipment.equippedPage << 16) | ((long)equipment.equipped_x << 8) | equipment.equipped_y;
            int ammo = equipment.state[10];
            if (key != recording.AmmoKey || recording.LastAmmo < 0)
            {
                recording.AmmoKey = key;
                recording.LastAmmo = ammo;
                return;
            }
            if (ammo < recording.LastAmmo)
            {
                var perShot = Mathf.Max(1, (int)gun.ammoPerShot);
                recording.Shots += Mathf.Max(1, (recording.LastAmmo - ammo) / perShot);
            }
            else if (ammo > recording.LastAmmo)
            {
                AddEvent(recording, "recarregou " + gun.itemName + " "
                    + recording.LastAmmo.ToString(CultureInfo.InvariantCulture) + "->"
                    + ammo.ToString(CultureInfo.InvariantCulture));
            }
            recording.LastAmmo = ammo;
        }

        // 0.2.0: a bandage, a pill, food or drink used (on itself).
        private static void OnConsumePerformed(Player instigatingPlayer, ItemConsumeableAsset consumeableAsset)
        {
            if (instigatingPlayer == null
                || consumeableAsset == null
                || instigatingPlayer.channel == null
                || instigatingPlayer.channel.owner == null
                || !s_Recordings.TryGetValue(instigatingPlayer.channel.owner.playerID.steamID.m_SteamID, out var recording))
            {
                return;
            }
            AddEvent(recording, "usou " + consumeableAsset.itemName);
        }

        private static void AddEvent(Recording recording, string text)
        {
            if (recording.Events.Length > 0)
            {
                recording.Events.Append(" | ");
            }
            recording.Events.Append(text);
        }

        private static void Append(StringBuilder line, float value, string format)
        {
            line.Append(',').Append(value.ToString(format, CultureInfo.InvariantCulture));
        }

        private static string Clean(string text)
        {
            return text.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ').Replace('"', '\'');
        }

        private static void Close(Recording recording)
        {
            try
            {
                recording.Writer.Flush();
                recording.Writer.Dispose();
            }
            catch
            {
                // The file stays as far as it was written.
            }
        }

        private static void CloseAll()
        {
            foreach (var recording in s_Recordings.Values)
            {
                AddEvent(recording, "gravacao encerrada");
                try
                {
                    WriteRow(null, recording, Time.realtimeSinceStartup, true);
                }
                catch
                {
                    // Closing anyway.
                }
                Close(recording);
            }
            s_Recordings.Clear();
        }
    }
}
