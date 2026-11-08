namespace Jellyfin.Plugin.SmartRecognizer.Configuration;

/// <summary>
/// Gives services the current plugin configuration. Read it on every call:
/// keys and toggles change at runtime from the settings page.
/// </summary>
public interface IPluginConfigurationAccessor
{
    PluginConfiguration Current { get; }
}

/// <summary>
/// Reads configuration from the loaded plugin instance; falls back to defaults
/// when the plugin is not loaded (e.g. in tests).
/// </summary>
public sealed class PluginConfigurationAccessor : IPluginConfigurationAccessor
{
    private static readonly PluginConfiguration Defaults = new();

    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? Defaults;
}
