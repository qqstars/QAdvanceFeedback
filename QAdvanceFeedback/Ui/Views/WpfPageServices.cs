using System;
using System.Windows;
using QAdvanceFeedback.Ui.ViewModels;

// Root namespace outside QAdvanceFeedback.* - see WpfInfrastructure.cs.
namespace QAdvanceFeedbackViews
{
    /// <summary>The settings page's modal messages, as the old page showed them: a confirm is a Yes / No
    /// MessageBox with the warning icon (Restore all defaults), an inform an OK MessageBox.</summary>
    public sealed class WpfDialogService : IDialogService
    {
        public bool Confirm(string message, string title) =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        public void Inform(string message, string title) =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The system clipboard. A locked clipboard (another app mid-copy) is swallowed, as the old
    /// page's health "Copy details" did - a settings button must never throw.</summary>
    public sealed class WpfClipboard : IClipboard
    {
        public void SetText(string text)
        {
            try { Clipboard.SetText(text ?? string.Empty); }
            catch (Exception) { /* clipboard can be locked by another process - never let this throw */ }
        }
    }
}
