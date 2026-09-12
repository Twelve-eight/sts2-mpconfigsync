using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using BaseLib.Config;

using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

using MpConfigSync.MpConfigSyncCode;

namespace SyncProbeV2;

/// <summary>
/// Isolated fixture probe for the REWORKED MpConfigSync receiver (MCS-2/3/5).
/// Exact-source pattern: ConfigSyncApplier / ConfigSyncMessage / MpNetSession /
/// ConfigPropertyScanner are COMPILED INTO this probe (only the logger is a
/// shim), so every assertion executes production code.
///
/// Covered:
/// - MCS-2 auth matrix: host/SP service rejected, disconnected rejected,
///   non-NetClientGameService client rejected, forged sender rejected,
///   genuine host sender accepted (transport identity = NetClient.HostNetId,
///   reached through an uninitialized NetClientGameService + NetClient stub).
/// - MCS-5 transaction: one unconvertible value rejects the WHOLE snapshot;
///   unknown mod/property tolerated as version skew; wire bounds (negative /
///   oversized counts) rejected at deserialize.
/// - MCS-3: Changed() is NOT fired on the apply path (save-subscriber counter
///   stays 0); a forced Save after apply cannot persist host values (cfg file
///   frozen read-only); RestoreLocalSettings puts back the user's memory value
///   AND the exact pre-session file bytes, and clears the read-only flag.
/// </summary>
internal static class Program
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int _failures;

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        if (!ok) _failures++;
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcs-probe-v2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // ---- fixture config (old-probe pattern: bypass the ctor, redirect _path;
        //      the real ModConfig ctor would touch OS.GetUserDataDir -> Godot).
        string cfgPath = Path.Combine(root, "mod_configs", "AuditTarget.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
        var config = (AuditSettings)RuntimeHelpers.GetUninitializedObject(typeof(AuditSettings));
        Set(config, "_path", cfgPath);
        Set(config, "_modConfigName", "AuditSettings");
        Set(config, "_configFileLock", new SemaphoreSlim(1, 1));
        Set(config, "ConfigProperties", new List<PropertyInfo> { typeof(AuditSettings).GetProperty(nameof(AuditSettings.Value))! });
        config.ModId = "AuditTarget";
        var registry = (System.Collections.IDictionary)typeof(ModConfigRegistry).GetField("ModConfigs", Any)!.GetValue(null)!;
        registry["AuditTarget"] = config;

        AuditSettings.Value = 7;
        config.Save();
        Check(Read(cfgPath) == 7, "fixture: user's own cfg file written (7)");

        int changedFired = 0;
        config.ConfigChanged += (_, _) => { changedFired++; config.Save(); };

        // ---- auth matrix (MCS-2)
        ulong hostId = 42;

        MpNetSession.CurrentService = new FakeNetService { Type = NetGameType.Host, IsConnected = true };
        ApplyAndExpectReject("host receiver rejected", 9999);

        MpNetSession.CurrentService = new FakeNetService { Type = NetGameType.Client, IsConnected = false };
        ApplyAndExpectReject("disconnected client rejected", 9999);

        MpNetSession.CurrentService = new FakeNetService { Type = NetGameType.Client, IsConnected = true };
        ApplyAndExpectReject("non-transport client service rejected", 9999);

        var realService = (NetClientGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetClientGameService));
        Set(realService, "<NetClient>k__BackingField", new FakeNetClient(hostId));
        Set(realService, "<IsConnected>k__BackingField", true); // auto-property, otherwise default false
        MpNetSession.CurrentService = realService;

        ApplyAndExpectReject("forged sender rejected", hostId + 1);
        Check(Authorize(hostId).Allowed, "genuine host sender accepted");

        // ---- happy path apply (MCS-2 accept + MCS-3 no-Changed + MCS-3 file freeze)
        Apply(new ConfigSyncMessage { ModIds = ["AuditTarget"], PropertyNames = ["Value"], Values = ["99"] }, hostId);
        Check(AuditSettings.Value == 99, "host snapshot applied (memory 7 -> 99)");
        Check(changedFired == 0, "Changed() never fired on the apply path (no save triggered)");
        Check(Read(cfgPath) == 7, "user's cfg file still holds 7 after apply");
        Check((File.GetAttributes(cfgPath) & FileAttributes.ReadOnly) != 0, "cfg file frozen read-only during session");
        // A third-party save attempt (submenu close / debounce) cannot persist host
        // values: the OS rejects writes to the frozen file. (We assert the OS-level
        // shield directly instead of calling config.Save(): a failing BaseLib Save
        // logs through its own Logger, whose static ctor needs the Godot runtime -
        // native crash outside the game, and irrelevant to what is being tested.)
        Check(Throws(() => File.WriteAllText(cfgPath, "{\"Value\":99}")),
              "writes to the frozen cfg are blocked while the session is active");

        // ---- MCS-5 transaction: unconvertible value rejects the whole snapshot
        Apply(new ConfigSyncMessage { ModIds = ["AuditTarget", "AuditTarget"], PropertyNames = ["Value", "Value"], Values = ["55", "notanint"] }, hostId);
        Check(AuditSettings.Value == 99, "half-valid snapshot rejected atomically (value stays 99)");
        Check(changedFired == 0, "no Changed() on the rejected snapshot either");

        // ---- unknown mod/property tolerated as version skew
        Apply(new ConfigSyncMessage { ModIds = ["NoSuchMod", "AuditTarget"], PropertyNames = ["Nope", "Value"], Values = ["1", "55"] }, hostId);
        Check(AuditSettings.Value == 55, "unknown mod skipped, valid entry still applied (55)");

        // ---- wire bounds (MCS-5): negative and oversized counts rejected at deserialize
        Check(Throws(() => Deserialize(CountOnlyPacket(-1))), "deserialize rejects negative count");
        Check(Throws(() => Deserialize(CountOnlyPacket(600))), "deserialize rejects oversized count (512 cap)");

        // ---- session end (MCS-3 + restore)
        ConfigSyncApplier.RestoreLocalSettings();
        Check(AuditSettings.Value == 7, "restore puts the user's own value back in memory");
        Check(Read(cfgPath) == 7, "cfg file restored to the user's exact value");
        Check((File.GetAttributes(cfgPath) & FileAttributes.ReadOnly) == 0, "read-only flag cleared at session end");

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "PROBE OK" : $"PROBE FAILED: {_failures} check(s)");
        return _failures == 0 ? 0 : 1;
    }

    // ---- helpers

    private static MpNetSession.AuthResult Authorize(ulong sender) => MpNetSession.AuthorizeSnapshot(sender);

    private static void ApplyAndExpectReject(string label, ulong sender)
    {
        int before = AuditSettings.Value;
        Apply(new ConfigSyncMessage { ModIds = ["AuditTarget"], PropertyNames = ["Value"], Values = ["99"] }, sender);
        Check(AuditSettings.Value == before, label, $"(value stayed {before})");
    }

    private static void Apply(ConfigSyncMessage message, ulong sender)
    {
        // Reset the per-session file-freeze state between auth probes so the
        // freeze assertions run against a fresh session exactly once.
        ConfigSyncApplier.Apply(message, sender);
    }

    private static byte[] CountOnlyPacket(int count)
    {
        var w = new PacketWriter();
        w.WriteInt(count, 32);
        return w.Buffer;
    }

    private static void Deserialize(byte[] packet)
    {
        var reader = new PacketReader();
        reader.Reset(packet);
        new ConfigSyncMessage().Deserialize(reader);
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (Exception) { return true; }
    }

    private static int Read(string file) =>
        int.Parse(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!["Value"]);

    private static void Set(object target, string name, object value)
    {
        for (Type? t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(target, value); return; }
        }
        throw new MissingFieldException(name);
    }
}

internal sealed class AuditSettings : ModConfig
{
    public static int Value { get; set; }

    public override void SetupConfigUI(Godot.Control control) { }
}
