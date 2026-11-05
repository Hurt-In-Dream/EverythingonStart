using System.Diagnostics;
using System.IO;
using System.Windows;
using EverythingonStart.Models;

namespace EverythingonStart.Services;

public sealed class ActionExecutorService
{
    private readonly EverythingSearchService _everythingService;

    public ActionExecutorService(EverythingSearchService everythingService)
    {
        _everythingService = everythingService;
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
