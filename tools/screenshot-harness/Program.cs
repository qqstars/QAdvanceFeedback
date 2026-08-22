using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using SimHub.Plugins;

namespace ScreenshotHarness
{
    /// <summary>
    /// See ScreenshotHarness.csproj's own header comment for the "why this exists" / "how to run
    /// it" summary, and docs\screenshot-styling-report.md (in the main repo) for the investigation
    /// this harness came out of.
    ///
    /// Merges MahApps.Metro's real resource dictionaries (the same ones SimHub itself merges, since
    /// SimHub's UI is built on MahApps) into a live WPF Application, hosts the real
    /// QAdvanceFeedback.Settings.SettingsControl inside a MahApps MetroWindow, and renders each tab
    /// to PNG per the standing capture rule in docs\architecture.md ("Settings screenshot capture
    /// rule"): Wheel Lock/Wheel Slip/G-Force capture ONLY the selected tab's own content (no tab
    /// strip, no Apply/Restore row); General captures the whole control (tab strip + content +
    /// button row).
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--dump-resources"))
                {
                    DumpResources();
                    return 0;
                }

                Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------------------------------
        // Diagnostic mode - lists every resource key actually available after merging MahApps'
        // dictionaries, so brush/key names used below are read off the real merged dictionaries
        // rather than assumed. Not needed for a normal screenshot run; kept as a maintenance aid
        // for future MahApps version bumps (a version bump could rename/retire these keys).
        // ------------------------------------------------------------------------------------
        private static void DumpResources()
        {
            var app = new Application();
            MergeMahAppsResources(app.Resources);

            var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectKeys(app.Resources, keys);
            foreach (var key in keys)
                Console.WriteLine(key);
            Console.WriteLine($"--- {keys.Count} total keys ---");

            string[] probe = { "WhiteBrush", "BlackBrush", "WindowBackgroundBrush", "WindowBrush", "ControlBackgroundBrush", "AccentColorBrush", "GrayBrush1", "LabelTextBrush", "TextBrush" };
            foreach (var name in probe)
            {
                var brush = app.Resources[name] as SolidColorBrush;
                Console.WriteLine($"{name} = {(brush != null ? brush.Color.ToString() : "N/A (not a SolidColorBrush or missing)")}");
            }
        }

        private static void CollectKeys(ResourceDictionary dict, ISet<string> into)
        {
            foreach (var key in dict.Keys)
                into.Add(Convert.ToString(key, CultureInfo.InvariantCulture));
            foreach (var merged in dict.MergedDictionaries)
                CollectKeys(merged, into);
        }

        // ------------------------------------------------------------------------------------
        // Resource merging - MahApps.Metro 1.5.0.23 (the version in lib\, matching SimHub
        // 9.11.22's own copy - see ..\fetch-simhub-refs.sh). Order matches the standard MahApps 1.x
        // App.xaml setup of that era (Controls, Fonts, then the base theme, then the accent).
        //
        // IMPORTANT, verified by direct BAML enumeration of lib\SimHub.Plugins.dll (see
        // docs\screenshot-styling-report.md): that assembly's own g.resources contain NO
        // "styles/simhubstyles.baml", "themes/generic.baml", "themes/genericshtitledgroup.baml" or
        // similar plugin-settings-chrome dictionaries - only per-custom-control default styles for
        // SimHub's OWN controls (ShMetroWindow, ShDayNightToggle, etc.), none of which
        // SettingsControl.xaml uses (it only uses plain WPF controls + mah:NumericUpDown/
        // mah:ToggleSwitch - confirmed by grepping its xmlns/control usage). So SimHub's dark
        // settings-panel look, for THIS control, comes entirely from MahApps' own implicit styles
        // (Controls.xaml defines `<Style TargetType="{x:Type TextBox}">` etc. keyed by type, which
        // SettingsControl.xaml's own `BasedOn="{StaticResource {x:Type TextBox}}"` styles pick up
        // automatically) - no SimHub.Plugins.dll dictionaries need to be (or can be) merged here.
        // ------------------------------------------------------------------------------------
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

            foreach (var source in sources)
            {
                try
                {
                    var dict = new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) };
                    into.MergedDictionaries.Add(dict);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to load resource dictionary '{source}' - see inner exception. " +
                        "This is exactly the 'dictionary throws on load' failure mode the task asked " +
                        "to diagnose rather than paper over.", ex);
                }
            }
        }

        // ------------------------------------------------------------------------------------
        // Main capture run
        // ------------------------------------------------------------------------------------
        private static void Run()
        {
            string repoRoot = FindRepoRoot();
            string imagesDir = Path.Combine(repoRoot, "docs", "images");
            Directory.CreateDirectory(imagesDir);
            // 1.0.6.0 (docs\release-1060-report.md, Part I) - BUG FIX: this used to compute imagesDir
            // above and then never actually write anything there, instead writing only to a
            // bin-directory-local "screenshot-out" folder that nothing ever copied out of docs\images -
            // silently leaving the real docs\images\settings-*.png files stale despite this tool
            // claiming (in this file's own header comment and the csproj's) to regenerate them. Writes
            // go directly to docs\images now, which is what a caller diffing/hash-checking that folder
            // actually needs.
            string outDir = imagesDir;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            MergeMahAppsResources(app.Resources);

            // Pin English regardless of the host machine's locale - "Max Grip" (Curve.Anchor.Critical)
            // must render correctly and reproducibly (see StringTableEn.cs).
            QAdvanceFeedback.Core.Localization.Strings.Culture = CultureInfo.GetCultureInfo("en-US");

            var plugin = new QAdvanceFeedback.QAdvanceFeedback();
            var settingsField = typeof(QAdvanceFeedback.QAdvanceFeedback).GetField(
                "_settings", BindingFlags.NonPublic | BindingFlags.Instance);
            if (settingsField == null)
                throw new InvalidOperationException("QAdvanceFeedback._settings field not found - the plugin class shape changed; update this harness.");
            settingsField.SetValue(plugin, new QAdvanceFeedback.Settings.QAdvanceFeedbackSettings());

            // PluginManager's real constructor loads WoteverCommon.dll (a SimHub-internal assembly
            // not in lib\ and not part of this plugin's own reference set - it is only ever supplied
            // by a real running SimHub host process). Constructing it normally
            // (`new PluginManager()`) throws FileNotFoundException standalone. Bypassing the
            // constructor with GetUninitializedObject gives SettingsControl a real, correctly-typed
            // PluginManager reference with default/zeroed fields - every call this control makes
            // through it (MotorsExportAvailabilityProvider.SafeGet) is already wrapped in try/catch
            // and degrades to "not available" on failure (see MotorsExportAvailabilityProvider's own
            // remarks), which is the same "no live SimHub telemetry" state any offscreen harness is
            // in anyway.
            var pluginManager = (PluginManager)FormatterServices.GetUninitializedObject(typeof(PluginManager));
            var control = new QAdvanceFeedback.Settings.SettingsControl(plugin, pluginManager);

            // Host in a real MahApps MetroWindow - consistent with how a SimHub-hosted plugin's
            // settings control lives inside SimHub's own (MahApps-based) dark-themed window chrome,
            // rather than a bare default Window that would leave the implicit styles resolvable but
            // the surrounding chrome/background unstyled. Off-screen (large negative Left/Top) +
            // ShowInTaskbar=false so nothing visibly pops over the user's desktop.
            var window = new MetroWindow
            {
                Title = "QAdvanceFeedback",
                Content = control,
                Width = 1048,
                Height = 900,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
                ResizeMode = ResizeMode.NoResize,
            };

            window.Show();
            PumpDispatcher();

            var mainTabs = (TabControl)control.FindName("MainTabs");
            if (mainTabs == null)
                throw new InvalidOperationException("Could not find MainTabs in SettingsControl's namescope.");

            CaptureTabContent(control, mainTabs, "WheelLockTab", Path.Combine(outDir, "settings-wheel-lock.png"));
            CaptureTabContent(control, mainTabs, "WheelSlipTab", Path.Combine(outDir, "settings-wheel-slip.png"));
            // Filename is "settings-gforce.png" (NOT "settings-g-force.png") - see docs\architecture.md's
            // own "Settings screenshot capture rule" remarks: the README links (and every other
            // settings-*.png in this folder) already use the no-hyphen form.
            CaptureTabContent(control, mainTabs, "GForceTab", Path.Combine(outDir, "settings-gforce.png"));
            CaptureWholeControl(control, mainTabs, "GeneralTab", Path.Combine(outDir, "settings-general.png"));

            window.Close();
            app.Shutdown();

            Console.WriteLine("Rendered PNGs written to: " + outDir);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Could not locate QAdvanceFeedback.sln above " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static void PumpDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        // Wheel Lock / Wheel Slip / G-Force: selected tab's own ScrollViewer content only (no tab
        // strip, no Apply/Restore row) - see docs\architecture.md's capture rule.
        private static void CaptureTabContent(UserControl control, TabControl mainTabs, string tabName, string outputPath)
        {
            var tab = (TabItem)control.FindName(tabName);
            if (tab == null)
                throw new InvalidOperationException($"Could not find TabItem '{tabName}'.");

            mainTabs.SelectedItem = tab;
            control.UpdateLayout();
            PumpDispatcher();
            control.UpdateLayout();

            // Search from mainTabs, NOT from the TabItem itself - a TabItem's own visual tree only
            // holds its header chrome; the selected tab's Content is realized by the TabControl's
            // OWN template (a ContentPresenter bound to SelectedContent), so the ScrollViewer only
            // appears in the visual tree rooted at the TabControl.
            var scrollViewer = FindVisualChild<ScrollViewer>(mainTabs);
            if (scrollViewer == null)
                throw new InvalidOperationException(
                    $"No ScrollViewer found under tab '{tabName}' after selecting it - capture rule " +
                    "requires one (see docs\\architecture.md). Refusing to fall back to a clipped or " +
                    "chrome-included capture.");

            var content = scrollViewer.Content as FrameworkElement;
            if (content == null)
                throw new InvalidOperationException($"Tab '{tabName}': ScrollViewer.Content is not a FrameworkElement.");

            double width = scrollViewer.ActualWidth > 0 ? scrollViewer.ActualWidth : 1024;
            ApplyDarkBackground(content);
            RenderElementToPng(content, width, outputPath);
        }

        // General: whole control (tab strip + General's content + Apply/Restore row) - see
        // docs\architecture.md's capture rule.
        private static void CaptureWholeControl(FrameworkElement control, TabControl mainTabs, string tabName, string outputPath)
        {
            var tab = (TabItem)((UserControl)control).FindName(tabName);
            if (tab == null)
                throw new InvalidOperationException($"Could not find TabItem '{tabName}'.");

            mainTabs.SelectedItem = tab;
            control.UpdateLayout();
            PumpDispatcher();
            control.UpdateLayout();

            ApplyDarkBackground(control);

            double width = control.ActualWidth > 0 ? control.ActualWidth : 1024;
            RenderElementToPng(control, width, outputPath);
        }

        // Gives the captured element itself an opaque, theme-correct dark background before
        // rendering. Required because RenderTargetBitmap renders ONLY the given element and its
        // descendants - an ancestor Window's themed Background does not "show through" for an
        // element with no Background of its own (typically Transparent), which is exactly the "bare
        // control with no themed parent keeps default [transparent/white] brushes" failure mode the
        // task warned about. WhiteBrush is MahApps' own (confusingly-named, but verified via
        // --dump-resources) key for the app/window background - it resolves to a near-black colour
        // under the BaseDark theme merged above, not literal white.
        private static void ApplyDarkBackground(FrameworkElement element)
        {
            var brush = element.TryFindResource("WhiteBrush") as Brush;
            if (element is Panel panel)
                panel.Background = brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            else if (element is Control ctl)
                ctl.Background = brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static void RenderElementToPng(FrameworkElement element, double width, string outputPath)
        {
            element.Measure(new Size(width, double.PositiveInfinity));
            double height = element.DesiredSize.Height;
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            PumpDispatcher();

            int pixelWidth = (int)Math.Ceiling(width);
            int pixelHeight = (int)Math.Ceiling(height);
            if (pixelWidth <= 0 || pixelHeight <= 0)
                throw new InvalidOperationException($"Refusing to render '{outputPath}' at {pixelWidth}x{pixelHeight}.");

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96d, 96d, PixelFormats.Pbgra32);
            rtb.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = new FileStream(outputPath, FileMode.Create))
                encoder.Save(fs);

            Console.WriteLine($"{Path.GetFileName(outputPath)}: {pixelWidth}x{pixelHeight}");
        }
    }
}
