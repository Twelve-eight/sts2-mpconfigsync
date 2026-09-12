using System;

using BaseLib.Abstracts;
using HarmonyLib;

using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// HOST push points, ordered so the snapshot reliably reaches clients BEFORE
/// the first deterministic config consumer runs (MCS-1).
///
/// The engine's transport is reliable and in-order per peer, and the host
/// transmits the begin-run messages from inside these methods. A prefix send
/// therefore lands on the client BEFORE the begin-run message, which the
/// client processes before its own SetUpNewMultiplayer -> InitializeShared ->
/// Populate -> GenerateRooms chain runs. That ordering IS the barrier; it is a
/// property of message order on one reliable channel, not of method names.
///
/// 1. StartRunLobby.BeginRunForAllPlayers   (new-run lobby: host funnel that
///    sends LobbyBeginRunMessage and calls BeginRunLocally)
/// 2. LoadRunLobby.TryBeginRunForAllPlayers (load-a-save lobby: host funnel
///    that sends LobbyBeginLoadedRunMessage)
/// 3. RunLobby.HandleClientRejoinRequestMessage (rejoin: prefix so the
///    snapshot precedes the engine's ClientRejoinResponseMessage, i.e. the
///    rejoining client rebuilds its state under the host's config, MCS-4)
/// 4. RunManager.InitializeShared postfix (kept as a BACKSTOP re-assertion
///    for a client that somehow missed the lobby push; by then the first
///    consumers have already run on the client, so this push alone can no
///    longer guarantee MCS-1 - it exists to converge values before Launch).
///
/// All sends are host-only and exception-isolated: config sync must never
/// break run startup (same contract as the InitializeShared patch).
/// </summary>
internal static class LobbySnapshotPush
{
    internal static void PushIfHost(INetGameService? net, string phase)
    {
        try
        {
            if (net == null || net.Type != NetGameType.Host)
            {
                return;
            }
            if (!MpConfigSyncConfig.Enabled)
            {
                MainFile.Log.Info($"Config sync disabled locally, not pushing ({phase})");
                return;
            }
            ConfigSyncMessage message = SnapshotBuilder.Build();
            if (message.ModIds.Count == 0)
            {
                MainFile.Log.Info($"Config sync: no syncable config entries found, nothing to push ({phase})");
                return;
            }
            CustomMessageWrapper.Send(message, net);
            MpNetSession.ObserveService(net);
            MainFile.Log.Info($"Config sync: pushed {message.ModIds.Count} entries to all peers ({phase})");
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Config sync push failed ({phase}): {e}");
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayers")]
internal static class StartRunLobbyBeginPushPatch
{
    private static void Prefix(StartRunLobby __instance) =>
        LobbySnapshotPush.PushIfHost(__instance.NetService, "start-lobby begin");
}

[HarmonyPatch(typeof(LoadRunLobby), "TryBeginRunForAllPlayers")]
internal static class LoadRunLobbyBeginPushPatch
{
    private static void Prefix(LoadRunLobby __instance) =>
        LobbySnapshotPush.PushIfHost(__instance.NetService, "load-lobby begin");
}

[HarmonyPatch(typeof(RunLobby), "HandleClientRejoinRequestMessage")]
internal static class RunLobbyRejoinPushPatch
{
    private static readonly System.Reflection.FieldInfo? NetServiceField =
        AccessTools.Field(typeof(RunLobby), "_netService");

    private static void Prefix(RunLobby __instance) =>
        LobbySnapshotPush.PushIfHost(NetServiceField?.GetValue(__instance) as INetGameService, "rejoin request");
}
