using IISManager.Core;

namespace IISManager.Agent.Backends;

internal static class SitePathGuard
{
    // Reject every reparse point in the chain, including the allowlisted root itself.
    // This deliberately excludes UNC, junctions, mount points and symlinks rather than following them.
    internal static string[] Validate(string requestedPath, string[] allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathFullyQualified(requestedPath) ||
            (OperatingSystem.IsWindows() && (requestedPath.StartsWith("\\\\", StringComparison.Ordinal) || requestedPath.AsSpan(2).Contains(':'))))
            throw new OperationException("site.pathInvalid", "物理目录须为本机绝对路径，不支持 UNC、设备路径或备用数据流。");
        string full;
        try { full = Path.GetFullPath(requestedPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new OperationException("site.pathInvalid", "物理目录路径无效。"); }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var allowed = allowedRoots.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).Any(root =>
        {
            root = Path.TrimEndingDirectorySeparator(root);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return full.Equals(root, comparison) || full.StartsWith(prefix, comparison);
        });
        if (!allowed) throw new OperationException("site.pathNotAllowed", "物理目录不在 Agent 配置的允许根目录内。");
        var chain = new List<string>();
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) throw new OperationException("site.pathNotFound", "物理目录和全部父目录必须已存在。");
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                throw new OperationException("site.pathReparsePoint", "物理目录链包含重解析点、联接或符号链接。");
            chain.Add(directory.FullName);
        }
        chain.Reverse();
        return chain.ToArray();
    }
}
