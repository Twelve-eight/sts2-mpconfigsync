using BaseLib.Config;

namespace MpConfigSync.MpConfigSyncCode;

/// <summary>
/// Local settings. Enabled=false => this mod still forwards nothing: the host patch
/// checks the toggle before pushing, and receivers ignore messages when their own
/// toggle is off (both ends must opt in, mirroring the "strong allow needs both ends"
/// semantics of Spire1's MP ignore toggles).
/// </summary>
[ConfigHoverTipsByDefault]
internal class MpConfigSyncConfig : SimpleModConfig
{
    public static bool Enabled { get; set; } = true;
}