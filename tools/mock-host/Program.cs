using System;
using System.IO;
using System.Windows;
using MockHost.Sim;
using MockHost.ViewModels;
using MockHost.Views;

namespace MockHost
{
    /// <summary>
    /// Runs the QAdvanceFeedback plugin the way SimHub does, without SimHub (docs\mock-host.md). See MockHost.csproj
    /// for how to build and start it.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // --capture <folder> [seconds]: save each tab as a PNG and exit (docs and checks; see WindowCapture).
            // The optional seconds (default 2) is how long each page runs before it is saved.
            string captureDir = args.Length >= 2 && args[0] == "--capture" ? Path.GetFullPath(args[1]) : null;
            double settle = args.Length >= 3 && double.TryParse(args[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0 ? seconds : 2.0;
            // SimHub's storage paths are relative (PluginsData\Common\<file>): running from the exe's own folder puts
            // every file the plugin writes in <exe folder>\PluginsData\Common, as in a SimHub install.
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string common = Path.Combine("PluginsData", "Common");
            Directory.CreateDirectory(common);

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            MergeMahAppsResources(app.Resources);
            // A UI fault is written to MockHost.log and shown, not a silent exit: this is a test bench.
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log(e.Exception);
                if (captureDir == null) MessageBox.Show(e.Exception.ToString(), "MockHost", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);

            var language = new LanguageViewModel(Path.Combine(common, LanguageViewModel.FileName));

            using (var runner = new PluginRunner())
            {
                // Whatever the plugin's Init reported (its settings page shows the same), with the full detail.
                foreach (var h in QAdvanceFeedback.Core.Health.HealthRegistry.Snapshot())
                    Log("Health after Init: " + h.Subsystem + " (" + h.Severity + "): " + h.Detail);
                var simulation = new GameSimulationViewModel(runner, Path.Combine(common, GameSimulationViewModel.FileName));
                var shakeIt = new ShakeItEffectsViewModel(Path.Combine(common, ShakeItEffectsViewModel.FileName));
                bool first = true;
                var window = new MainWindow(new MainViewModel(shakeIt, simulation, language), () =>
                {
                    if (first) { first = false; return runner.CreateSettingsControl(); }
                    return runner.RecreateSettingsControl();
                });
                window.Closing += (s, e) =>
                {
                    shakeIt.Save();
                    simulation.Save();
                };
                if (captureDir != null) WindowCapture.CaptureTabsThenClose(window, captureDir, TimeSpan.FromSeconds(settle));
                runner.Start();
                return app.Run(window);
            }   // Dispose: stops the DataUpdate thread, then the plugin's End (which saves its files)
        }

        private static void Log(Exception e) => Log(e?.ToString());

        private static void Log(string text)
        {
            try { File.AppendAllText("MockHost.log", DateTime.Now.ToString("s") + " " + text + Environment.NewLine); }
            catch (Exception) { /* nothing more to do */ }
        }

        // SimHub's UI is MahApps-based: the plugin's page and editors pick up these implicit styles, as in SimHub
        // (the same dictionaries tools\screenshot-harness merges).
        private static void MergeMahAppsResources(ResourceDictionary into)
        {
            string[] sources =
            {
                "pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml",
                "pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml",
                "pack://application:,,,/MahApps.Metro;component/Styles/Colors.xaml",
                "pack://application:,,,/MahApps.Metro;component/Styles/Accents/BaseDark.xaml",
                "pack://application:,,,/MahApps.Metro;component/Styles/Accents/Blue.xaml",
            };
            foreach (string source in sources)
                into.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) });
        }
    }
}
