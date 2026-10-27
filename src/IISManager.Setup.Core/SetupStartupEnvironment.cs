using System.Security;

namespace IISManager.Setup.Core;

public static class SetupStartupEnvironment
{
    public static void PrepareTemporaryDirectory(string temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectory) || !Path.IsPathFullyQualified(temporaryDirectory))
            throw new SetupException("INSTALLER_TEMP_PATH_INVALID", "当前用户临时目录必须是完整路径。");
        try
        {
            // Windows session TEMP paths may be configured before the session
            // directory exists. WinForms visual styles write here during init.
            var directory = Directory.CreateDirectory(temporaryDirectory);
            var probe = Path.Combine(directory.FullName, ".iismanager-startup-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new SetupException("INSTALLER_TEMP_UNAVAILABLE",
                "当前用户临时目录无法创建或写入，请核对该目录与当前运行身份的权限；安装器不会更改系统 TEMP 或放宽权限。", exception);
        }
    }
}
