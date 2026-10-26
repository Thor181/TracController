using Microsoft.Win32;

namespace TracController;

internal static class TracLocator
{
    private static readonly string[] KnownRelativePaths =
    [
        @"CheckPoint\Endpoint Connect\trac.exe",
        @"CheckPoint\Endpoint Security\Endpoint Connect\trac.exe",
        @"CheckPoint\Endpoint Security VPN\trac.exe",
    ];

    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var env = Environment.GetEnvironmentVariable("TRAC_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var root in programFiles)
            foreach (var rel in KnownRelativePaths)
            {
                var candidate = Path.Combine(root, rel);
                if (File.Exists(candidate))
                    return candidate;
            }

        if (FromRegistry() is { } fromRegistry)
            return fromRegistry;

        if (FromPath() is { } fromPath)
            return fromPath;

        // Нестандартная раскладка: ищем рекурсивно внутри папок CheckPoint
        foreach (var root in programFiles)
        {
            var cpDir = Path.Combine(root, "CheckPoint");
            if (!Directory.Exists(cpDir))
                continue;
            try
            {
                var found = Directory.EnumerateFiles(cpDir, "trac.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 6,
                }).FirstOrDefault();
                if (found != null)
                    return found;
            }
            catch
            {
                // нет доступа — пропускаем
            }
        }

        return null;
    }

    private static string? FromRegistry()
    {
        // Ищем установленный продукт Check Point в списке "Программы и компоненты"
        string[] uninstallKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        foreach (var keyPath in uninstallKeys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key == null)
                continue;
            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                var publisher = sub?.GetValue("Publisher") as string;
                var location = sub?.GetValue("InstallLocation") as string;
                if (publisher == null || location == null || !publisher.Contains("Check Point", StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = Path.Combine(location, "trac.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? FromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "trac.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // кривой элемент PATH
            }
        }
        return null;
    }
}
