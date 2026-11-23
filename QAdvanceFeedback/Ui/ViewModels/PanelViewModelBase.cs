using System;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// A ShakeIt panel view model that listens to the settings hub while its view is on screen.
    /// ShakeIt builds a NEW view each time it shows the editor, and every view shares the container's
    /// view model, so loaded views are COUNTED:
    /// <list type="bullet">
    /// <item><see cref="Activate"/> (a view loaded) starts the view model with the first view.</item>
    /// <item><see cref="Deactivate"/> (a view unloaded) stops it only when no view is left - checked
    /// after the current layout pass, so a view being replaced does not stop a running test.</item>
    /// </list>
    /// Hub events arrive on any thread and are handled on the UI thread.
    /// </summary>
    public abstract class PanelViewModelBase : ObservableObject, IActivatable
    {
        private SettingsHub _subscribedTo;
        private int _views;

        protected PanelViewModelBase(IPanelHost host, ITicker ticker, IUiDispatcher ui)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
            Ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        protected IPanelHost Host { get; }
        protected ITicker Ticker { get; }
        protected IUiDispatcher Ui { get; }

        public bool IsActive { get; private set; }

        public void Activate()
        {
            _views++;
            if (IsActive) return;
            IsActive = true;
            _subscribedTo = Host.Hub;
            if (_subscribedTo != null) _subscribedTo.Changed += OnHubChangedAnyThread;
            OnActivated();
        }

        public void Deactivate()
        {
            if (_views > 0) _views--;
            if (_views > 0) return;
            Ui.PostLater(() => { if (_views == 0) StopNow(); });
        }

        private void StopNow()
        {
            if (!IsActive) return;
            IsActive = false;
            if (_subscribedTo != null) _subscribedTo.Changed -= OnHubChangedAnyThread;
            _subscribedTo = null;
            OnDeactivated();
        }

        protected abstract void OnActivated();
        protected abstract void OnDeactivated();
        protected abstract void OnHubChanged(SettingsChangedEventArgs e);

        private void OnHubChangedAnyThread(object sender, SettingsChangedEventArgs e) =>
            Ui.Post(() => { if (IsActive) OnHubChanged(e); });
    }
}
