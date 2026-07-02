namespace DupeHunter;

/// <summary>
/// Reports a long operation as a sequence of discrete, timed steps. Each step occupies its own line; the
/// one in progress shows an animated spinner so the user can see the program is alive, and when it
/// finishes (because the next step begins, or the reporter is disposed) its line is finalized with a
/// check mark and the wall-clock time that step took.
/// </summary>
public interface IStepProgress
{
    /// <summary>
    /// Finalize the current step (printing its elapsed time) and begin a new one on a fresh line.
    /// </summary>
    void BeginStep(string label);

    /// <summary>
    /// Update the in-progress step's label in place — e.g. a running count — without starting a new
    /// line or resetting its timer. Only visible on an interactive console; ignored when redirected.
    /// </summary>
    void UpdateStep(string label);
}
