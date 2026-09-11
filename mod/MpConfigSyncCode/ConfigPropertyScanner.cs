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
        // Exact mirror of BaseLib's ModConfig.CheckConfigProperties
        // (BaseLib/Config/ModConfig.cs:143-153):
        //     foreach (var property in configType.GetProperties())
        //     {
        //         if (property.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
        //         if (!property.CanRead || !property.CanWrite) continue;
        //         if (property.GetMethod?.IsStatic != true) continue;
        //         ConfigProperties.Add(property);
        //     }
        //
        // The previous version used
        // GetProperties(Public | Static | FlattenHierarchy), which is NOT the
        // same set. FlattenHierarchy additionally returns INHERITED public
        // statics - properties BaseLib never puts in ConfigProperties and never
        // persists to mod_configs/*.cfg. Syncing one would write a value the
        // receiver does not save, so it would silently drift back on reload
        // while looking successfully synced. Instance properties are dropped by
        // both paths (BaseLib enumerates them only to reject them), so calling
        // GetProperties() the way BaseLib does is the only way to guarantee "we
        // sync exactly what the settings UI persists".
        //
        // BaseLib also warns for each rejected non-static property; we stay
        // silent because the resulting set is identical and the warning would
        // just be duplicate noise in the log.
        var result = new List<PropertyInfo>();
        foreach (PropertyInfo property in configType.GetProperties())
        {
            if (property.GetCustomAttribute<ConfigIgnoreAttribute>() != null)
                continue;
            if (!property.CanRead || !property.CanWrite)
                continue;
            if (property.GetMethod?.IsStatic != true)
                continue;
            result.Add(property);
        }
        return result;
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