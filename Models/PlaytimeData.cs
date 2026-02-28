using System.Collections.Generic;

namespace Jellyfin.Plugin.TimeLimiter.Models;

/// <summary>
/// Persisted playback time data for all users.
/// </summary>
public class PlaytimeData
{
    /// <summary>
    /// Gets or sets the playback records.
    /// Key: userId (string), Value: dictionary of date "yyyy-MM-dd" -> seconds completed.
    /// </summary>
    public Dictionary<string, Dictionary<string, long>> Records { get; set; } = new();
}
