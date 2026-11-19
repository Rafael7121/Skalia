using System.Collections.Generic;

namespace SkaliaDeps
{
    public sealed class InventorySortPreferencesData
    {
        public Dictionary<ulong, List<int>> PriorityGroups { get; set; } =
            new Dictionary<ulong, List<int>>();

        public Dictionary<ulong, List<int>> StoragePriorityGroups { get; set; } =
            new Dictionary<ulong, List<int>>();

        public Dictionary<ulong, bool> UseSameSortForStorage { get; set; } =
            new Dictionary<ulong, bool>();
    }
}
