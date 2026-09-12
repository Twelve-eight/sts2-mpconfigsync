using System;
using System.ComponentModel;
using System.Reflection;

using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Builds the host-side snapshot from every registered mod config. Shared by
/// the lobby barrier pushes and the InitializeShared backstop so every entry
/// point pushes the SAME snapshot shape.
/// </summary>
internal static class SnapshotBuilder
{
    internal static ConfigSyncMessage Build()
    {
        var message = new ConfigSyncMessage();
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
