using System;
using System.Collections.Generic;
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
        ApplyPatches();

        Log.Info($"{ModId} initialized (host pushes config snapshot at run start; receiver restores user's own settings at run end - config files are never rewritten)");
    }

    /// <summary>
    /// Applies every [HarmonyPatch] class in this assembly individually, each
    /// with its own processor and its own try/catch.
    ///
    /// Why not <c>Harmony.PatchAll(assembly)</c> (which is what this method used
    /// to call, contradicting its own comment): PatchAll walks the assembly in
    /// one pass, so an exception raised while processing any one patch class
    /// aborts the remaining classes. With two patch classes that means a single
    /// bad target silently disables the host push OR the run-end restore and the
    /// mod looks "loaded but inert". Here a failure is contained to the class
    /// that caused it, every class's outcome is logged, and the summary line
    /// makes a partial failure visible.
    /// </summary>
    private static void ApplyPatches()
    {
        var harmony = new HarmonyLib.Harmony(ModId);
        Assembly assembly = Assembly.GetExecutingAssembly();
        int patchedMethods = 0;
        int failedClasses = 0;

        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition)
            {
                continue;
            }
            if (!HasHarmonyPatch(type))
            {
                continue;
            }
            try
            {
                // CreateClassProcessor + Patch() is exactly what PatchAll does
                // per type internally, so the resulting patches are identical -
                // only the error containment differs.
                List<MethodInfo> patched = harmony.CreateClassProcessor(type).Patch();
                if (patched.Count > 0)
                {
                    patchedMethods += patched.Count;
                    Log.Info($"patched {patched.Count} method(s) via {type.FullName}");
                }
            }
            catch (Exception e)
            {
                failedClasses++;
                Log.Error($"Harmony patch class {type.FullName} failed (continuing with the remaining classes): {e}");
            }
        }

        Log.Info($"Harmony: {patchedMethods} method(s) patched across {assembly.GetTypes().Length} type(s), {failedClasses} patch class(es) failed");
    }

    /// <summary>
    /// True when the type (or one of its declared methods) carries
    /// [HarmonyPatch]. Checked explicitly so a type without any patch attribute
    /// is skipped instead of relying on CreateClassProcessor tolerating it.
    /// </summary>
    private static bool HasHarmonyPatch(Type type)
    {
        if (type.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true).Length > 0)
        {
            return true;
        }
        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true).Length > 0)
            {
                return true;
            }
        }
        return false;
    }
}