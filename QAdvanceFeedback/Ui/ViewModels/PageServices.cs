namespace QAdvanceFeedback.Ui.ViewModels
{
    // Services the settings page view models call for things only the view layer can do
    // (docs\full-mvvm-plan.md §3.2). WPF / SimHub implementations live in Integration\ or Ui\Views; tests
    // use fakes.

    /// <summary>Modal messages.</summary>
    public interface IDialogService
    {
        /// <summary>A yes/no question (a warning); true when the driver answered yes.</summary>
        bool Confirm(string message, string title);

        /// <summary>An informational message with an OK button.</summary>
        void Inform(string message, string title);
    }

    /// <summary>The system clipboard.</summary>
    public interface IClipboard
    {
        void SetText(string text);
    }

    /// <summary>SimHub's property picker.</summary>
    public interface IPropertyPicker
    {
        /// <summary>The picked property, or null when cancelled.</summary>
        string Pick(string current);
    }

    /// <summary>SimHub's script (NCalc / JavaScript) editor.</summary>
    public interface IScriptEditor
    {
        /// <summary>The edited script, or null when cancelled.</summary>
        string Edit(string currentScript, string title);
    }
}
