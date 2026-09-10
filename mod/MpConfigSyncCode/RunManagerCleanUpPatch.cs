using System;

using HarmonyLib;

using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Receiver side cleanup: when the run session ends (CleanUp runs for finished runs,
/// abandoned runs, disconnects, and returns to menu), put the user's own config values
/// back in memory. Together with the applier's no-Save policy this makes every config
/// override strictly session-scoped: host values live only while the MP session lives,
/// and the user's own settings file is never touched.
/// </summary>
[HarmonyPatch(typeof(RunManager), "CleanUp")]
internal static class RunManagerCleanUpPatch
{
    private static void Postfix()
    {
        try
        {
            ConfigSyncApplier.RestoreLocalSettings();
        }
        catch (Exception e)
        {
            // Restore must never break session cleanup.
            MainFile.Log.Error($"Config sync restore failed: {e}");
        }
    }
}