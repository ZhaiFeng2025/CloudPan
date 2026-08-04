using System.Text.RegularExpressions;
using CloudPan.Contract;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 同步路径/选择集工具（T-099 从 SyncEngine 拆分）：持有排除集（_selectedPaths）与忽略规则
/// （_ignorePatterns）可变状态，提供路径是否入选同步范围、忽略规则命中与重命名前缀判定。
/// 路径绝对换算仍经 SyncPath（T-070 拆分，避免重复实现），本类只承载「选择/忽略」判定职责。
/// </summary>
internal sealed class SyncPathSelector
{
    private readonly string _syncRoot;
    private readonly List<Regex> _ignorePatterns;
    private volatile List<string> _selectedPaths;

    public SyncPathSelector(string syncRoot, List<string>? selectedPaths, List<Regex> ignorePatterns)
    {
        _syncRoot = syncRoot;
        _ignorePatterns = ignorePatterns;
        _selectedPaths = selectedPaths ?? new List<string> { "/" };
    }

    /// <summary>排除集（T-063：运行时热更新，引用替换语义）。读取方应单次读取引用保持单次调用内一致。</summary>
    public List<string> SelectedPaths
    {
        get => _selectedPaths;
        set => _selectedPaths = value ?? new List<string> { "/" };
    }

    /// <summary>忽略规则（供 SyncBrowseService 查询侧复用）。</summary>
    public List<Regex> IgnorePatterns => _ignorePatterns;

    /// <summary>相对路径命中忽略规则（内置 *.tmp 等 + 用户 .syncignore）。</summary>
    public bool ShouldIgnore(string relativePath) => SyncIgnoreParser.ShouldIgnore(relativePath, _ignorePatterns);

    /// <summary>绝对本地路径命中忽略规则（经相对路径换算）。</summary>
    public bool ShouldIgnoreScan(string fullPath) => SyncPath.ShouldIgnore(_syncRoot, fullPath, _ignorePatterns);

    /// <summary>检查路径是否在已选择的同步范围内（排除集语义，T-047）。</summary>
    /// <remarks>
    /// SelectedPaths 语义（v2 排除集）：
    /// - 空集合 → 显式全不同步（取消全选后不回退为 { "/" } 全选）。
    /// - 含 "/"（全选默认值，含 v1.0.0 旧版选择集恒含根节点）→ 全选，不排除任何路径。
    /// - 其余 → 排除子树列表：命中任一排除子树（含深层路径）→ 不同步。
    /// </remarks>
    public bool IsPathSelected(string path)
    {
        // 局部快照：读取一次引用，单次调用内语义一致（热更新替换引用不影响本次判断，T-063）
        List<string> selectedPaths = _selectedPaths;

        // 空集合 = 显式全不同步（不再回退为 { "/" } 全选）
        if (selectedPaths.Count == 0)
        {
            return false;
        }

        // 含 "/"（全选默认值 / v1.0.0 旧版选择集恒含根节点）→ 全选
        if (selectedPaths.Contains("/"))
        {
            return true;
        }

        // 排除集：命中任一排除子树 → 不同步
        string normalized = path.TrimEnd('/') + "/";
        bool excluded = selectedPaths.Any(sp =>
        {
            string p = sp.TrimEnd('/') + "/";
            return normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                   || path.Equals(sp.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        });
        return !excluded;
    }

    /// <summary>
    /// T-066：判断路径是否位于任一前缀（未决重命名的旧前缀/新前缀）覆盖的子树内。
    /// 前缀归一化为目录边界（"/photos" → "/photos/"），避免误伤相似路径（"/photosx"）。
    /// </summary>
    public static bool IsUnderAnyPrefix(string path, IReadOnlyList<string> prefixes)
    {
        string normalized = path.TrimEnd('/') + "/";
        foreach (string prefix in prefixes)
        {
            string p = prefix.TrimEnd('/') + "/";
            if (normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
