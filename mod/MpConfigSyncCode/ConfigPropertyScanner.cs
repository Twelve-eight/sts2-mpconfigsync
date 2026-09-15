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

/// <summary>
/// Operation-scoped property-map cache for ONE <see cref="ConfigSyncApplier.Apply"/>
/// or <see cref="ConfigSyncApplier.RestoreLocalSettings"/> pass (MCS-1, 2026-09-15):
/// one descriptor resolution per distinct mod per operation instead of one per
/// packet entry / per restored property.
///
/// Lifecycle:
/// - Producer/owner: the Apply (or RestoreLocalSettings) call creates the
///   instance after its authorization/guard checks and passes it down through
///   the operation.
/// - First-consumer: the validation loop (Apply) or per-property restore loop
///   (RestoreLocalSettings) of that same call. Each distinct modId resolves
///   through <see cref="Resolve"/> exactly once per operation, on first
///   reference, and the result is memoized for the remaining entries.
/// - Cleanup: method-local instance; garbage when the operation returns.
///   Nothing survives the packet or the session boundary.
///
/// Why operation-scoped instead of a process-global ModId-keyed cache:
/// ModConfigRegistry is mutable across packets - a mod can be absent today,
/// register later in the session, or be replaced by a new config instance. A
/// global cache could serve descriptors for a config that no longer exists or
/// hide one that just registered. Resolving at most once per mod PER OPERATION
/// keeps every packet's metadata exactly as fresh as the per-entry Resolve it
/// replaces, while cutting P entries x full scans down to one scan per
/// distinct mod.
///
/// Populated on first reference rather than pre-built over the whole registry
/// on purpose: pre-building would scan every registered config even when the
/// packet references a few (worse than one-scan-per-referenced-mod), and would
/// move a broken config's Scan exception out of the caller's per-entry
/// try/catch, changing which packets get rejected. Resolving through Resolve
/// keeps exception, absent-mod, and duplicate-ModId registry semantics byte
/// for byte.
///
/// What is cached: immutable reflection metadata only (property name ->
/// PropertyInfo per mod). Config VALUES are never cached here; every value
/// read, conversion, and write goes through the live property each time.
///
/// Failure semantics preserved from the per-entry Resolve: exceptions from
/// Get/Scan propagate uncached on first reference, inside the caller's
/// existing per-entry try/catch, so Apply still rejects the whole snapshot and
/// Restore still skips just that property, and a failed resolution is retried
/// (not memoized) if referenced again - identical to per-entry Resolve.
/// </summary>
internal sealed class OperationPropertyMap
{
    private readonly Dictionary<string, Dictionary<string, PropertyInfo>?> _maps = new();

    /// <summary>
    /// Distinct modIds resolved during this operation (absent mods included -
    /// their null result is memoized too). Informational only, for logging.
    /// </summary>
    internal int ResolvedModCount => _maps.Count;

    /// <summary>
    /// Resolves <paramref name="modId"/> through <see cref="Resolve"/> at most
    /// once per operation. Null result = mod absent locally; empty dictionary =
    /// registered but zero eligible properties (same distinction as Resolve).
    /// </summary>
    internal Dictionary<string, PropertyInfo>? Get(string modId)
    {
        // A null modId cannot key the dictionary; resolve it directly so the
        // registry decides, exactly like the per-entry Resolve this replaces.
        if (modId == null)
            return ConfigPropertyScanner.Resolve(modId);
        if (_maps.TryGetValue(modId, out Dictionary<string, PropertyInfo>? cached))
            return cached;
        Dictionary<string, PropertyInfo>? resolved = ConfigPropertyScanner.Resolve(modId);
        _maps[modId] = resolved;
        return resolved;
    }
}