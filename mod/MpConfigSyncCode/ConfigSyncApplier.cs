using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Receiver side: apply a host-pushed config snapshot to the local ModConfigRegistry.
///
/// Session-scoped semantics: before the first host push, every touched property's
/// local value is snapshotted. On run-session end (RunManager.CleanUp) the snapshot
/// is restored in memory only - the user's own config file is NEVER rewritten by this
/// mod (no Save() call anywhere on the apply path). Each multiplayer session starts
/// from the user's own settings again.
///
/// Per entry: resolve mod -> resolve property -> invariant-convert -> SetValue.
/// Unknown mod / unknown property / conversion failure => skip with a log line
/// (version skew between peers must never crash the client).
///
/// After all entries: for every touched config fire Changed() + ConfigReloaded()
/// (settings UI rows re-read). Deliberately NO Save() - see above.
/// </summary>
internal static class ConfigSyncApplier
{
    /// <summary>PropertyName per mod, captured BEFORE the first host push this session.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> RestoreSnapshot = new();

    private static bool _restoring;

    internal static bool HasSnapshot => RestoreSnapshot.Count > 0;

    internal static void Apply(ConfigSyncMessage message, ulong senderId)
    {
        if (!MpConfigSyncConfig.Enabled)
        {
            MainFile.Log.Info("Config sync disabled locally, ignoring host push");
            return;
        }

        int count = Math.Min(Math.Min(message.ModIds.Count, message.PropertyNames.Count), message.Values.Count);
        MainFile.Log.Info($"Received config sync from host {senderId}: {count} entries");

        int applied = 0, skipped = 0;
        var touched = new HashSet<string>();

        for (int i = 0; i < count; i++)
        {
            if (ApplyOne(message.ModIds[i], message.PropertyNames[i], message.Values[i]))
            {
                applied++;
                touched.Add(message.ModIds[i]);
            }
            else
            {
                skipped++;
            }
        }

        foreach (string modId in touched)
        {
            ModConfig? config = ModConfigRegistry.Get(modId);
            if (config == null)
                continue;
            try
            {
                // Same refresh the settings UI performs after a control change, so any
                // open settings page reflects the host's values for THIS session.
                config.Changed();
                config.ConfigReloaded();
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Failed to refresh config UI for {modId}: {e.Message}");
            }
        }

        MainFile.Log.Info($"Config sync applied: {applied} entries, {skipped} skipped, {touched.Count} mods touched (session-scoped, no file writes)");
    }

    /// <summary>Runs on RunManager.CleanUp: put the user's own values back in memory.</summary>
    internal static void RestoreLocalSettings()
    {
        if (RestoreSnapshot.Count == 0 || _restoring)
            return;
        _restoring = true;
        try
        {
            foreach ((string modId, Dictionary<string, string> props) in RestoreSnapshot)
            {
                ModConfig? config = ModConfigRegistry.Get(modId);
                if (config == null)
                    continue;
                foreach ((string name, string value) in props)
                {
                    try
                    {
                        PropertyInfo? prop = ConfigPropertyScanner.Resolve(modId)?.GetValueOrDefault(name);
                        if (prop == null)
                            continue;
                        object? converted = TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(value);
                        if (converted != null)
                            prop.SetValue(null, converted);
                    }
                    catch (Exception e)
                    {
                        MainFile.Log.Debug($"Restore skip {modId}.{name}: {e.Message}");
                    }
                }
                try
                {
                    config.Changed();
                    config.ConfigReloaded();
                }
                catch (Exception e)
                {
                    MainFile.Log.Error($"Failed to refresh config UI for {modId} on restore: {e.Message}");
                }
            }
            RestoreSnapshot.Clear();
            MainFile.Log.Info("Config sync session ended: local settings restored in memory");
        }
        finally
        {
            _restoring = false;
        }
    }

    private static bool ApplyOne(string modId, string propertyName, string value)
    {
        try
        {
            Dictionary<string, PropertyInfo>? props = ConfigPropertyScanner.Resolve(modId);
            if (props == null)
            {
                MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: mod not present locally");
                return false;
            }
            if (!props.TryGetValue(propertyName, out PropertyInfo? prop))
            {
                MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: property not found (version skew?)");
                return false;
            }

            // Snapshot the user's own value BEFORE the first overwrite (per session).
            if (!RestoreSnapshot.TryGetValue(modId, out Dictionary<string, string>? snap))
            {
                snap = new Dictionary<string, string>();
                RestoreSnapshot[modId] = snap;
            }
            if (!snap.ContainsKey(propertyName))
            {
                object? own = prop.GetValue(null);
                snap[propertyName] = own == null ? string.Empty : TypeDescriptor.GetConverter(prop.PropertyType).ConvertToInvariantString(own)!;
            }

            TypeConverter converter = TypeDescriptor.GetConverter(prop.PropertyType);
            object? converted = converter.ConvertFromInvariantString(value);
            if (converted == null)
            {
                MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: conversion returned null");
                return false;
            }

            prop.SetValue(null, converted);
            return true;
        }
        catch (Exception e)
        {
            MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: {e.Message}");
            return false;
        }
    }
}