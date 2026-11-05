using System.IO;
using System.Text.Json;
using EverythingonStart.Models;
using Microsoft.Win32;

namespace EverythingonStart.Services;

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public WinKeyTriggerMode TriggerMode { get; set; } = WinKeyTriggerMode.InterceptTypingInStartMenu;
    public WebSearchEngine SearchEngine { get; set; } = WebSearchEngine.Bing;
    public string EverythingInstanceName { get; set; } = string.Empty;
}

public sealed class AppSettingsService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunValueName = "EverythingonStart";

    private readonly string _settingsFilePath;

    public AppSettings Current { get; private set; } = new();

    public AppSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EverythingonStart"
        );
        Directory.CreateDirectory(dir);
        _settingsFilePath = Path.Combine(dir, "settings.json");
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_settingsFilePath))
            return;

        var json = File.ReadAllText(_settingsFilePath);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
        if (loaded != null)
        {
            if (loaded.Version < 2)
            {
                loaded.Version = 2;
                loaded.TriggerMode = WinKeyTriggerMode.InterceptTypingInStartMenu;
                Current = loaded;
                Save();
                return;
            }

            Current = loaded;
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsFilePath, json);
    }

    public bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
        return key?.GetValue(AppRunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
        if (key == null)
            return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                key.SetValue(AppRunValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppRunValueName, throwOnMissingValue: false);
        }
    }

    public string BuildWebSearchUrl(string query)
    {
        var encoded = Uri.EscapeDataString(query.Trim());
        return Current.SearchEngine switch
        {
            WebSearchEngine.Google => $"https://www.google.com/search?q={encoded}",
            WebSearchEngine.Baidu => $"https://www.baidu.com/s?wd={encoded}",
            _ => $"https://www.bing.com/search?q={encoded}"
        };
    }
}
