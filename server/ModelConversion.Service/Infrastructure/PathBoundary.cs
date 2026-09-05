namespace ModelConversion.Service.Infrastructure;

public static class PathBoundary
{
    public static string ResolveExistingFile(string root, string relativePath)
    {
        var candidate = ResolveOwnedPath(root, relativePath);
        if (!File.Exists(candidate))
            throw new FileNotFoundException("输入文件不存在", candidate);
        return candidate;
    }

    public static string ResolveOwnedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("路径必须是非空相对路径", nameof(relativePath));

        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
        var rootPrefix = rootFullPath + Path.DirectorySeparatorChar;

        // [2026-09-04 路径安全] 所有用户输入都必须被约束在配置根目录内，防止读取宿主机任意文件。
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("路径越出允许的根目录");
        return candidate;
    }
}
