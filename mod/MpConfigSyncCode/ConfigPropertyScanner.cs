using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Mirrors BaseLib's ModConfig.CheckConfigProperties filtering: the exact set of
/// properties the settings UI persists, which is also the exact set we sync.
/// Static + readable + writable + no [ConfigIgnore].
/// </summary>
internal static class ConfigPropertyScanner
{
    internal sealed record ScannedConfig(string ModId, ModConfig Config, List<PropertyInfo> Properties);

    internal static List<ScannedConfig> ScanAll()
    {
        var result = new List<ScannedConfig>();
        foreach (ModConfig config in ModConfigRegistry.GetAll())
        {
            if (string.IsNullOrEmpty(config.ModId))
                continue;
            List<PropertyInfo> props = Scan(config.GetType());
            if (props.Count > 0)
                result.Add(new ScannedConfig(config.ModId!, config, props));
        }
        return result;
    }

    internal static List<PropertyInfo> Scan(Type configType)
    {
        return configType
            .GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.GetCustomAttribute<ConfigIgnoreAttribute>() == null)
            .ToList();
    }

    /// <summary>ModId -> property map for one mod's config, resolved locally on the receiver.</summary>
    internal static Dictionary<string, PropertyInfo>? Resolve(string modId)
    {
        ModConfig? config = ModConfigRegistry.Get(modId);
        if (config == null)
            return null;
        return Scan(config.GetType()).ToDictionary(p => p.Name, p => p);
    }
}