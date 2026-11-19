using System.Collections.Generic;
using UnityEngine;

namespace SkaliaDeps
{
    public sealed class BotNavigationSnapshot
    {
        public BotNavigationSnapshot(
            string state,
            Vector3 position,
            Vector3 destination,
            IReadOnlyList<Vector3> points,
            int currentPointIndex,
            bool pathPending)
        {
            State = state;
            Position = position;
            Destination = destination;
            Points = points;
            CurrentPointIndex = currentPointIndex;
            PathPending = pathPending;
        }

        public string State { get; }
        public Vector3 Position { get; }
        public Vector3 Destination { get; }
        public IReadOnlyList<Vector3> Points { get; }
        public int CurrentPointIndex { get; }
        public bool PathPending { get; }
    }
}
