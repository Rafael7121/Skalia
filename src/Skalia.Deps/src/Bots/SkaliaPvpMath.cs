using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y: the body test of a round against the player being fought,
    // kept free of the game's types so it is checked outside the game
    // (scratch test "pvptest"): an upright cylinder from the feet up to
    // the body's height, the zone by the height and the side of the hit.
    internal enum PvpBodyZone
    {
        Head,
        Chest,
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg,
        Prone
    }

    internal static class SkaliaPvpMath
    {
        internal static bool TryHitUprightBody(
            Vector3 root,
            float height,
            float radius,
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            out float distance)
        {
            distance = 0f;
            var ox = origin.x - root.x;
            var oz = origin.z - root.z;
            var a = direction.x * direction.x + direction.z * direction.z;
            var top = root.y + height;
            float hit;
            if (a < 0.000001f)
            {
                // Straight up or down: inside the circle, the cap is hit.
                if (ox * ox + oz * oz > radius * radius || Mathf.Abs(direction.y) < 0.000001f)
                {
                    return false;
                }
                var capY = direction.y < 0f ? top : root.y;
                if (direction.y < 0f ? origin.y < root.y : origin.y > top)
                {
                    return false;
                }
                hit = origin.y >= root.y && origin.y <= top ? 0f : (capY - origin.y) / direction.y;
            }
            else
            {
                // From the closest approach to the body's axis: the plain
                // quadratic lost centimeters to rounding at 150m.
                var closest = -(ox * direction.x + oz * direction.z) / a;
                var cx = ox + direction.x * closest;
                var cz = oz + direction.z * closest;
                var miss = cx * cx + cz * cz;
                if (miss > radius * radius)
                {
                    return false;
                }
                var half = Mathf.Sqrt((radius * radius - miss) / a);
                var enter = Mathf.Max(0f, closest - half);
                var leave = closest + half;
                if (leave < 0f)
                {
                    return false;
                }
                var enterY = origin.y + direction.y * enter;
                var leaveY = origin.y + direction.y * leave;
                if (enterY >= root.y && enterY <= top)
                {
                    hit = enter;
                }
                else if (enterY > top && leaveY <= top && direction.y < 0f)
                {
                    hit = (top - origin.y) / direction.y;
                }
                else if (enterY < root.y && leaveY >= root.y && direction.y > 0f)
                {
                    hit = (root.y - origin.y) / direction.y;
                }
                else
                {
                    return false;
                }
            }
            if (hit < 0f || hit > maxDistance)
            {
                return false;
            }
            distance = hit;
            return true;
        }

        internal static PvpBodyZone PickZone(Vector3 root, float height, float radius, bool prone, Vector3 right, Vector3 point)
        {
            if (prone)
            {
                return PvpBodyZone.Prone;
            }
            var relative = (point.y - root.y) / height;
            var side = Vector3.Dot(point - root, right);
            if (relative >= 0.86f)
            {
                return PvpBodyZone.Head;
            }
            if (relative >= 0.5f)
            {
                return Mathf.Abs(side) > radius * 0.75f
                    ? (side < 0f ? PvpBodyZone.LeftArm : PvpBodyZone.RightArm)
                    : PvpBodyZone.Chest;
            }
            return side < 0f ? PvpBodyZone.LeftLeg : PvpBodyZone.RightLeg;
        }
    }
}
