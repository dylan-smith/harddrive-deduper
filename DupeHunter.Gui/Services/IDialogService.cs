namespace DupeHunter.Gui.Services;

/// <summary>User-facing prompts, behind an interface so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>A yes/no question. Returns true when the user confirms.</summary>
    bool Confirm(string title, string message);

    /// <summary>
    /// A yes/no question about a destructive, irreversible action; rendered with a warning icon and
    /// defaulting to No so a stray Enter never deletes anything.
    /// </summary>
    bool ConfirmDanger(string title, string message);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);
}
