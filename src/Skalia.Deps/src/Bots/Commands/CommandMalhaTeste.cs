using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.AI;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps.Commands
{
    // Experimento: gera uma malha da Unity a partir dos colisores reais em
    // volta do jogador, com o corpo do bot, e compara com a malha dos
    // zumbis. Nao muda a navegacao; a malha e descartada no fim.
    [Command("malhateste")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandMalhaTeste : UnturnedCommand
    {
        private static readonly int SourceMask = RayMasks.BLOCK_COLLISION | RayMasks.GROUND | RayMasks.GROUND2;
        private static readonly int MovingMask = RayMasks.PLAYER | RayMasks.ENEMY | RayMasks.AGENT | RayMasks.VEHICLE;
        private static bool s_Running;
        private static float s_StepOffset = 0.55f;
        private static float s_Slope = 59f;
        private readonly ILogger m_Logger;

        public CommandMalhaTeste(IServiceProvider serviceProvider, ILogger<CommandMalhaTeste> logger)
            : base(serviceProvider)
        {
            m_Logger = logger;
        }

        protected override async UniTask OnExecuteAsync()
        {
            var half = 100f;
            if (Context.Parameters.Length > 0
                && (!float.TryParse(Context.Parameters[0], NumberStyles.Float, CultureInfo.InvariantCulture, out half)
                    || half < 10f
                    || half > 150f))
            {
                await PrintAsync("Uso: /malhateste [meio lado em metros, 10 a 150; padrao 100]");
                return;
            }
            if (s_Running)
            {
                await PrintAsync("Ja tem um teste de malha rodando.");
                return;
            }
            s_Running = true;
            try
            {
                await UniTask.SwitchToMainThread();
                var player = ((UnturnedUser)Context.Actor).Player.Player;
                var center = player.transform.position;
                var controller = player.movement.controller;
                s_StepOffset = controller != null ? controller.stepOffset : 0.55f;
                s_Slope = Level.info?.configData != null && Level.info.configData.Max_Walkable_Slope > -0.5f
                    ? Level.info.configData.Max_Walkable_Slope
                    : 59f;
                await RunAsync(center, half, async line =>
                {
                    m_Logger.LogInformation("Teste de malha: {Line}", line);
                    await PrintAsync(line);
                    // A Unity so aceita as chamadas da malha na thread principal.
                    await UniTask.SwitchToMainThread();
                });
            }
            finally
            {
                s_Running = false;
            }
        }

        private static async UniTask RunAsync(Vector3 center, float half, Func<string, UniTask> report)
        {
            // 40m acima e abaixo do jogador: o bunker tem salas 27m abaixo
            // da entrada.
            var bounds = new Bounds(center, new Vector3(half * 2f, 80f, half * 2f));
            var memoryBefore = GC.GetTotalMemory(false);

            var collect = Stopwatch.StartNew();
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                bounds,
                SourceMask,
                NavMeshCollectGeometry.PhysicsColliders,
                0,
                new List<NavMeshBuildMarkup>(),
                sources);
            // Portas abrem (o bot ja sabe abrir) e gente, zumbi e carro mudam
            // de lugar: nada disso vira parede na malha.
            var doors = 0;
            var movers = 0;
            sources.RemoveAll(source =>
            {
                if (!(source.component is Component component) || component == null)
                {
                    return false;
                }
                if (((1 << component.gameObject.layer) & MovingMask) != 0)
                {
                    movers++;
                    return true;
                }
                if (component.GetComponentInParent<InteractableObjectBinaryState>() != null)
                {
                    doors++;
                    return true;
                }
                return false;
            });
            collect.Stop();
            var meshes = 0;
            var unreadable = 0;
            var terrains = 0;
            var primitives = 0;
            foreach (var source in sources)
            {
                if (source.shape == NavMeshBuildSourceShape.Mesh)
                {
                    meshes++;
                    if (source.sourceObject is Mesh mesh && !mesh.isReadable)
                    {
                        unreadable++;
                    }
                }
                else if (source.shape == NavMeshBuildSourceShape.Terrain)
                {
                    terrains++;
                }
                else
                {
                    primitives++;
                }
            }
            await report(string.Format(CultureInfo.InvariantCulture,
                "degrau {12:0.00}m, rampa {13:0} graus; area {0:0}x{0:0}m em ({1:0}, {2:0}, {3:0}); {4} colisores lidos em {5} ms ({6} malhas, {7} sem leitura, {8} terrenos, {9} caixas/capsulas/esferas); fora da malha: {10} de portas/armarios, {11} de jogadores/zumbis/carros.",
                half * 2f, center.x, center.y, center.z, sources.Count, collect.ElapsedMilliseconds,
                meshes, unreadable, terrains, primitives, doors, movers, s_StepOffset, s_Slope));

            // Corpo do jogador: raio da postura, degrau do controlador e a
            // rampa maxima que o jogo deixa andar (59 graus no padrao).
            var settings = NavMesh.CreateSettings();
            settings.agentRadius = PlayerStance.RADIUS;
            settings.agentHeight = 1.8f;
            settings.agentClimb = s_StepOffset;
            settings.agentSlope = s_Slope;
            settings.overrideVoxelSize = true;
            settings.voxelSize = 0.2f;
            // No servidor a versao assincrona chega a 100% e nunca termina:
            // a Unity nao roda a etapa que integra a malha no modo sem tela.
            // A sincrona trava o servidor enquanto gera, entao a area e curta.
            var build = Stopwatch.StartNew();
            var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            build.Stop();
            if (data == null)
            {
                await report("a Unity recusou gerar a malha (BuildNavMeshData voltou vazio).");
                return;
            }
            var instance = NavMesh.AddNavMeshData(data);
            var memoryAfter = GC.GetTotalMemory(false);

            var triangulation = NavMesh.CalculateTriangulation();
            var area = 0f;
            for (var index = 0; index + 2 < triangulation.indices.Length; index += 3)
            {
                var a = triangulation.vertices[triangulation.indices[index]];
                var b = triangulation.vertices[triangulation.indices[index + 1]];
                var c = triangulation.vertices[triangulation.indices[index + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            await report(string.Format(CultureInfo.InvariantCulture,
                "malha gerada em {0} ms (servidor parado esse tempo){1}; {2} triangulos, {3:0} m2; memoria gerenciada {4:+0.0;-0.0} MB (a malha em si fica fora dessa conta).",
                build.ElapsedMilliseconds, string.Empty, triangulation.indices.Length / 3, area,
                (memoryAfter - memoryBefore) / 1048576.0));

            var filter = new NavMeshQueryFilter { agentTypeID = settings.agentTypeID, areaMask = NavMesh.AllAreas };

            // Malha dos zumbis dentro da mesma area: quantos triangulos a
            // malha nova tambem cobre, e quantos dos que a auditoria marcou
            // como parede ela deixa de fora.
            var zombieTotal = 0;
            var zombieCovered = 0;
            var blockedTotal = 0;
            var blockedCovered = 0;
            var zombieCenters = new List<Vector3>();
            if (AstarPath.active?.data?.graphs != null)
            {
                foreach (var graph in AstarPath.active.data.graphs)
                {
                    if (!(graph is NavmeshBase navmesh))
                    {
                        continue;
                    }
                    navmesh.GetNodes(node =>
                    {
                        var position = (Vector3)node.position;
                        if (Mathf.Abs(position.x - center.x) > half
                            || Mathf.Abs(position.z - center.z) > half)
                        {
                            return;
                        }
                        var covered = NavMesh.SamplePosition(position, out _, 0.6f, filter);
                        zombieTotal++;
                        zombieCenters.Add(position);
                        if (covered)
                        {
                            zombieCovered++;
                        }
                        if (SkaliaBotNavmeshAudit.IsBodyBlocked(node))
                        {
                            blockedTotal++;
                            if (covered)
                            {
                                blockedCovered++;
                            }
                        }
                    });
                }
            }
            await report(string.Format(CultureInfo.InvariantCulture,
                "malha dos zumbis aqui: {0} triangulos, {1} ({2:0}%) tambem na malha nova; dos {3} marcados como parede pela auditoria, {4} continuam na malha nova{5}.",
                zombieTotal, zombieCovered, zombieTotal > 0 ? 100f * zombieCovered / zombieTotal : 0f,
                blockedTotal, blockedCovered,
                SkaliaBotNavmeshAudit.BodyAuditReady ? string.Empty : " (auditoria ainda nao rodou: chame um bot antes)"));

            var onMesh = NavMesh.SamplePosition(center, out var here, 1.5f, filter);
            var reachable = 0;
            var tried = 0;
            if (onMesh)
            {
                var path = new NavMeshPath();
                for (var step = 0; step < 8; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 45f, 0f) * Vector3.forward;
                    if (!NavMesh.SamplePosition(center + direction * half * 0.6f, out var far, 6f, filter))
                    {
                        continue;
                    }
                    tried++;
                    if (NavMesh.CalculatePath(here.position, far.position, filter, path)
                        && path.status == NavMeshPathStatus.PathComplete)
                    {
                        reachable++;
                    }
                }
            }
            await report(onMesh
                ? string.Format(CultureInfo.InvariantCulture,
                    "voce esta na malha nova ({0:0.0}m do ponto mais perto); rota completa para {1} de {2} pontos a {3:0}m em volta.",
                    (here.position - center).magnitude, reachable, tried, half * 0.6f)
                : "voce nao esta em cima da malha nova (nada a ate 1.5m).");

            await ComparePathsAsync(zombieCenters, filter, report);

            NavMesh.RemoveNavMeshData(instance);
            UnityEngine.Object.Destroy(data);
        }

        // Mesmas 40 viagens nas duas malhas, entre pontos sorteados da malha
        // dos zumbis a 15m ou mais um do outro. Cada trecho da rota e testado
        // com raios na altura do joelho e do peito: um trecho que bate em
        // parede e um trecho que o corpo nao atravessa.
        private static async UniTask ComparePathsAsync(
            List<Vector3> centers,
            NavMeshQueryFilter filter,
            Func<string, UniTask> report)
        {
            if (centers.Count < 10 || AstarPath.active == null)
            {
                return;
            }
            var random = new System.Random(12345);
            var mask = RayMasks.BLOCK_COLLISION;
            var unityPath = new NavMeshPath();
            int trips = 0, unityFound = 0, zombieFound = 0;
            int unitySegments = 0, unityWalls = 0, zombieSegments = 0, zombieWalls = 0;
            int unityWallTrips = 0, zombieWallTrips = 0;
            float unityLength = 0f, zombieLength = 0f;
            var names = new Dictionary<string, int>();
            var breaks = new Dictionary<string, int>();
            var nearby = new List<BotMapObject>();
            for (var attempt = 0; attempt < 400 && trips < 40; attempt++)
            {
                var from = centers[random.Next(centers.Count)];
                var to = centers[random.Next(centers.Count)];
                if ((from - to).GetHorizontalMagnitude() < 15f
                    || !NavMesh.SamplePosition(from, out var fromHit, 0.6f, filter)
                    || !NavMesh.SamplePosition(to, out var toHit, 0.6f, filter))
                {
                    continue;
                }
                trips++;
                var zombie = ABPath.Construct(from, to);
                AstarPath.StartPath(zombie);
                AstarPath.BlockUntilCalculated(zombie);
                var zombieOk = !zombie.error && zombie.vectorPath != null && zombie.vectorPath.Count > 1;
                var unityOk = NavMesh.CalculatePath(fromHit.position, toHit.position, filter, unityPath)
                    && unityPath.status == NavMeshPathStatus.PathComplete;
                if (zombieOk && !unityOk)
                {
                    var reason = DescribeBreak(fromHit.position, zombie.vectorPath!, filter, unityPath, nearby);
                    breaks.TryGetValue(reason, out var seen);
                    breaks[reason] = seen + 1;
                }
                if (!zombieOk || !unityOk)
                {
                    if (zombieOk) zombieFound++;
                    if (unityOk) unityFound++;
                    continue;
                }
                zombieFound++;
                unityFound++;
                var unityHits = CountWalls(unityPath.corners, mask, ref unitySegments, ref unityLength, null);
                var zombieHits = CountWalls(zombie.vectorPath!.ToArray(), mask, ref zombieSegments, ref zombieLength, names);
                unityWalls += unityHits;
                zombieWalls += zombieHits;
                if (unityHits > 0) unityWallTrips++;
                if (zombieHits > 0) zombieWallTrips++;
            }
            var both = Math.Min(unityFound, zombieFound);
            await report(string.Format(CultureInfo.InvariantCulture,
                "{0} viagens sorteadas: malha nova achou rota em {1}, a dos zumbis em {2}.",
                trips, unityFound, zombieFound));
            await report(string.Format(CultureInfo.InvariantCulture,
                "malha nova: {0} trechos, {1} batem em parede, {2} viagens com parede, {3:0}m no total. Malha dos zumbis: {4} trechos, {5} batem em parede, {6} viagens com parede, {7:0}m no total.",
                unitySegments, unityWalls, unityWallTrips, unityLength,
                zombieSegments, zombieWalls, zombieWallTrips, zombieLength));
            if (names.Count > 0)
            {
                var list = new List<KeyValuePair<string, int>>(names);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var top = new List<string>();
                for (var index = 0; index < list.Count && index < 6; index++)
                {
                    top.Add(list[index].Key + " x" + list[index].Value);
                }
                await report("paredes atravessadas pela malha dos zumbis: " + string.Join(", ", top) + ".");
            }
            if (breaks.Count > 0)
            {
                await report("onde a malha nova perde a rota: " + Top(breaks, 12) + ".");
            }
        }

        private static string Top(Dictionary<string, int> counts, int limit)
        {
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var top = new List<string>();
            for (var index = 0; index < list.Count && index < limit; index++)
            {
                top.Add(list[index].Key + " x" + list[index].Value);
            }
            return string.Join("; ", top);
        }

        // Onde a malha nova perde a rota que a dos zumbis faz: o primeiro
        // ponto da rota dos zumbis que a malha nova nao alcanca a partir da
        // saida, e o objeto do catalogo mais perto dali.
        private static string DescribeBreak(
            Vector3 start,
            List<Vector3> zombiePath,
            NavMeshQueryFilter filter,
            NavMeshPath scratch,
            List<BotMapObject> nearby)
        {
            for (var index = 1; index < zombiePath.Count; index++)
            {
                var point = zombiePath[index];
                string kind;
                if (!NavMesh.SamplePosition(point, out var hit, 0.6f, filter))
                {
                    kind = "fora da malha nova";
                }
                else if (!NavMesh.CalculatePath(start, hit.position, filter, scratch)
                         || scratch.status != NavMeshPathStatus.PathComplete)
                {
                    kind = "sem ligacao";
                }
                else
                {
                    continue;
                }
                var previous = zombiePath[index - 1];
                var probe = (previous + point) * 0.5f;
                nearby.Clear();
                SkaliaBotObjectMap.Query(probe, 2.5f, nearby);
                BotMapObject? best = null;
                var bestDistance = float.PositiveInfinity;
                foreach (var candidate in nearby)
                {
                    var distance = SkaliaBotObjectMap.FootprintDistance(candidate, probe);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
                var rise = point.y - previous.y;
                var what = best != null
                    ? SkaliaBotObjectMap.CategoryName(best.Category) + " " + best.Name
                    : Mathf.Abs(rise) > 0.4f ? string.Format(CultureInfo.InvariantCulture, "degrau/rampa de {0:0.0}m", rise) : "nada no catalogo";
                return string.Format(CultureInfo.InvariantCulture, "{0} perto de {1} em ({2:0}, {3:0}, {4:0})",
                    kind, what, probe.x, probe.y, probe.z);
            }
            return "rota dos zumbis toda alcancavel (so o fim difere)";
        }

        private static readonly float[] s_BodyHeights = { 0.5f, 1.1f };

        private static int CountWalls(
            Vector3[] corners,
            int mask,
            ref int segments,
            ref float length,
            Dictionary<string, int>? names)
        {
            var hits = 0;
            for (var index = 0; index + 1 < corners.Length; index++)
            {
                segments++;
                length += (corners[index + 1] - corners[index]).magnitude;
                foreach (var height in s_BodyHeights)
                {
                    var wall = SkaliaBotNavmeshAudit.FirstWall(corners[index], corners[index + 1], height, mask);
                    if (wall == null || wall.GetComponentInParent<InteractableObjectBinaryState>() != null)
                    {
                        continue;
                    }
                    hits++;
                    if (names != null)
                    {
                        var name = SkaliaBotObjectMap.TryGetByCollider(wall, out var mapObject)
                            ? mapObject.Name
                            : wall is TerrainCollider ? "terreno" : wall.transform.root.name;
                        names.TryGetValue(name, out var count);
                        names[name] = count + 1;
                    }
                    break;
                }
            }
            return hits;
        }
    }
}
