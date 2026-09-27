using System;
using System.ComponentModel;
using System.Reflection;

using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Builds the host-side snapshot from every registered mod config. Shared by
/// the lobby barrier pushes and the InitializeShared backstop so every entry
/// point pushes the SAME snapshot shape.
///
/// WIRE-CAP FILTER (R05-04, 2026-09-22): a field that fails
/// <see cref="ConfigSyncMessage.FitsWireLimit"/> cannot be carried by the
/// protocol at all - an un-filtered entry makes the whole packet unparseable for
/// every receiver, which used to silently kill the entire snapshot. Such an entry
/// is therefore dropped here and counted in <c>oversizedSkipped</c>, with one
/// Error log per offending key saying that the key keeps each end's own value.
/// The value is NEVER truncated: truncation would rewrite a user's setting
/// without telling anyone, whereas a dropped entry is a visible, per-key
/// divergence (the same trade-off the receiver-side caps already make).
/// </summary>
internal static class SnapshotBuilder
{
    internal static ConfigSyncMessage Build(out int oversizedSkipped)
    {
        var message = new ConfigSyncMessage();
        oversizedSkipped = 0;
        foreach (ConfigPropertyScanner.ScannedConfig scanned in ConfigPropertyScanner.ScanAll())
        {
            foreach (PropertyInfo prop in scanned.Properties)
            {
                try
                {
                    object? current = prop.GetValue(null);
                    if (current == null)
                    {
                        continue;
                    }
                    string value = TypeDescriptor.GetConverter(prop.PropertyType).ConvertToInvariantString(current)!;
                    string key = $"{scanned.ModId}.{prop.Name}";
                    // Any one failing field makes the WHOLE entry un-carryable: the
                    // receiver rejects the packet, not just the field.
                    if (!ConfigSyncMessage.FitsWireLimit(scanned.ModId, out string reason)
                        || !ConfigSyncMessage.FitsWireLimit(prop.Name, out reason)
                        || !ConfigSyncMessage.FitsWireLimit(value, out reason))
                    {
                        // Counted and logged ONCE for this entry, then left at each
                        // end's own value - never truncated, never sent.
                        oversizedSkipped++;
                        MainFile.Log.Error($"Config sync: skipping oversized entry {key}: {reason}; the key keeps each end's own value (not truncated)");
                        continue;
                    }
                    message.ModIds.Add(scanned.ModId);
                    message.PropertyNames.Add(prop.Name);
                    message.Values.Add(value);
                }
                catch (Exception e)
                {
                    MainFile.Log.Debug($"Config sync snapshot skip {scanned.ModId}.{prop.Name}: {e.Message}");
                }
            }
        }
        return message;
    }
}
