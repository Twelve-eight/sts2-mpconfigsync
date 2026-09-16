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
/// Isolated probe for WS-0916-04: the freeze/apply/restore DECISION logic of
/// ConfigSyncApplier, exercised against temp cfg files with no engine, no Godot
/// and no game process.
///
/// The production sources are COMPILED IN PLACE from ../../mod/MpConfigSyncCode
/// (only MainFile.Log is a shim, because the real logger's static initializer
/// needs the Godot runtime), so this probe cannot drift from the shipped
/// receiver: it fails to build exactly when the receiver fails to build.
///
/// Covered:
/// 1. Incremental + idempotent freeze: a mod introduced by a SECOND packet in
///    the same session gets its own cfg frozen (the pre-fix code returned early
///    whenever FileBackups was already non-empty, leaving later mods writable).
/// 2. Freeze-failure policy (all-or-nothing): a mod whose cfg cannot be frozen
///    rejects the WHOLE packet before any SetValue, and the same packet
///    succeeds once the obstacle is gone.
/// 3. Setter-failure outcome: a throwing setter is reported as outcome=PARTIAL
///    with explicit applied/failed counts, never as a silent partial commit.
/// 4. Lossless restore: a failed restore RETAINS the backup entry (the session's
///    only copy of the user's bytes) and reports it as an error; a later restore
///    attempt still recovers the exact bytes.
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

        // ---- fixture configs: one type per mod so each has its OWN static
        //      property (a shared type would share one static field and make the
        //      per-mod assertions meaningless). _path is redirected so the real
        //      ModConfig constructor - which touches OS.GetUserDataDir -> Godot -
        //      is bypassed exactly like sync-probe-v2 does.
        var target = Register("AuditTarget", typeof(TargetSettings), CfgPaths);
        Register("AuditSecond", typeof(SecondSettings), CfgPaths);
        Register("AuditThird", typeof(ThirdSettings), CfgPaths);
        Register("AuditThrowing", typeof(ThrowingSettings), CfgPaths);

        // Write the user's own cfg files through BaseLib's own Save().
        TargetSettings.Value = 7;
        SecondSettings.Value = 11;
        ThirdSettings.Value = 13;
        target.Save();
        ModConfigRegistry.Get("AuditSecond")!.Save();
        ModConfigRegistry.Get("AuditThird")!.Save();

        byte[] targetBytes = File.ReadAllBytes(CfgPaths["AuditTarget"]);
        Check(Read(CfgPaths["AuditTarget"]) == 7, "fixture: AuditTarget.cfg holds the user's own 7");
        Check(Read(CfgPaths["AuditSecond"]) == 11, "fixture: AuditSecond.cfg holds the user's own 11");
        Check(Read(CfgPaths["AuditThird"]) == 13, "fixture: AuditThird.cfg holds the user's own 13");

        // ---- genuine-host transport identity (same route sync-probe-v2 uses:
        //      an uninitialized NetClientGameService + a NetClient stub).
        ulong hostId = 42;
        var realService = (NetClientGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetClientGameService));
        Set(realService, "<NetClient>k__BackingField", new FakeNetClient(hostId));
        Set(realService, "<IsConnected>k__BackingField", true);
        MpNetSession.CurrentService = realService;
        Check(MpNetSession.AuthorizeSnapshot(hostId).Allowed, "fixture: genuine host sender authorized");

        ResetSession();
        RunIncrementalFreezeTest(hostId);
        RunFreezeFailureTest(hostId);
        RunSetterFailureTest(hostId);
        RunRestoreRetentionTest(hostId, targetBytes);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "PROBE OK" : $"PROBE FAILED: {_failures} check(s)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- 1. incremental + idempotent freeze ----------

    private static void RunIncrementalFreezeTest(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 1. incremental freeze across packets ==");

        Apply(Packet(("AuditTarget", "Value", "99")), hostId);
        Check(TargetSettings.Value == 99, "packet 1 applied to AuditTarget (7 -> 99)");
        Check(ReadOnly(CfgPaths["AuditTarget"]), "packet 1 froze AuditTarget.cfg");
        Check(Read(CfgPaths["AuditTarget"]) == 7, "AuditTarget.cfg still holds the user's 7 on disk");
        Check(!ReadOnly(CfgPaths["AuditSecond"]), "AuditSecond.cfg is NOT frozen yet (nothing named it)");

        // The defect: a later packet naming a NEW mod must freeze that mod too.
        Apply(Packet(("AuditSecond", "Value", "55")), hostId);
        Check(SecondSettings.Value == 55, "packet 2 applied to AuditSecond (11 -> 55)");
        Check(ReadOnly(CfgPaths["AuditSecond"]),
              "packet 2 froze AuditSecond.cfg (incremental, not skipped by packet 1's freeze)");
        Check(Throws(() => File.WriteAllText(CfgPaths["AuditSecond"], "{\"Value\":\"55\"}")),
              "writes to the newly frozen AuditSecond.cfg are blocked");
        Check(Read(CfgPaths["AuditSecond"]) == 11, "AuditSecond.cfg still holds the user's 11 on disk");

        // Idempotence: re-naming an already frozen mod keeps the ORIGINAL bytes.
        Apply(Packet(("AuditTarget", "Value", "98")), hostId);
        Check(TargetSettings.Value == 98, "packet 3 re-applied to the already frozen AuditTarget");
        Check(Read(CfgPaths["AuditTarget"]) == 7, "re-freeze is idempotent: backup bytes stay the original 7");
    }

    // ---------- 2. freeze failure is all-or-nothing ----------

    private static void RunFreezeFailureTest(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 2. freeze failure rejects the whole packet ==");

        ResetSession();
        int beforeTarget = TargetSettings.Value;
        int beforeThird = ThirdSettings.Value;

        // Hold the file open exclusively: File.Exists still reports true (so the
        // freeze is attempted, not skipped as "no file"), while ReadAllBytes
        // throws IOException exactly like a permission/AV/lock failure would.
        using (LockExclusive(CfgPaths["AuditThird"]))
        {
            Apply(Packet(("AuditTarget", "Value", "123"), ("AuditThird", "Value", "456")), hostId);
        }

        Check(TargetSettings.Value == beforeTarget,
              "no value committed for the protected mod (all-or-nothing)", $"(stayed {beforeTarget})");
        Check(ThirdSettings.Value == beforeThird,
              "no value committed for the unprotected mod either", $"(stayed {beforeThird})");
        Check(LogContains("freeze FAILED"), "freeze failure is reported at Error level");
        Check(LogContains("outcome=REJECTED"), "final state is reported as outcome=REJECTED");
        Check(!FileBackups().Contains(CfgPaths["AuditThird"]),
              "a file that failed to freeze is never recorded as a backup");

        // Recovery: once the obstacle is gone the same packet applies.
        Apply(Packet(("AuditTarget", "Value", "123"), ("AuditThird", "Value", "456")), hostId);
        Check(TargetSettings.Value == 123, "same packet applies once the freeze can succeed (AuditTarget 123)");
        Check(ThirdSettings.Value == 456, "same packet applies once the freeze can succeed (AuditThird 456)");
        Check(ReadOnly(CfgPaths["AuditThird"]), "AuditThird.cfg is frozen after the successful retry");
    }

    // ---------- 3. setter failure has an explicit outcome ----------

    private static void RunSetterFailureTest(ulong hostId)
    {
        Console.WriteLine();
        Console.WriteLine("== 3. setter failure is reported as PARTIAL ==");

        ResetSession();
        Apply(Packet(("AuditTarget", "Value", "321"), ("AuditThrowing", "Value", "1")), hostId);

        Check(TargetSettings.Value == 321, "the healthy entry still applied (AuditTarget 321)");
        Check(LogContains("outcome=PARTIAL"), "final state is reported as outcome=PARTIAL");
        Check(LogContains("applied=1/2"), "the log states the exact applied count (applied=1/2)");
        Check(LogContains("failed=1"), "the log states the exact failed count (failed=1)");
        Check(LogContains("Config sync setter failed for AuditThrowing.Value"),
              "the failing entry is named in the log");
    }

    // ---------- 4. failed restore retains the recovery copy ----------

    private static void RunRestoreRetentionTest(ulong hostId, byte[] targetBytes)
    {
        Console.WriteLine();
        Console.WriteLine("== 4. failed restore retains the backup ==");

        ResetSession();
        int ownBefore = TargetSettings.Value;
        Apply(Packet(("AuditTarget", "Value", "777")), hostId);
        Check(TargetSettings.Value == 777, "host value applied in memory");
        Check(FileBackups().Contains(CfgPaths["AuditTarget"]), "the user's bytes are backed up for the session");

        // The attribute reset succeeds even while the file is held exclusively;
        // the byte write is what fails, which is exactly the "restore failed
        // after the file was unfrozen" case that used to lose the only copy.
        using (LockExclusive(CfgPaths["AuditTarget"]))
        {
            ConfigSyncApplier.RestoreLocalSettings();
        }

        Check(TargetSettings.Value == ownBefore, "in-memory restore still happens (777 -> own value)");
        Check(FileBackups().Contains(CfgPaths["AuditTarget"]),
              "backup RETAINED after a failed restore (pre-fix it was cleared unconditionally)");
        Check(LogContains("backup RETAINED"), "the retention is reported at Error level");
        Check(!ReadOnly(CfgPaths["AuditTarget"]),
              "the failed restore left the file unprotected (attribute reset already ran)");

        // The retained entry must not become a hole in the freeze: a later packet
        // naming that mod has to protect the file again instead of skipping it as
        // "already backed up", which would let a subscriber persist host values.
        Apply(Packet(("AuditTarget", "Value", "555")), hostId);
        Check(ReadOnly(CfgPaths["AuditTarget"]),
              "a later packet RE-FREEZES a mod whose restore failed (no protection hole)");
        Check(LogContains("re-frozen for this session"), "the re-freeze is logged");
        Check(FileBackups().Contains(CfgPaths["AuditTarget"]),
              "the retained backup is kept across the re-freeze");

        // A later attempt must still recover the exact pre-session bytes.
        ConfigSyncApplier.RestoreLocalSettings();
        Check(FileBackups().Count == 0, "the retained entry is dropped once the restore succeeds");
        Check(!ReadOnly(CfgPaths["AuditTarget"]), "the read-only flag is cleared on the successful restore");
        Check(File.ReadAllBytes(CfgPaths["AuditTarget"]).AsSpan().SequenceEqual(targetBytes),
              "the user's exact pre-session bytes are recovered by the later attempt");
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

    private static IDictionary FileBackups() =>
        (IDictionary)typeof(ConfigSyncApplier).GetField("FileBackups", Any)!.GetValue(null)!;

    private static IDictionary RestoreSnapshotField() =>
        (IDictionary)typeof(ConfigSyncApplier).GetField("RestoreSnapshot", Any)!.GetValue(null)!;

    /// <summary>Fresh session between scenarios: no backups, no value snapshot,
    /// and every fixture cfg unfrozen. The in-memory values are left as they are.</summary>
    private static void ResetSession()
    {
        FileBackups().Clear();
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

    private static FileStream LockExclusive(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// Reads the fixture's "Value" field. Tolerant on purpose: when a check
    /// fails, a simulated third-party write may have left different JSON behind,
    /// and the probe must report that as a FAIL instead of aborting the run.
    /// </summary>
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

internal sealed class ThirdSettings : ModConfig
{
    public static int Value { get; set; }
    public override void SetupConfigUI(Godot.Control control) { }
}

/// <summary>Registered so the probe can drive a mid-commit setter failure.</summary>
internal sealed class ThrowingSettings : ModConfig
{
    public static int Value
    {
        get => 0;
        set => throw new InvalidOperationException("probe: this setter always throws");
    }

    public override void SetupConfigUI(Godot.Control control) { }
}
