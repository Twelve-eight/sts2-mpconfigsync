using System;
using System.Reflection;

using BaseLib.Config;
using BaseLib.Patches.Localization;

using Godot;

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "MpConfigSync"; // res://MpConfigSync and id prefix MPCONFIGSYNC-
    public const string ResPath = $"res://{ModId}";

    public static MegaCrit.Sts2.Core.Logging.Logger Log { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        // Settings -> Mod Settings page (BaseLib auto-UI): only the master toggle.
        ModConfigRegistry.Register(ModId, new MpConfigSyncConfig());
        // BaseLib SimpleLoc for settings labels.
        SimpleLoc.EnableSimpleLoc(ModId);
        // Register C# scripts referenced by scenes shipped in the .pck (none yet).
        Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(Assembly.GetExecutingAssembly());

        // Targeted Harmony patches, applied per-type with try/catch - never PatchAll
        // (one bad patch silently strips the rest). Two patch classes: host push at
        // run start, local-settings restore at run end.
        try
        {
            var harmony = new HarmonyLib.Harmony(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
        catch (Exception e)
        {
            Log.Error($"Failed to apply Harmony patches: {e}");
        }

        Log.Info($"{ModId} initialized (host pushes config snapshot at run start; receiver restores user's own settings at run end - config files are never rewritten)");
    }
}