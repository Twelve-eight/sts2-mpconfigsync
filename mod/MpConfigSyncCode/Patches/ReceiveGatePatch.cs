using System;

using HarmonyLib;

namespace MpConfigSync.MpConfigSyncCode.Patches;

/// <summary>
/// Runtime fail-closed guard for the receive entry point (F09, 2026-10-02;
/// moved here from MainFile.cs so the guard is a real, single-purpose file).
///
/// RESIDENT BY NECESSITY - THE ONE DELIBERATE EXCEPTION TO CONDITIONAL
/// REGISTRATION: the caller of the guarded method is not ours. BaseLib's own
/// CustomMessageWrapper handler (BaseLib.Abstracts/CustomMessageWrapper.cs:75-77,
/// HandleCustomMessage -> message.Message.HandleMessage(senderId)) is registered
/// on every run's InitializeShared, regardless of which of our patch classes
/// were installed. The consumer files (ConfigSyncMessage.cs:406-433 and
/// ConfigSyncApplier.cs:69-75, owned by B01/B03 in this round) check only the
/// master switch, so if this prefix were absent a peer with EnableReceiving=false
/// (or a group closed by fail-closed) would still apply host snapshots while the
/// master switch stayed on. Editing those consumer files is outside this write
/// set, so the prefix is the fail-closed enforcement point and is installed
/// whenever the mod is loaded - including when the receiving gate was off at
/// process start, where it is exactly what keeps the group closed.
///
/// SCOPE IS MINIMAL AND IT NEVER EXECUTES SYNC: this class is a single prefix
/// on ConfigSyncMessage.HandleMessage that returns false to skip the guarded
/// method. It never sends, applies, converts or mutates a value. Any
/// evaluation failure returns false (fail closed).
///
/// WHY IT MUST READ BOTH FLAGS: the process-start registration snapshot
/// (MainFile.ReceivePatchesRegistered) keeps a gate that was off at process
/// start closed for the whole process - and MainFile.ApplyPatches also clears it
/// when a protective patch cannot be verified - while the live computed gate
/// (MpConfigSyncConfig.ReceivingEnabled) makes turning receiving OFF
/// mid-session take effect immediately. The guarded method's own legacy
/// Enabled-only check remains as defense in depth for the master switch.
/// </summary>
[HarmonyPatch(typeof(ConfigSyncMessage), nameof(ConfigSyncMessage.HandleMessage))]
internal static class ReceiveGatePatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        try
        {
            if (!MainFile.ReceivePatchesRegistered)
            {
                return false;
            }
            return MpConfigSyncConfig.ReceivingEnabled;
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Config sync receive gate failed closed: {e.Message}");
            return false;
        }
    }
}

/// <summary>
/// LAST-RESORT receive blocker (F09, 2026-10-02), installed ONLY when
/// <see cref="ReceiveGatePatch"/> itself cannot be installed or verified.
///
/// WHY THIS EXISTS: the request requires that a guard that fails to install
/// must not leave the legacy Enabled-only consumer path open. For the receive
/// direction the legacy path is
/// BaseLib wrapper -> ConfigSyncMessage.HandleMessage -> ConfigSyncApplier.Apply,
/// and both consumer files check only the master switch. Forcing the master
/// switch off in memory would close the path only until something re-enables
/// that mutable static (settings UI, config reload), so it is not a hard block.
///
/// WHAT IT BLOCKS: ConfigSyncApplier.Apply is the ONLY place an incoming host
/// value is ever committed (the commit loop that calls PropertyInfo.SetValue);
/// HandleMessage's preflight only inspects and logs. A prefix that returns
/// false therefore makes every later snapshot a no-op at the mutation entry
/// point, independent of Enabled, EnableReceiving and the registration flags,
/// for the rest of the process. It never sends, applies or mutates anything.
///
/// WHY THIS TARGET AND NOT THE SMALL AUTHORIZATION HELPER: the commit method is
/// far larger than any JIT inlining budget, so the patched entry point cannot
/// be bypassed by the caller having inlined a copy of the original body. It is
/// also our own stable method rather than a game-build-dependent target, so a
/// future engine change cannot silently remove the block.
/// </summary>
[HarmonyPatch(typeof(ConfigSyncApplier), nameof(ConfigSyncApplier.Apply))]
internal static class ReceiveFailClosedBlockerPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        MainFile.Log.Error("Config sync receive blocker: host snapshot rejected (receive guard unavailable, process is fail closed)");
        return false;
    }
}
