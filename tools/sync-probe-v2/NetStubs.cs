using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Platform;

namespace MpConfigSync
{

/// <summary>
/// Exact-source fixture shim: the copied receiver sources call MainFile.Log;
/// the real MainFile's Logger static-initializer reaches into the Godot
/// runtime (native access violation outside the engine). Everything else in
/// the sources is production code.
/// </summary>
internal static class MainFile
{
    public static LogShim Log { get; } = new();
}

internal sealed class LogShim
{
    public void Info(string m) => Lines.Add("INFO " + m);
    public void Warn(string m) => Lines.Add("WARN " + m);
    public void Error(string m) => Lines.Add("ERROR " + m);
    public void Debug(string m) => Lines.Add("DEBUG " + m);
    public List<string> Lines { get; } = new();
}

}

namespace SyncProbeV2
{
    /// <summary>Minimal INetGameService stub for the auth matrix (reject branches).</summary>
    internal sealed class FakeNetService : INetGameService
    {
        public ulong NetId { get; set; } = 1;
        public bool IsConnected { get; set; } = true;
        public bool IsGameLoading { get; set; }
        public NetGameType Type { get; set; } = NetGameType.Client;
        public PlatformType Platform { get; set; }
        public PeerVersionInfo LocalVersion { get; set; }
#pragma warning disable CS0067
        public event Action<NetErrorInfo>? Disconnected;
#pragma warning restore CS0067
        public void SendMessage<T>(T message, ulong playerId) where T : INetMessage { }
        public void SendMessage<T>(T message) where T : INetMessage { }
        public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> h) where T : INetMessage { }
        public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> h) where T : INetMessage { }
        public void Update() { }
        public void Disconnect(NetError reason, bool now = false) { }
        public ConnectionStats? GetStatsForPeer(ulong peerId) => null;
        public void SetGameLoading(bool isLoading) { }
        public void SetBufferMessages(bool bufferMessages) { }
        public string? GetRawLobbyIdentifier() => null;
    }

    /// <summary>Minimal NetClient stub: only HostNetId is consulted by the auth path.</summary>
    internal sealed class FakeNetClient : NetClient
    {
        private ulong HostNetIdValue { get; }
        public FakeNetClient(ulong hostNetId) : base(null!) => HostNetIdValue = hostNetId;
        public override bool IsConnected => true;
        public override ulong NetId => 1;
        public override ulong HostNetId => HostNetIdValue;
        public override void Update() { }
        public override void SendMessageToHost(byte[] bytes, int length, NetTransferMode mode, int channel = 0) { }
        public override void DisconnectFromHost(NetError reason, bool now = false) { }
        public override string? GetRawLobbyIdentifier() => null;
    }
}
