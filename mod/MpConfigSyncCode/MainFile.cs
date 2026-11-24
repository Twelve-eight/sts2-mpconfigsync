using System;
using System.Collections.Generic;
using System.Reflection;

using BaseLib.Config;
using BaseLib.Patches.Localization;

using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

using MpConfigSync.MpConfigSyncCode.Patches;

namespace MpConfigSync.MpConfigSyncCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "MpConfigSync"; // res://MpConfigSync and id prefix MPCONFIGSYNC-
    public const string ResPath = $"res://{ModId}";

    public static MegaCrit.Sts2.Core.Logging.Logger Log { get; } = new(ModId, LogType.Generic);

    /// <summary>
    /// Registration snapshot, taken ONCE in <see cref="ApplyPatches"/> before
    /// any patch is installed: whether the sending / receiving patch classes
    /// were registered for THIS process (F05-rerun, 2026-10-02; fail-closed
    /// handling added by F09, 2026-10-02).
    ///
    /// Why a snapshot instead of only the live config value: the config can
    /// change mid-session - from the settings UI, or from an incoming host
    /// snapshot, because MpConfigSync's own config is itself part of the
    /// synchronized surface. A gate that was OFF at process start must stay
    /// closed until the next process start, because its patch classes are
    /// simply not installed; reading only the live value would let a
    /// mid-session change re-enable a consumer whose registration never
    /// happened. The runtime gate prefixes therefore require BOTH this
    /// snapshot AND the live computed gate.
    ///
    /// FAIL CLOSED (F09): <see cref="ApplyPatches"/> clears these flags when a
    /// protective patch cannot be installed or verified. The guards read the
    /// flags on every call, so a cleared flag blocks the corresponding
    /// direction for the rest of the process, and clearing both flags before
    /// any lobby exists also removes every caller of
    /// <see cref="MpNetSession.ObserveService"/> (no early handler can be
    /// registered that the cleanup postfix would have to remove).
    /// </summary>
    internal static bool SendPatchesRegistered { get; private set; }

    /// <inheritdoc cref="SendPatchesRegistered"/>
    internal static bool ReceivePatchesRegistered { get; private set; }

    public static void Initialize()
    {
        // Settings -> Mod Settings page (BaseLib auto-UI): master toggle plus
        // the sending / receiving behavior-group toggles (F05-rerun).
        ModConfigRegistry.Register(ModId, new MpConfigSyncConfig());
        // BaseLib SimpleLoc for settings labels.
        SimpleLoc.EnableSimpleLoc(ModId);
        // Register C# scripts referenced by scenes shipped in the .pck (none yet).
        Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(Assembly.GetExecutingAssembly());

        // Targeted Harmony patches, applied per-type with try/catch - never PatchAll
        // (one bad patch silently strips the rest). The registration gate inside
        // only installs the patch classes the process-start gates allow.
        ApplyPatches();

        Log.Info($"{ModId} initialized (send patches registered={SendPatchesRegistered}, receive patches registered={ReceivePatchesRegistered}; receiver restores the user's own settings at run end - config files are never rewritten)");
    }

    /// <summary>
    /// Applies the [HarmonyPatch] classes of this assembly individually, each
    /// with its own processor and its own try/catch, and only those the
    /// registration gate allows (see <see cref="ShouldRegisterPatchClass"/>).
    ///
    /// Two phases (F09, 2026-10-02):
    /// 1. PROTECTIVE classes (<see cref="ReceiveGatePatch"/>,
    ///    <see cref="SendGatePatch"/>,
    ///    <see cref="RunManagerInitializeSharedCleanupPatch"/>, plus the
    ///    <see cref="ReceiveFailClosedBlockerPatch"/> fallback) are installed
    ///    FIRST and VERIFIED against Harmony's own patch info. A protective
    ///    class that cannot be verified fails closed BEFORE any consumer class
    ///    is installed, so a half-installed guard can never leave the legacy
    ///    Enabled-only consumer path open. Installation order also matters:
    ///    failing closed clears the registration flags before phase 2, so no
    ///    consumer patch class (and no <see cref="MpNetSession.ObserveService"/>
    ///    caller) is installed afterwards.
    /// 2. CONSUMER classes (push points, lobby captures, run-end restore, the
    ///    backstop push, the display-only menu patch) are installed one by one
    ///    under the registration snapshot. A failure stays contained to its own
    ///    class and is logged.
    ///
    /// Why not <c>Harmony.PatchAll(assembly)</c> (which is what this method used
    /// to call, contradicting its own comment): PatchAll walks the assembly in
    /// one pass, so an exception raised while processing any one patch class
    /// aborts the remaining classes. Here a failure is contained to the class
    /// that caused it, every class's outcome is logged, and the summary line
    /// makes a partial failure visible.
    /// </summary>
    private static void ApplyPatches()
    {
        // Registration snapshot FIRST: read the live gates exactly once, before
        // installing anything. See SendPatchesRegistered for the contract.
        SendPatchesRegistered = MpConfigSyncConfig.SendingEnabled;
        ReceivePatchesRegistered = MpConfigSyncConfig.ReceivingEnabled;

        var harmony = new HarmonyLib.Harmony(ModId);
        Assembly assembly = Assembly.GetExecutingAssembly();
        int patchedMethods = 0;
        int failedClasses = 0;
        int skippedClasses = 0;

        // ---- Phase 1a: receive guard. ----
        // RESIDENT BY NECESSITY (F09): unlike the send side, the caller is not
        // ours. BaseLib's own CustomMessageWrapper handler calls
        // ConfigSyncMessage.HandleMessage on every run, regardless of which of
        // our patch classes were registered, and the consumer files
        // (ConfigSyncMessage / ConfigSyncApplier, owned by B01/B03 in this
        // round) check only the master switch. Without this verified prefix a
        // snapshot would still be applied while EnableReceiving is off, and a
        // mid-session master-on (settings UI) would re-open a path that was
        // closed at process start. This is therefore the one protective class
        // that must be resident whenever the mod is loaded. Its scope is
        // minimal and it never executes sync: it is a single prefix on
        // ConfigSyncMessage.HandleMessage that returns false to skip the
        // handler; it never sends, applies or mutates a value, and any
        // evaluation failure returns false (fail closed).
        if (TryInstallVerifiedPatch(
                harmony,
                typeof(ReceiveGatePatch),
                AccessTools.Method(typeof(ConfigSyncMessage), nameof(ConfigSyncMessage.HandleMessage)),
                out string receiveFailure))
        {
            patchedMethods++;
        }
        else
        {
            failedClasses++;
            ReceivePatchesRegistered = false;
            // FAIL CLOSED, step 2: hard-block the receive path at the one
            // method that actually commits host values. Forcing the master
            // switch off would only hold until something re-enables that
            // mutable static (settings UI, config reload), so it is not a hard
            // block. ConfigSyncApplier.Apply is the only place an incoming
            // value is written (its commit loop calls PropertyInfo.SetValue;
            // ConfigSyncMessage.HandleMessage only preflights and logs), it is
            // our own stable method rather than a game-build-dependent target,
            // and it is far larger than any JIT inlining budget, so a prefix
            // that returns false cannot be bypassed by the caller having
            // inlined the original body. Every later snapshot becomes a no-op
            // independently of Enabled and of the registration flags.
            if (TryInstallVerifiedPatch(
                    harmony,
                    typeof(ReceiveFailClosedBlockerPatch),
                    AccessTools.Method(typeof(ConfigSyncApplier), nameof(ConfigSyncApplier.Apply)),
                    out string blockerFailure))
            {
                patchedMethods++;
                Log.Error($"Config sync FAIL CLOSED: receive guard not installed ({receiveFailure}); hard blocker installed on ConfigSyncApplier.Apply - every host snapshot is rejected for the rest of this process (no cfg file is written)");
            }
            else
            {
                failedClasses++;
                // Last resort: no verified guard and no verified hard blocker.
                // Force the master switch off in memory (no Save()/Changed()
                // call, cfg files untouched, next process start re-reads the
                // user's own value) and close both registration flags before
                // phase 2, so no consumer class and no ObserveService caller is
                // installed either. The send guard is skipped too (phase 1b
                // tests SendPatchesRegistered), so the push entry point has no
                // caller at all.
                MpConfigSyncConfig.Enabled = false;
                SendPatchesRegistered = false;
                ReceivePatchesRegistered = false;
                Log.Error($"Config sync FAIL CLOSED: receive guard AND hard blocker both failed (guard={receiveFailure}; blocker={blockerFailure}); forcing the master switch off in memory for this process - no snapshots will be sent or applied, no cfg file is written");
            }
        }

        // ---- Phase 1b: send guard. ----
        // Registered ONLY when the sending gate was on at process start (F09):
        // that is exactly when a registered patch class reaches
        // LobbySnapshotPush.PushIfHost (the three lobby push points and the
        // backstop push). When sending is off at process start no push patch
        // class is registered, so the push entry point has no caller and the
        // guard is not needed. If the guard cannot be verified while it IS
        // needed, the snapshot flag is cleared and no push class is registered,
        // so the push path stays closed.
        if (SendPatchesRegistered)
        {
            if (TryInstallVerifiedPatch(
                    harmony,
                    typeof(SendGatePatch),
                    AccessTools.Method(typeof(LobbySnapshotPush), nameof(LobbySnapshotPush.PushIfHost)),
                    out string sendFailure))
            {
                patchedMethods++;
            }
            else
            {
                failedClasses++;
                SendPatchesRegistered = false;
                Log.Error($"Config sync FAIL CLOSED: send guard not installed ({sendFailure}); sending is closed for this process (no push patch class will be registered)");
            }
        }
        else
        {
            skippedClasses++;
            Log.Info("config gate: send guard not registered (sending closed at process start or by fail-closed; no push patch class is installed, so the push entry point has no caller)");
        }

        // ---- Phase 1c: early-handler cleanup. ----
        // MpNetSession.ObserveService registers BaseLib's CustomMessageWrapper
        // handler EARLY (during the lobby constructor or the host push) so
        // lobby-phase snapshots are not dropped by the message bus. BaseLib
        // registers the same handler again in its own InitializeShared postfix,
        // and the engine does not dedupe handlers, so every early registration
        // must be removed before BaseLib's postfix runs. Two classes do that:
        // - RunManagerInitializeSharedPatch (B03-owned, unmodified): its postfix
        //   pushes and then unregisters for its own push, so the send path is
        //   self-cleaning;
        // - this cleanup class: covers the lobby-constructor registrations and,
        //   whenever it is installed, is a second idempotent cleanup.
        // It is installed whenever either group is on, because ObserveService
        // can be reached from either group's code paths. When BOTH groups are
        // off no ObserveService caller is registered at all, so no cleanup is
        // needed. If this class cannot be verified, both registration flags are
        // cleared before phase 2, so no ObserveService caller exists and the
        // resident receive guard blocks the incoming path.
        if (SendPatchesRegistered || ReceivePatchesRegistered)
        {
            if (TryInstallVerifiedPatch(
                    harmony,
                    typeof(RunManagerInitializeSharedCleanupPatch),
                    AccessTools.Method(typeof(RunManager), "InitializeShared"),
                    out string cleanupFailure))
            {
                patchedMethods++;
            }
            else
            {
                failedClasses++;
                // FAIL CLOSED: a duplicate CustomMessageWrapper handler would
                // double-handle every mod's custom messages for the session.
                // Clearing both flags before phase 2 removes every
                // ObserveService caller (lobby captures and push points), so no
                // early registration can happen; the receive guard reads
                // ReceivePatchesRegistered and blocks the incoming path.
                SendPatchesRegistered = false;
                ReceivePatchesRegistered = false;
                Log.Error($"Config sync FAIL CLOSED: early-handler cleanup patch not installed ({cleanupFailure}); both directions are closed for this process");
            }
        }
        else
        {
            skippedClasses++;
            Log.Info("config gate: early-handler cleanup patch not registered (both groups closed; no ObserveService caller is installed)");
        }

        // ---- Phase 2: consumer classes. ----
        foreach (Type type in assembly.GetTypes())
        {
            // NOTE: do NOT skip IsAbstract here - C# STATIC classes report
            // IsAbstract=true, and every patch class in this mod is static.
            // The old filter silently skipped ALL of them (live log:
            // "0 method(s) patched across 23 type(s)"), leaving every MCS
            // patch unmounted while reporting zero failures.
            if (type.IsGenericTypeDefinition)
            {
                continue;
            }
            if (!HasHarmonyPatch(type))
            {
                continue;
            }
            // Protective classes are installed (and verified) in phase 1.
            // ReceiveFailClosedBlockerPatch is installed only in the receive
            // guard's failure path, so it must never be installed here.
            if (type == typeof(ReceiveGatePatch)
                || type == typeof(SendGatePatch)
                || type == typeof(ReceiveFailClosedBlockerPatch)
                || type == typeof(RunManagerInitializeSharedCleanupPatch))
            {
                continue;
            }
            if (!ShouldRegisterPatchClass(type, out string skipReason))
            {
                skippedClasses++;
                Log.Info($"config gate: skipping Harmony patch class {type.FullName} ({skipReason})");
                continue;
            }
            try
            {
                // CreateClassProcessor + Patch() is exactly what PatchAll does
                // per type internally, so the resulting patches are identical -
                // only the error containment differs.
                List<MethodInfo> patched = harmony.CreateClassProcessor(type).Patch();
                if (HasNullReplacement(patched))
                {
                    failedClasses++;
                    Log.Error($"Harmony patch class {type.FullName} returned a null replacement (patch NOT installed)");
                    continue;
                }
                if (patched.Count == 0)
                {
                    failedClasses++;
                    Log.Error($"Harmony patch class {type.FullName} produced no patched methods (target missing or patch refused)");
                    continue;
                }
                patchedMethods += patched.Count;
                Log.Info($"patched {patched.Count} method(s) via {type.FullName}");
            }
            catch (Exception e)
            {
                failedClasses++;
                Log.Error($"Harmony patch class {type.FullName} failed (continuing with the remaining classes): {e}");
            }
        }

        Log.Info($"Harmony: {patchedMethods} method(s) patched across {assembly.GetTypes().Length} type(s), {failedClasses} patch class(es) failed, {skippedClasses} skipped by config gate");
    }

    /// <summary>
    /// Installs one protective patch class and VERIFIES the result (F09,
    /// 2026-10-02). The verification is deliberately redundant with the patch
    /// call: a patch class can throw after partially installing, Harmony can
    /// return a null replacement when a prepare hook refuses, and the target
    /// method can drift in a future game build. For the protective classes a
    /// silent failure would leave the legacy Enabled-only consumer path (or a
    /// duplicated message handler) behind, so "not verified" is treated as "not
    /// installed" and the caller fails closed.
    ///
    /// All three protective classes are single-target classes, so the returned
    /// replacement list must contain exactly one non-null entry; a shorter list
    /// is the "incomplete set" failure mode and is rejected here.
    /// </summary>
    private static bool TryInstallVerifiedPatch(HarmonyLib.Harmony harmony, Type patchClass, MethodBase? target, out string failure)
    {
        failure = string.Empty;
        if (target == null)
        {
            failure = "target method not found (game build drift)";
            return false;
        }
        try
        {
            List<MethodInfo> patched = harmony.CreateClassProcessor(patchClass).Patch();
            if (patched.Count != 1)
            {
                failure = $"Harmony returned {patched.Count} replacement(s) for a single-target protective class";
                return false;
            }
            if (patched[0] == null)
            {
                failure = "Harmony returned a null replacement (patch not installed)";
                return false;
            }
            if (!IsOurPatchInstalled(harmony, target, patchClass))
            {
                failure = $"post-install verification did not find {patchClass.Name} on {target.DeclaringType?.Name}.{target.Name}";
                return false;
            }
            Log.Info($"patched 1 method(s) via {patchClass.FullName} (verified protective patch)");
            return true;
        }
        catch (Exception e)
        {
            failure = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// True when Harmony's shared patch info for the target lists one of OUR
    /// patch methods declared by <paramref name="patchClass"/> (F09). This is the
    /// post-install source of truth: it reads the same state the engine uses at
    /// call time, not the return value of the patch call.
    /// </summary>
    private static bool IsOurPatchInstalled(HarmonyLib.Harmony harmony, MethodBase target, Type patchClass)
    {
        HarmonyLib.Patches? info = HarmonyLib.Harmony.GetPatchInfo(target);
        if (info == null)
        {
            return false;
        }
        foreach (HarmonyLib.Patch patch in info.Prefixes)
        {
            if (IsOurPatch(patch, harmony, patchClass))
            {
                return true;
            }
        }
        foreach (HarmonyLib.Patch patch in info.Postfixes)
        {
            if (IsOurPatch(patch, harmony, patchClass))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsOurPatch(HarmonyLib.Patch patch, HarmonyLib.Harmony harmony, Type patchClass) =>
        patch != null
        && patch.owner == harmony.Id
        && patch.PatchMethod != null
        && patch.PatchMethod.DeclaringType == patchClass;

    private static bool HasNullReplacement(List<MethodInfo> patched)
    {
        foreach (MethodInfo method in patched)
        {
            if (method == null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Registration gate for the CONSUMER patch classes (phase 2 of
    /// <see cref="ApplyPatches"/>). The mapping is exhaustive and FAIL CLOSED -
    /// a patch class that is not explicitly classified here is NOT registered,
    /// so a future patch cannot silently escape its group's gate.
    ///
    /// The three protective classes (<see cref="ReceiveGatePatch"/>,
    /// <see cref="SendGatePatch"/>,
    /// <see cref="RunManagerInitializeSharedCleanupPatch"/>) never reach this
    /// method: they are installed (and verified) in phase 1.
    ///
    /// Group rules (F09, 2026-10-02):
    /// - Sending group: the three host push points plus the
    ///   <see cref="RunManagerInitializeSharedPatch"/> backstop. Registered only
    ///   when the sending gate was on at process start, so
    ///   <see cref="LobbySnapshotPush.PushIfHost"/> has no caller - and the send
    ///   guard is not needed - whenever sending is off.
    /// - Receiving group: lobby-constructor capture (early BaseLib handler
    ///   registration + sender authentication) and the run-end restore of the
    ///   user's own values.
    /// - Display-only patch: the main-menu "N of them affect gameplay" suffix.
    ///   Deliberately ungated - it is not part of the config-sync behavior, and
    ///   its own class doc documents the always-on contract.
    /// </summary>
    private static bool ShouldRegisterPatchClass(Type type, out string reason)
    {
        // Sending group: the three host push points (new-run lobby, load-run
        // lobby, rejoin request).
        if (type == typeof(StartRunLobbyBeginPushPatch)
            || type == typeof(LoadRunLobbyBeginPushPatch)
            || type == typeof(RunLobbyRejoinPushPatch))
        {
            reason = $"sending gate off at process start (Enabled={MpConfigSyncConfig.Enabled}, EnableSending={MpConfigSyncConfig.EnableSending})";
            return SendPatchesRegistered;
        }

        // Backstop push (sending). Its own postfix also removes the early
        // handler registration it causes, so it is safe to install for the
        // sending group alone.
        if (type == typeof(RunManagerInitializeSharedPatch))
        {
            reason = $"sending gate off at process start (Enabled={MpConfigSyncConfig.Enabled}, EnableSending={MpConfigSyncConfig.EnableSending})";
            return SendPatchesRegistered;
        }

        // Receiving group: lobby-constructor capture (early BaseLib handler
        // registration + sender authentication) and the run-end restore of the
        // user's own values.
        if (type == typeof(StartRunLobbyCtorCapturePatch)
            || type == typeof(LoadRunLobbyCtorCapturePatch)
            || type == typeof(RunLobbyCtorCapturePatch)
            || type == typeof(RunManagerCleanUpPatch))
        {
            reason = $"receiving gate off at process start (Enabled={MpConfigSyncConfig.Enabled}, EnableReceiving={MpConfigSyncConfig.EnableReceiving})";
            return ReceivePatchesRegistered;
        }

        // Display-only patch: the main-menu "N of them affect gameplay"
        // suffix. Deliberately ungated - it is not part of the config-sync
        // behavior, and its own class doc documents the always-on contract.
        if (type == typeof(MainMenuGameplayModCountPatch))
        {
            reason = "display-only patch (not part of config sync)";
            return true;
        }

        // FAIL CLOSED: an unclassified patch class is never registered.
        reason = "unclassified patch class - not registered (fail closed; classify it in ShouldRegisterPatchClass)";
        return false;
    }

    /// <summary>
    /// True when the type (or one of its declared methods) carries
    /// [HarmonyPatch]. Checked explicitly so a type without any patch attribute
    /// is skipped instead of relying on CreateClassProcessor tolerating it.
    /// </summary>
    private static bool HasHarmonyPatch(Type type)
    {
        if (type.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true).Length > 0)
        {
            return true;
        }
        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true).Length > 0)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Early-handler cleanup on RunManager.InitializeShared (F09, 2026-10-02).
///
/// WHY THIS EXISTS: <see cref="MpNetSession.ObserveService"/> registers
/// BaseLib's CustomMessageWrapper handler EARLY (during the lobby constructor
/// or the host push) so lobby-phase snapshots are not dropped by the message
/// bus. BaseLib registers the same handler again in its own InitializeShared
/// postfix, and the engine does not dedupe handlers, so the early registration
/// must be removed before BaseLib's postfix runs. The B03-owned
/// RunManagerInitializeSharedPatch removes the registration its own backstop
/// push causes; this class covers the lobby-constructor registrations, and it
/// is a second idempotent cleanup for the send path whenever it is installed.
/// It is installed whenever either group is on (see ApplyPatches phase 1c),
/// verified like the guards, and if it cannot be installed both registration
/// flags are cleared before any ObserveService caller is installed.
///
/// [HarmonyBefore("BaseLib")] is the ordering contract: this postfix must run
/// before BaseLib's own InitializeShared postfix registers its handler, or the
/// unregister would remove BaseLib's handler as well (the engine removes all
/// equal delegates).
/// </summary>
[HarmonyPatch(typeof(RunManager), "InitializeShared")]
[HarmonyBefore("BaseLib")]
internal static class RunManagerInitializeSharedCleanupPatch
{
    private static void Postfix(RunManager __instance)
    {
        try
        {
            MpNetSession.UnregisterEarlyHandler(__instance.NetService);
        }
        catch (Exception e)
        {
            // Cleanup must never break run startup.
            MainFile.Log.Error($"Config sync early-handler cleanup failed: {e.Message}");
        }
    }
}
