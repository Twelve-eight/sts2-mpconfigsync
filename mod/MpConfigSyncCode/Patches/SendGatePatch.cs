using System;

using HarmonyLib;

namespace MpConfigSync.MpConfigSyncCode.Patches;

/// <summary>
/// Runtime fail-closed guard for the host push entry point (F09, 2026-10-02;
/// moved here from MainFile.cs so the guard is a real, single-purpose file).
///
/// REGISTRATION IS CONDITIONAL: MainFile.ApplyPatches installs this class only
/// when the sending gate was on at process start (Enabled AND EnableSending).
/// When sending is off at process start no push patch class is registered, so
/// LobbySnapshotPush.PushIfHost has no caller and this guard is not needed.
/// When the class is needed but cannot be installed or verified,
/// MainFile.ApplyPatches clears MainFile.SendPatchesRegistered BEFORE installing
/// any consumer class, so no push patch class is registered either - the push
/// path stays closed even though this guard is absent, and no later
/// mid-session change can re-open it (no caller exists).
///
/// SCOPE IS MINIMAL AND IT NEVER EXECUTES SYNC: this class is a single prefix
/// on LobbySnapshotPush.PushIfHost that returns false to skip the guarded
/// method. It never sends, applies, converts or mutates a value. Any
/// evaluation failure returns false (fail closed).
///
/// WHY IT MUST READ BOTH FLAGS: the process-start registration snapshot
/// (MainFile.SendPatchesRegistered) keeps a gate that was off at process start
/// closed for the whole process, because its patch classes were never
/// registered; the live computed gate (MpConfigSyncConfig.SendingEnabled)
/// makes turning sending OFF mid-session take effect immediately. The guarded
/// method itself only checks the master switch (legacy Enabled-only path), so
/// this prefix is what enforces the group gate at runtime.
/// </summary>
[HarmonyPatch(typeof(LobbySnapshotPush), nameof(LobbySnapshotPush.PushIfHost))]
internal static class SendGatePatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        try
        {
            if (!MainFile.SendPatchesRegistered)
            {
                return false;
            }
            return MpConfigSyncConfig.SendingEnabled;
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Config sync send gate failed closed: {e.Message}");
            return false;
        }
    }
}
