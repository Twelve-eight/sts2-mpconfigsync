using System;

using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Current MP transport context, captured by the lobby-constructor patches.
///
/// WHY THIS EXISTS (MCS-2 auth): a config snapshot can arrive in three phases -
/// lobby (before the run exists), live run (re-assertion), rejoin. Only the
/// live-run phase has RunManager.Instance.NetService populated; during the
/// lobby the service lives on StartRunLobby/LoadRunLobby, which the engine
/// constructs with it. The ctor postfixes remember the latest service so the
/// receiver can authenticate the SENDER in every phase.
///
/// AUTH MODEL (matches the engine's own handshake check,
/// NetClientGameService.HandshakeMessageReceived: senderId == NetClient.HostNetId):
/// a snapshot is only applied when WE are a connected client and the sender's
/// transport identity IS the host. Hosts reject every snapshot (client -> host
/// config pushes are never legitimate); singleplayer/replay rejects because
/// Type != Client. BaseLib's ShouldBroadcast=false only stops relaying - it
/// does NOT stop a host from RECEIVING a client-sent message, which is exactly
/// the hole the sender check closes.
/// </summary>
internal static class MpNetSession
{
    /// <summary>Latest lobby/net service seen; the lobby and the run share the same object.</summary>
    internal static INetGameService? CurrentService { get; set; }

    /// <summary>Called by the lobby constructor postfixes.</summary>
    internal static void ObserveService(INetGameService? service)
    {
        if (service != null)
        {
            CurrentService = service;
        }
    }

    /// <summary>Read-only capture of one authentication decision for logging/tests.</summary>
    internal readonly record struct AuthResult(bool Allowed, string Reason);

    internal static AuthResult AuthorizeSnapshot(ulong senderId)
    {
        INetGameService? net = CurrentService ?? RunManager.Instance?.NetService;
        if (net == null)
        {
            return new AuthResult(false, "no active net service (no session)");
        }
        if (!net.IsConnected)
        {
            return new AuthResult(false, "net service not connected");
        }
        if (net.Type != NetGameType.Client)
        {
            return new AuthResult(false, $"receiver is {net.Type}, not Client - host/SP never accepts config pushes");
        }
        if (net is not NetClientGameService client)
        {
            return new AuthResult(false, $"unexpected service type {net.GetType().Name}, cannot resolve host id");
        }
        ulong hostId;
        try
        {
            hostId = client.HostNetId;
        }
        catch (InvalidOperationException e)
        {
            return new AuthResult(false, $"host id unavailable: {e.Message}");
        }
        if (senderId != hostId)
        {
            return new AuthResult(false, $"sender {senderId} is not the host ({hostId})");
        }
        return new AuthResult(true, "client <- current host");
    }
}
