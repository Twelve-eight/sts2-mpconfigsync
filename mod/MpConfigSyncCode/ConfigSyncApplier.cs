using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;

using BaseLib.Config;

using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Receiver side: validate then atomically apply a host-pushed config snapshot.
///
/// AUTH (MCS-2, 2026-09-12): the old code applied ANY sender's message as "from
/// host" - the probe pushed sender 9999 with no session and rewrote a registered
/// setting. Now <see cref="MpNetSession.AuthorizeSnapshot"/> requires: an active
/// connected service, receiver role == Client, and senderId == the transport-level
/// host id (NetClientGameService.HostNetId, the same identity check the engine's
/// handshake uses). Hosts/SP/replay reject; a forged payload senderId cannot pass
/// because the check uses the transport peer id, not message contents.
///
/// TRANSACTION (MCS-5): the whole snapshot is resolved + converted FIRST. Any
/// resolved-but-unconvertible value rejects the entire snapshot - a half-applied
/// config must never start a run. Unknown mods/properties are tolerated version
/// skew (a mod the client lacks has no local consumers to desync), logged, and do
/// not block. SetValues happen only after full validation; a setter that throws
/// mid-commit is logged per-entry and the run start continues (config sync must
/// not crash the game), with the failure stated loudly.
///
/// NO FILE WRITES THROUGH ANY PATH (MCS-3): Changed() is no longer fired on the
/// apply path (it signals "user edited" and Qurious/BaseLib subscribers save on
/// it, on submenu close, and on debounce). Only ConfigReloaded() refreshes UI.
/// Defense in depth: on the first apply of a session the touched mods' cfg FILES
/// are snapshotted and marked read-only, so a third-party subscriber that still
/// saves (open settings page) cannot persist host values even by accident; at
/// session end (RunManager.CleanUp) the read-only flag is lifted and the user's
/// own bytes are restored verbatim. A mid-session crash therefore also leaves
/// the user's file untouched.
///
/// Session scope: per-entry in-memory snapshot of the user's own values, restored
/// at CleanUp. Each session starts from the user's own settings again.
/// </summary>
internal static class ConfigSyncApplier
{
    /// <summary>PropertyName per mod, captured BEFORE the first host push this session.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> RestoreSnapshot = new();

    /// <summary>cfg file path -> original bytes, captured before the first apply.</summary>
    private static readonly Dictionary<string, byte[]> FileBackups = new();

    private static bool _restoring;

    internal static bool HasSnapshot => RestoreSnapshot.Count > 0;

    internal static void Apply(ConfigSyncMessage message, ulong senderId)
    {
        if (!MpConfigSyncConfig.Enabled)
        {
            MainFile.Log.Info("Config sync disabled locally, ignoring host push");
            return;
        }

        // ---- MCS-2: transport-level authorization before anything else.
        MpNetSession.AuthResult auth = MpNetSession.AuthorizeSnapshot(senderId);
        if (!auth.Allowed)
        {
            MainFile.Log.Warn($"Config sync REJECTED {message.ModIds.Count} entries from sender {senderId}: {auth.Reason}");
            return;
        }

        int count = Math.Min(Math.Min(message.ModIds.Count, message.PropertyNames.Count), message.Values.Count);
        MainFile.Log.Info($"Config sync accepted from host {senderId}: validating {count} entries");

        // ---- MCS-1 (2026-09-15): one property-map resolution per distinct mod
        // for this whole packet. Created here - AFTER authorization, so the
        // auth check still precedes any scanning - filled on first reference
        // per mod by ConfigPropertyScanner.Resolve (same BaseLib eligibility
        // scan, unchanged), and discarded when this method returns: nothing is
        // cached across packets, so an absent / later-registered / replaced
        // config is re-resolved fresh on the next one. See OperationPropertyMap.
        var propertyMaps = new OperationPropertyMap();

        // ---- MCS-5 phase 1: validate everything, change nothing.
        var pending = new List<(PropertyInfo Prop, object Value, string ModId, string Name)>();
        int unknownSkipped = 0;
        for (int i = 0; i < count; i++)
        {
            string modId = message.ModIds[i];
            string propertyName = message.PropertyNames[i];
            string raw = message.Values[i];
            try
            {
                Dictionary<string, PropertyInfo>? props = propertyMaps.Get(modId);
                if (props == null)
                {
                    unknownSkipped++;
                    MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: mod not present locally (version skew)");
                    continue;
                }
                if (!props.TryGetValue(propertyName, out PropertyInfo? prop))
                {
                    unknownSkipped++;
                    MainFile.Log.Debug($"Sync skip {modId}.{propertyName}: property not found (version skew)");
                    continue;
                }
                object? converted = TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(raw);
                if (converted == null)
                {
                    // A present property with an unconvertible value would leave
                    // this client's determinism different from the host's: reject
                    // the WHOLE snapshot instead of half-applying.
                    MainFile.Log.Error($"Config sync REJECTED: {modId}.{propertyName} value '{raw}' is not a valid {prop.PropertyType.Name}");
                    return;
                }
                pending.Add((prop, converted, modId, propertyName));
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Config sync REJECTED: validation of {modId}.{propertyName} failed: {e.Message}");
                return;
            }
        }

        // ---- MCS-3: file protection must exist before any value changes.
        ProtectConfigFiles(pending);

        // ---- MCS-5 phase 2: commit.
        int applied = 0;
        var touched = new HashSet<string>();
        foreach (var entry in pending)
        {
            try
            {
                SnapshotOwnValue(entry.Prop, entry.ModId, entry.Name);
                entry.Prop.SetValue(null, entry.Value);
                applied++;
                touched.Add(entry.ModId);
            }
            catch (Exception e)
            {
                // A throwing setter is a mod bug, not a protocol error; the rest
                // of the snapshot still applies (its remaining values are valid),
                // and the failure is on the record.
                MainFile.Log.Error($"Config sync setter failed for {entry.ModId}.{entry.Name}: {e.Message}");
            }
        }

        // ---- UI refresh only: ConfigReloaded re-reads values for display.
        // Changed() is deliberately NOT fired: it means "the user edited" and
        // triggers save pipelines that must never see host values as a save
        // trigger (MCS-3).
        foreach (string modId in touched)
        {
            ModConfig? config = ModConfigRegistry.Get(modId);
            if (config == null)
            {
                continue;
            }
            try
            {
                config.ConfigReloaded();
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Failed to refresh config UI for {modId}: {e.Message}");
            }
        }

        MainFile.Log.Info($"Config sync applied: {applied}/{count} entries "
                          + $"({unknownSkipped} unknown mod/property skipped, {count - applied - unknownSkipped} setter failures), "
                          + $"{touched.Count} mods touched (session-scoped; cfg files frozen until session end), "
                          + $"{propertyMaps.ResolvedModCount} distinct mods resolved this packet (MCS-1)");
    }

    /// <summary>Runs on RunManager.CleanUp: put the user's own values and files back.</summary>
    internal static void RestoreLocalSettings()
    {
        if (RestoreSnapshot.Count == 0 || _restoring)
        {
            return;
        }
        _restoring = true;
        try
        {
            // ---- MCS-1 (2026-09-15): one property-map resolution per distinct
            // mod for this whole restore pass instead of one per property. Same
            // lifecycle as Apply's map: created here, filled on first reference
            // per mod, garbage when CleanUp returns. Restore semantics are
            // unchanged - missing properties still skip per property, and a
            // failed resolution is not memoized, so each property keeps its own
            // skip decision exactly like the per-entry Resolve it replaced.
            var propertyMaps = new OperationPropertyMap();
            foreach ((string modId, Dictionary<string, string> props) in RestoreSnapshot)
            {
                ModConfig? config = ModConfigRegistry.Get(modId);
                if (config == null)
                {
                    continue;
                }
                foreach ((string name, string value) in props)
                {
                    try
                    {
                        PropertyInfo? prop = propertyMaps.Get(modId)?.GetValueOrDefault(name);
                        if (prop == null)
                        {
                            continue;
                        }
                        object? converted = TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(value);
                        if (converted != null)
                        {
                            prop.SetValue(null, converted);
                        }
                    }
                    catch (Exception e)
                    {
                        MainFile.Log.Debug($"Restore skip {modId}.{name}: {e.Message}");
                    }
                }
                try
                {
                    config.ConfigReloaded();
                }
                catch (Exception e)
                {
                    MainFile.Log.Error($"Failed to refresh config UI for {modId} on restore: {e.Message}");
                }
            }
            RestoreSnapshot.Clear();
            MainFile.Log.Info("Config sync session ended: local settings restored in memory");

            // MCS-3: unfreeze files and put the user's exact bytes back, undoing
            // anything a third-party subscriber managed to write meanwhile.
            UnprotectConfigFiles();
        }
        finally
        {
            _restoring = false;
        }
    }

    private static void SnapshotOwnValue(PropertyInfo prop, string modId, string propertyName)
    {
        if (!RestoreSnapshot.TryGetValue(modId, out Dictionary<string, string>? snap))
        {
            snap = new Dictionary<string, string>();
            RestoreSnapshot[modId] = snap;
        }
        if (snap.ContainsKey(propertyName))
        {
            return;
        }
        object? own = prop.GetValue(null);
        snap[propertyName] = own == null ? string.Empty : TypeDescriptor.GetConverter(prop.PropertyType).ConvertToInvariantString(own)!;
    }

    // ---------- MCS-3 file protection ----------

    /// <summary>
    /// BaseLib writes each mod's cfg into &lt;user data&gt;/mod_configs/&lt;Namespace&gt;.cfg.
    /// Snapshot + read-only every touched mod's file so no subscriber (debounced
    /// save, submenu close, BaseLib save lifecycle) can persist host values.
    /// </summary>
    private static void ProtectConfigFiles(List<(PropertyInfo Prop, object Value, string ModId, string Name)> pending)
    {
        if (FileBackups.Count > 0)
        {
            return; // already protecting this session
        }
        var mods = new HashSet<string>();
        foreach (var entry in pending)
        {
            mods.Add(entry.ModId);
        }
        string? dir = ModConfigsDir(mods);
        if (dir == null)
        {
            return;
        }
        foreach (string modId in mods)
        {
            string path = Path.Combine(dir, modId + ".cfg");
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                FileBackups[path] = File.ReadAllBytes(path);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                MainFile.Log.Info($"Config sync: {Path.GetFileName(path)} frozen for this session");
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Config sync: could not freeze {path}: {e.Message}");
                FileBackups.Remove(path);
            }
        }
    }

    private static void UnprotectConfigFiles()
    {
        foreach ((string path, byte[] bytes) in FileBackups)
        {
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.WriteAllBytes(path, bytes);
                MainFile.Log.Info($"Config sync: {Path.GetFileName(path)} restored to pre-session bytes");
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Config sync: could not restore {path}: {e.Message}");
            }
        }
        FileBackups.Clear();
    }

    /// <summary>
    /// Same location BaseLib writes mod cfgs (user_data_dir/mod_configs).
    /// Resolved from a registered config's own BaseLib file path
    /// (ModConfig._path) so no Godot API is touched on this path - the probe
    /// can exercise the full freeze/restore flow outside the engine, and the
    /// game-side behavior is identical (BaseLib itself computes _path from
    /// OS.GetUserDataDir()). Null when nothing resolvable (nothing to protect).
    /// </summary>
    private static string? ModConfigsDir(IEnumerable<string> modIds)
    {
        var pathField = typeof(ModConfig).GetField("_path",
            BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (string modId in modIds)
        {
            ModConfig? config = ModConfigRegistry.Get(modId);
            if (config == null || pathField == null)
            {
                continue;
            }
            string? path = pathField.GetValue(config) as string;
            if (!string.IsNullOrEmpty(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                {
                    return dir;
                }
            }
        }
        return null;
    }
}
