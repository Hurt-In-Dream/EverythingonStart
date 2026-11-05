using System.Diagnostics;
using System.IO;
using System.Windows;
using EverythingonStart.Models;
using Microsoft.Win32;

namespace EverythingonStart.Services;

public sealed class ActionExecutorService
{
    private readonly EverythingSearchService _everythingService;
    private string? _cachedEverythingExePath;

    public ActionExecutorService(EverythingSearchService everythingService)
    {
        _everythingService = everythingService;
    }

    public bool ExecuteOpenInEverything(string searchText)
    {
        var exePath = ResolveEverythingExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        var trimmed = searchText.Trim();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        };

        if (!string.IsNullOrEmpty(trimmed))
        {
            var escaped = trimmed.Replace("\"", "\"\"");
            psi.Arguments = $"-s \"{escaped}\"";
        }

        Process.Start(psi);
        return true;
    }

    public string? ResolveEverythingExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_cachedEverythingExePath) && File.Exists(_cachedEverythingExePath))
            return _cachedEverythingExePath;

        string[] candidatePaths =
        [
            @"D:\Everything\Everything.exe",
            @"D:\Everything\Everything64.exe",
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files\Everything\Everything64.exe",
            @"C:\Program Files\Everything 1.5a\Everything64.exe",
            @"C:\Program Files\Everything 1.5a\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe"
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                _cachedEverythingExePath = path;
                return path;
            }
        }

        string[] uninstallRoots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var rootPath in uninstallRoots)
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(rootPath);
            if (rootKey == null)
                continue;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName") as string;
                if (displayName == null || !displayName.Contains("Everything", StringComparison.OrdinalIgnoreCase))
                    continue;

                var installLocation = subKey?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    var candidate = Path.Combine(installLocation, "Everything.exe");
                    if (File.Exists(candidate))
                    {
                        _cachedEverythingExePath = candidate;
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    public bool ExecuteOpen(SearchResultItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
            return false;

        if (item.IsFileSystemEntry)
            _everythingService.IncrementRunCount(item.FullPath);

        var psi = new ProcessStartInfo
        {
            FileName = item.FullPath,
            UseShellExecute = true
        };

        if (!item.IsWeb && !item.IsSetting && !string.IsNullOrWhiteSpace(item.DirectoryPath) && Directory.Exists(item.DirectoryPath))
            psi.WorkingDirectory = item.DirectoryPath;

        Process.Start(psi);
        return true;
    }

    public bool ExecuteRunAsAdminOrTakeOwnership(SearchResultItem? item)
    {
        if (item == null || item.IsWeb || item.IsSetting || string.IsNullOrWhiteSpace(item.FullPath))
            return false;

        _everythingService.IncrementRunCount(item.FullPath);

        var ext = Path.GetExtension(item.FullPath);
        bool isExecutable = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);

        if (isExecutable)
        {
            var psi = new ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }

        var takeOwnCmd = $"/c takeown /f \"{item.FullPath}\" & icacls \"{item.FullPath}\" /grant administrators:F";
        var takeOwnPsi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = takeOwnCmd,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(takeOwnPsi);
        return true;
    }

    public bool ExecuteOpenFileLocation(SearchResultItem? item)
    {
        if (item == null || !item.SupportsOpenLocation)
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{item.FullPath}\"",
            UseShellExecute = true
        };
        Process.Start(psi);
        return true;
    }

    public bool ExecuteCopyPath(SearchResultItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
            return false;

        Clipboard.SetDataObject(item.FullPath, copy: true);
        return true;
    }
}
