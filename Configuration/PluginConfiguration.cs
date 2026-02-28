using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TimeLimiter.Configuration;

/// <summary>
/// Plugin configuration for the Time Limiter plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the default daily playback limit in minutes.
    /// 0 means no limit.
    /// </summary>
    public int DefaultDailyLimitMinutes { get; set; } = 120;

    /// <summary>
    /// Gets or sets per-user limit overrides.
    /// </summary>
    public UserLimitEntry[] UserLimits { get; set; } = [];
}

/// <summary>
/// Per-user daily limit override entry.
/// </summary>
public class UserLimitEntry
{
    /// <summary>
    /// Gets or sets the user's unique identifier.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the user's display name (for reference in config UI).
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the daily limit in minutes.
    /// 0 = no limit, -1 = use global default.
    /// </summary>
    public int DailyLimitMinutes { get; set; } = -1;
}
