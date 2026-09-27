using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using BaseLib.Abstracts;
using BaseLib.Config;

using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

using MpConfigSync;
using MpConfigSync.MpConfigSyncCode;

namespace SyncProbeV4;

/// <summary>
/// Isolated probe for R05-01 (deserialization allocated before the length check)
/// and R05-04 (an oversized string config silently killed the whole snapshot).
///
/// The production sources are COMPILED IN PLACE from ../../mod/MpConfigSyncCode
/// (only MainFile.Log is a shim, because the real logger's static initializer
/// needs the Godot runtime), so this probe cannot drift from the shipped code: it
/// fails to build exactly when the production sources fail to build.
///
/// PACKET LAYOUT (the same bytes the engine's NetMessageBus hands to
/// Deserialize). NetMessageBus.SerializeMessage writes
/// WriteByte((byte)message.ToId()) then WriteULong(senderId) then
/// message.Serialize(writer), and TryDeserializeMessage consumes the same two
/// fields with ReadByte() + ReadULong() before calling message.Deserialize.
/// ToId() is not callable outside the game (MessageTypes._cache is only built at
/// runtime), so BuildEnginePacket writes a constant id byte and ReadEnginePacket
/// consumes it symmetrically - the id is never resolved by this probe.
/// Everything below is therefore real engine PacketReader/PacketWriter state
/// over a real engine byte layout, with the production Serialize/Deserialize in
/// the middle.
///
/// Covered:
/// 1. round-trip fidelity, including CJK and 4-byte UTF-8 values and a payload
///    whose first string starts at a bit offset that is not a multiple of 8;
/// 2. legacy evidence: the ENGINE's own PacketReader.ReadString still allocates
///    >= 1e9 bytes from a declared length of 1000000000 (the pre-fix mod called
///    exactly this and only compared the resulting length afterwards);
/// 3. the fixed receiver rejects the SAME payload with InvalidDataException while
///    allocating under 100000 bytes;
/// 4. the malformed-length matrix (negative, int.MaxValue, above the byte cap,
///    truncated, mid-string truncation, entry count out of range), each asserted
///    to reject BEFORE allocating;
/// 5. boundary acceptance/rejection at exactly 256 CJK chars / 768 bytes, 128
///    emoji / 256 chars, and 257 CJK chars / 771 bytes;
/// 6. no half commit: a packet whose first entry is valid and whose second entry
///    is oversized throws and leaves the applier and every fixture cfg untouched;
/// 7. the sender-side filter end to end: the builder drops the oversized key,
///    counts it, logs which key it dropped, and the host push sends only the
///    healthy entry.
/// </summary>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Upper bound for "rejected before allocating". Generous enough to
    /// absorb exception-object and JIT noise, far below the 2^30-byte allocation
    /// the engine path performs for the same declared length.</summary>
    private const long BoundedAllocation = 100_000;

    /// <summary>Message id byte; the probe consumes it symmetrically and never
    /// resolves it (see the class comment).</summary>
    private const byte MessageIdByte = 0x42;

    private const ulong SenderId = 7;

    /// <summary>The declared string length named by R05-01.</summary>
    private const int HostileDeclaredLength = 1_000_000_000;

    private static int _failures;
    private static string _dir = string.Empty;
    private static readonly Dictionary<string, string> CfgPaths = new();

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        if (!ok) _failures++;
    }

    private static void Note(string text) => Console.WriteLine("      " + text);

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    public static int Main()
    {
        Console.WriteLine("MpConfigSync sync-probe-v4 (R05-01 / R05-04)");
        Console.WriteLine($"engine sts2.dll : {typeof(PacketReader).Assembly.Location}");
        Console.WriteLine($"engine version  : {typeof(PacketReader).Assembly.GetName().Version}");
        Console.WriteLine("production sources compiled IN PLACE from ../../mod/MpConfigSyncCode (no copies)");

        string root = Path.Combine(Path.GetTempPath(), "mcs-probe-v4-" + Guid.NewGuid().ToString("N")[..8]);
        _dir = Path.Combine(root, "mod_configs");
        Directory.CreateDirectory(_dir);

        // Genuine-host transport identity (the same route sync-probe-v3 uses): an
        // uninitialized NetClientGameService + a NetClient stub. Scenario 6 needs
        // this so the real Apply path COULD run - otherwise "nothing was applied"
        // would be vacuous.
        ulong hostId = 42;
        InstallGenuineHost(hostId);
        Check(MpNetSession.AuthorizeSnapshot(hostId).Allowed,
              "fixture: a genuine host sender is authorized (the real Apply path can run)");

        RunRoundTrip();
        RunLegacyEngineAllocation();
        RunFixedRejection();
        RunMalformedMatrix();
        RunBoundary();
        RunNoHalfCommit(hostId);
        RunSenderFilter();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "PROBE OK" : $"PROBE FAILED: {_failures} check(s)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- 1. round-trip fidelity ----------

    private static void RunRoundTrip()
    {
        Section("1. round-trip fidelity (CJK, 4-byte UTF-8, non-byte-aligned start)");

        string ascii = "probe-value-42";
        string cjk = Cjk(4);
        string emoji = Emoji(2);

        Check(cjk.Length == 4 && Encoding.UTF8.GetByteCount(cjk) == 12,
              "fixture: the CJK value is 4 chars / 12 UTF-8 bytes",
              $"(chars={cjk.Length} bytes={Encoding.UTF8.GetByteCount(cjk)})");
        Check(emoji.Length == 4 && Encoding.UTF8.GetByteCount(emoji) == 8,
              "fixture: the 4-byte UTF-8 value is 4 UTF-16 chars / 8 UTF-8 bytes",
              $"(chars={emoji.Length} bytes={Encoding.UTF8.GetByteCount(emoji)})");

        var sent = new ConfigSyncMessage();
        Add(sent, "ProbeRoundTrip", "Ascii", ascii);
        Add(sent, "ProbeRoundTrip", "Cjk", cjk);
        Add(sent, "ProbeRoundTrip", "Emoji", emoji);

        // 1a: engine layout, byte aligned. The expected size is derived from the
        // documented layout ([id][sender][count] then 3 length-prefixed strings per
        // entry), so this asserts the layout, not the implementation's arithmetic.
        byte[] packet = BuildEnginePacket(sent, SenderId);
        int expectedLength = 1 + 8 + 4
                             + EntryWireBytes("ProbeRoundTrip", "Ascii", ascii)
                             + EntryWireBytes("ProbeRoundTrip", "Cjk", cjk)
                             + EntryWireBytes("ProbeRoundTrip", "Emoji", emoji);
        Check(packet.Length == expectedLength,
              "the packet is exactly the documented engine layout size",
              $"(bytes={packet.Length} expected={expectedLength})");

        var received = new ConfigSyncMessage();
        Exception? error = null;
        try
        {
            received = ReadEnginePacket(packet);
        }
        catch (Exception e)
        {
            error = e;
        }
        Check(error == null, "the byte-aligned snapshot deserializes without error", $"(ex={Describe(error)})");
        Check(Equal(received.ModIds, sent.ModIds) && Equal(received.PropertyNames, sent.PropertyNames)
              && Equal(received.Values, sent.Values),
              "a byte-aligned snapshot round-trips through the production Serialize/Deserialize",
              $"(3 entries, {packet.Length} bytes)");
        Check(At(received.Values, 1) == cjk,
              "the CJK value survives byte-identically", $"(chars={At(received.Values, 1).Length})");
        Check(At(received.Values, 2) == emoji,
              "the 4-byte UTF-8 value survives byte-identically", $"(chars={At(received.Values, 2).Length})");

        // 1b: the same snapshot with the payload starting at a bit offset that is
        // not a multiple of 8, i.e. exactly what another message's WriteBool or
        // quantized field produces before a string.
        var writer = NewWriter();
        writer.WriteBool(true);
        sent.Serialize(writer);
        byte[] offsetPacket = Finish(writer);

        var reader = new PacketReader();
        reader.Reset(offsetPacket);
        bool flag = reader.ReadBool();
        Check(flag && reader.BitPosition % 8 != 0,
              "fixture: the message payload starts at a non-byte-aligned bit offset",
              $"(bit={reader.BitPosition})");

        var offsetReceived = new ConfigSyncMessage();
        Exception? offsetError = null;
        try
        {
            offsetReceived.Deserialize(reader);
        }
        catch (Exception e)
        {
            offsetError = e;
        }
        Check(offsetError == null, "the bit-offset payload deserializes without error", $"(ex={Describe(offsetError)})");
        Check(Equal(offsetReceived.Values, sent.Values),
              "the same strings round-trip when the first string starts at a non-byte-aligned bit offset",
              $"(chars={string.Join(",", offsetReceived.Values.ConvertAll(v => v.Length.ToString()))})");
        Check(reader.BitPosition == writer.BitPosition,
              "the reader consumed exactly the bits the writer produced",
              $"(reader={reader.BitPosition} writer={writer.BitPosition})");
    }

    // ---------- 2. legacy evidence: the engine primitive allocates unbounded ----------

    private static void RunLegacyEngineAllocation()
    {
        Section("2. legacy evidence: the ENGINE's ReadString allocates from the declared length");
        Note("payload: [id byte][sender ulong][count int32=1][declared length int32][body bytes]");
        Note("warm-up uses declared=1024 so JIT cost is not inside the measured call");

        EngineReadString(HostilePacket(1, 1024, 16));

        EngineOutcome measured = EngineReadString(HostilePacket(1, HostileDeclaredLength, 16));
        Note($"declared length         : {HostileDeclaredLength}");
        Note($"outcome                 : {measured.Outcome}");
        Note($"thread allocated delta  : {measured.ThreadDelta} bytes");
        Note($"process allocated delta : {measured.TotalDelta} bytes");

        Check(measured.ThreadDelta >= 1_000_000_000,
              "engine ReadString allocated >= 1e9 bytes on the calling thread",
              $"(delta={measured.ThreadDelta})");
        Check(measured.TotalDelta >= 1_000_000_000,
              "engine ReadString allocated >= 1e9 bytes process-wide",
              $"(delta={measured.TotalDelta})");
        Check(!measured.Outcome.StartsWith("InvalidDataException", StringComparison.Ordinal),
              "the engine primitive itself has no wire-cap rejection (this is the pre-fix code path)",
              $"(outcome={measured.Outcome})");
    }

    // ---------- 3. the fixed receiver rejects the same payload ----------

    private static void RunFixedRejection()
    {
        Section("3. the production Deserialize rejects the SAME payload before allocating");

        byte[] hostile = HostilePacket(1, HostileDeclaredLength, 16);
        TryProductionDeserialize(hostile, new ConfigSyncMessage(), out _, out _); // JIT warm-up

        var message = new ConfigSyncMessage();
        Exception? error = TryProductionDeserialize(hostile, message, out long threadDelta, out long totalDelta);
        Note($"outcome                 : {Describe(error)}");
        Note($"thread allocated delta  : {threadDelta} bytes");
        Note($"process allocated delta : {totalDelta} bytes");

        Check(error is InvalidDataException,
              "production Deserialize throws InvalidDataException", $"(ex={Describe(error)})");
        Check(error != null && error.Message.Contains("entry 0 modId", StringComparison.Ordinal),
              "the rejection names the field it rejected", $"(message={error?.Message})");
        Check(error != null && error.Message.Contains("1000000000", StringComparison.Ordinal),
              "the rejection names the offending size", $"(message={error?.Message})");
        Check(threadDelta < BoundedAllocation,
              "the fixed path allocates under 100000 bytes on the calling thread", $"(delta={threadDelta})");
        Check(totalDelta < BoundedAllocation,
              "the fixed path allocates under 100000 bytes process-wide", $"(delta={totalDelta})");
        Check(message.ModIds.Count == 0 && message.Values.Count == 0,
              "the rejected message carries no partially decoded entries");
    }

    // ---------- 4. malformed-length matrix ----------

    private static void RunMalformedMatrix()
    {
        Section("4. malformed-length matrix (every case rejects BEFORE allocating)");

        var cases = new (string Label, byte[] Packet, string Fragment)[]
        {
            ("declared length -1", HostilePacket(1, -1, 16), "is negative"),
            ("declared length int.MaxValue", HostilePacket(1, int.MaxValue, 16), "exceeds the 768-byte wire cap"),
            ("declared length MaxStringBytes+1 (769)", HostilePacket(1, 769, 16), "exceeds the 768-byte wire cap"),
            ("declared length 768 on a packet too short to hold it", HostilePacket(1, 768, 0), "does not fit"),
            ("declared length 200 truncated mid-string", HostilePacket(1, 200, 20), "does not fit"),
            ("entry count 513", CountPacket(513), "entry count 513 outside [0,512]"),
            ("entry count -1", CountPacket(-1), "entry count -1 outside [0,512]"),
        };

        foreach (var c in cases)
        {
            TryProductionDeserialize(c.Packet, new ConfigSyncMessage(), out _, out _); // JIT every path first
        }

        foreach (var c in cases)
        {
            var message = new ConfigSyncMessage();
            Exception? error = TryProductionDeserialize(c.Packet, message, out long threadDelta, out long totalDelta);
            Check(error is InvalidDataException && error.Message.Contains(c.Fragment, StringComparison.Ordinal),
                  $"rejected with InvalidDataException: {c.Label}",
                  $"(ex={Describe(error)})");
            Check(threadDelta < BoundedAllocation && totalDelta < BoundedAllocation,
                  $"allocation stayed bounded: {c.Label}",
                  $"(threadDelta={threadDelta} totalDelta={totalDelta} packet={c.Packet.Length}B)");
        }
    }

    // ---------- 5. boundary acceptance and rejection ----------

    private static void RunBoundary()
    {
        Section("5. boundary acceptance and rejection");

        int charCap = ConfigSyncMessage.MaxStringLength;
        int byteCap = ConfigSyncMessage.MaxStringBytes;

        string cjkAtCharCap = Cjk(charCap);
        string cjkOverCap = Cjk(charCap + 1);
        string emojiAtCharCap = Emoji(charCap / 2);

        Check(cjkAtCharCap.Length == charCap && Encoding.UTF8.GetByteCount(cjkAtCharCap) == byteCap,
              "fixture: the CJK value is exactly the char cap and exactly the byte cap",
              $"(chars={cjkAtCharCap.Length} bytes={Encoding.UTF8.GetByteCount(cjkAtCharCap)})");
        Check(emojiAtCharCap.Length == charCap && Encoding.UTF8.GetByteCount(emojiAtCharCap) == charCap * 2,
              "fixture: the emoji value is exactly the char cap / 512 UTF-8 bytes",
              $"(chars={emojiAtCharCap.Length} bytes={Encoding.UTF8.GetByteCount(emojiAtCharCap)})");
        Check(cjkOverCap.Length == charCap + 1 && Encoding.UTF8.GetByteCount(cjkOverCap) == byteCap + 3,
              "fixture: the over-cap CJK value is char cap+1 / byte cap+3",
              $"(chars={cjkOverCap.Length} bytes={Encoding.UTF8.GetByteCount(cjkOverCap)})");

        Check(ConfigSyncMessage.FitsWireLimit(cjkAtCharCap, out string reasonAtCap) && reasonAtCap.Length == 0,
              "FitsWireLimit accepts exactly the char cap in CJK / the byte cap",
              $"(reason='{reasonAtCap}')");
        Check(ConfigSyncMessage.FitsWireLimit(emojiAtCharCap, out string reasonEmoji) && reasonEmoji.Length == 0,
              "FitsWireLimit accepts exactly the char cap in emoji", $"(reason='{reasonEmoji}')");
        Check(ConfigSyncMessage.FitsWireLimit("", out string reasonEmpty) && reasonEmpty.Length == 0,
              "FitsWireLimit accepts the empty string", $"(reason='{reasonEmpty}')");

        bool fitsOverCap = ConfigSyncMessage.FitsWireLimit(cjkOverCap, out string reasonOverCap);
        Check(!fitsOverCap, "FitsWireLimit rejects the over-cap CJK value");
        Check(reasonOverCap.Contains(cjkOverCap.Length.ToString(), StringComparison.Ordinal)
              && reasonOverCap.Contains(Encoding.UTF8.GetByteCount(cjkOverCap).ToString(), StringComparison.Ordinal)
              && reasonOverCap.Contains(charCap.ToString(), StringComparison.Ordinal)
              && reasonOverCap.Contains(byteCap.ToString(), StringComparison.Ordinal),
              "the rejection reason names both actual counts and both caps", $"(reason={reasonOverCap})");

        // The accepted boundaries must survive the production round trip.
        Check(RoundTrip("ProbeBoundary", "Cjk", cjkAtCharCap, out string gotCjk),
              "the char-cap CJK value round-trips through the production Serialize/Deserialize");
        Check(gotCjk == cjkAtCharCap, "the CJK boundary value is identical after the round trip");
        Check(RoundTrip("ProbeBoundary", "Emoji", emojiAtCharCap, out string gotEmoji),
              "the char-cap emoji value round-trips through the production Serialize/Deserialize");
        Check(gotEmoji == emojiAtCharCap, "the emoji boundary value is identical after the round trip");

        // The rejected boundary must be rejected by the receiver too. The packet is
        // built by the engine's own WriteString, so the declared length is real.
        byte[] rejectedPacket = RawValuePacket("ProbeBoundary", "Value", cjkOverCap);
        var message = new ConfigSyncMessage();
        Exception? error = TryProductionDeserialize(rejectedPacket, message, out long threadDelta, out _);
        Check(error is InvalidDataException,
              "Deserialize rejects the over-cap declared value", $"(ex={Describe(error)})");
        Check(error != null && error.Message.Contains("entry 0 value", StringComparison.Ordinal),
              "that rejection names the value field", $"(message={error?.Message})");
        Check(error != null && error.Message.Contains(byteCap.ToString(), StringComparison.Ordinal),
              "that rejection names the byte cap", $"(message={error?.Message})");
        Check(threadDelta < BoundedAllocation,
              "the boundary rejection allocates under 100000 bytes", $"(threadDelta={threadDelta})");
        Check(message.ModIds.Count == 0,
              "the boundary rejection leaves no partially decoded entries on the message");
    }

    // ---------- 6. no half commit ----------

    private static void RunNoHalfCommit(ulong hostId)
    {
        Section("6. no half commit when the SECOND entry is oversized");

        Registry().Clear();
        CfgPaths.Clear();
        ModConfig target = Register("ProbeTarget", typeof(ProbeTargetSettings), CfgPaths);
        ProbeTargetSettings.Value = "own-target-value";
        target.Save();

        Check(File.Exists(CfgPaths["ProbeTarget"]), "fixture: ProbeTarget.cfg exists on disk");
        Check(!IsReadOnly(CfgPaths["ProbeTarget"]), "fixture: ProbeTarget.cfg starts writable");

        // Positive control: the same packet SHAPE does commit when it is valid.
        // Without this, "nothing was applied" would pass even on a broken fixture.
        var valid = new ConfigSyncMessage();
        Add(valid, "ProbeTarget", "Value", "99");
        ConfigSyncApplier.Apply(valid, hostId);
        Check(ProbeTargetSettings.Value == "99",
              "positive control: the real applier commits a valid packet", $"(value={ProbeTargetSettings.Value})");
        Check(IsReadOnly(CfgPaths["ProbeTarget"]),
              "positive control: the applier froze the fixture cfg file");
        Check(FileBackups().Count == 1,
              "positive control: the applier recorded the file backup", $"(count={FileBackups().Count})");

        ConfigSyncApplier.RestoreLocalSettings();
        Check(ProbeTargetSettings.Value == "own-target-value",
              "positive control: the session restore put the fixture's own value back",
              $"(value={ProbeTargetSettings.Value})");
        Check(!IsReadOnly(CfgPaths["ProbeTarget"]),
              "positive control: the restore lifted the read-only flag");
        Check(FileBackups().Count == 0 && RestoreSnapshotField().Count == 0,
              "positive control: the session state is clean again");

        // The real test: entry 0 is a valid write, entry 1 declares 1e9 bytes.
        MainFile.Log.Lines.Clear();
        byte[] packet = TwoEntryHostilePacket();
        var message = new ConfigSyncMessage();
        Exception? error = TryProductionDeserialize(packet, message, out long threadDelta, out _);

        Check(error is InvalidDataException,
              "the two-entry packet is rejected", $"(ex={Describe(error)})");
        Check(error != null && error.Message.Contains("entry 1", StringComparison.Ordinal),
              "the rejection names the SECOND entry, not the valid first one", $"(message={error?.Message})");
        Check(threadDelta < BoundedAllocation,
              "the rejection allocated under 100000 bytes", $"(threadDelta={threadDelta})");

        Check(message.ModIds.Count == 0 && message.PropertyNames.Count == 0 && message.Values.Count == 0,
              "nothing from the partially decoded packet survived on the message");
        Check(ProbeTargetSettings.Value == "own-target-value",
              "the fixture config still holds its own value (no half commit)",
              $"(value={ProbeTargetSettings.Value})");
        Check(FileBackups().Count == 0,
              "ConfigSyncApplier.FileBackups is empty", $"(count={FileBackups().Count})");
        Check(RestoreSnapshotField().Count == 0,
              "ConfigSyncApplier.RestoreSnapshot is empty", $"(count={RestoreSnapshotField().Count})");
        Check(!CfgPaths.Values.Any(IsReadOnly),
              "no fixture cfg file gained the read-only attribute");
        Check(!LogContains("outcome="),
              "the applier never ran for the rejected packet (no outcome line logged)");
    }

    // ---------- 7. sender-side filter, end to end ----------

    private static void RunSenderFilter()
    {
        Section("7. sender-side filter, end to end");

        Registry().Clear();
        CfgPaths.Clear();
        Register("ProbeHealthy", typeof(ProbeHealthySettings), CfgPaths);
        Register("ProbeOversized", typeof(ProbeOversizedSettings), CfgPaths);

        string healthyValue = "healthy-value";
        string oversizedValue = new string('x', ConfigSyncMessage.MaxStringLength + 44);
        ProbeHealthySettings.Value = healthyValue;
        ProbeOversizedSettings.Value = oversizedValue;
        Check(oversizedValue.Length > ConfigSyncMessage.MaxStringLength
              && !ConfigSyncMessage.FitsWireLimit(oversizedValue, out _),
              "fixture: the oversized value exceeds the wire cap",
              $"(chars={oversizedValue.Length} bytes={Encoding.UTF8.GetByteCount(oversizedValue)})");

        MainFile.Log.Lines.Clear();
        ConfigSyncMessage built = SnapshotBuilder.Build(out int oversizedSkipped);

        Check(oversizedSkipped == 1,
              "Build reports exactly one oversized entry", $"(oversizedSkipped={oversizedSkipped})");
        Check(!built.ModIds.Contains("ProbeOversized"),
              "the oversized entry is absent from the snapshot");
        Check(built.ModIds.Contains("ProbeHealthy"),
              "the healthy entry IS present in the same snapshot", $"(entries={built.ModIds.Count})");
        int healthyIndex = built.ModIds.IndexOf("ProbeHealthy");
        Check(healthyIndex >= 0 && At(built.Values, healthyIndex) == healthyValue,
              "the healthy entry carries its full value");
        Check(MainFile.Log.Lines.Exists(l => l.StartsWith("ERROR", StringComparison.Ordinal)
                                             && l.Contains("ProbeOversized.Value", StringComparison.Ordinal)),
              "the Error log names modId.propertyName",
              $"(log={MainFile.Log.Lines.Find(l => l.Contains("ProbeOversized", StringComparison.Ordinal))})");
        Check(LogContains("not truncated"),
              "the log states that the key keeps each end's own value and is not truncated");
        Check(ProbeOversizedSettings.Value.Length == oversizedValue.Length
              && ProbeOversizedSettings.Value == oversizedValue,
              "the oversized fixture value was neither truncated nor mutated",
              $"(length={ProbeOversizedSettings.Value.Length})");

        // The host push must hand exactly the healthy entry to the transport.
        MainFile.Log.Lines.Clear();
        var host = new FakeNetService { Type = NetGameType.Host, IsConnected = true };
        LobbySnapshotPush.PushIfHost(host, "probe");

        Check(host.Sent.Count == 1,
              "the host push sent exactly one message", $"(sent={host.Sent.Count})");
        ConfigSyncMessage? pushed = host.Sent.Count == 1
            ? (host.Sent[0] as CustomMessageWrapper)?.Message as ConfigSyncMessage
            : null;
        Check(pushed != null, "the sent wrapper carries a ConfigSyncMessage");
        if (pushed != null)
        {
            Check(pushed.ModIds.Count == 1 && pushed.ModIds[0] == "ProbeHealthy",
                  "the sent message contains the healthy entry only",
                  $"(entries={pushed.ModIds.Count})");
            Check(!pushed.ModIds.Contains("ProbeOversized"),
                  "the sent message does NOT contain the oversized entry");
            Check(pushed.Values.Count == 1 && pushed.Values[0] == healthyValue,
                  "the sent value is the full healthy value");
        }
        Check(LogContains("1 skipped as oversized"),
              "the push log line reports the skipped count",
              $"(log={MainFile.Log.Lines.Find(l => l.Contains("pushed", StringComparison.Ordinal))})");

        // Defense in depth: a hand-built oversized message can never reach the wire.
        var handBuilt = new ConfigSyncMessage();
        Add(handBuilt, "ProbeOversized", "Value", oversizedValue);
        var writer = NewWriter();
        Exception? serializeError = null;
        try
        {
            handBuilt.Serialize(writer);
        }
        catch (Exception e)
        {
            serializeError = e;
        }
        Check(serializeError is InvalidOperationException,
              "Serialize refuses a hand-built oversized message", $"(ex={Describe(serializeError)})");
        Check(serializeError != null && serializeError.Message.Contains("ProbeOversized.Value", StringComparison.Ordinal),
              "the refusal names the key", $"(message={serializeError?.Message})");
        Check(writer.BitPosition == 0,
              "nothing was written before the refusal", $"(bitPosition={writer.BitPosition})");
    }

    // ---------- engine-layout helpers ----------

    /// <summary>Fresh writer with the engine's grow-warning disabled: the warning
    /// path reaches the engine's Logger, which this probe must never touch.</summary>
    private static PacketWriter NewWriter() => new() { WarnOnGrow = false };

    /// <summary>
    /// The byte stream the engine's NetMessageBus hands to Deserialize:
    /// [message id byte][sender ulong][message payload]. See the class comment for
    /// why the id byte is a constant here.
    /// </summary>
    private static byte[] BuildEnginePacket(ConfigSyncMessage message, ulong senderId)
    {
        var writer = NewWriter();
        writer.WriteByte(MessageIdByte);
        writer.WriteULong(senderId);
        message.Serialize(writer);
        return Finish(writer);
    }

    /// <summary>Consumes the id/sender prefix the way TryDeserializeMessage does,
    /// then calls the production Deserialize.</summary>
    private static ConfigSyncMessage ReadEnginePacket(byte[] packet)
    {
        var reader = new PacketReader();
        reader.Reset(packet);
        reader.ReadByte();
        reader.ReadULong();
        var message = new ConfigSyncMessage();
        message.Deserialize(reader);
        return message;
    }

    /// <summary>
    /// The transport sends exactly BytePosition bytes, so trim the writer's
    /// growable buffer the same way before a reader sees it: leaving the trailing
    /// zeros in would let them satisfy the truncation pre-flight.
    /// </summary>
    private static byte[] Finish(PacketWriter writer)
    {
        byte[] full = writer.Buffer;
        int length = writer.BytePosition;
        var packet = new byte[length];
        Array.Copy(full, packet, length);
        return packet;
    }

    /// <summary>Wire bytes of one entry: three 4-byte length prefixes plus the
    /// UTF-8 payloads, i.e. exactly what PacketWriter.WriteString emits.</summary>
    private static int EntryWireBytes(string modId, string propertyName, string value) =>
        (4 + Encoding.UTF8.GetByteCount(modId))
        + (4 + Encoding.UTF8.GetByteCount(propertyName))
        + (4 + Encoding.UTF8.GetByteCount(value));

    /// <summary>[id][sender][count][declared string length][bodyBytes zeros].</summary>
    private static byte[] HostilePacket(int count, int declaredLength, int bodyBytes)
    {
        var writer = NewWriter();
        writer.WriteByte(MessageIdByte);
        writer.WriteULong(SenderId);
        writer.WriteInt(count, 32);
        writer.WriteInt(declaredLength, 32);
        for (int i = 0; i < bodyBytes; i++)
        {
            writer.WriteByte(0);
        }
        return Finish(writer);
    }

    /// <summary>[id][sender][count] and nothing else.</summary>
    private static byte[] CountPacket(int count)
    {
        var writer = NewWriter();
        writer.WriteByte(MessageIdByte);
        writer.WriteULong(SenderId);
        writer.WriteInt(count, 32);
        return Finish(writer);
    }

    /// <summary>
    /// A structurally valid one-entry packet carrying the value's REAL UTF-8 bytes
    /// through the engine's own WriteString, so the declared length is the true
    /// encoded size rather than a hand-written number.
    /// </summary>
    private static byte[] RawValuePacket(string modId, string propertyName, string value)
    {
        var writer = NewWriter();
        writer.WriteByte(MessageIdByte);
        writer.WriteULong(SenderId);
        writer.WriteInt(1, 32);
        writer.WriteString(modId);
        writer.WriteString(propertyName);
        writer.WriteString(value);
        return Finish(writer);
    }

    /// <summary>
    /// Entry 0 is a valid write for ProbeTarget.Value; entry 1 declares 1e9 bytes
    /// for its modId, which no receiver can carry.
    /// </summary>
    private static byte[] TwoEntryHostilePacket()
    {
        var writer = NewWriter();
        writer.WriteByte(MessageIdByte);
        writer.WriteULong(SenderId);
        writer.WriteInt(2, 32);
        writer.WriteString("ProbeTarget");
        writer.WriteString("Value");
        writer.WriteString("99");
        writer.WriteInt(HostileDeclaredLength, 32);
        return Finish(writer);
    }

    // ---------- measurement helpers ----------

    private readonly record struct EngineOutcome(string Outcome, long ThreadDelta, long TotalDelta);

    /// <summary>
    /// The pre-fix mod's exact call sequence: read the entry count, then call the
    /// ENGINE's ReadString on the declared length. Measured around ReadString only.
    /// </summary>
    private static EngineOutcome EngineReadString(byte[] packet)
    {
        var reader = new PacketReader();
        reader.Reset(packet);
        reader.ReadByte();
        reader.ReadULong();
        reader.ReadInt(32);

        long threadBefore = GC.GetAllocatedBytesForCurrentThread();
        long totalBefore = GC.GetTotalAllocatedBytes(precise: true);
        string outcome;
        try
        {
            string s = reader.ReadString();
            outcome = $"returned a string of {s.Length} chars";
        }
        catch (Exception e)
        {
            outcome = $"{e.GetType().Name}: {e.Message}";
        }
        long threadAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAfter = GC.GetTotalAllocatedBytes(precise: true);

        return new EngineOutcome(outcome, threadAfter - threadBefore, totalAfter - totalBefore);
    }

    /// <summary>
    /// Consumes the id/sender prefix, then measures ONLY the production
    /// Deserialize call. Returns the exception it threw, or null on success.
    /// </summary>
    private static Exception? TryProductionDeserialize(
        byte[] packet, ConfigSyncMessage target, out long threadDelta, out long totalDelta)
    {
        var reader = new PacketReader();
        reader.Reset(packet);
        reader.ReadByte();
        reader.ReadULong();

        long threadBefore = GC.GetAllocatedBytesForCurrentThread();
        long totalBefore = GC.GetTotalAllocatedBytes(precise: true);
        Exception? error = null;
        try
        {
            target.Deserialize(reader);
        }
        catch (Exception e)
        {
            error = e;
        }
        threadDelta = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        totalDelta = GC.GetTotalAllocatedBytes(precise: true) - totalBefore;
        return error;
    }

    /// <summary>Round-trips one entry through the production Serialize/Deserialize.</summary>
    private static bool RoundTrip(string modId, string propertyName, string value, out string got)
    {
        var sent = new ConfigSyncMessage();
        Add(sent, modId, propertyName, value);
        try
        {
            ConfigSyncMessage received = ReadEnginePacket(BuildEnginePacket(sent, SenderId));
            got = At(received.Values, 0);
            return received.ModIds.Count == 1
                   && received.ModIds[0] == modId
                   && received.PropertyNames.Count == 1
                   && received.PropertyNames[0] == propertyName
                   && got == value;
        }
        catch (Exception e)
        {
            got = $"<{e.GetType().Name}: {e.Message}>";
            return false;
        }
    }

    // ---------- fixture helpers ----------

    private static void Add(ConfigSyncMessage message, string modId, string propertyName, string value)
    {
        message.ModIds.Add(modId);
        message.PropertyNames.Add(propertyName);
        message.Values.Add(value);
    }

    /// <summary>ASCII-only source: the CJK character comes from its escape.
    /// U+914D is a BMP char, so it is 3 UTF-8 bytes.</summary>
    private static string Cjk(int chars) => new string('\u914d', chars);

    /// <summary>ASCII-only source: U+1F600 is one 4-byte UTF-8 char and one
    /// UTF-16 surrogate pair, i.e. 2 chars.</summary>
    private static string Emoji(int count) => string.Concat(Enumerable.Repeat("\U0001F600", count));

    /// <summary>Bounds-safe read: a failed decode must report a FAIL, not crash
    /// the probe before the remaining scenarios run.</summary>
    private static string At(List<string> list, int index) =>
        index >= 0 && index < list.Count ? list[index] : "<missing>";

    /// <summary>
    /// Registers a fixture mod config the way sync-probe-v3 does: bypass the
    /// ModConfig constructor (it calls OS.GetUserDataDir -> Godot), redirect _path
    /// to the probe's temp dir, install the lock and the property list, then write
    /// the instance into ModConfigRegistry's own dictionary so the production
    /// scanner and applier find it.
    /// </summary>
    private static ModConfig Register(string modId, Type configType, Dictionary<string, string> paths)
    {
        string cfgPath = Path.Combine(_dir, modId + ".cfg");
        paths[modId] = cfgPath;
        var config = (ModConfig)RuntimeHelpers.GetUninitializedObject(configType);
        Set(config, "_path", cfgPath);
        Set(config, "_modConfigName", configType.Name);
        Set(config, "_configFileLock", new SemaphoreSlim(1, 1));
        Set(config, "ConfigProperties", new List<PropertyInfo> { configType.GetProperty("Value")! });
        config.ModId = modId;
        Registry()[modId] = config;
        return config;
    }

    /// <summary>Genuine-host transport identity, the same route sync-probe-v3 uses.</summary>
    private static void InstallGenuineHost(ulong hostId)
    {
        var service = (NetClientGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetClientGameService));
        Set(service, "<NetClient>k__BackingField", new FakeNetClient(hostId));
        Set(service, "<IsConnected>k__BackingField", true);
        MpNetSession.CurrentService = service;
    }

    private static IDictionary Registry() =>
        (IDictionary)typeof(ModConfigRegistry).GetField("ModConfigs", Any)!.GetValue(null)!;

    private static IDictionary FileBackups() =>
        (IDictionary)typeof(ConfigSyncApplier).GetField("FileBackups", Any)!.GetValue(null)!;

    private static IDictionary RestoreSnapshotField() =>
        (IDictionary)typeof(ConfigSyncApplier).GetField("RestoreSnapshot", Any)!.GetValue(null)!;

    private static void Set(object target, string name, object value)
    {
        for (Type? t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo? field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }
        }
        throw new MissingFieldException(name);
    }

    private static bool IsReadOnly(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    private static bool LogContains(string fragment) =>
        MainFile.Log.Lines.Exists(l => l.Contains(fragment, StringComparison.Ordinal));

    private static bool Equal(List<string> a, List<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);

    private static string Describe(Exception? error) =>
        error == null ? "OK (no exception)" : $"{error.GetType().Name}: {error.Message}";
}

/// <summary>Fixture for scenario 6: the FIRST entry of the hostile packet is a
/// valid write for this property, so a half commit would be visible here.</summary>
internal sealed class ProbeTargetSettings : ModConfig
{
    public static string Value { get; set; } = "own-target-value";

    public override void SetupConfigUI(Godot.Control optionContainer) { }
}

/// <summary>Fixture for scenario 7: the entry that must survive the filter.</summary>
internal sealed class ProbeHealthySettings : ModConfig
{
    public static string Value { get; set; } = string.Empty;

    public override void SetupConfigUI(Godot.Control optionContainer) { }
}

/// <summary>Fixture for scenario 7: the entry whose 300-char value cannot be
/// carried, and which must therefore be dropped, counted and logged.</summary>
internal sealed class ProbeOversizedSettings : ModConfig
{
    public static string Value { get; set; } = string.Empty;

    public override void SetupConfigUI(Godot.Control optionContainer) { }
}
