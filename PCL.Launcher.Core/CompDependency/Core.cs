using PCL.Core.Minecraft.ResourceProject;
using CompFile = PCL.Comp.CompFile;
using CompLoaderType = PCL.Comp.CompLoaderType;
using CompProject = PCL.Comp.CompProject;
using LocalCompFile = PCL.LocalComp.LocalCompFile;

namespace PCL;

public static partial class CompDependency
{
    public static ModDependencyRequest BuildRequest(
        CompFile file,
        CompProject project,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        string targetModsFolder)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(project);

        var source = GetSource(project.FromCurseForge);
        // 同时使用 Dependencies（已解析）与 RawDependencies（API 原始 ID），
        // 避免因 Modrinth 返回内部 ID 导致 Dependencies 未回填时依赖检测失效。
        var requiredIds = file
            .Dependencies
            .Concat(file.RawDependencies)
            .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var dependencies = requiredIds
            .Select(dependencyId => new ModDependencyReference
            {
                ProjectId = dependencyId,
                Source = source,
                IsRequired = true
            })
            .Concat(
                file
                    .OptionalDependencies
                    .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                    .Select(dependencyId => new ModDependencyReference
                    {
                        ProjectId = dependencyId,
                        Source = source,
                        IsRequired = false
                    }))
            .ToList();

        return new ModDependencyRequest
        {
            TargetMinecraftVersion = targetMinecraftVersion,
            TargetLoaders = ToLoaderNames(targetLoaders),
            RequiredDependencies = dependencies,
            InstalledMods = ScanInstalledMods(targetModsFolder),
            ProjectResolver = ResolveProjectFiles
        };
    }

    public static List<InstalledModIdentity> ScanInstalledMods(string targetModsFolder)
    {
        var result = new List<InstalledModIdentity>();
        if (string.IsNullOrWhiteSpace(targetModsFolder) || !Directory.Exists(targetModsFolder))
            return result;

        foreach (var path in Directory.GetFiles(targetModsFolder))
        {
            if (!LocalCompFile.IsModFile(path)) continue;

            var localFile = new LocalCompFile(path);
            localFile.Load();

            var source = localFile.Comp is null
                ? null
                : GetSource(localFile.Comp.FromCurseForge);
            var gameVersions = localFile
                    .CompFile?.GameVersions
                    ?.Where(static version => !string.IsNullOrWhiteSpace(version))
                    .Select(Comp.NormalizeGameVersion)
                    .Where(static version => !string.IsNullOrWhiteSpace(version))
                    .ToList()
                ?? [];
            var loaders = ToLoaderNames(localFile.CompFile?.ModLoaders);

            if (!string.IsNullOrWhiteSpace(localFile.Comp?.Id)
                && !string.IsNullOrWhiteSpace(source))
            {
                result.Add(
                    new InstalledModIdentity
                    {
                        SourceProjectId = localFile.Comp.Id,
                        Source = source,
                        ModId = localFile.ModId,
                        GameVersions = gameVersions,
                        Loaders = loaders
                    });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(localFile.CompFile?.ProjectId))
            {
                var fileSource = GetSource(localFile.CompFile.FromCurseForge);
                result.Add(
                    new InstalledModIdentity
                    {
                        SourceProjectId = localFile.CompFile.ProjectId,
                        Source = fileSource,
                        ModId = localFile.ModId,
                        GameVersions = gameVersions,
                        Loaders = loaders
                    });
                continue;
            }

            result.Add(
                new InstalledModIdentity
                {
                    SourceProjectId = null,
                    Source = null,
                    ModId = localFile.ModId,
                    GameVersions = gameVersions,
                    Loaders = loaders
                });
        }

        return result;
    }

    public static ModDependencyProject? ResolveProjectFiles(string source, string projectId)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
            return null;

        var fromCurseForge =
            string.Equals(source, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var files = Comp.CompFilesGet(projectId, fromCurseForge);

        if (!Comp.compProjectCache.TryGetValue(projectId, out var compProject))
        {
            // 兜底：请求 ID 与返回 ID 不一致时（如用 slug 请求、返回内部 ID），按 Id/Slug 匹配
            compProject = Comp
                .compProjectCache
                .Values
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Slug, projectId, StringComparison.OrdinalIgnoreCase));
            if (compProject is null)
                return null;
        }

        if (compProject.FromCurseForge != fromCurseForge)
            return null;

        return new ModDependencyProject
        {
            ProjectId = compProject.Id,
            Source = source,
            ProjectName = compProject.TranslatedName ?? compProject.RawName,
            Files =
            [
                .. files.Select(compFile => new ModDependencyFile
                {
                    Id = compFile.Id,
                    DisplayName = compFile.DisplayName,
                    Version = compFile.Version,
                    GameVersions = compFile
                            .GameVersions
                            ?.Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .Select(Comp.NormalizeGameVersion)
                            .Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .ToList()
                        ?? [],
                    Loaders = ToLoaderNames(compFile.ModLoaders),
                    ReleaseType = MapReleaseType(compFile.Status),
                    ReleaseDate = compFile.ReleaseDate,
                    RequiredDependencies =
                    [
                        .. compFile
                            .Dependencies
                            .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                            .Select(dependencyId => new ModDependencyReference
                            {
                                ProjectId = dependencyId,
                                Source = source,
                                IsRequired = true
                            })
                    ]
                })
            ]
        };
    }
}