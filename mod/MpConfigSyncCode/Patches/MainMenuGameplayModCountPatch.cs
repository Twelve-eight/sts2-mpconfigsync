using System.Linq;

using HarmonyLib;

using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.addons.mega_text;

namespace MpConfigSync.MpConfigSyncCode.Patches;

/// <summary>
/// 始终开启(不受 MpConfigSyncConfig.Enabled 门控): 在主菜单右下角引擎渲染的
/// "已加载 N 个 mod" 一行之后, 追加 "其中 M 个影响游戏内容 (gameplay=1)"。
/// <para>
/// M 用引擎自己的判据: manifest.affects_gameplay(缺省 true) —— 与引擎多人握手
/// (<see cref="ModManager.GetGameplayRelevantModNameList"/>) 用的是同一个字段, 所以这里显示的
/// "影响游戏内容的 mod 数" 与联机比对口径一致。这正是 MpConfigSync 关心的那部分 mod。
/// </para>
/// <para>
/// 落点: <see cref="NDebugInfoLabelManager.UpdateText"/> 的 Postfix。引擎在该方法里把整行文本
/// 写进 <c>_moddedWarning</c> (仅主菜单、且 IsRunningModded 时可见); 我们在其后读回当前文本并追加,
/// 用 <c>SetTextAutoSize</c> 重新自适应字号(与引擎写该标签的方式一致)。仅在 isMainMenu 且
/// 联机判定为 modded 时追加; 非主菜单(局内 "MODDED (n)")与未 modded 情形一律不动。
/// </para>
/// <para>
/// 幂等: 追加前先按标记 <see cref="Marker"/> 判断本行是否已带过后缀, 避免引擎多次调用 UpdateText
/// (语言切换 / commitId 回填) 造成重复拼接。文案<b>内联</b>按当前语言给中英双语(见 BuildSuffix):
/// 早期尝试过把文案放进 loc 表, 但引擎 loc 管线会把 "(gameplay=1)" 括注改写成 SmartFormat 复数模板并
/// 渲染出原始模板串(实测), 故改为内联字符串, 保证"始终显示"且括注精确。
/// </para>
/// </summary>
[HarmonyPatch(typeof(NDebugInfoLabelManager), "UpdateText")]
internal static class MainMenuGameplayModCountPatch
{
    // 零宽空格标记: 追加过的行以它结尾, 用于幂等判断(不影响可见文本)。
    private const string Marker = "\u200B"; // 零宽空格, 仅作幂等标记, 不影响可见文本


    [HarmonyPostfix]
    private static void AppendGameplayModCount(NDebugInfoLabelManager __instance)
    {
        try
        {
            if (!__instance.isMainMenu || !ModManager.IsRunningModded())
            {
                return;
            }

            MegaLabel? label = __instance._moddedWarning;
            if (label == null || !label.Visible)
            {
                return;
            }

            string current = label.Text ?? string.Empty;
            if (current.EndsWith(Marker, System.StringComparison.Ordinal))
            {
                return; // 本行已带过后缀, 幂等返回(语言切换/commitId 回填会重复调用 UpdateText)
            }

            int gameplayCount = ModManager.GetLoadedMods()
                .Count(m => m.manifest?.affectsGameplay ?? true);

            string suffix = BuildSuffix(gameplayCount);
            MainFile.Log.Info($"main-menu label: base=[{current}] gameplayCount={gameplayCount} suffix=[{suffix.Replace("\n", "\\n")}]");
            label.SetTextAutoSize(current + suffix + Marker);
        }
        catch (System.Exception e)
        {
            // 纯展示增强, 绝不因它影响主菜单加载。
            MainFile.Log.Error($"main-menu gameplay mod-count suffix failed (ignored): {e}");
        }
    }

    /// <summary>
    /// 后缀文案。刻意<b>不</b>走引擎 loc 表: 实测把 "(gameplay=1)" 放进 loc 值时, 引擎 loc 管线会把
    /// 括号段改写成 SmartFormat 复数模板 "{gameplayCount:plural:|gameplay=1}", 渲染出原始模板串。
    /// 这里直接按当前语言内联中英双语字符串, 保证"始终显示"且文案精确(含 gameplay=1 括注)。
    /// </summary>
    private static string BuildSuffix(int gameplayCount)
    {
        bool zhs = LocManager.Instance.Language == "zhs";
        return zhs
            ? $"\n其中 {gameplayCount} 个影响游戏内容 (gameplay=1)"
            : $"\n{gameplayCount} of them affect gameplay (gameplay=1)";
    }
}
