// 隔离验证: 复刻 MainMenuGameplayModCountPatch 的两处纯逻辑 —
//   1) gameplay 计数判据 = manifest.affects_gameplay(缺省 true), 与引擎 GetGameplayRelevantModNameList 同口径;
//   2) 幂等标记(零宽空格结尾)避免 UpdateText 多次调用重复拼接。
// 不覆盖 Harmony 落点 / MegaLabel / 引擎 loc(那由集中构建 + 实机覆盖)。
int failures = 0;
void Check(string name, bool ok){ if(ok) System.Console.WriteLine($"通过: {name}"); else { failures++; System.Console.Error.WriteLine($"失败: {name}"); } }

const string Marker = "\u200B";

// 与补丁一致的计数: affects_gameplay 缺省 true。
static int GameplayCount(System.Collections.Generic.IEnumerable<bool?> affectsGameplayFlags)
    => System.Linq.Enumerable.Count(affectsGameplayFlags, f => f ?? true);

// 与补丁一致的追加(幂等)。
static string AppendOnce(string current, int gameplayCount)
{
    if (current.EndsWith(Marker, System.StringComparison.Ordinal)) return current;
    string suffix = $"\n{gameplayCount} of them affect gameplay (gameplay=1)";
    return current + suffix + Marker;
}

var mixed = new bool?[] { true, false, null, true, false }; // 3 个 gameplay(true,null->true,true)
Check("计数: true/null 计入, false 不计", GameplayCount(mixed) == 3);
Check("计数: 全 false = 0", GameplayCount(new bool?[]{false,false}) == 0);
Check("计数: 全缺省(null)全计入", GameplayCount(new bool?[]{null,null,null}) == 3);

string baseText = "3 mods loaded";
string once = AppendOnce(baseText, 2);
Check("首次追加包含后缀", once.Contains("2 of them affect gameplay (gameplay=1)"));
Check("首次追加以标记结尾", once.EndsWith(Marker, System.StringComparison.Ordinal));
string twice = AppendOnce(once, 2);
Check("二次调用幂等(不重复拼接)", twice == once);
string thrice = AppendOnce(twice, 5); // 即便计数变化, 已带标记就不再改(本行这轮已处理)
Check("已带标记则整行不再变更", thrice == once);

System.Console.WriteLine($"菜单计数探针场景: 7, 失败: {failures}");
System.Environment.ExitCode = failures == 0 ? 0 : 1;
