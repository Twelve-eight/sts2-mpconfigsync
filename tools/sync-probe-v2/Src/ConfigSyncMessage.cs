using System;
using System.Collections.Generic;

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
/// Bounds (MCS-5): Deserialize enforces entry/string caps so a hostile or corrupt
/// packet cannot force huge allocations; Apply validates the whole snapshot before
/// committing any value.
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

    public List<string> ModIds = new();
    public List<string> PropertyNames = new();
    public List<string> Values = new();

    /// <summary>Host-originated, no relay; deliver on receipt in every phase.</summary>
    public bool ShouldBroadcast => false;

    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        int count = Math.Min(Math.Min(ModIds.Count, PropertyNames.Count), Values.Count);
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
            throw new IndexOutOfRangeException($"config snapshot entry count {count} outside [0,{MaxEntries}]");
        }
        var modIds = new List<string>(count);
        var propertyNames = new List<string>(count);
        var values = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string modId = reader.ReadString();
            string propertyName = reader.ReadString();
            string value = reader.ReadString();
            if (modId.Length > MaxStringLength || propertyName.Length > MaxStringLength || value.Length > MaxStringLength)
            {
                throw new IndexOutOfRangeException($"config snapshot string field exceeds {MaxStringLength} chars");
            }
            modIds.Add(modId);
            propertyNames.Add(propertyName);
            values.Add(value);
        }
        ModIds = modIds;
        PropertyNames = propertyNames;
        Values = values;
    }

    public void HandleMessage(ulong senderId)
    {
        ConfigSyncApplier.Apply(this, senderId);
    }
}
