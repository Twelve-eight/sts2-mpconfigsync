using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

using BaseLib.Config;

using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Receiver side: validate then atomically apply a host-pushed config snapshot.
///
/// AUTH (MCS-2, 2026-09-12): the old code applied ANY sender's message as "from
/// host" - the probe pushed sender 9999 with no session and rewrote a registered
/// setting. Now <see cref="MpNetSession.AuthorizeSnapshot"/> requires: an active
/// connected service, receiver role == Client, and senderId == the host id
/// (NetClientGameService.HostNetId, the same comparison the engine's handshake
/// uses). Hosts/SP/replay reject. senderId is the id the message bus hands to
/// HandleMessage(ulong); the frozen fixture measured it as the packet's sender
/// field, not the transport envelope, so the guarantee is "only a message whose
/// sender field claims the host id is applied" - bounded there by the client-only
/// receive path, the host's role rejection and the host not relaying config
/// messages. Whether a real session can deliver a non-host packet to a client is
/// unverified.
///
/// TRANSACTION (MCS-5): the whole snapshot is resolved + converted FIRST. Any
/// resolved-but-unconvertible value rejects the entire snapshot - a half-applied
/// config must never start a run. Unknown mods/properties are tolerated version
/// skew (a mod the client lacks has no local consumers to desync), logged, and do
/// not block. SetValues happen only after full validation succeeded. A setter or
/// own-value-snapshot failure mid-commit is logged per-entry and the run start
/// continues (config sync must not crash the game), but the final log line then
/// reports outcome=PARTIAL with the exact applied / skipped / failed counts, so a
/// partially applied snapshot is never silent. Every failure path (validation
/// rejection, commit failure, restore failure) emits the same
/// applied/failed/skipped/outcome vocabulary.
///
/// SETTER ROLLBACK (2026-10-26): a throwing setter means the host value was NOT
/// committed, so the own-value snapshot taken moments earlier must not survive it.
/// When that snapshot was created by this very commit attempt (no earlier packet
/// had overridden the property) the local value is set back to the user's own
/// value and the snapshot entry is dropped - the property is exactly where it was
/// before the packet, and a later user edit can still be captured. Retaining such
/// an entry instead is what turned one throwing setter into permanent state: the
/// property could never be recaptured, every later CleanUp retried the same
/// throwing setter (outcome=PARTIAL forever), and a value the user changed in
/// between would eventually be overwritten with the stale capture. Only when the
/// rollback itself throws, and when a host value from an EARLIER entry of the same
/// property is still in effect (then the entry is the session's only copy of the
/// user's value), is the entry retained for a later CleanUp attempt.
///
/// NO FILE I/O (B01, 2026-10-02): the receiver NEVER reads, writes, locks, or
/// changes the read-only attribute of mod_configs/*.cfg. Earlier revisions
/// snapshotted those files and set ReadOnly as defense in depth, but that touched
/// the SHARED AppData directory used by both the Steam install and every test
/// copy, could leave read-only residue after a crash, and contradicted the
/// documented "no file writes" contract (the code and its comments disagreed).
/// Overrides are session-scoped in memory only: SnapshotOwnValue captures each
/// touched property before its first override, and RestoreLocalSettings puts the
/// user's own values back at RunManager.CleanUp. The cfg files themselves are
/// never touched by this class; BaseLib's own save pipeline is the only writer.
///
/// RESTORE RETENTION (B01, 2026-10-02): during a restore, an entry is dropped
/// from RestoreSnapshot ONLY after its own value was converted and SetValue
/// succeeded. A setter, converter, or property-resolution failure keeps the entry
/// (the session's only copy of the user's original value) and is reported at Error
/// level; a later CleanUp retries it instead of the entry becoming unreachable.
/// This is the in-memory equivalent of the old "only drop the file backup after
/// bytes AND attributes were restored" invariant - with no file I/O there is no
/// file backup left to retain, so the retained memory snapshot is the recovery
/// copy. On the COMMIT path the opposite direction applies: an entry whose host
/// value was never committed is rolled back and dropped (SETTER ROLLBACK above).
///
/// Session scope: per-entry in-memory snapshot of the user's own values, restored
/// at CleanUp. Each session starts from the user's own settings again.
/// </summary>
internal static class ConfigSyncApplier
{
    /// <summary>
    /// ModId -> (propertyName -> the user's own invariant value), captured BEFORE
    /// the first host override of that property this session. An entry exists only
    /// while a host value is in effect OR while a restore of one is still pending;
    /// see the SETTER ROLLBACK note on <see cref="Apply"/>.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string?>> RestoreSnapshot = new();

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

        // ---- MCS-5 phase 1: validate everything, change nothing. Collect all
        // validation failures so the rejection line reports the real counts
        // instead of aborting on the first bad entry (B01, 2026-10-02).
        var pending = new List<(PropertyInfo Prop, object Value, string ModId, string Name)>();
        int unknownSkipped = 0;
        int validationFailed = 0;
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
                    validationFailed++;
                    MainFile.Log.Error($"Config sync validation failed: {modId}.{propertyName} value '{raw}' is not a valid {prop.PropertyType.Name}");
                    continue;
                }
                pending.Add((prop, converted, modId, propertyName));
            }
            catch (Exception e)
            {
                validationFailed++;
                MainFile.Log.Error($"Config sync validation failed: {modId}.{propertyName}: {e.Message}");
            }
        }

        if (validationFailed > 0)
        {
            // Whole-packet rejection: nothing has been committed, and this is
            // the failure path's explicit applied/failed/skipped/outcome line.
            MainFile.Log.Error($"Config sync outcome=REJECTED applied=0/{count} entries "
                               + $"(skipped={unknownSkipped} unknown mod/property, failed={validationFailed} validation failure(s), excluded={pending.Count} validated entries): "
                               + "whole packet rejected, nothing committed");
            return;
        }

        // ---- MCS-5 phase 2: commit. SnapshotOwnValue runs before each SetValue
        // so the user's own value is captured exactly once per property per
        // session; a snapshot failure skips that entry (no host value committed)
        // and is counted separately from a setter failure.
        int applied = 0;
        int failed = 0;
        int snapshotFailed = 0;
        int setterFailed = 0;
        int rolledBack = 0;
        var touched = new HashSet<string>();
        foreach (var entry in pending)
        {
            bool capturedNow;
            try
            {
                capturedNow = SnapshotOwnValue(entry.Prop, entry.ModId, entry.Name);
            }
            catch (Exception e)
            {
                snapshotFailed++;
                failed++;
                MainFile.Log.Error($"Config sync own-value snapshot failed for {entry.ModId}.{entry.Name}: {e.Message} "
                                   + "- entry skipped, no host value committed, local value left unchanged");
                continue;
            }
            try
            {
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
                setterFailed++;
                failed++;
                if (!capturedNow)
                {
                    // An EARLIER entry (a previous packet, or an earlier entry of
                    // this one) already committed a host value for this property,
                    // so the retained entry is the session's only copy of the
                    // user's own value: rolling it back now would revert that
                    // host value as well.
                    MainFile.Log.Error($"Config sync setter failed for {entry.ModId}.{entry.Name}: {e.Message} "
                                       + "- an earlier host value stays in effect; the user's own value is "
                                       + "RETAINED in memory for the CleanUp restore");
                    continue;
                }
                if (RollBackOwnValue(entry.Prop, entry.ModId, entry.Name, out string rollbackError))
                {
                    // Nothing of this packet survives for the entry and no
                    // snapshot entry is left behind, so the property is exactly
                    // where it was before the packet and a later user edit is
                    // still captured on the next push.
                    rolledBack++;
                    MainFile.Log.Error($"Config sync setter failed for {entry.ModId}.{entry.Name}: {e.Message} "
                                       + "- the local value was rolled back to the user's own value; no own-value snapshot retained");
                }
                else
                {
                    MainFile.Log.Error($"Config sync setter failed for {entry.ModId}.{entry.Name}: {e.Message} "
                                       + $"- rolling the local value back also failed ({rollbackError}); "
                                       + "the own-value snapshot is RETAINED in memory for the CleanUp restore");
                }
            }
        }

        // ---- UI refresh only: ConfigReloaded re-reads values for display.
        // Changed() is deliberately NOT fired: it means "the user edited" and
        // triggers save pipelines that must never see host values as a save
        // trigger.
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

        // Unambiguous final state (B01): the outcome word plus the exact
        // applied/skipped/failed counts make a partial commit impossible to miss.
        string outcome = failed == 0 ? "OK" : "PARTIAL";
        MainFile.Log.Info($"Config sync outcome={outcome} applied={applied}/{count} entries "
                          + $"(skipped={unknownSkipped} unknown mod/property, failed={failed} = {snapshotFailed} snapshot + {setterFailed} setter "
                          + $"({rolledBack} rolled back), excluded=0), "
                          + $"{touched.Count} mods touched (session-scoped in memory; no cfg file reads, writes or attribute changes), "
                          + $"{propertyMaps.ResolvedModCount} distinct mods resolved this packet (MCS-1)");
        if (failed > 0)
        {
            MainFile.Log.Error($"Config sync outcome=PARTIAL: {applied}/{count} entries applied; "
                               + $"{failed} entry(ies) not committed ({snapshotFailed} captured nothing, "
                               + $"{rolledBack} rolled back to the local value with no snapshot retained, "
                               + $"{setterFailed - rolledBack} retain the user's own value in memory for the CleanUp restore)");
        }
    }

    /// <summary>Runs on RunManager.CleanUp: put the user's own values back.</summary>
    internal static void RestoreLocalSettings()
    {
        // Also runs again when a previous pass retained an entry after a failed
        // restore (B01), so a later attempt can still recover the user's value
        // instead of the retained entry being unreachable forever.
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
            var modsToRemove = new List<string>();
            int restored = 0;
            int failed = 0;
            int skipped = 0;

            foreach ((string modId, Dictionary<string, string?> props) in RestoreSnapshot)
            {
                ModConfig? config = ModConfigRegistry.Get(modId);
                if (config == null)
                {
                    // The config may re-register later in this process (or the
                    // entry is the only copy of the user's value); retain it and
                    // report instead of dropping it silently.
                    skipped += props.Count;
                    MainFile.Log.Error($"Config sync restore: mod {modId} is not registered locally; "
                                       + $"{props.Count} original value(s) RETAINED in memory for a later attempt");
                    continue;
                }

                var restoredNames = new List<string>();
                foreach ((string name, string? value) in props)
                {
                    try
                    {
                        PropertyInfo? prop = propertyMaps.Get(modId)?.GetValueOrDefault(name);
                        if (prop == null)
                        {
                            skipped++;
                            MainFile.Log.Error($"Config sync restore: {modId}.{name} is not present locally; "
                                               + "original value RETAINED in memory for a later attempt");
                            continue;
                        }
                        object? converted;
                        if (value == null)
                        {
                            converted = null;
                        }
                        else
                        {
                            converted = TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(value);
                            if (converted == null)
                            {
                                failed++;
                                MainFile.Log.Error($"Config sync restore: {modId}.{name} could not convert the original value; "
                                                   + "entry RETAINED in memory for a later attempt");
                                continue;
                            }
                        }
                        prop.SetValue(null, converted);
                        restoredNames.Add(name);
                        restored++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        MainFile.Log.Error($"Config sync restore failed for {modId}.{name}: {e.Message} "
                                           + "- entry RETAINED in memory for a later attempt");
                    }
                }

                // Remove only the entries whose SetValue actually succeeded;
                // every failed/skipped entry stays reachable for the next CleanUp.
                foreach (string name in restoredNames)
                {
                    props.Remove(name);
                }
                if (props.Count == 0)
                {
                    modsToRemove.Add(modId);
                }

                if (restoredNames.Count > 0)
                {
                    try
                    {
                        config.ConfigReloaded();
                    }
                    catch (Exception e)
                    {
                        MainFile.Log.Error($"Failed to refresh config UI for {modId} on restore: {e.Message}");
                    }
                }
            }

            foreach (string modId in modsToRemove)
            {
                RestoreSnapshot.Remove(modId);
            }

            int remaining = 0;
            foreach (Dictionary<string, string?> props in RestoreSnapshot.Values)
            {
                remaining += props.Count;
            }

            string outcome = (failed == 0 && skipped == 0) ? "OK" : "PARTIAL";
            MainFile.Log.Info($"Config sync restore outcome={outcome} applied={restored} failed={failed} skipped={skipped} "
                              + $"remaining={remaining} entry(ies) in {RestoreSnapshot.Count} mod(s) "
                              + "(session-scoped in memory; no cfg file reads, writes or attribute changes)");
            if (failed > 0 || skipped > 0)
            {
                MainFile.Log.Error($"Config sync restore outcome=PARTIAL: applied={restored} failed={failed} skipped={skipped}; "
                                   + "unrestored entries are RETAINED in memory for a later CleanUp attempt");
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Captures the user's own value for (modId, propertyName) unless it was
    /// already captured this session. Returns TRUE when the entry was created by
    /// this call - i.e. no host override of this property was in effect before
    /// it, so a failed commit of the first override has nothing to protect and
    /// <see cref="RollBackOwnValue"/> may discard the entry. Returns FALSE when an
    /// earlier packet already captured it: the entry is then the only copy of the
    /// user's value and must survive.
    /// </summary>
    private static bool SnapshotOwnValue(PropertyInfo prop, string modId, string propertyName)
    {
        RestoreSnapshot.TryGetValue(modId, out Dictionary<string, string?>? snap);
        if (snap != null && snap.ContainsKey(propertyName))
        {
            return false;
        }

        // Capture and convert BEFORE registering anything: a throwing getter or
        // converter must not leave an empty mod entry behind that would keep
        // HasSnapshot true without holding a recoverable value (B01).
        object? own = prop.GetValue(null);
        string? captured = own == null
            ? null
            : TypeDescriptor.GetConverter(prop.PropertyType).ConvertToInvariantString(own)!;

        if (snap == null)
        {
            snap = new Dictionary<string, string?>();
            RestoreSnapshot[modId] = snap;
        }
        snap[propertyName] = captured;
        return true;
    }

    /// <summary>
    /// Undo the own-value capture of a property whose host value was never
    /// committed: write the user's own value back (a setter may have mutated
    /// before it threw) and drop the snapshot entry, so the property is exactly
    /// where it was before the packet and a later user edit is captured again.
    /// The entry is only dropped when the write succeeded; on any failure it stays
    /// in <see cref="RestoreSnapshot"/>, because it is still the only copy of the
    /// value to restore.
    /// </summary>
    private static bool RollBackOwnValue(PropertyInfo prop, string modId, string propertyName, out string error)
    {
        error = string.Empty;
        if (!RestoreSnapshot.TryGetValue(modId, out Dictionary<string, string?>? snap) || snap == null
            || !snap.TryGetValue(propertyName, out string? own))
        {
            error = "no own-value snapshot to roll back";
            return false;
        }
        try
        {
            object? ownValue = own == null
                ? null
                : TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(own);
            if (own != null && ownValue == null)
            {
                error = $"'{own}' did not convert back to {prop.PropertyType.Name}";
                return false;
            }
            prop.SetValue(null, ownValue);
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        snap.Remove(propertyName);
        if (snap.Count == 0)
        {
            RestoreSnapshot.Remove(modId);
        }
        return true;
    }
}