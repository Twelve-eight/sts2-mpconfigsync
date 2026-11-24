using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Local settings. Master switch plus two behavior-group switches, all defaulting
/// to true so the shipped default behavior is unchanged (F05-rerun, 2026-10-02).
///
/// GATE CONTRACT:
/// - <see cref="Enabled"/> is the master switch: false => this mod neither
///   pushes nor accepts config snapshots.
/// - <see cref="EnableSending"/> gates the host push path.
/// - <see cref="EnableReceiving"/> gates the receiver path.
/// - <see cref="SendingEnabled"/> / <see cref="ReceivingEnabled"/> are the
///   computed gates consumed by MainFile's registration filter and by its
///   runtime gate prefixes. They are [ConfigIgnore]: not persisted, not shown
///   and not synced, so a computed value can never drift from its inputs.
/// - Registration layer: MainFile reads the gates ONCE at mod initialization
///   and only installs the patch classes their group owns. A group that was off
///   at process start therefore needs a process restart before it can be turned
///   on - its patch classes are not registered.
/// - Runtime layer: the gate prefixes re-check the live values on every push
///   and every incoming snapshot, so turning a gate OFF takes effect
///   immediately even though already-installed patch classes are not
///   unpatched.
/// - The receiver never rewrites cfg files: host values are applied in memory
///   for the session and the user's own values are restored at run end (B01).
/// </summary>
[ConfigHoverTipsByDefault]
internal class MpConfigSyncConfig : SimpleModConfig
{
    /// <summary>Master switch. false => neither push nor accept config snapshots.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Sending group (host side). false => the host never builds or sends a
    /// snapshot. When false at process start, the send patch classes are not
    /// registered for this process; turning it off mid-session stops further
    /// pushes immediately through the runtime gate.
    /// </summary>
    public static bool EnableSending { get; set; } = true;

    /// <summary>
    /// Receiving group (peer side). false => incoming host snapshots are
    /// ignored. When false at process start, the receive patch classes are not
    /// registered for this process; turning it off mid-session ignores further
    /// snapshots immediately through the runtime gate.
    /// </summary>
    public static bool EnableReceiving { get; set; } = true;

    /// <summary>Computed send gate: master AND sending group. Not persisted or synced.</summary>
    [ConfigIgnore]
    public static bool SendingEnabled => Enabled && EnableSending;

    /// <summary>Computed receive gate: master AND receiving group. Not persisted or synced.</summary>
    [ConfigIgnore]
    public static bool ReceivingEnabled => Enabled && EnableReceiving;
}