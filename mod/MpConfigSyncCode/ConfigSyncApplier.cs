using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Receiver side: apply a host-pushed config snapshot to the local ModConfigRegistry.
///
/// Per entry: resolve mod -> resolve property -> invariant-convert -> SetValue.
/// Unknown mod / unknown property / conversion failure => skip with a log line
/// (version skew between peers must never crash the client).
///
/// After all entries: for every touched config fire Changed() + ConfigReloaded()
/// (settings UI rows re-read) and Save() (atomic write to mod_configs/<mod>.cfg so
/// startup-time keys align on the next launch).
/// </summary>
internal static class ConfigSyncApplier
{
    internal static int AppliedEntries;
    internal static int SkippedEntries;
    internal static readonly HashSet<string> TouchedMods = new();

    internal static void Apply(ConfigSyncMessage message, ulong senderId)
    {
        if (!MpConfigSyncConfig.Enabled)
        {
            MainFile.Log.Info("Config sync disabled locally, ignoring host push");
            return;
        }

        AppliedEntries = 0;
        SkippedEntries = 0;
        TouchedMods.Clear();

        int count = Math.Min(Math.Min(message.ModIds.Count, message.PropertyNames.Count), message.Values.Count);
        MainFile.Log.Info($"Received config sync from host {senderId}: {count} entries");

        for (int i = 0; i < count; i++)
        {
            ApplyOne(message.ModIds[i], message.PropertyNames[i], message.Values[i]);
        }

        foreach (string modId in TouchedMods)
        {
            ModConfig? config = ModConfigRegistry.Get(modId);
            if (config == null)
                continue;
            try
            {
                // Same call sequence the settings UI performs after a control change.
                config.Changed();
                config.ConfigReloaded();
                config.Save();
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Failed to save/refresh config for {modId}: {e.Message}");
            }
        }

        MainFile.Log.Info($"Config sync applied: {AppliedEntries} entries, {SkippedEntries} skipped, {TouchedMods.Count} mods touched");
    }

    private static void ApplyOne(string modId, string propertyName, string value)
    {
        try
        {
            Dictionary<string, PropertyInfo>? props = ConfigPropertyScanner.Resolve(modId);
            if (props == null)
            {
                MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: mod not present locally");
                SkippedEntries++;
                return;
            }
            if (!props.TryGetValue(propertyName, out System.Reflection.PropertyInfo? prop))
            {
                MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: property not found (version skew?)");
                SkippedEntries++;
                return;
            }

            TypeConverter converter = TypeDescriptor.GetConverter(prop.PropertyType);
            object? converted = converter.ConvertFromInvariantString(value);
            if (converted == null)
            {
                MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: conversion returned null");
                SkippedEntries++;
                return;
            }

            prop.SetValue(null, converted);
            AppliedEntries++;
            TouchedMods.Add(modId);
        }
        catch (Exception e)
        {
            MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: {e.Message}");
            SkippedEntries++;
        }
    }
}