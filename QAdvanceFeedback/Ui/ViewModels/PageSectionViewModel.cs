using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// Base of every settings page tab / section view model. Properties read the draft
    /// (<see cref="S"/>) every time and write it through <see cref="Edit{T}"/>; when the draft is
    /// reloaded (a ShakeIt-side change adopted, defaults restored) every property is re-read.
    /// </summary>
    public abstract class PageSectionViewModel : ObservableObject
    {
        protected PageSectionViewModel(SettingsPageContext ctx)
        {
            Ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            // Same lifetime as the draft (both belong to the page view model), so no unsubscribe.
            Ctx.Draft.Reloaded += (s, e) => OnDraftReloaded(e);
        }

        protected SettingsPageContext Ctx { get; }

        /// <summary>The draft settings. Never cache parts of it.</summary>
        protected QAdvanceFeedbackSettings S => Ctx.Settings;

        /// <summary>Writes <paramref name="value"/> into the draft when it differs from
        /// <paramref name="current"/>, raises PropertyChanged for <paramref name="propertyName"/> and marks
        /// the draft edited. Returns whether it changed.</summary>
        protected bool Edit<T>(T current, T value, Action<T> write, [CallerMemberName] string propertyName = null)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (EqualityComparer<T>.Default.Equals(current, value)) return false;
            write(value);
            OnPropertyChanged(propertyName);
            Ctx.Draft.MarkEdited();
            return true;
        }

        /// <summary>Called on the UI thread after the draft was reloaded. The default re-reads every
        /// property (PropertyChanged with a null name); override to rebuild derived state, then call base.</summary>
        protected virtual void OnDraftReloaded(DraftReloadedEventArgs e) => OnPropertyChanged(null);
    }
}
