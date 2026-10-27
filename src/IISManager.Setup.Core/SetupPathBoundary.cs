namespace IISManager.Setup.Core;

// Pure path checks shared with the Windows backend so their actual contract can run in portable self-tests.
public static class SetupPathBoundary
{
    public static bool IsUnder(string path, string root) => !string.IsNullOrWhiteSpace(path) &&
        !string.IsNullOrWhiteSpace(root) && (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    public static string GetRuntimeRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path) ||
            Path.GetFullPath(root) != root || Path.GetFullPath(path) != path || !IsUnder(path: path, root: root))
            throw Invalid("原运行记录包含不规范或越界路径。");
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!PayloadArchive.IsSafeRelativePath(relative)) throw Invalid("原运行记录路径无效。");
        return relative;
    }

    private static SetupException Invalid(string message) => new("UI_RECOVERY_RUNTIME_RECORD_INVALID", message);
}
