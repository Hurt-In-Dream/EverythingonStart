using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EverythingonStart.Services;

/// <summary>Completes off-screen render preparation before keyboard interception starts.</summary>
public static class PopupWindowPreparationService
{
    // .NET 10 WPF has no public presentation fence: Rendering and ContentRendered
    // precede asynchronous channel completion. Keep this runtime-dependent bridge
    // isolated and fail closed if its signatures disappear in a future runtime.
    private static readonly Type? MediaContext = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext");
    private static readonly MethodInfo? FromDispatcher = MediaContext?.GetMethod("From",
        BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(Dispatcher)], null);
    private static readonly MethodInfo? CompleteRender = MediaContext?.GetMethod("CompleteRender",
        BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
    private static readonly PropertyInfo? Channel = MediaContext?.GetProperty("Channel", BindingFlags.Instance | BindingFlags.NonPublic);
    public static bool IsAvailable => FromDispatcher != null && CompleteRender != null && Channel != null;

    public static bool TryPresent(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (!window.IsVisible || !IsAvailable) return false;
        try
        {
            var context = FromDispatcher!.Invoke(null, [window.Dispatcher]);
            if (context == null || Channel!.GetValue(context) == null) return false;
            CompleteRender!.Invoke(context, null);
            return DwmFlush() >= 0;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or
            EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    public static bool PreWarm(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (window.IsVisible) return false;
        double left = window.Left, top = window.Top;
        bool showActivated = window.ShowActivated;
        bool contentRendered = false, ready = false, fenceQueued = false;
        int frames = 0;
        TimeSpan? lastFrame = null;
        var loop = new DispatcherFrame();
        var deadline = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        EventHandler content = (_, _) => contentRendered = true;
        EventHandler rendering = (_, args) =>
        {
            if (fenceQueued || !contentRendered || args is not RenderingEventArgs frame ||
                lastFrame != null && frame.RenderingTime <= lastFrame.Value) return;
            lastFrame = frame.RenderingTime;
            if (++frames < 3) return;
            fenceQueued = true;
            window.Dispatcher.BeginInvoke(() =>
            {
                // A queued callback may outlive a timeout; never touch a closed/hidden target.
                if (!loop.Continue || !window.IsVisible) return;
                ready = TryPresent(window);
                loop.Continue = false;
            }, DispatcherPriority.Input);
        };
        deadline.Tick += (_, _) => loop.Continue = false;
        window.ContentRendered += content;
        CompositionTarget.Rendering += rendering;
        try
        {
            window.Left = -10000;
            window.Top = -10000;
            window.ShowActivated = false;
            window.Show();
            window.UpdateLayout();
            deadline.Start();
            Dispatcher.PushFrame(loop);
            return ready;
        }
        finally
        {
            loop.Continue = false;
            deadline.Stop();
            window.ContentRendered -= content;
            CompositionTarget.Rendering -= rendering;
            window.Hide();
            window.Left = left;
            window.Top = top;
            window.ShowActivated = showActivated;
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
