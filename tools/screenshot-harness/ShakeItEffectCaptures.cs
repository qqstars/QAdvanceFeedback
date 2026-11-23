using System;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using QAdvanceFeedback.Integration.ShakeIt;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedbackViews;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;

namespace ScreenshotHarness
{
    /// <summary>
    /// Renders the native QAdvanceFeedback ShakeIt effects' editors (docs\shakeit-native-effects-design.md)
    /// to docs\images\shakeit-*.png, English and Simplified Chinese.
    /// <para/>
    /// Each image is the effect's own settings panel with, for WheelLock/WheelSlip, the read-only
    /// projection graph that ShakeIt shows in its response-curve slot - i.e. exactly the controls this
    /// plugin contributes to ShakeIt's effect editor. ShakeIt's surrounding editor chrome (gain bar,
    /// output/frequency settings, channel mapping) belongs to SimHub and needs a running SimHub with an
    /// output device, so it is not part of these images.
    /// </summary>
    internal static class ShakeItEffectCaptures
    {
        /// <summary>The effects (and the page's Clutch tab) reach the plugin through
        /// QAdvanceFeedback.Instance/Hub, exactly as in SimHub.</summary>
        public static void Prepare(QAdvanceFeedback.QAdvanceFeedback plugin)
        {
            SetProperty(plugin, "Instance", plugin, isStatic: true);
            SetProperty(plugin, "Hub", new SettingsHub(() => plugin.Settings, _ => { }), isStatic: false);
        }

        public static void Run(QAdvanceFeedback.QAdvanceFeedback plugin, string outDir)
        {
            Prepare(plugin);

            // Show both key data states: WheelLock on Auto (SMax/S90/S75 read-only, the shipped
            // reference since nothing is learned offscreen), WheelSlip on Manual with SMax only.
            WheelChannelSettings lockCh = plugin.Settings.Lock, slipCh = plugin.Settings.Slip;
            lockCh.NormalizePattern = QAdvanceFeedback.Core.Normalized.NormalizePattern.Mapping;
            lockCh.KeyDataPoints.SetAutoGenerate(KeyDataPanelModel.Identity(lockCh), true);
            slipCh.NormalizePattern = QAdvanceFeedback.Core.Normalized.NormalizePattern.MaxGripOnly;
            slipCh.KeyDataPoints.SetAutoGenerate(KeyDataPanelModel.Identity(slipCh), false);

            foreach (var culture in new[] { ("en-US", ""), ("zh-Hans", ".zh-Hans") })
            {
                QAdvanceFeedback.Core.Localization.Strings.Culture = CultureInfo.GetCultureInfo(culture.Item1);

                CaptureWheel<QAdvanceFeedbackWheelLockContainer>(EffectRoutingNames.Mono, null,
                    Path.Combine(outDir, "shakeit-wheellock" + culture.Item2 + ".png"));
                CaptureWheel<QAdvanceFeedbackWheelSlipContainer>(EffectRoutingNames.Mono, null,
                    Path.Combine(outDir, "shakeit-wheelslip" + culture.Item2 + ".png"));
                // The Lock Test Effect running: Front/Rear routing, so the Front Channel / Rear Channel
                // group sliders (docs\slip-angle-design.md 6) show above the four wheel sliders, and the
                // two output balls sit on the curve.
                CaptureWheel<QAdvanceFeedbackWheelLockContainer>(EffectRoutingNames.FrontRear, new[] { 85.0, 70.0, 40.0, 20.0 },
                    Path.Combine(outDir, "shakeit-wheellock-test" + culture.Item2 + ".png"));
                var gforce = new GForceEffectViewModel(PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher());
                Capture(Compose(new GForceEffectView(gforce), null), Path.Combine(outDir, "shakeit-gforce" + culture.Item2 + ".png"));
                // The G-Force Test Effect running: the pad, then the Lock/Slip and Slip Angle bars under it.
                var gforceTest = new GForceEffectViewModel(PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher());
                Capture(Compose(new GForceEffectView(gforceTest), null), Path.Combine(outDir, "shakeit-gforce-test" + culture.Item2 + ".png"),
                    () => { gforceTest.Test.IsTestEnabled = true; gforceTest.Test.PadX = 0.4; gforceTest.Test.PadY = 0.5; gforceTest.Test.Wheel = 0.6; gforceTest.Test.Angle = 0.3; });
                gforceTest.Test.Stop();

                // Clutch Bite: idle, then the Test Effect running from the slider at 42 (no pedal axis here).
                var clutch = new ClutchBiteViewModel(PluginPanelHost.Default, PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher());
                Capture(Compose(new ClutchBiteView(clutch), null), Path.Combine(outDir, "shakeit-clutch" + culture.Item2 + ".png"));
                var clutchTest = new ClutchBiteViewModel(PluginPanelHost.Default, PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher());
                Capture(Compose(new ClutchBiteView(clutchTest), null), Path.Combine(outDir, "shakeit-clutch-test" + culture.Item2 + ".png"),
                    () => { clutchTest.Test.IsTestEnabled = true; clutchTest.Test.Slider = 42; });
                clutchTest.Test.Stop();

                // Slip Angle: the panel with its degrees -> projected graph below.
                var slipAngle = new SlipAngleEffectViewModel(PluginPanelHost.Default, PluginPanelHost.Default, new DispatcherTicker(), new WpfUiDispatcher());
                Capture(Compose(new SlipAngleEffectView(slipAngle), new SlipAngleGraphView(slipAngle)),
                    Path.Combine(outDir, "shakeit-slipangle" + culture.Item2 + ".png"));
            }

            QAdvanceFeedback.Core.Localization.Strings.Culture = CultureInfo.GetCultureInfo("en-US");
        }

        // SimHub's ShakeIt container base class cannot be constructed outside SimHub (its constructor
        // loads WoteverLocalization.dll, which only a SimHub install has). The views only read and write
        // the container's routing, so they get an unconstructed container whose routing field is set.
        // Its view model (internal, created lazily) feeds both the panel and the graph, as in SimHub.
        private static void CaptureWheel<TContainer>(string routing, double[] testSliders, string outputPath)
            where TContainer : QAdvanceFeedbackWheelChannelContainerBase
        {
            var container = (TContainer)FormatterServices.GetUninitializedObject(typeof(TContainer));
            FieldInfo mode = typeof(EffectsContainerBase).GetField("aggregationMode", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mode == null) throw new InvalidOperationException("EffectsContainerBase.aggregationMode not found - update this harness.");
            mode.SetValue(container, routing);
            PropertyInfo vmProperty = typeof(QAdvanceFeedbackWheelChannelContainerBase).GetProperty("ViewModel", BindingFlags.NonPublic | BindingFlags.Instance);
            if (vmProperty == null) throw new InvalidOperationException("ViewModel not found - update this harness.");
            var vm = (WheelChannelEffectViewModel)vmProperty.GetValue(container);

            var panel = new WheelChannelEffectView(vm);
            var graph = new GraphView(vm.Graph);
            Action beforeCapture = null;
            if (testSliders != null)
            {
                vm.Test.FrontLeft = testSliders[0]; vm.Test.FrontRight = testSliders[1];
                vm.Test.RearLeft = testSliders[2]; vm.Test.RearRight = testSliders[3];
                beforeCapture = () => vm.Test.IsTestEnabled = true;
            }
            Capture(Compose(panel, graph), outputPath, beforeCapture);
            vm.Test.Stop();
        }

        private static void SetProperty(object target, string name, object value, bool isStatic)
        {
            PropertyInfo p = typeof(QAdvanceFeedback.QAdvanceFeedback).GetProperty(name,
                BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance));
            if (p == null) throw new InvalidOperationException("QAdvanceFeedback." + name + " not found - update this harness.");
            p.SetValue(isStatic ? null : target, value);
        }

        // The effect's own settings panel, then (for WheelLock/WheelSlip) the graph ShakeIt shows in
        // its response-curve slot, in the order the ShakeIt editor stacks them.
        private static FrameworkElement Compose(FrameworkElement panel, FrameworkElement curve)
        {
            var stack = new StackPanel { Width = 600 };
            stack.Children.Add(panel);
            if (curve != null) stack.Children.Add(curve);
            // Padding (not Margin): RenderTargetBitmap draws the element itself, so a margin would be
            // outside the image and the content would touch its edges.
            return new Border { Padding = new Thickness(12), Child = stack };
        }

        private static void Capture(FrameworkElement content, string outputPath, Action beforeCapture = null)
        {
            var window = new MetroWindow
            {
                Title = "ShakeIt",
                Content = content,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -5000,
                Top = -5000,
                ResizeMode = ResizeMode.NoResize,
            };
            window.Show();
            Pump();
            beforeCapture?.Invoke();
            PumpFor(TimeSpan.FromMilliseconds(600));   // let MahApps' toggle-switch animations finish
            content.UpdateLayout();
            Pump();

            Brush background = content.TryFindResource("WhiteBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            if (content is Border border) border.Background = background;
            else if (content is Panel panel) panel.Background = background;

            content.Measure(new Size(content.ActualWidth, double.PositiveInfinity));
            double height = content.DesiredSize.Height;
            content.Arrange(new Rect(0, 0, content.ActualWidth, height));
            content.UpdateLayout();
            Pump();

            int w = (int)Math.Ceiling(content.ActualWidth), h = (int)Math.Ceiling(height);
            if (w <= 0 || h <= 0) throw new InvalidOperationException($"Refusing to render '{outputPath}' at {w}x{h}.");
            var rtb = new RenderTargetBitmap(w, h, 96d, 96d, PixelFormats.Pbgra32);
            rtb.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = new FileStream(outputPath, FileMode.Create)) encoder.Save(fs);
            Console.WriteLine($"{Path.GetFileName(outputPath)}: {w}x{h}");

            window.Close();
        }

        private static void PumpFor(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = duration };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
