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

    private static readonly System.Collections.Generic.HashSet<INetGameService> EarlyRegistered = new();

    /// <summary>
    /// Called by the lobby constructor postfixes. ALSO registers BaseLib's
    /// CustomMessageWrapper handler on the service EARLY (second-round review,
    /// 2026-09-13): BaseLib only registers it at RunManager.InitializeShared
    /// postfix, and NetMessageBus DROPS messages whose type has no handler -
    /// so lobby-time config pushes (the whole MCS-1 barrier) would be thrown
    /// away. Our own registration is undone in the InitializeShared backstop
    /// patch, because the engine does NOT dedupe handlers and BaseLib
    /// registers the same handler again there.
    /// </summary>
    internal static void ObserveService(INetGameService? service)
    {
        if (service == null)
        {
            return;
        }
        CurrentService = service;
        if (EarlyRegistered.Add(service))
        {
            try
            {
                typeof(BaseLib.Abstracts.CustomMessageWrapper)
                    .GetMethod("Register", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.Invoke(null, new object?[] { service });
                MainFile.Log.Info("Config sync: CustomMessageWrapper registered early for the lobby phase");
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Config sync: early CustomMessageWrapper registration failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Undo the early registration once BaseLib's own InitializeShared postfix
    /// has (or will) register the handler on this service, so exactly one
    /// handler remains regardless of postfix order.
    /// </summary>
    internal static void UnregisterEarlyHandler(INetGameService? service)
    {
        if (service == null || !EarlyRegistered.Remove(service))
        {
            return;
        }
        try
        {
            typeof(BaseLib.Abstracts.CustomMessageWrapper)
                .GetMethod("Unregister", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object?[] { service });
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Config sync: early CustomMessageWrapper unregister failed: {e.Message}");
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
