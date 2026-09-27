using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// One host->peer config snapshot: a flat list of (modId, propertyName, invariant string).
/// Wire format: int count, then per entry three strings. Uses the same invariant-string
/// semantics as BaseLib's mod_configs/*.cfg files, so every type BaseLib supports
/// (bool/int/float/double/string/enum/Color) round-trips through the TypeDescriptor
/// converters on both ends.
///
/// DELIVERY (MCS-1, rewritten 2026-09-12): <see cref="ShouldBuffer"/> is now FALSE.
/// The primary send happens in the LOBBY (StartRunLobby.BeginRunForAllPlayers /
/// LoadRunLobby.TryBeginRunForAllPlayers prefixes) and on rejoin
/// (RunLobby.HandleClientRejoinRequestMessage prefix) - before the engine transmits
/// its begin-run / rejoin-response traffic on the same reliable ordered channel, so
/// the client applies the config BEFORE its own run initialisation reads it. The old
/// reasoning ("buffer to Launch, deliver mid- InitialiseShared would be worse")
/// confused "does not race the synchronizers" with "arrives before the first
/// consumer": buffered application at Launch is AFTER Populate/GenerateRooms/
/// OnRunCreated/Qurious prefix generation and can never satisfy MCS-1
/// (astra-advice 2026-09-12). The RunManager.InitializeShared postfix push remains
/// as a backstop re-assertion only.
///
/// Bounds (MCS-5; pre-allocation guarantee tightened R05-01/R05-04 2026-09-22):
/// the caps are enforced BEFORE any allocation the packet can drive. Deserialize
/// reads each declared string length and rejects a negative / over-cap /
/// truncated length first; only then does it read into a per-thread scratch
/// buffer capped at <see cref="MaxStringBytes"/>, so a hostile or corrupt length
/// can no longer make the engine's PacketReader allocate first (its ReadString
/// path allocated up to 2 GiB from the declared length, then failed on the read).
/// Entry-count and string rejections are <see cref="InvalidDataException"/>s.
/// On the sending side the snapshot builder FILTERS fields the caps cannot carry
/// and logs which key it dropped - it never truncates them, because truncation
/// would silently change the user's configured value. Apply still validates the
/// whole snapshot before committing any value.
///
/// Receiver-side metadata resolution (MCS-1, 2026-09-15): Apply resolves per-mod
/// property descriptors once per distinct mod per operation via an
/// operation-scoped map that is discarded with the call - cached metadata only,
/// never config values. Wire format, delivery semantics, and bounds here are
/// untouched by that change.
/// </summary>
public class ConfigSyncMessage : ICustomMessage
{
    /// <summary>Maximum entries per snapshot. Far above any real mod config surface.</summary>
    public const int MaxEntries = 512;

    /// <summary>Maximum characters per string field. Mod ids / property names / values are all short.</summary>
    public const int MaxStringLength = 256;

    /// <summary>
    /// Maximum UTF-8 bytes per string field. Invariant: a string of at most
    /// <see cref="MaxStringLength"/> (256) UTF-16 chars occupies at most 768 UTF-8
    /// bytes, because a BMP char encodes to at most 3 bytes and a surrogate pair
    /// costs 2 chars for 4 bytes (2 bytes per char). This byte cap therefore never
    /// rejects a string the char cap would accept; it is the value the receiver
    /// checks before touching any buffer. Both ends share these numbers - raising
    /// them would make an older peer (cap 256) reject whole packets.
    /// </summary>
    public const int MaxStringBytes = MaxStringLength * 3;

    /// <summary>
    /// True when <paramref name="value"/> fits BOTH wire caps: at most
    /// <see cref="MaxStringLength"/> UTF-16 chars and at most
    /// <see cref="MaxStringBytes"/> UTF-8 bytes. Null and empty always fit. The
    /// value is never truncated and never mutated.
    ///
    /// Policy (R05-04): the host-side builder drops an entry whose field does not
    /// fit and logs which key it dropped, so the user sees a visible, per-key
    /// divergence (each end keeps its own value) instead of a silently rewritten
    /// setting. A packet no receiver could parse is never put on the wire.
    /// </summary>
    /// <param name="value">Candidate field value; never mutated.</param>
    /// <param name="reason">Empty when the value fits; otherwise a short English sentence naming the actual char count, the actual UTF-8 byte count and both caps.</param>
    public static bool FitsWireLimit(string? value, out string reason)
    {
        if (string.IsNullOrEmpty(value))
        {
            reason = string.Empty;
            return true;
        }
        int charCount = value.Length;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (charCount <= MaxStringLength && byteCount <= MaxStringBytes)
        {
            reason = string.Empty;
            return true;
        }
        reason = $"{charCount} chars / {byteCount} UTF-8 bytes exceeds the {MaxStringLength}-char / {MaxStringBytes}-byte wire cap";
        return false;
    }

    public List<string> ModIds = new();
    public List<string> PropertyNames = new();
    public List<string> Values = new();

    /// <summary>
    /// Per-thread scratch buffer for <see cref="ReadBoundedString"/>. Reused across
    /// fields and packets so a decoded field allocates only the resulting string;
    /// every read is length-checked first, so the buffer can never be overrun.
    /// </summary>
    [ThreadStatic]
    private static byte[]? _stringBuffer;

    /// <summary>Host-originated, no relay; deliver on receipt in every phase.</summary>
    public bool ShouldBroadcast => false;

    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        int count = Math.Min(Math.Min(ModIds.Count, PropertyNames.Count), Values.Count);
        // R05-04 defense in depth: SnapshotBuilder filters oversized fields before a
        // message is built, so this guard must be unreachable for host-generated
        // snapshots. It exists so a hand-built message can never put a packet on the
        // wire that every receiver has to reject wholesale. Deliberately a throw,
        // not a truncation and not a silent skip: the entry count is already fixed
        // by this point, and dropping a field would corrupt the wire layout.
        for (int i = 0; i < count; i++)
        {
            string key = $"{ModIds[i]}.{PropertyNames[i]}";
            EnsureFitsWireLimit(ModIds[i], key, "modId");
            EnsureFitsWireLimit(PropertyNames[i], key, "propertyName");
            EnsureFitsWireLimit(Values[i], key, "value");
        }
        writer.WriteInt(count, 32);
        for (int i = 0; i < count; i++)
        {
            writer.WriteString(ModIds[i]);
            writer.WriteString(PropertyNames[i]);
            writer.WriteString(Values[i]);
        }
    }

    public void Deserialize(PacketReader reader)
    {
        int count = reader.ReadInt(32);
        // MCS-5: reject absurd counts outright instead of pre-sizing from
        // attacker-chosen values (the old code allocated List(count) with
        // count = int.MinValue / 10000 before any data check).
        if (count < 0 || count > MaxEntries)
        {
            throw new InvalidDataException($"config snapshot entry count {count} outside [0,{MaxEntries}]");
        }
        var modIds = new List<string>(count);
        var propertyNames = new List<string>(count);
        var values = new List<string>(count);
        // R05-01: every declared length is checked BEFORE any buffer is touched, so a
        // hostile length can no longer make the engine's PacketReader.ReadString
        // allocate first (length 1e9 -> new byte[2^30], then IndexOutOfRange) and
        // fail only afterwards.
        // Residual, engine-side: reader.ReadInt(32) itself throws
        // IndexOutOfRangeException when the packet ends inside a 4-byte length
        // prefix (BitSerializationUtil.GetBitsAtPosition indexes the packet buffer).
        // It allocates nothing and applies to every engine message type, so it is
        // left to the engine rather than shadowed here.
        for (int i = 0; i < count; i++)
        {
            string modId = ReadBoundedString(reader, $"entry {i} modId");
            string propertyName = ReadBoundedString(reader, $"entry {i} propertyName");
            string value = ReadBoundedString(reader, $"entry {i} value");
            modIds.Add(modId);
            propertyNames.Add(propertyName);
            values.Add(value);
        }
        ModIds = modIds;
        PropertyNames = propertyNames;
        Values = values;
    }

    /// <summary>
    /// Sender-side pre-flight for one field: throws when the field cannot be
    /// carried by the wire caps, naming the entry (<paramref name="key"/>) and the
    /// field. See <see cref="FitsWireLimit"/> for the no-truncation policy.
    /// </summary>
    private static void EnsureFitsWireLimit(string? value, string key, string fieldName)
    {
        if (!FitsWireLimit(value, out string reason))
        {
            throw new InvalidOperationException($"Config sync cannot serialize {key} {fieldName}: {reason}");
        }
    }

    /// <summary>
    /// Reads one length-prefixed string with every bound enforced BEFORE any
    /// allocation that the declared length could drive, mirroring the engine's wire
    /// layout exactly: int32 UTF-8 byte length, then that many bytes starting at the
    /// current bit offset (strings are not byte-aligned in general). Rejected:
    /// negative length, length above <see cref="MaxStringBytes"/>, a length that
    /// runs past the end of the packet buffer, and a decoded value above
    /// <see cref="MaxStringLength"/> chars. The first three reject before anything
    /// is allocated; the char-cap rejection runs after a bounded read, and every
    /// allocation on the way is capped by <see cref="MaxStringBytes"/> (at most 768
    /// bytes of scratch buffer, at most 768 chars of decoded string) - the declared
    /// length never sizes an allocation. The decode call matches the engine's
    /// (<c>Encoding.UTF8.GetString(buffer, 0, length)</c>, non-throwing) so valid
    /// data decodes byte-identically.
    /// </summary>
    private static string ReadBoundedString(PacketReader reader, string fieldLabel)
    {
        int length = reader.ReadInt(32);
        if (length < 0)
        {
            throw new InvalidDataException($"{fieldLabel}: declared string length {length} is negative");
        }
        if (length > MaxStringBytes)
        {
            throw new InvalidDataException($"{fieldLabel}: declared string length {length} bytes exceeds the {MaxStringBytes}-byte wire cap");
        }
        // Truncation pre-flight in long arithmetic: the highest byte index the
        // engine's ReadBytes would touch is (BitPosition + length*8 - 1) / 8, so
        // BitPosition + length*8 <= Buffer.Length*8 is the exact fit test. Widened
        // to long because Buffer.Length*8 overflows int for buffers > 256 MiB.
        if ((long)reader.BitPosition + (long)length * 8L > (long)reader.Buffer.Length * 8L)
        {
            throw new InvalidDataException($"{fieldLabel}: declared string length {length} bytes does not fit the {reader.Buffer.Length}-byte packet at bit {reader.BitPosition}");
        }
        byte[]? buffer = _stringBuffer;
        if (buffer == null)
        {
            buffer = new byte[MaxStringBytes];
            _stringBuffer = buffer;
        }
        else if (buffer.Length < length)
        {
            // Defensive only: the cached buffer is allocated at MaxStringBytes, which
            // is >= every length accepted above, so this branch cannot trigger for a
            // well-formed caller. It exists so a future change to the cache cannot
            // turn into a buffer overrun.
            buffer = new byte[length];
        }
        reader.ReadBytes(buffer, length);
        string decoded = Encoding.UTF8.GetString(buffer, 0, length);
        if (decoded.Length > MaxStringLength)
        {
            throw new InvalidDataException($"{fieldLabel}: {decoded.Length} chars exceeds the {MaxStringLength}-char wire cap ({length} UTF-8 bytes)");
        }
        return decoded;
    }

    public void HandleMessage(ulong senderId)
    {
        ConfigSyncApplier.Apply(this, senderId);
    }
}
