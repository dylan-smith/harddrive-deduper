using System.Diagnostics;

namespace DupeHunter;

/// <summary>
/// Console implementation of <see cref="IStepProgress"/>. On an interactive console a background timer
/// repaints the current step's line in place a few times a second so the spinner animates while a step
/// blocks; completed steps stay on screen with their timing. When output is redirected, each step is
/// written once (on completion) as a plain <c>label (1.2s)</c> line, so logs stay readable rather than
/// filling with spinner frames.
/// </summary>
/// <remarks>
/// In-place repaint uses ANSI cursor control (see <see cref="ConsoleVt"/>); the same fallback as
/// <see cref="MultiProgress"/> applies when that isn't available.
/// </remarks>
internal sealed class StepProgress : IStepProgress, IAsyncDisposable
{
    private const string ClearLine = "\r[K"; // carriage-return, then erase to end of line
    private static readonly char[] Frames = ['|', '/', '-', '\\'];
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(125);

    private readonly bool _live;
    private readonly object _lock = new();
    private readonly Timer? _timer;

    // Current step state, all guarded by _lock. _label is null when no step is active (before the first
    // BeginStep and after the last step is finalized).
    private string? _label;
    private long _stepStartTicks;
    private int _frame;

    public StepProgress(string header)
    {
        Console.WriteLine(header);
        _live = !Console.IsOutputRedirected && ConsoleVt.TryEnable();
        if (_live)
        {
            _timer = new Timer(_ => Tick(), null, Interval, Interval);
        }
    }

    public void BeginStep(string label)
    {
        lock (_lock)
        {
            FinalizeCurrent();
            _label = label;
            _stepStartTicks = Stopwatch.GetTimestamp();
            _frame = 0;

            if (_live)
            {
                Paint();
            }
            else
            {
                // Open the line now; it's completed with its elapsed time when the step finalizes.
                Console.Write("  " + Trim(label));
            }
        }
    }

    public void UpdateStep(string label)
    {
        lock (_lock)
        {
            _label = label;
            if (_live)
            {
                Paint(); // repaint immediately so a fast loop's count keeps up between timer ticks
            }
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (_label is null)
            {
                return;
            }

            _frame++;
            Paint();
        }
    }

    /// <summary>Repaint the in-progress step's line in place (live console only). Caller holds the lock.</summary>
    private void Paint() => Console.Write($"{ClearLine}  {Frames[_frame % Frames.Length]} {_label}");

    /// <summary>Complete the current step's line with a check mark and its elapsed time. Caller holds the lock.</summary>
    private void FinalizeCurrent()
    {
        if (_label is null)
        {
            return;
        }

        var elapsed = FormatElapsed(Stopwatch.GetElapsedTime(_stepStartTicks));
        Console.Write(_live
            ? $"{ClearLine}  ✓ {Trim(_label)} ({elapsed})\n"
            : $" ({elapsed})\n");
        _label = null;
    }

    public async ValueTask DisposeAsync()
    {
        // Await the timer's disposal so no repaint is in flight, then finalize the last step.
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        lock (_lock)
        {
            FinalizeCurrent();
        }
    }

    /// <summary>Drop a trailing ellipsis so a completed step doesn't read as still ongoing.</summary>
    private static string Trim(string label) => label.TrimEnd('…', '.', ' ');

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}s" : $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
}
