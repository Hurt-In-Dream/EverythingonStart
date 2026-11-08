namespace EverythingonStart.Services;

/// <summary>Waits for a rendered cover before transferring focus; never injects keyboard input.</summary>
public sealed class PopupHandoverController(
    Func<bool> canContinue,
    Func<bool> presentFrame,
    Action activate,
    Action cancel)
{
    private TimeSpan? _lastFrame;
    private int _coveredFrames;
    private bool _coverRendered;
    public bool IsPending { get; private set; } = true;

    public void MarkCoverReady()
    {
        if (!IsPending) return;
        _coverRendered = true;
        _lastFrame = null;
        _coveredFrames = 0;
    }

    public void OnFrame(TimeSpan timestamp)
    {
        if (!IsPending) return;
        if (!canContinue()) { Cancel(); return; }
        if (!_coverRendered) return;
        if (_lastFrame != null && timestamp <= _lastFrame.Value) return;
        _lastFrame = timestamp;
        // Rendering fires BEFORE submission. The third distinct event allows two cover
        // frames to be submitted; the first can still contain cold HWND/layout preparation.
        // Cold render-thread preparation is completed off-screen at application startup.
        if (++_coveredFrames < 3 || !presentFrame()) return;
        // Foreground may change while the compositor fence is waiting.
        if (!canContinue()) { Cancel(); return; }
        IsPending = false;
        activate();
    }

    public void Cancel()
    {
        if (!IsPending) return;
        IsPending = false;
        cancel();
    }
}
