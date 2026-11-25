using System;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using MockHost.ViewModels;

namespace MockHost.Views
{
    public partial class MainWindow : MetroWindow
    {
        /// <summary>The plugin's settings page is a control SimHub hosts, not a view model, so it is placed here,
        /// the way SimHub places it in its own window - and placed again (a new one) when the language changes.</summary>
        internal MainWindow(MainViewModel viewModel, Func<Control> pluginSettings)
        {
            InitializeComponent();
            DataContext = viewModel;
            SettingsHost.Content = pluginSettings();
            viewModel.Language.Changed += () => SettingsHost.Content = pluginSettings();
        }
    }
}
