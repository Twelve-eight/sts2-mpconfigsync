using System;

using HarmonyLib;

using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Lobby constructor capture: remembers the active INetGameService so the
/// receiver can authenticate snapshots in EVERY phase (MCS-2). The lobby and
/// the run share the same service object (SetUpNewMultiplayer receives
/// lobby.NetService), so one capture covers lobby, run and rejoin.
/// </summary>
[HarmonyPatch(typeof(StartRunLobby), MethodType.Constructor,
    new[] { typeof(MegaCrit.Sts2.Core.Runs.GameMode), typeof(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService),
        typeof(IStartRunLobbyListener), typeof(int) })]
internal static class StartRunLobbyCtorCapturePatch
{
    private static void Postfix(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService netService) =>
        MpNetSession.ObserveService(netService);
}

[HarmonyPatch(typeof(LoadRunLobby), MethodType.Constructor,
    new[] { typeof(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService),
        typeof(ILoadRunLobbyListener), typeof(MegaCrit.Sts2.Core.Saves.SerializableRun) })]
internal static class LoadRunLobbyCtorCapturePatch
{
    private static void Postfix(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService netService) =>
        MpNetSession.ObserveService(netService);
}

[HarmonyPatch(typeof(RunLobby), MethodType.Constructor,
    new[] { typeof(MegaCrit.Sts2.Core.Runs.GameMode), typeof(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService),
        typeof(IRunLobbyListener), typeof(IPlayerCollection), typeof(System.Collections.Generic.IEnumerable<RunLobbyPlayer>) })]
internal static class RunLobbyCtorCapturePatch
{
    private static void Postfix(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService netService) =>
        MpNetSession.ObserveService(netService);
}
