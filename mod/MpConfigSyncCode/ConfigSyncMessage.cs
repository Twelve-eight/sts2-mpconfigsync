using System;
using System.Collections.Generic;

using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// One host->peer config snapshot: a flat list of (modId, propertyName, invariant string).
/// Wire format: int count, then per entry three strings. Uses the same invariant-string
/// semantics as BaseLib's mod_configs/*.cfg files, so every type BaseLib supports
/// (bool/int/float/double/string/enum/Color) round-trips through the TypeDescriptor
/// converters on both ends.
///
/// Sent by the host once per run session (RunManager.InitializeShared postfix) via
/// CustomMessageWrapper.Send. ShouldBroadcast=false: host-originated, no relay needed.
///
/// <para>
/// Buffering: <c>ShouldBuffer</c> is deliberately NOT overridden, so it keeps
/// <see cref="ICustomMessage"/>'s default of <c>true</c>. This is the intended
/// behaviour, not an oversight. The host sends the snapshot from the
/// RunManager.InitializeShared postfix, which runs after the run lobby has
/// already enabled buffering (<c>StartRunLobby.cs:498</c> /
/// <c>LoadRunLobby.cs:320</c>), and buffered messages are released by
/// <c>RunManager.Launch()</c> (<c>RunManager.cs:711-717</c>,
/// <c>SetBufferMessages(false)</c>). So the receiver applies the snapshot at
/// launch, never during <c>InitializeShared</c> itself - which is exactly what we
/// want, because it cannot race the synchronizers that <c>InitializeShared</c> is
/// still building. Overriding <c>ShouldBuffer</c> to <c>false</c> would deliver
/// the snapshot mid-initialisation instead and is strictly worse.
/// </para>
/// </summary>
public class ConfigSyncMessage : ICustomMessage
{
    public List<string> ModIds = new();
    public List<string> PropertyNames = new();
    public List<string> Values = new();

    public bool ShouldBroadcast => false;

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
        ModIds = new List<string>(count);
        PropertyNames = new List<string>(count);
        Values = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            ModIds.Add(reader.ReadString());
            PropertyNames.Add(reader.ReadString());
            Values.Add(reader.ReadString());
        }
    }

    public void HandleMessage(ulong senderId)
    {
        ConfigSyncApplier.Apply(this, senderId);
    }
}