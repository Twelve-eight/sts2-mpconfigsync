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
/// not block. SetValues happen only after full validation AND after the freeze
/// pass below succeeded. A setter that throws mid-commit is logged per-entry and
/// the run start continues (config sync must not crash the game), but the final
/// log line then reports outcome=PARTIAL with the exact applied / skipped /
/// excluded / failed counts, so a partially applied snapshot is never silent.
///
/// FREEZE FAILURE POLICY (WS-0916-04, 2026-09-16): freezing is ALL-OR-NOTHING
/// per packet, matching the MCS-5 all-or-nothing validation stance. It is
/// incremental - each packet freezes the mods THAT packet names, so a mod
/// introduced by a later packet in the same session is frozen then, and never
/// skipped because an earlier packet froze a different mod - and idempotent: a
/// path already frozen this session keeps its ORIGINAL pre-session bytes and is
/// not re-snapshotted. If a mod named by the packet has a cfg file on disk that
/// cannot be snapshotted and marked read-only, the WHOLE packet is rejected
/// before the first SetValue, with an Error-level log line; no value is changed,
/// so a mod can never end up half-applied or committed without file protection.
/// A mod with no cfg file on disk has no user bytes to protect and nothing to
/// freeze: that is not a failure and does not block (which also keeps sync
/// working for mods whose settings were never saved).
///
/// NO FILE WRITES THROUGH ANY PATH (MCS-3): Changed() is no longer fired on the
/// apply path (it signals "user edited" and Qurious/BaseLib subscribers save on
/// it, on submenu close, and on debounce). Only ConfigReloaded() refreshes UI.
/// Defense in depth: before a packet commits, every mod that packet names has
/// its cfg FILE snapshotted and marked read-only, so a third-party subscriber
/// that still saves (open settings page) cannot persist host values even by
/// accident; at session end (RunManager.CleanUp) the read-only flag is lifted
/// and the user's own bytes are restored verbatim. A mid-session crash
/// therefore also leaves the user's file untouched. RESTORE RETENTION
/// (WS-0916-04, 2026-09-16): an entry is dropped only after its bytes AND its
/// read-only flag were restored successfully. A failing entry is kept in
/// FileBackups and reported at Error level, so the session's only recovery copy
/// of the user's bytes is never destroyed by a failed restore; a later CleanUp
/// (or the user) can still recover it.
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
        // ALL-OR-NOTHING per packet (see FREEZE FAILURE POLICY above): if any mod
        // this packet names cannot be frozen, nothing is committed. Freezing is
        // incremental, so mods named only by this later packet are frozen here.
        if (!ProtectConfigFiles(pending))
        {
            MainFile.Log.Error($"Config sync outcome=REJECTED applied=0/{count} entries "
                               + $"(skipped={unknownSkipped} unknown mod/property, failed=0, "
                               + $"excluded={pending.Count} entries = whole packet): "
                               + "file freeze failed, nothing committed (MCS-5 all-or-nothing)");
            return;
        }

        // ---- MCS-5 phase 2: commit.
        int applied = 0;
        int failed = 0;
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
                // and the failure is on the record. The summary line below then
                // reports outcome=PARTIAL with the exact counts, so a partially
                // applied snapshot is never silent.
                failed++;
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

        // Unambiguous final state (WS-0916-04): the outcome word plus the exact
        // applied/skipped/failed counts make a partial commit impossible to miss.
        string outcome = failed == 0 ? "OK" : "PARTIAL";
        MainFile.Log.Info($"Config sync outcome={outcome} applied={applied}/{count} entries "
                          + $"(skipped={unknownSkipped} unknown mod/property, failed={failed} setter failures, excluded=0), "
                          + $"{touched.Count} mods touched (session-scoped; cfg files frozen until session end), "
                          + $"{propertyMaps.ResolvedModCount} distinct mods resolved this packet (MCS-1)");
        if (failed > 0)
        {
            MainFile.Log.Error($"Config sync outcome=PARTIAL: {applied}/{count} entries applied, "
                               + $"{failed} setter failure(s) left those entries at their previous local values");
        }
    }

    /// <summary>Runs on RunManager.CleanUp: put the user's own values and files back.</summary>
    internal static void RestoreLocalSettings()
    {
        // Also runs again when a previous pass retained a file backup after a
        // failed restore (WS-0916-04), so a later attempt can still recover the
        // user's bytes instead of the retained entry being unreachable forever.
        if ((RestoreSnapshot.Count == 0 && FileBackups.Count == 0) || _restoring)
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
            // A failed entry stays in FileBackups (lossless-restore guard) and is
            // already reported at Error level inside UnprotectConfigFiles.
            if (UnprotectConfigFiles())
            {
                MainFile.Log.Info($"Config sync session ended: all {FileBackups.Count} protected cfg file(s) restored");
            }
            else
            {
                MainFile.Log.Error($"Config sync session ended: {FileBackups.Count} cfg file(s) could not be restored "
                                   + "and are RETAINED in memory for a later attempt or manual recovery");
            }
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
    /// Snapshot + read-only every mod THIS packet names, so no subscriber
    /// (debounced save, submenu close, BaseLib save lifecycle) can persist host
    /// values.
    ///
    /// INCREMENTAL + IDEMPOTENT (WS-0916-04): each call freezes the mods of its
    /// own packet, so a mod introduced by a later packet in the same session is
    /// frozen when that packet arrives - never skipped because an earlier packet
    /// froze a different mod. A path already in <see cref="FileBackups"/> keeps
    /// its original pre-session bytes and is not re-snapshotted.
    ///
    /// ALL-OR-NOTHING (WS-0916-04): returns false as soon as any named mod has a
    /// cfg file on disk that cannot be snapshotted and marked read-only; the
    /// caller then commits nothing, so no mod is ever half-applied and no host
    /// value is committed for a file that is not actually protected. Freezes
    /// already performed by this call are KEPT: they are protective, they hold
    /// the user's original bytes, and session end restores them losslessly. A mod
    /// with no cfg file on disk has no user bytes to protect and is not a
    /// failure.
    /// </summary>
    /// <returns>True when every named mod is now protected (or had nothing to protect).</returns>
    private static bool ProtectConfigFiles(List<(PropertyInfo Prop, object Value, string ModId, string Name)> pending)
    {
        var mods = new HashSet<string>();
        foreach (var entry in pending)
        {
            mods.Add(entry.ModId);
        }
        string? dir = ModConfigsDir(mods);
        if (dir == null)
        {
            return true; // no cfg location resolvable: nothing exists to protect
        }
        foreach (string modId in mods)
        {
            string? path = null;
            try
            {
                path = Path.Combine(dir, modId + ".cfg");
                if (FileBackups.ContainsKey(path))
                {
                    if (IsFrozen(path))
                    {
                        continue; // still frozen this session; original bytes kept
                    }
                    // A previous restore lifted the read-only flag but failed to
                    // write the bytes back, so the entry is RETAINED as the
                    // recovery copy. The file must still be protected for this
                    // packet, so re-apply the flag; the retained bytes are left
                    // untouched (they are the user's pre-session bytes and were
                    // never successfully overwritten).
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    MainFile.Log.Warn($"Config sync: {Path.GetFileName(path)} re-frozen for this session "
                                      + "(a previous restore left it unprotected; retained backup kept)");
                    continue;
                }
                if (!File.Exists(path))
                {
                    continue; // never saved: no user bytes exist to protect
                }
                byte[] bytes = File.ReadAllBytes(path);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                // Recorded only once the freeze actually took effect, so
                // FileBackups never holds bytes for a file that is not frozen.
                FileBackups[path] = bytes;
                MainFile.Log.Info($"Config sync: {Path.GetFileName(path)} frozen for this session");
            }
            catch (Exception e)
            {
                // Data-integrity failure, not a log line: the caller rejects the
                // whole packet before any SetValue.
                MainFile.Log.Error($"Config sync freeze FAILED for {path ?? modId + ".cfg"}: {e.Message} "
                                   + "- packet rejected, no value committed");
                return false;
            }
        }
        return true;
    }

    /// <summary>True when the cfg file currently carries the read-only freeze flag.</summary>
    private static bool IsFrozen(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception)
        {
            // Unreadable attributes mean "not confirmed protected"; the caller
            // re-applies the flag (or fails the freeze) rather than assuming.
            return false;
        }
    }

    /// <summary>
    /// Lift the read-only flag and put the user's exact bytes back, undoing
    /// anything a third-party subscriber managed to write meanwhile.
    ///
    /// LOSSY-RESTORE GUARD (WS-0916-04): an entry is dropped ONLY after both the
    /// attribute reset and the byte write succeeded. A failing entry STAYS in
    /// <see cref="FileBackups"/> - that dictionary is the session's only copy of
    /// the user's bytes - and is reported at Error level, so a later CleanUp or
    /// the user can still recover it. FileBackups is never cleared
    /// unconditionally.
    /// </summary>
    /// <returns>True when every known entry was restored and dropped.</returns>
    private static bool UnprotectConfigFiles()
    {
        if (FileBackups.Count == 0)
        {
            return true;
        }
        int restored = 0;
        int retained = 0;
        // Snapshot the keys: successful entries are removed from the dictionary.
        foreach (string path in new List<string>(FileBackups.Keys))
        {
            byte[] bytes = FileBackups[path];
            try
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.WriteAllBytes(path, bytes);
                FileBackups.Remove(path);
                restored++;
                MainFile.Log.Info($"Config sync: {Path.GetFileName(path)} restored to pre-session bytes");
            }
            catch (Exception e)
            {
                retained++;
                MainFile.Log.Error($"Config sync: could not restore {path}: {e.Message} "
                                   + "- backup RETAINED for a later attempt or manual recovery");
            }
        }
        if (retained > 0)
        {
            MainFile.Log.Error($"Config sync: {retained} cfg file(s) NOT restored ({restored} restored); "
                               + "their pre-session bytes are retained in memory");
        }
        return retained == 0;
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
