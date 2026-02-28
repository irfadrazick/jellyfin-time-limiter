using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TimeLimiter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TimeLimiter;

/// <summary>
/// The main plugin class for the Time Limiter plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Time Limiter";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a8b3c2d1-e4f5-6789-abcd-ef0123456789");

    /// <inheritdoc />
    public override string Description => "Limits daily playback time per user with configurable limits and warnings.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "TimeLimiter",
                DisplayName = "Time Limiter",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "schedule"
            }
        ];
    }
}
