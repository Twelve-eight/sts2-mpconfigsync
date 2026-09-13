using System;

using HarmonyLib;

using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Host backstop push on RunManager.InitializeShared.
///
/// ROLE CHANGE (MCS-1, 2026-09-12): this used to be the ONLY push, and its old
/// comment claimed buffering until Launch was desirable. That reasoning failed
/// the first-consumer test - Populate/GenerateRooms/OnRunCreated and Qurious's
/// SetUpNewMultiplayer prefix generation all read config BEFORE Launch, so a
/// buffered snapshot can never align the values those consumers used.
/// The primary barrier now lives in the LOBBY pushes
/// (<see cref="LobbySnapshotPush"/>) which precede the begin-run traffic on the
/// same reliable ordered channel. This patch stays as a backstop re-assertion:
/// it converges any client that missed the lobby push before Launch, and it is
/// idempotent with the lobby push (equal values).
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
            {
                return;
            }
            LobbySnapshotPush.PushIfHost(net, "initialize-shared backstop");
            // BaseLib registers CustomMessageWrapper on this service in ITS
            // InitializeShared postfix; undo our lobby-phase registration so
            // the handler is not installed twice (engine does not dedupe).
            MpNetSession.UnregisterEarlyHandler(net);
        }
        catch (Exception e)
        {
            // Config sync must never break run startup.
            MainFile.Log.Error($"Config sync backstop push failed: {e}");
        }
    }
}
