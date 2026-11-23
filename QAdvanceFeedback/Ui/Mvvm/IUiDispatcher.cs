using System;

namespace QAdvanceFeedback.Ui.Mvvm
{
    /// <summary>
    /// Runs work on the UI thread. View models receive hub events and plugin callbacks from SimHub's data
    /// thread and change their bound state only through this. The WPF implementation lives in Ui\Views;
    /// tests run the work inline.
    /// </summary>
    public interface IUiDispatcher
    {
        void Post(Action action);

        /// <summary>Runs <paramref name="action"/> after the current layout pass, so an Unloaded
        /// followed at once by a Loaded (a view being replaced) can be told apart from leaving.</summary>
        void PostLater(Action action);
    }
}
