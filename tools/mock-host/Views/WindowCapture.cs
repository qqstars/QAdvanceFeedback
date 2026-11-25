using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MockHost.ViewModels;

namespace MockHost.Views
{
    /// <summary>
    /// <c>MockHost.exe --capture &lt;folder&gt;</c>: once the window is up, show each tab in turn (on ShakeIt Effects,
    /// each added effect), let the plugin's live panels tick for a moment, save the window's content as
    /// mockhost-&lt;n&gt;-&lt;name&gt;.png, then close (which saves everything, as a normal exit does).
    /// </summary>
    internal static class WindowCapture
    {
        public static void CaptureTabsThenClose(Window window, string folder, TimeSpan settle)
        {
            Directory.CreateDirectory(folder);
            window.ContentRendered += (s, e) =>
            {
                var tabs = (TabControl)window.FindName("Tabs");
                var shakeIt = ((MainViewModel)window.DataContext).ShakeIt;
                var steps = new List<Tuple<string, Action>>
                {
                    Step("plugin-settings", () => tabs.SelectedIndex = 0),
                    Step("plugin-slip-angle", () => { tabs.SelectedIndex = 0; ShowPluginTab(window, "SlipAngleTab"); }),
                };
                foreach (EffectItemViewModel effect in shakeIt.Effects)
                {
                    EffectItemViewModel item = effect;
                    steps.Add(Step("shakeit-" + item.Type.Effect.ToString().ToLowerInvariant(), () => { tabs.SelectedIndex = 1; shakeIt.SelectedEffect = item; }));
                }
                if (shakeIt.Effects.Count == 0) steps.Add(Step("shakeit-effects", () => tabs.SelectedIndex = 1));
                steps.Add(Step("game-simulation", () => tabs.SelectedIndex = 2));
                steps.Add(Step("language", () => tabs.SelectedIndex = 3));
                // A live switch to the other language (the page and the editors are rebuilt), then back again.
                var language = ((MainViewModel)window.DataContext).Language;
                LanguageOption start = language.Selected, other = language.Options[language.Options[0] == start ? 1 : 0];
                steps.Add(Step("switched-plugin-settings", () => { language.Selected = other; tabs.SelectedIndex = 0; }));
                if (shakeIt.Effects.Count > 0)
                    steps.Add(Step("switched-shakeit", () => { tabs.SelectedIndex = 1; shakeIt.SelectedEffect = shakeIt.Effects[0]; }));
                steps.Add(Step("switched-back", () => { language.Selected = start; tabs.SelectedIndex = 0; }));

                int index = 0;
                steps[0].Item2();
                var timer = new DispatcherTimer { Interval = settle };
                timer.Tick += (_, __) =>
                {
                    Save((FrameworkElement)window.Content, Path.Combine(folder, "mockhost-" + (index + 1) + "-" + steps[index].Item1 + ".png"));
                    index++;
                    if (index < steps.Count) steps[index].Item2();
                    else { timer.Stop(); window.Close(); }
                };
                timer.Start();
            };
        }

        // The plugin page's own tab (its TabItem x:Names, as tools\screenshot-harness uses them).
        private static void ShowPluginTab(Window window, string name)
        {
            var host = (ContentControl)window.FindName("SettingsHost");
            var page = (host?.Content as FrameworkElement)?.FindName("Page") as FrameworkElement;
            if (page?.FindName("MainTabs") is TabControl inner && page.FindName(name) is TabItem item) inner.SelectedItem = item;
        }

        private static Tuple<string, Action> Step(string name, Action show) => Tuple.Create(name, show);

        private static void Save(FrameworkElement content, string path)
        {
            int w = (int)Math.Ceiling(content.ActualWidth), h = (int)Math.Ceiling(content.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                Brush background = content.TryFindResource("WhiteBrush") as Brush ?? Brushes.Black;
                dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
