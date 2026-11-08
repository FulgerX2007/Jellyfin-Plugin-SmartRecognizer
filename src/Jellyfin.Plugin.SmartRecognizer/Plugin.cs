using System.Globalization;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SmartRecognizer;

/// <summary>
/// Plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "Smart Recognizer";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the loaded plugin instance, or null outside the server.</summary>
    public static Plugin? Instance { get; private set; }

    public override string Name => PluginName;

    public override Guid Id => Guid.Parse("6f1d2c7e-3b9a-4e51-9c8d-2a7f4b0e5d13");

    public override string Description =>
        "Recognizes movies and series from messy file names and fetches metadata from TMDb, Kinopoisk, OMDb and TVDB.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // The settings page itself is added in Task 20.
        return [];
    }

    internal static string ResourcePath(string relative) =>
        string.Format(CultureInfo.InvariantCulture, "{0}.{1}", typeof(Plugin).Namespace, relative);
}
