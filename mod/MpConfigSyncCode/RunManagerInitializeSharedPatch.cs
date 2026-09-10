using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

using BaseLib.Abstracts;
using BaseLib.Config;

using HarmonyLib;

using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Host side: on every multiplayer run session start (RunManager.InitializeShared),
/// snapshot all registered mod configs and push them to every peer via BaseLib's
/// CustomMessageWrapper channel.
///
/// Race analysis (engine v0.111): InitializeShared runs synchronously inside the main
/// loop; the NetMessageBus only delivers packets when INetGameService.Update() is
/// pumped, which happens after InitializeShared (and every postfix, including BaseLib's
/// wrapper registration) has returned. Host-side Send only needs NetService assigned +
/// CustomMessageWrapper.Initialize() (ran at startup via PostModInitPatch), so postfix
/// ordering between our patch and BaseLib's is irrelevant.
/// </summary>
[HarmonyPatch(typeof(RunManager), "InitializeShared")]
internal static class RunManagerInitializeSharedPatch
{
    private static void Postfix(RunManager __instance)
    {
        try
        {
            INetGameService net = __instance.NetService;
            if (net == null)
                return;

            // Only the host pushes. Singleplayer/Replay have no peers; clients wait.
            if (net.Type != NetGameType.Host)
                return;

            if (!MpConfigSyncConfig.Enabled)
            {
                MainFile.Log.Info("Config sync disabled locally, not pushing");
                return;
            }

            ConfigSyncMessage message = BuildSnapshot();
            if (message.ModIds.Count == 0)
            {
                MainFile.Log.Info("Config sync: no syncable config entries found, nothing to push");
                return;
            }

            CustomMessageWrapper.Send(message, net);
            MainFile.Log.Info($"Config sync: pushed {message.ModIds.Count} entries to all peers");
        }
        catch (Exception e)
        {
            // Config sync must never break run startup.
            MainFile.Log.Error($"Config sync push failed: {e}");
        }
    }

    private static ConfigSyncMessage BuildSnapshot()
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
                        continue;
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