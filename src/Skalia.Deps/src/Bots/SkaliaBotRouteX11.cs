using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x11: the back and forth of the x10 test (10 circles in 30 min,
    // up to 7 routes in 8s each). Near furniture or a wall the bot stands a
    // little off the mesh; the mesh line to the next corner then starts on
    // the mesh edge and "hits" it, the route was thrown away or the bot was
    // sent back to the corner it had passed, and the next route began behind
    // it again. A corner close by that the body reaches in a straight line,
    // over ground, is simply walked to.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float CornerWalkableMeters = 3f;

        private static bool IsCornerWalkable(Vector3 position, Vector3 corner)
        {
            var leg = corner - position;
            var height = leg.y;
            leg.y = 0f;
            var distance = leg.magnitude;
            if (distance > CornerWalkableMeters || Mathf.Abs(height) > 0.5f)
            {
                return false;
            }
            if (distance < 0.05f)
            {
                return true;
            }
            if (!SkaliaBotUnityNavmesh.IsBodyClear(position, corner))
            {
                return false;
            }
            // Ground all the way (no gap or roof edge between them).
            for (var step = 0.5f; step < distance; step += 0.75f)
            {
                var probe = position + leg / distance * step;
                var lineY = position.y + height * (step / distance);
                if (!Physics.Raycast(new Vector3(probe.x, lineY + 0.6f, probe.z), Vector3.down, out var ground, 1.4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                    || ground.point.y < lineY - 0.6f)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
