using System.Collections.Generic;

namespace SkaliaDeps
{
    public sealed class SpawnLoadoutStateData
    {
        public Dictionary<string, Dictionary<ulong, bool>> GrantedLives { get; set; } =
            new Dictionary<string, Dictionary<ulong, bool>>();
    }
}
