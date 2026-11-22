using System.Text;

namespace PCL;

/// <summary>
///     本地模组冲突检查：前置缺失、版本不兼容、重复同类、模组主动声明的冲突。
///     完全复用 <see cref="LocalCompFile" /> 已解析好的元数据，不重新读取 jar 内容。
/// </summary>
public static class ModConflictChecker
{
    /// <summary>
    ///     这些依赖目标不是模组，无法按模组依赖关系判定，直接跳过以免误报。
    /// </summary>
    private static readonly HashSet<string> NonModDependencyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilt-loader",
        "forge", "neoforge", "fabric", "quilt", "fabric-api-base"
    };

    /// <summary>一条检查结果。</summary>
    /// <param name="Kind">问题类型，如「前置缺失」「版本不兼容」「重复」「冲突」。</param>
    /// <param name="ModId">相关的模组 Id。</param>
    /// <param name="Message">给人看的描述。</param>
    /// <param name="FileName">出问题的文件，可能为空。</param>
    public sealed record Issue(string Kind, string ModId, string Message, string? FileName);

    /// <summary>
    ///     检查一组本地模组文件，返回所有发现的问题。
    /// </summary>
    /// <param name="mods">模组文件列表，通常直接扫描 mods 目录得到。</param>
    public static List<Issue> Check(IEnumerable<LocalComp.LocalCompFile> mods)
    {
        var issues = new List<Issue>();

        // 已禁用的模组不会生效，既不算「已安装」，也不参与重复检测
        var active = mods
            .Where(m => m.State == LocalComp.LocalCompFile.LocalFileStatus.Fine)
            .ToList();

        // 已安装索引：主 ModId 加上 ProvidedIds（multi-mod jar 的兄弟 id 与 Fabric provides）
        var installed = new Dictionary<string, (LocalComp.LocalCompFile file, string? version)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mod in active)
        {
            if (!string.IsNullOrWhiteSpace(mod.ModId) && !installed.ContainsKey(mod.ModId))
                installed[mod.ModId] = (mod, mod.Version);

            foreach (var provided in mod.ProvidedIds)
            {
                if (string.IsNullOrWhiteSpace(provided) || installed.ContainsKey(provided))
                    continue;

                installed[provided] = (mod, mod.Version);
            }
        }

        // 1. 重复同类：同一个 ModId 出现在多个文件里
        foreach (var group in active
                     .Where(m => !string.IsNullOrWhiteSpace(m.ModId))
                     .GroupBy(m => m.ModId!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(new Issue(
                "重复",
                group.Key,
                $"检测到 {group.Count()} 个文件都提供了 {group.Key}",
                string.Join("、", group.Select(f => f.FileName))));
        }

        // 2. 前置缺失：仅判断依赖 id 是否存在，并把「依赖同一个缺失前置」的模组合并成一条。
        //    不在此处做「版本不兼容」比较——Forge/NeoForge 的依赖约束是 Minecraft 版本区间，
        //    Fabric/Quilt 的约束是模组自身版本谓词，量纲不同，比较必然误报。
        //    key = 缺失的前置 id，value = 依赖它的模组名（去重、保序）。
        var missingByDependency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in active)
        {
            foreach (var dependency in mod.DependencyDeclarations)
            {
                var id = dependency.Id;
                if (string.IsNullOrWhiteSpace(id) || dependency.Optional || NonModDependencyIds.Contains(id))
                    continue;
                if (installed.ContainsKey(id))
                    continue;

                if (!missingByDependency.TryGetValue(id, out var dependents))
                {
                    dependents = [];
                    missingByDependency[id] = dependents;
                }

                var name = mod.Name ?? mod.ModId ?? id;
                if (!dependents.Contains(name, StringComparer.OrdinalIgnoreCase))
                    dependents.Add(name);
            }
        }

        foreach (var pair in missingByDependency)
        {
            var id = pair.Key;
            var dependents = pair.Value;
            var who = dependents.Count switch
            {
                0 => "",
                1 => dependents[0],
                _ => string.Join("、", dependents)
            };
            issues.Add(new Issue(
                "前置缺失",
                id,
                $"缺少 {id}，被 {who} 依赖",
                null));
        }

        // 3. 模组主动声明的不兼容
        foreach (var mod in active)
        {
            foreach (var conflict in mod.ConflictDeclarations)
            {
                var target = conflict.Target;
                if (string.IsNullOrWhiteSpace(target) || !installed.ContainsKey(target))
                    continue;

                issues.Add(new Issue(
                    "冲突",
                    target,
                    $"{mod.Name} 声明与 {target} 不兼容" + (conflict.Hard ? "（硬冲突）" : ""),
                    mod.FileName));
            }
        }

        return issues;
    }

    /// <summary>
    ///     把检查结果整理成可直接展示给用户的文本报告（紧凑排版）。
    /// </summary>
    public static string BuildReport(List<Issue> issues)
    {
        if (issues.Count == 0)
            return "未发现问题：前置依赖均已满足，也没有检测到重复或冲突。";

        var builder = new StringBuilder();
        builder.Append("发现 ").Append(issues.Count).Append(" 个问题：");

        foreach (var issue in issues)
        {
            builder.Append("\n\n[").Append(issue.Kind).Append("] ").Append(issue.ModId);
            builder.Append('\n').Append("  ").Append(issue.Message);
            if (!string.IsNullOrWhiteSpace(issue.FileName))
                builder.Append("（").Append(issue.FileName).Append('）');
        }

        builder.Append("\n\n结果仅供参考，部分模组自身标注的信息可能有误。");
        return builder.ToString();
    }
}
