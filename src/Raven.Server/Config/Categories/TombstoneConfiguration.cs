using System.ComponentModel;

using Raven.Server.Config.Attributes;
using Raven.Server.Config.Settings;

namespace Raven.Server.Config.Categories
{
    public class TombstoneConfiguration : ConfigurationCategory
    {
        [DefaultValue(5)]
        [TimeUnit(TimeUnit.Minutes)]
        [ConfigurationEntry("Raven/Tombstones/CleanupIntervalInMin")]
        [Description("Time (in minutes) between tombstone cleanups.")]
        public TimeSetting CleanupInterval { get; set; }
    }
}