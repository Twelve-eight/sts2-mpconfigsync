using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using BaseLib.Config;

using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

using MpConfigSync;
using MpConfigSync.MpConfigSyncCode;

namespace SyncProbeV3;

/// <summary>
/// Isolated probe for the RECEIVER TRANSACTION of ConfigSyncApplier, with no
/// engine, no Godot and no game process.
///
/// Rewritten 2026-10-26 for the B01 contract (no cfg file I/O) plus the setter
/// rollback of the same review. The previous version asserted the WS-0916-04
/// file freeze (ReadOnly attribute + FileBackups), which no longer exists: it
/// reflected on a removed field and could not run. The production sources are
/// still COMPILED IN PLACE from ../../mod/MpConfigSyncCode (only MainFile.Log is
/// a shim), so this probe cannot drift from the shipped receiver.
///
/// Covered: own-value capture stays in memory (cfg bytes and attributes
/// untouched); a setter that rejects the host value is rolled back and leaves no
/// stale snapshot entry; a setter that rejects a LATER value keeps the earlier
/// packet's host value and retains the user's original; a read-only cfg file
/// neither blocks apply nor gets modified; a restore whose mod is unregistered
/// retains the entry and a later attempt still restores; an always-throwing
/// setter is the documented RETAINED fallback (nothing silently dropped).
/// </summary>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static int _failures;
    private static string _dir = string.Empty;
    private static readonly Dictionary<string, string> CfgPaths = new();

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}  {detail}");
        if (!ok) _failures++;
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcs-probe-v3-" + Guid.NewGuid().ToString("N")[..8]);
        _dir = Path.Combine(root, "mod_configs");
        Directory.CreateDirectory(_dir);

        // One type per mod: each fixture owns its own static property, so the
        // per-mod assertions cannot alias. _path is redirected so the real
        // ModConfig constructor (OS.GetUserDataDir -> Godot) is bypassed.
        var target = Register("AuditTarget", typeof(TargetSettings), CfgPaths);
        Register("AuditSecond", typeof(SecondSettings), CfgPaths);
        Register("AuditRejecting", typeof(RejectingSettings), CfgPaths);
        Register("AuditThrowing", typeof(ThrowingSettings), CfgPaths);

        TargetSettings.Value = 7;
        SecondSettings.Value = 11;
        RejectingSettings.Value = 13;
        target.Save();
        ModConfigRegistry.Get("AuditSecond")!.Save();
        ModConfigRegistry.Get("AuditRejecting")!.Save();

        byte[] targetBytes = File.ReadAllBytes(CfgPaths["AuditTarget"]);
        byte[] secondBytes = File.ReadAllBytes(CfgPaths["AuditSecond"]);
        Check(Read(CfgPaths["AuditTarget"]) == 7, "fixture: AuditTarget.cfg holds the user's own 7");
        Check(Read(CfgPaths["AuditSecond"]) == 11, "fixture: AuditSecond.cfg holds the user's own 11");

        // Genuine-host transport identity: uninitialized NetClientGameService + NetClient stub.
        ulong hostId = 42;
        var realService = (NetClientGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetClientGameService));
        Set(realService, "<NetClient>k__BackingField", new FakeNetClient(hostId));
        Set(realService, "<IsConnected>k__BackingField", true);
        MpNetSession.CurrentService = realService;
        Check(MpNetSession.AuthorizeSnapshot(hostId).Allowed, "fixture: genuine host sender authorized");

        RunCaptureIsMemoryOnly(hostId, targetBytes, secondBytes);
        RunRejectedValueIsRolledBack(hostId);
        RunLaterRejectionKeepsEarlierHostValue(hostId);
        RunReadOnlyCfgIsNotTouched(hostId, targetBytes);
        RunUnregisteredModRetention(hostId);
        RunAlwaysThrowingFallback(hostId);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "PROBE OK" : $"PROBE FAILED: {_failures} check(s)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- 1. the capture is memory-only ----------

    private static void RunCaptureIsMemoryOnly(ulong hostId, byte[] targetBytes, byte[] secondBytes)
    {
        Console.WriteLine();
        Console.WriteLine("== 1. own-value capture touches no cfg file ==");

        ResetSession();
        Apply(Packet(("AuditTarget", "Value", "99")), hostId);
        Check(TargetSettings.Value == 99, "packet 1 applied in memory (7 -> 99)");
        Check(OwnValue("AuditTarget", "Value") == "7", "the user's own 7 was captured in memory");
        Check(File.ReadAllBytes(CfgPaths["AuditTarget"]).AsSpan().SequenceEqual(targetBytes),
              "AuditTarget.cfg bytes are unchanged (no read-back, no write)");
        Check(!ReadOnly(CfgPaths["AuditTarget"]), "AuditTarget.cfg was not made read-only (no attribute change)");

        // Incremental: a mod introduced by a SECOND packet must be captured too.
        Apply(Packet(("AuditSecond", "Value", "55")), hostId);
        Check(SecondSettings.Value == 55, "packet 2 applied in memory (11 -> 55)");
        Check(OwnValue("AuditSecond", "Value") == "11", "packet 2 captured the second mod's own value");
        Check(File.ReadAllBytes(CfgPaths["AuditSecond"]).AsSpan().SequenceEqual(secondBytes),
              "AuditSecond.cfg bytes are unchanged as well");

        ConfigSyncApplier.RestoreLocalSettings();
        Check(TargetSettings.Value == 7 && SecondSettings.Value == 11,
              "CleanUp put both mods back to the user's own values", $"(target={TargetSettings.Value}, second={SecondSettings.Value})");
        Check(SnapshotCount() == 0, "the session snapshot is empty after a clean restore");
        Check(LogContains("Config sync restore outcome=OK"), "the restore reports outcome=OK");
    }

    // ---------- 2. a rejected host value is rolled back ----------

    private static void RunRejectedValueIsRolledBack(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 2. a setter that rejects the host value rolls the capture back ==");

        ResetSession();
        Apply(Packet(("AuditTarget", "Value", "321"), ("AuditRejecting", "Value", "-1")), hostId);

        Check(TargetSettings.Value == 321, "the healthy entry still applied (AuditTarget 321)");
        Check(RejectingSettings.Value == 13, "the rejecting property kept its own value (13)");
        Check(LogContains("outcome=PARTIAL"), "the packet is reported as outcome=PARTIAL");
        Check(LogContains("applied=1/2"), "the log states applied=1/2");
        Check(LogContains("failed=1"), "the log states failed=1");
        Check(LogContains("(1 rolled back)"), "the summary counts the rolled-back entry");
        Check(LogContains("no own-value snapshot retained"), "the rollback is named in the log");
        Check(OwnValue("AuditRejecting", "Value") == null,
              "the rejected entry left NO own-value snapshot behind (no stale capture)");

        ConfigSyncApplier.RestoreLocalSettings();
        Check(LogContains("Config sync restore outcome=OK"), "the next CleanUp reports outcome=OK, not a permanent PARTIAL");
        Check(TargetSettings.Value == 7, "the applied entry was restored to the user's own value");
        Check(SnapshotCount() == 0, "the session snapshot is empty again");
    }

    // ---------- 3. a later rejection keeps the earlier host value ----------

    private static void RunLaterRejectionKeepsEarlierHostValue(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 3. a rejection after an earlier host value keeps that value + the capture ==");

        ResetSession();
        Apply(Packet(("AuditRejecting", "Value", "5")), hostId);
        Check(RejectingSettings.Value == 5, "packet 1 applied the host value 5");
        Check(OwnValue("AuditRejecting", "Value") == "13", "the user's own 13 was captured");

        Apply(Packet(("AuditRejecting", "Value", "-2")), hostId);
        Check(RejectingSettings.Value == 5, "the rejected second packet left the earlier host value 5 in effect");
        Check(OwnValue("AuditRejecting", "Value") == "13", "the own-value capture is retained, not dropped");
        Check(LogContains("earlier host value stays in effect"), "the branch is named in the log");

        ConfigSyncApplier.RestoreLocalSettings();
        Check(RejectingSettings.Value == 13, "CleanUp restored the user's own 13");
        Check(SnapshotCount() == 0, "the session snapshot is empty again");
    }

    // ---------- 4. a read-only cfg file is irrelevant ----------

    private static void RunReadOnlyCfgIsNotTouched(ulong hostId, byte[] targetBytes)
    {
        Console.WriteLine();
        Console.WriteLine("== 4. a read-only cfg file neither blocks apply nor gets modified ==");

        ResetSession();
        string path = CfgPaths["AuditTarget"];
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            Apply(Packet(("AuditTarget", "Value", "42")), hostId);
            Check(TargetSettings.Value == 42, "the host value applied while the cfg was read-only");
            Check(ReadOnly(path), "the read-only attribute is still set (the applier never touches attributes)");
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(targetBytes), "the read-only cfg bytes are unchanged");
            Check(!LogContains("Access denied") && !LogContains("System.UnauthorizedAccessException"),
                  "no file-access failure was logged by the applier");

            ConfigSyncApplier.RestoreLocalSettings();
            Check(TargetSettings.Value == 7, "CleanUp restored the user's own value in memory");
            Check(ReadOnly(path), "the restore did not clear the user's read-only attribute");
            Check(LogContains("Config sync restore outcome=OK"), "the restore reports outcome=OK");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }
    }

    // ---------- 5. retention is reachable for a later attempt ----------

    private static void RunUnregisteredModRetention(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 5. an unrestorable entry is retained and a later attempt restores it ==");

        ResetSession();
        var targetConfig = ModConfigRegistry.Get("AuditTarget")!;
        Apply(Packet(("AuditTarget", "Value", "77")), hostId);
        Check(TargetSettings.Value == 77, "host value applied (77)");

        Registry().Remove("AuditTarget");
        ConfigSyncApplier.RestoreLocalSettings();
        Check(TargetSettings.Value == 77, "nothing was restored while the mod is unregistered");
        Check(LogContains("is not registered locally"), "the retention is reported at Error level");
        Check(LogContains("remaining=1 entry"), "the summary states the retained entry count");
        Check(OwnValue("AuditTarget", "Value") == "7", "the capture is still reachable in memory");

        Registry()["AuditTarget"] = targetConfig;
        ConfigSyncApplier.RestoreLocalSettings();
        Check(TargetSettings.Value == 7, "the retry restored the user's own value");
        Check(SnapshotCount() == 0, "the snapshot is empty after the successful retry");
        Check(LogContains("Config sync restore outcome=OK"), "the retry reports outcome=OK");
    }

    // ---------- 6. the always-throwing fallback keeps the only copy ----------

    private static void RunAlwaysThrowingFallback(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 6. an unrollbackable setter keeps the capture (documented fallback) ==");

        ResetSession();
        Apply(Packet(("AuditSecond", "Value", "66"), ("AuditThrowing", "Value", "1")), hostId);
        Check(SecondSettings.Value == 66, "the healthy entry still applied (AuditSecond 66)");
        Check(LogContains("rolling the local value back also failed"),
              "the failed rollback is named in the log");
        Check(LogContains("RETAINED in memory for the CleanUp restore"),
              "the capture is retained rather than silently dropped");
        Check(OwnValue("AuditThrowing", "Value") == "0", "the throwing entry keeps the user's own value in memory");

        ConfigSyncApplier.RestoreLocalSettings();
        Check(SecondSettings.Value == 11, "the healthy mod was restored (66 -> 11)");
        Check(SnapshotCount() == 1, "only the unrestorable entry remains", $"(count={SnapshotCount()})");
        Check(LogContains("Config sync restore outcome=PARTIAL"), "the restore reports PARTIAL, not OK");
        ResetSession();
    }

    // ---------- helpers ----------

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

    private static IDictionary Registry() =>
        (IDictionary)typeof(ModConfigRegistry).GetField("ModConfigs", Any)!.GetValue(null)!;

    private static IDictionary RestoreSnapshotField() =>
        (IDictionary)typeof(ConfigSyncApplier).GetField("RestoreSnapshot", Any)!.GetValue(null)!;

    /// <summary>Total captured entries across all mods.</summary>
    private static int SnapshotCount()
    {
        int count = 0;
        foreach (object props in RestoreSnapshotField().Values)
        {
            count += ((IDictionary)props).Count;
        }
        return count;
    }

    /// <summary>
    /// Captured value for one property, or null when no entry exists. The fixtures
    /// never capture a null value, so null unambiguously means "no entry".
    /// </summary>
    private static string? OwnValue(string modId, string propertyName)
    {
        IDictionary snapshots = RestoreSnapshotField();
        if (!snapshots.Contains(modId))
        {
            return null;
        }
        var props = (IDictionary)snapshots[modId]!;
        return props.Contains(propertyName) ? (string?)props[propertyName] : null;
    }

    /// <summary>Fresh scenario: no snapshot, no log noise, no residual read-only flag.</summary>
    private static void ResetSession()
    {
        RestoreSnapshotField().Clear();
        foreach (string path in CfgPaths.Values)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }
        MainFile.Log.Lines.Clear();
    }

    private static ConfigSyncMessage Packet(params (string ModId, string Prop, string Value)[] entries)
    {
        var message = new ConfigSyncMessage();
        foreach (var e in entries)
        {
            message.ModIds.Add(e.ModId);
            message.PropertyNames.Add(e.Prop);
            message.Values.Add(e.Value);
        }
        return message;
    }

    private static void Apply(ConfigSyncMessage message, ulong sender) =>
        ConfigSyncApplier.Apply(message, sender);

    private static bool LogContains(string fragment) =>
        MainFile.Log.Lines.Exists(l => l.Contains(fragment, StringComparison.Ordinal));

    private static bool ReadOnly(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    private static int Read(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        return int.Parse(doc.RootElement.GetProperty("Value").ToString());
    }

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

internal sealed class TargetSettings : ModConfig
{
    public static int Value { get; set; }
    public override void SetupConfigUI(Godot.Control control) { }
}

internal sealed class SecondSettings : ModConfig
{
    public static int Value { get; set; }
    public override void SetupConfigUI(Godot.Control control) { }
}

/// <summary>Setter rejects negative values: the realistic "setter failure" shape.</summary>
internal sealed class RejectingSettings : ModConfig
{
    private static int _value = 13;

    public static int Value
    {
        get => _value;
        set
        {
            if (value < 0) throw new InvalidOperationException("probe: this setter rejects negative values");
            _value = value;
        }
    }

    public override void SetupConfigUI(Godot.Control control) { }
}

/// <summary>Setter always throws: drives the documented RETAINED fallback.</summary>
internal sealed class ThrowingSettings : ModConfig
{
    public static int Value
    {
        get => 0;
        set => throw new InvalidOperationException("probe: this setter always throws");
    }

    public override void SetupConfigUI(Godot.Control control) { }
}
