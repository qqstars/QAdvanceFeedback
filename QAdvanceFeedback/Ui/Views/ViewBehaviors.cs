using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QAdvanceFeedback.Ui.Mvvm;

// Global namespace alias note: see WpfInfrastructure.cs (the plugin class shadows the QAdvanceFeedback
// namespace in WPF-generated code, so views live in QAdvanceFeedbackViews).
namespace QAdvanceFeedbackViews
{
    /// <summary>
    /// <c>v:ViewModelLifetime.Activate="True"</c> on a view: Loaded calls <see cref="IActivatable.Activate"/>
    /// and Unloaded calls <see cref="IActivatable.Deactivate"/> on the element's DataContext. Replaces the
    /// <c>Loaded += ... Activate</c> lines of code-behind, so a view's code-behind is only
    /// <c>InitializeComponent(); DataContext = viewModel;</c>.
    /// </summary>
    public static class ViewModelLifetime
    {
        public static readonly DependencyProperty ActivateProperty = DependencyProperty.RegisterAttached(
            "Activate", typeof(bool), typeof(ViewModelLifetime), new PropertyMetadata(false, OnActivateChanged));

        public static bool GetActivate(DependencyObject d) => (bool)d.GetValue(ActivateProperty);
        public static void SetActivate(DependencyObject d, bool value) => d.SetValue(ActivateProperty, value);

        private static void OnActivateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement fe)) return;
            fe.Loaded -= OnLoaded;
            fe.Unloaded -= OnUnloaded;
            if (!(bool)e.NewValue) return;
            fe.Loaded += OnLoaded;
            fe.Unloaded += OnUnloaded;
            if (fe.IsLoaded) OnLoaded(fe, null);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e) =>
            ViewGuard.Run(() => ((sender as FrameworkElement)?.DataContext as IActivatable)?.Activate());

        private static void OnUnloaded(object sender, RoutedEventArgs e) =>
            ViewGuard.Run(() => ((sender as FrameworkElement)?.DataContext as IActivatable)?.Deactivate());
    }

    /// <summary>
    /// Runs a bound command when a control event fires, for the few events that have no bindable property:
    /// <c>v:EventCommand.Loaded="{Binding RefreshCommand}"</c> on any FrameworkElement,
    /// <c>v:EventCommand.DropDownOpened="{Binding RefreshSourcesCommand}"</c> on a ComboBox. The command
    /// runs with a null parameter, inside <see cref="ViewGuard"/>.
    /// </summary>
    public static class EventCommand
    {
        public static readonly DependencyProperty LoadedProperty = DependencyProperty.RegisterAttached(
            "Loaded", typeof(ICommand), typeof(EventCommand), new PropertyMetadata(null, OnLoadedChanged));

        public static ICommand GetLoaded(DependencyObject d) => (ICommand)d.GetValue(LoadedProperty);
        public static void SetLoaded(DependencyObject d, ICommand value) => d.SetValue(LoadedProperty, value);

        public static readonly DependencyProperty DropDownOpenedProperty = DependencyProperty.RegisterAttached(
            "DropDownOpened", typeof(ICommand), typeof(EventCommand), new PropertyMetadata(null, OnDropDownOpenedChanged));

        public static ICommand GetDropDownOpened(DependencyObject d) => (ICommand)d.GetValue(DropDownOpenedProperty);
        public static void SetDropDownOpened(DependencyObject d, ICommand value) => d.SetValue(DropDownOpenedProperty, value);

        private static void OnLoadedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement fe)) return;
            fe.Loaded -= OnLoaded;
            if (e.NewValue != null) fe.Loaded += OnLoaded;
        }

        private static void OnDropDownOpenedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ComboBox cb)) return;
            cb.DropDownOpened -= OnDropDownOpened;
            if (e.NewValue != null) cb.DropDownOpened += OnDropDownOpened;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e) =>
            Execute(GetLoaded((DependencyObject)sender));

        private static void OnDropDownOpened(object sender, EventArgs e) =>
            Execute(GetDropDownOpened((DependencyObject)sender));

        private static void Execute(ICommand command) => ViewGuard.Run(() =>
        {
            if (command != null && command.CanExecute(null)) command.Execute(null);
        });
    }
}
