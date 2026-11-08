using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EverythingonStart.Services;

// Side-effect-free packaging probe: no settings, tray, hotkeys, windows, or Everything connection.
public static class PackageDiagnosticsService
{
    public static int Run(TextWriter output)
    {
        var assembly = typeof(PackageDiagnosticsService).Assembly;
        var native = new Dictionary<string, bool>();
        foreach (string name in new[] { "Everything3.dll", "Everything64.dll" })
        {
            bool loaded = NativeLibrary.TryLoad(name, assembly, null, out var handle);
            native[name] = loaded;
            if (loaded) NativeLibrary.Free(handle);
        }
        using var tray = AppIconService.CreateTrayIcon();
        bool icon = AppIconService.WindowIcon != null && tray.Width > 0;
        string[] licenses = assembly.GetManifestResourceNames().Where(name => name.StartsWith("EverythingonStart.Licenses.", StringComparison.Ordinal)).ToArray();
        bool presentationFence = PopupWindowPreparationService.IsAvailable;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            application = "EverythingonStart", version = assembly.GetName().Version?.ToString(3),
            nativeLibraries = native, iconAvailable = icon, embeddedLicenseCount = licenses.Length,
            presentationFenceAvailable = presentationFence,
            externalManagedDllPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "EverythingonStart.dll"))
        }));
        return native.Values.All(value => value) && icon && licenses.Length >= 8 && presentationFence ? 0 : 1;
    }

    public static void PrintLicenses(TextWriter output)
    {
        var assembly = typeof(PackageDiagnosticsService).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("EverythingonStart.Licenses.", StringComparison.Ordinal)).Order())
        {
            output.WriteLine($"\n=== {name} ===\n");
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            output.WriteLine(reader.ReadToEnd());
        }
    }
}
