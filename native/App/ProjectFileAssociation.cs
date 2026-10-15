using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VectorAnimationEngine;

/// <summary>
/// 在当前用户注册表（HKCU，无需管理员权限）注册 .v2d 单文件工程的扩展名、
/// 图标与双击打开命令。图标从嵌入程序集资源释放到 LocalAppData，内容不变时不重复写入。
/// </summary>
internal static class ProjectFileAssociation
{
    internal const string PackageExtension = ".v2d";
    private const string ProgId = "VectorAnimationEngine.Project.v2d";
    private const string ProjectTypeLabel = "Vector 2D Animation Project";
    private const string IconResourceName = "VectorAnimationEngine.ProjectFile.ico";

    private static string IconTargetPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vector2DAnimationEngine",
        "project-file.ico");

    internal static void Ensure()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath)) return;
            if (!TryWriteIconFile()) return;

            var expectedCommand = $"\"{executablePath}\" \"%1\"";
            var expectedIcon = $"\"{IconTargetPath}\",0";

            using var extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PackageExtension);
            var currentProgId = extensionKey.GetValue(string.Empty) as string;
            var changed = !string.Equals(currentProgId, ProgId, StringComparison.Ordinal);
            extensionKey.SetValue(string.Empty, ProgId);
            using var openWithKey = extensionKey.CreateSubKey("OpenWithProgids");
            if (openWithKey.GetValue(ProgId) is not byte[])
            {
                openWithKey.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.Binary);
                changed = true;
            }

            using var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId);
            if (!string.Equals(progIdKey.GetValue(string.Empty) as string, ProjectTypeLabel, StringComparison.Ordinal))
            {
                progIdKey.SetValue(string.Empty, ProjectTypeLabel);
                changed = true;
            }

            using var defaultIconKey = progIdKey.CreateSubKey("DefaultIcon");
            if (!string.Equals(defaultIconKey.GetValue(string.Empty) as string, expectedIcon, StringComparison.Ordinal))
            {
                defaultIconKey.SetValue(string.Empty, expectedIcon);
                changed = true;
            }

            using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
            if (!string.Equals(commandKey.GetValue(string.Empty) as string, expectedCommand, StringComparison.Ordinal))
            {
                commandKey.SetValue(string.Empty, expectedCommand);
                changed = true;
            }

            if (changed)
            {
                NotifyShellAssociationChanged();
                AppLog.Info("Registered .v2d project file association.");
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn("Could not register the .v2d file association: " + exception.Message);
        }
    }

    private static bool TryWriteIconFile()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resourceStream = assembly.GetManifestResourceStream(IconResourceName);
            if (resourceStream is null)
            {
                AppLog.Warn("The embedded project file icon resource was not found.");
                return false;
            }

            using var memory = new MemoryStream();
            resourceStream.CopyTo(memory);
            var iconBytes = memory.ToArray();
            if (iconBytes.Length == 0) return false;

            var target = IconTargetPath;
            var targetDirectory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetDirectory);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(iconBytes))
            {
                return true;
            }

            var temporaryPath = target + ".new";
            File.WriteAllBytes(temporaryPath, iconBytes);
            File.Move(temporaryPath, target, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Warn("Could not install the project file icon: " + exception.Message);
            return false;
        }
    }

    private static void NotifyShellAssociationChanged()
    {
        // SHCNE_ASSOCCHANGED：通知资源管理器刷新扩展名图标与打开方式。
        const uint shcneAssocChanged = 0x08000000;
        SHChangeNotify(shcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
