using System;
using OpenMod.API.Ioc;

namespace SkaliaDeps
{
    [Service]
    public interface IGroundCleanupService
    {
        void Start();

        void Stop();

        int RemoveDroppedItems();

        TimeSpan GetAutomaticAnnouncementDelay(DateTimeOffset now);
    }
}
