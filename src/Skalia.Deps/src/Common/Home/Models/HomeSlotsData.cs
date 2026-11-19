using System.Collections.Generic;

namespace SkaliaDeps
{
    public sealed class HomeSlotsData
    {
        public Dictionary<ulong, HomeSlotAssignment> Players { get; set; } =
            new Dictionary<ulong, HomeSlotAssignment>();
    }

    public sealed class HomeSlotAssignment
    {
        public List<uint> Homes { get; set; } = new List<uint>();

        public Dictionary<uint, string> Names { get; set; } =
            new Dictionary<uint, string>();

        // Mantidos para migrar automaticamente os dados gravados pelas versoes antigas.
        public uint Home1 { get; set; }

        public uint Home2 { get; set; }

        public HomeSlotAssignment Clone()
        {
            return new HomeSlotAssignment
            {
                Homes = new List<uint>(GetHomes()),
                Names = new Dictionary<uint, string>(GetNames()),
                Home1 = Home1,
                Home2 = Home2
            };
        }

        public Dictionary<uint, string> GetNames()
        {
            Names ??= new Dictionary<uint, string>();
            return Names;
        }

        public List<uint> GetHomes()
        {
            Homes ??= new List<uint>();
            if (Homes.Count == 0)
            {
                if (Home1 != 0)
                {
                    Homes.Add(Home1);
                }

                if (Home2 != 0 && Home2 != Home1)
                {
                    Homes.Add(Home2);
                }
            }

            SyncLegacySlots();
            return Homes;
        }

        public void SyncLegacySlots()
        {
            Home1 = Homes.Count > 0 ? Homes[0] : 0;
            Home2 = Homes.Count > 1 ? Homes[1] : 0;
        }
    }
}
