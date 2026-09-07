using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The G-Force tab's two v1.0.8 additions: the LEFT/RIGHT PREVIEW GRAPH beside the shake settings,
    /// and the TEST EFFECT panel at the bottom of the tab.
    /// <para/>
    /// Split into its own partial rather than added to the 2700-line <c>SettingsControl.xaml.cs</c>:
    /// both features are self-contained (they own their canvases, their timer and their drag state and
    /// nothing else reads them), and keeping them here means the main file's own concerns stay findable.
    /// </summary>
    public partial class SettingsControl
    {
        // ---- Preview graph geometry. Every number here is matched to the Lock/Slip curve plot rather
        //      than chosen independently, so the two graphs read as the same instrument. ----

        /// <summary>Same 2 px the Lock/Slip curve polyline uses.</summary>
        private const double PreviewStroke = 2.0;

        /// <summary>Same 5 px as <c>HorizontalTickHeight</c> on the Lock/Slip plot. (The owner recalled
        /// 8 px; the shipped value is 5, confirmed in the source before this was written.)</summary>
        private const double PreviewTick = 5.0;

        private const double PreviewMarginTop = 10.0;
        private const double PreviewMarginRight = 10.0;
        private const double PreviewMarginLeft = 34.0;    // room for the "100%" label
        private const double PreviewLabelGap = 3.0;       // clear space under the ms labels

        /// <summary>How far the 0 ms rule overshoots the 100% line, so it reads as an origin.</summary>
        private const double PreviewOriginOvershoot = 5.0;

        private static readonly Brush PreviewLeftBrush = Brushes.ForestGreen;
        private static readonly Brush PreviewRightBrush = Brushes.Crimson;
        private static readonly Brush PreviewAxisBrush = Brushes.Black;
        private static readonly Brush PreviewGridBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));

        // ---- Test Effect state. ----

        private DispatcherTimer _testTimer;
        private GForceEngine _testEngine;

        /// <summary>The throwaway settings the panel drives its engine from - NEVER the plugin's
        /// live object. See PushSimulatedFrame.</summary>
        private GForceSettings _testSettings;

        /// <summary>-1..+1 on each axis, in PAD coordinates: X is +1 at the right of the pad, Y is +1
        /// at its top. The top is BRAKING and the left is TURNING RIGHT, so both are negated on the
        /// way into telemetry - see PushSimulatedFrame.</summary>
        private double _testPadX, _testPadY;

        /// <summary>0..1 along the bar.</summary>
        private double _testWheel;

        private Ellipse _testPadBall, _testBarBall;
        private bool _testDraggingPad, _testDraggingBar;

        private const double TestBallDiameter = 22.0;
        private const double TestPadInset = 18.0;   // keeps the ball inside the drawn frame

        // =====================================================================================
        // PREVIEW GRAPH
        // =====================================================================================

        /// <summary>
        /// Redraws one second of the shake as the CURRENT settings would produce it - the same
        /// <see cref="GForceShake.FeelingPair"/> the engine calls, at the configured frequency, hold and
        /// feeling, so the picture cannot drift from the sound.
        /// </summary>
        private void RefreshShakePreview()
        {
            Canvas canvas = GForceShakePreviewCanvas;
            if (canvas == null) return;

            canvas.Children.Clear();

            double width = canvas.Width;
            double height = canvas.Height;
            double plotLeft = PreviewMarginLeft;
            double plotRight = width - PreviewMarginRight;
            double labelHeight = 14.0;
            double axisY = height - PreviewLabelGap - labelHeight - PreviewTick;
            double plotTop = PreviewMarginTop;

            // THE CURVE MUST NOT TOUCH EITHER RULE. Half a stroke of clearance at both ends keeps the
            // line fully between the 0 axis and the (invisible) 100% line, so neither is obscured.
            double half = PreviewStroke / 2.0;
            double zeroY = axisY - half;
            double fullY = plotTop + half;

            double frequency = GForceShakeFrequency.Value ?? 5.0;
            double hold = (GForceShakeSustain.Value ?? 0.0) / 100.0;
            ShakeFeeling feeling = SelectedShakeFeeling();
            double effectiveHold = GForceShake.EffectiveHold(hold, feeling);

            // ---- gridlines every 200 ms, labelled 0..800 (never 1000 - it sits on the right edge). ----
            for (int ms = 0; ms <= 1000; ms += 200)
            {
                double x = plotLeft + (plotRight - plotLeft) * (ms / 1000.0);
                bool isOrigin = ms == 0;

                canvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = isOrigin ? plotTop - PreviewOriginOvershoot : plotTop,
                    Y2 = axisY,
                    Stroke = isOrigin ? PreviewAxisBrush : PreviewGridBrush,
                    StrokeThickness = 1,
                });

                canvas.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = axisY, Y2 = axisY + PreviewTick,
                    Stroke = PreviewAxisBrush, StrokeThickness = 1,
                });

                if (ms == 1000) continue;   // the owner's explicit exception

                var label = new TextBlock
                {
                    Text = ms.ToString(CultureInfo.InvariantCulture) + "ms",
                    FontSize = 10,
                    Foreground = PreviewAxisBrush,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, x - (ms == 0 ? 0 : label.DesiredSize.Width / 2.0));
                Canvas.SetTop(label, axisY + PreviewTick);
                canvas.Children.Add(label);
            }

            // ---- the 0 rule, and the 100% marker on the left of the origin. ----
            canvas.Children.Add(new Line
            {
                X1 = plotLeft, X2 = plotRight, Y1 = axisY, Y2 = axisY,
                Stroke = PreviewAxisBrush, StrokeThickness = 1,
            });

            var fullLabel = new TextBlock { Text = "100%", FontSize = 10, Foreground = PreviewAxisBrush };
            fullLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(fullLabel, plotLeft - fullLabel.DesiredSize.Width - 3.0);
            Canvas.SetTop(fullLabel, plotTop - fullLabel.DesiredSize.Height / 2.0);
            canvas.Children.Add(fullLabel);

            // ---- the two traces. ----
            var left = new Polyline { Stroke = PreviewLeftBrush, StrokeThickness = PreviewStroke };
            var right = new Polyline { Stroke = PreviewRightBrush, StrokeThickness = PreviewStroke };

            const int samples = 400;
            for (int i = 0; i <= samples; i++)
            {
                double seconds = i / (double)samples;                 // exactly one second
                GForceShake.FeelingPair(frequency, seconds, effectiveHold, feeling,
                    out double l, out double r);

                double x = plotLeft + (plotRight - plotLeft) * seconds;
                left.Points.Add(new Point(x, zeroY + (fullY - zeroY) * l));
                right.Points.Add(new Point(x, zeroY + (fullY - zeroY) * r));
            }

            canvas.Children.Add(left);
            canvas.Children.Add(right);
        }

        private ShakeFeeling SelectedShakeFeeling()
            => ParseEnum(GetSelectedTag(GForceShakeFeelingCombo, ShakeFeeling.OppositePhase.ToString()),
                         ShakeFeeling.OppositePhase);

        /// <summary>
        /// A feeling change REWRITES the frequency to that feeling's own default and shows or hides the
        /// hold control - Blending pins its own hold, so leaving the spinner visible would invite an
        /// edit that does nothing. Called only from the dropdown, never from the load path.
        /// </summary>
        private void OnShakeFeelingChanged()
        {
            // SETS THE FREQUENCY, deliberately discarding a hand-tuned value - owner's explicit
            // instruction, 2026-09-06: "set the frequency as 10HZ (Even the user override to their own
            // frequency value)", and 5 Hz for Blending. Called only from the dropdown, never from the
            // load path, so a fresh install (which has never picked a feeling) keeps its shipped 5 Hz.
            //
            // The HOLD is not set here: Blending pins its own and RefreshShakeFeelingControls hides the
            // spinner for it, so there is no per-feeling hold value to hand the driver.
            GForceShakeFrequency.Value = GForceSettings.DefaultShakeFrequencyFor(SelectedShakeFeeling());
            RefreshShakeFeelingControls();
        }

        private void RefreshShakeFeelingControls()
        {
            using (_dirty.BeginLoading())
            {
                ShakeFeeling feeling = SelectedShakeFeeling();

                switch (feeling)
                {
                    case ShakeFeeling.SamePhase:
                        GForceShakeFeelingDesc.Text = Strings.Get("GForce.Shake.Feeling.Same.Desc");
                        break;
                    case ShakeFeeling.Blending:
                        GForceShakeFeelingDesc.Text = Strings.Get("GForce.Shake.Feeling.Blending.Desc");
                        break;
                    default:
                        GForceShakeFeelingDesc.Text = Strings.Get("GForce.Shake.Feeling.Opposite.Desc");
                        break;
                }

                Visibility holdVisibility = feeling == ShakeFeeling.Blending
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                GForceLblShakeSustain.Visibility = holdVisibility;
                GForceShakeSustain.Visibility = holdVisibility;
                GForceShakeSustainHint.Visibility = holdVisibility;

                RefreshShakePreview();
            }
        }

        // =====================================================================================
        // TEST EFFECT
        // =====================================================================================

        private void WireGForceTest()
        {
            // NOTHING IN HERE EVER CALLS MarkDirty (owner: "NO IMPACT when doing anything with the test
            // effect to the persisted settings"). These two handlers used to, from before the panel was
            // excluded from WireDirtyTracking's reflective sweep - so the exclusion was in place but this
            // hand-wiring quietly kept lighting up Apply anyway. The panel is a diagnostic harness: it is
            // never read by SaveToSettings, never written by LoadFromSettings, and must never make the
            // page look edited.
            GForceTestEnabled.Checked += (s, e) => RefreshGForceTestControls();
            GForceTestEnabled.Unchecked += (s, e) => RefreshGForceTestControls();
            GForceTestRate.ValueChanged += (s, e) => RestartTestTimer();

            // The panel is a diagnostic tool, not a persisted preference: it always opens OFF at 60 Hz
            // rather than remembering a previous session's state.
            using (_dirty.BeginLoading())
            {
                GForceTestRate.Value = 60.0;
                GForceTestLblLong.Text = Strings.Get("GForce.Test.Readout.Accel");
            }

            BuildTestPad();
            BuildTestBar();
            RefreshGForceTestControls();
        }

        /// <summary>
        /// THE PANEL EXISTS ONLY WHILE THE TOGGLE IS ON (owner's explicit requirement): every control and
        /// label is collapsed, and the timer that produces simulated frames is stopped outright rather
        /// than left running against a hidden panel.
        /// </summary>
        private void RefreshGForceTestControls()
        {
            using (_dirty.BeginLoading())
            {
                bool on = GForceTestEnabled.IsChecked == true;
                GForceTestPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

                if (on) RestartTestTimer();
                else StopTestTimer();
            }
        }

        private void RestartTestTimer()
        {
            StopTestTimer();
            if (GForceTestEnabled.IsChecked != true) return;

            double hz = ClampMath.Clamp(GForceTestRate.Value ?? 60.0, 1.0, 240.0);
            _testTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromSeconds(1.0 / hz),
            };
            _testTimer.Tick += (s, e) => PushSimulatedFrame();
            _testTimer.Start();
        }

        /// <summary>
        /// Stops the simulation AND HANDS THE PUBLISHED CHANNELS BACK. Both halves matter: without the
        /// release, switching the panel off - or simply closing the settings page - would leave
        /// <c>QAdvanceFeedback.GForce.*</c> pinned at whatever the ball was last touching, and the live
        /// pipeline permanently suppressed. Safe to call when nothing is running.
        /// </summary>
        private void StopTestTimer()
        {
            if (_testTimer != null)
            {
                _testTimer.Stop();
                _testTimer = null;
            }

            if (_plugin != null) _plugin.PublishTestEffect(null);
        }

        /// <summary>
        /// One simulated frame, through the REAL <see cref="GForceEngine"/> with EVERY G-Force setting
        /// on this page applied (owner's requirement).
        /// <para/>
        /// The settings are read straight out of the live controls into a throwaway
        /// <see cref="GForceSettings"/> and applied to the engine, which is the same object and the same
        /// call the plugin makes each frame - so output scales, cornering splits, sustains, transition
        /// scales, the maxima, the lateral direction, the shake mode/feeling/hold/frequency/trigger AND
        /// the lock/slip scales all reach the result exactly as they would in the car. Anything that
        /// changes the felt output in game changes it here.
        /// </summary>
        private void PushSimulatedFrame()
        {
            if (GForceTestEnabled.IsChecked != true) return;

            // EVERY G-FORCE SETTING ON THE PAGE, applied through the same ApplyTo the plugin uses - but
            // read into a THROWAWAY GForceSettings, never the plugin's live one. Calling SaveToSettings()
            // here (as this used to) pushed every uncommitted edit on the page into the running plugin
            // the moment the panel was switched on, which is an Apply the driver never asked for. The
            // owner's rule: nothing about the Test Effect is ever saved.
            if (_testSettings == null) _testSettings = new GForceSettings();
            SaveGForceToSettings(_testSettings);
            GForceSettings live = _testSettings;

            if (_testEngine == null) _testEngine = new GForceEngine();
            live.ApplyTo(_testEngine);

            double accelMax = live.FixedAccelMaxG;
            double decelMax = live.FixedDecelMaxG;
            double latMax = live.FixedLatMaxG;

            // PAD ORIENTATION (owner, 2026-09-06): UP IS BRAKING, DOWN IS ACCELERATING, and the LEFT of
            // the pad is TURNING RIGHT. The ball marks where the load is thrown: brake and you go
            // forward/up; turn right and you go to your left. `_testPadY` is +1 at the top of the pad,
            // so the LONGITUDINAL axis is read directly (top = positive = braking).
            //
            // THE LATERAL AXIS IS **NOT** NEGATED. The engine's convention is that a POSITIVE lateralG
            // biases the RIGHT pads (PairFromLevelAndBoost: right = level + signed), i.e. positive means
            // TURNING LEFT. The pad's -1 is its left edge, which is turning right, and turning right must
            // read negative - so `_testPadX` maps straight through. Negating it here (as a first cut did)
            // sent a full left drag in as "turning left" and lit up the RIGHT pads while the left sat at
            // zero, which is exactly backwards. The engine itself was never wrong.
            bool accelerating = _testPadY < 0.0;
            double longitudinalG = accelerating ? -_testPadY * accelMax : _testPadY * decelMax;
            double lateralG = _testPadX * latMax;

            // The wheel value drives BOTH channels; the engine applies the lock and slip scales itself,
            // so a scale change is felt here exactly as it is in the car.
            double wheel = ClampMath.To0100(_testWheel * 100.0);

            double speed = 100.0;
            var oldFrame = new TelemetryFrame(groundSpeedKmh: accelerating ? speed : speed + 1.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: accelerating ? speed + 1.0 : speed,
                longitudinalG: longitudinalG,
                lateralG: lateralG,
                brakePercent: accelerating ? 0.0 : 80.0,
                throttlePercent: accelerating ? 80.0 : 0.0);

            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow,
                _testTimer != null ? _testTimer.Interval : TimeSpan.FromMilliseconds(16));

            GForceOutput output = _testEngine.Compute(
                sample, accelMax, decelMax, wheelLockAll0100: wheel, wheelSlipAll0100: wheel,
                latMaxG: latMax);

            // OUT TO THE REAL PROPERTIES. Without this the panel moved its own readout and nothing
            // else: QAdvanceFeedback.GForce.* stayed null with no game running, so ShakeIt - the whole
            // reason the panel exists - received nothing to play.
            _plugin.PublishTestEffect(output);

            UpdateTestReadout(longitudinalG, lateralG, accelerating, wheel, output,
                accelerating ? accelMax : decelMax, latMax);
        }

        private void UpdateTestReadout(
            double longitudinalG, double lateralG, bool accelerating, double wheel, GForceOutput output,
            double longMax, double latMax)
        {
            GForceTestLblLong.Text = Strings.Get(accelerating
                ? "GForce.Test.Readout.Accel"
                : "GForce.Test.Readout.Decel");

            GForceTestValLong.Text = Format(longitudinalG, longMax);
            GForceTestValLat.Text = Format(lateralG, latMax);
            // SAME 6-WIDE RIGHT-ALIGNED FIELD as Format()'s leading number. Written with no width at
            // all, this one value started flush left while every other row was padded, so it was the
            // only line in the readout that did not line up.
            GForceTestValWheel.Text = string.Format(CultureInfo.InvariantCulture, "{0,6:F1}", wheel);

            GForceTestValFront.Text = Pair(output.BottomFrontLeft, output.BottomFrontRight);
            GForceTestValRear.Text = Pair(output.BottomRearLeft, output.BottomRearRight);
            GForceTestValLow.Text = Pair(output.BackLowLeft, output.BackLowRight);
            GForceTestValTop.Text = Pair(output.BackTopLeft, output.BackTopRight);
        }

        private static string Format(double g, double max)
        {
            double percent = max > 1e-9 ? Math.Abs(g) / max * 100.0 : 0.0;
            return string.Format(CultureInfo.InvariantCulture, "{0,6:F2}G ({1,5:F1}%)", Math.Abs(g), percent);
        }

        private static string Pair(double? left, double? right)
            => string.Format(CultureInfo.InvariantCulture, "{0,5:F1} / {1,5:F1}",
                             left ?? 0.0, right ?? 0.0);

        // ---- The two drag targets. ----

        private void BuildTestPad()
        {
            Canvas canvas = GForceTestPadCanvas;
            double size = canvas.Width;
            double inner = size - TestPadInset * 2.0;

            canvas.Children.Add(new Rectangle
            {
                Width = inner,
                Height = inner,
                RadiusX = 6,
                RadiusY = 6,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3F, 0x51, 0xB5)),
                StrokeThickness = 2,
                Fill = new LinearGradientBrush(
                    Color.FromRgb(0xFA, 0xFB, 0xFF), Color.FromRgb(0xEC, 0xEF, 0xF8), 90.0),
            });
            Canvas.SetLeft(canvas.Children[canvas.Children.Count - 1], TestPadInset);
            Canvas.SetTop(canvas.Children[canvas.Children.Count - 1], TestPadInset);

            AddAxis(canvas, TestPadInset, size / 2.0, size - TestPadInset, size / 2.0);
            AddAxis(canvas, size / 2.0, TestPadInset, size / 2.0, size - TestPadInset);

            // OWNER'S ORIENTATION (2026-09-06): the ball marks where the load is thrown, so the TOP is
            // braking and the BOTTOM is accelerating, and dragging LEFT means turning RIGHT.
            AddAxisLabel(canvas, Strings.Get("GForce.Test.Axis.TurnRight"), TestPadInset + 3, size / 2.0 - 16);
            AddAxisLabelRightAligned(canvas, Strings.Get("GForce.Test.Axis.TurnLeft"), size - TestPadInset - 3, size / 2.0 - 16);
            AddAxisLabel(canvas, Strings.Get("GForce.Test.Axis.Decel"), size / 2.0 + 5, TestPadInset + 2);
            AddAxisLabel(canvas, Strings.Get("GForce.Test.Axis.Accel"), size / 2.0 + 5, size - TestPadInset - 19);

            _testPadBall = MakeBall();
            canvas.Children.Add(_testPadBall);
            PositionPadBall();

            canvas.MouseLeftButtonDown += (s, e) =>
            {
                _testDraggingPad = true;
                canvas.CaptureMouse();
                DragPadTo(e.GetPosition(canvas));
            };
            canvas.MouseMove += (s, e) => { if (_testDraggingPad) DragPadTo(e.GetPosition(canvas)); };
            canvas.MouseLeftButtonUp += (s, e) =>
            {
                _testDraggingPad = false;
                canvas.ReleaseMouseCapture();
                SnapPadIfInsideDeadzone();
            };
        }

        private void BuildTestBar()
        {
            Canvas canvas = GForceTestBarCanvas;
            double width = canvas.Width;
            double height = canvas.Height;
            double barHeight = 26.0;
            double top = (height - barHeight) / 2.0;

            var bar = new Rectangle
            {
                Width = width - TestPadInset * 2.0,
                Height = barHeight,
                RadiusX = barHeight / 2.0,
                RadiusY = barHeight / 2.0,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3F, 0x51, 0xB5)),
                StrokeThickness = 2,
                Fill = new LinearGradientBrush(
                    Color.FromRgb(0xFA, 0xFB, 0xFF), Color.FromRgb(0xE6, 0xEA, 0xF6), 90.0),
            };
            canvas.Children.Add(bar);
            Canvas.SetLeft(bar, TestPadInset);
            Canvas.SetTop(bar, top);

            _testBarBall = MakeBall();
            canvas.Children.Add(_testBarBall);
            PositionBarBall();

            canvas.MouseLeftButtonDown += (s, e) =>
            {
                _testDraggingBar = true;
                canvas.CaptureMouse();
                DragBarTo(e.GetPosition(canvas));
            };
            canvas.MouseMove += (s, e) => { if (_testDraggingBar) DragBarTo(e.GetPosition(canvas)); };
            canvas.MouseLeftButtonUp += (s, e) =>
            {
                _testDraggingBar = false;
                canvas.ReleaseMouseCapture();
                SnapBarIfInsideDeadzone();
            };
        }

        private static Ellipse MakeBall() => new Ellipse
        {
            Width = TestBallDiameter,
            Height = TestBallDiameter,
            Stroke = new SolidColorBrush(Color.FromRgb(0x8E, 0x1B, 0x1B)),
            StrokeThickness = 1.5,
            Fill = new RadialGradientBrush(Color.FromRgb(0xFF, 0x8A, 0x80), Color.FromRgb(0xC6, 0x28, 0x28))
            {
                GradientOrigin = new Point(0.35, 0.30),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.75,
                RadiusY = 0.75,
            },
            Cursor = Cursors.SizeAll,
        };

        private static void AddAxis(Canvas canvas, double x1, double y1, double x2, double y2)
            => canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)),
                StrokeThickness = 1.5,
            });

        /// <summary>
        /// Places a label so its RIGHT edge lands on <paramref name="right"/>, by measuring it rather
        /// than assuming a width. The right-hand axis label used to be positioned with a hard-coded
        /// offset sized for the English "Turn left"; the far shorter Chinese wording then floated well
        /// clear of the frame edge.
        /// </summary>
        private static void AddAxisLabelRightAligned(Canvas canvas, string text, double right, double top)
        {
            TextBlock label = MakeAxisLabel(text);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, right - label.DesiredSize.Width);
            Canvas.SetTop(label, top);
            canvas.Children.Add(label);
        }

        private static TextBlock MakeAxisLabel(string text) => new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
        };

        private static void AddAxisLabel(Canvas canvas, string text, double left, double top)
        {
            TextBlock label = MakeAxisLabel(text);
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            canvas.Children.Add(label);
        }

        /// <summary>
        /// THE BALL CAN NEVER LEAVE ITS BOX. The travel is the inner frame inset by the ball's own
        /// radius, so its EDGE stops on the frame rather than its centre.
        /// </summary>
        private void DragPadTo(Point p)
        {
            Canvas canvas = GForceTestPadCanvas;
            double size = canvas.Width;
            double travel = (size - TestPadInset * 2.0 - TestBallDiameter) / 2.0;
            double centre = size / 2.0;

            _testPadX = ClampMath.Clamp((p.X - centre) / travel, -1.0, 1.0);
            _testPadY = ClampMath.Clamp((centre - p.Y) / travel, -1.0, 1.0);   // up is positive
            PositionPadBall();
        }

        private void DragBarTo(Point p)
        {
            Canvas canvas = GForceTestBarCanvas;
            double left = TestPadInset + TestBallDiameter / 2.0;
            double right = canvas.Width - TestPadInset - TestBallDiameter / 2.0;

            _testWheel = ClampMath.To01((p.X - left) / (right - left));
            PositionBarBall();
        }

        /// <summary>
        /// RE-CENTRE WHEN DROPPED INSIDE THE BALL'S OWN FOOTPRINT (owner's rule): a reading whose
        /// magnitude is under half a ball's width, on BOTH axes, snaps back to dead centre - so the
        /// simulation can be returned to zero without pixel-hunting.
        /// </summary>
        private void SnapPadIfInsideDeadzone()
        {
            double travel = (GForceTestPadCanvas.Width - TestPadInset * 2.0 - TestBallDiameter) / 2.0;
            double deadzone = (TestBallDiameter / 2.0) / travel;

            if (Math.Abs(_testPadX) < deadzone && Math.Abs(_testPadY) < deadzone)
            {
                _testPadX = 0.0;
                _testPadY = 0.0;
                PositionPadBall();
            }
        }

        private void SnapBarIfInsideDeadzone()
        {
            double left = TestPadInset + TestBallDiameter / 2.0;
            double right = GForceTestBarCanvas.Width - TestPadInset - TestBallDiameter / 2.0;
            double deadzone = (TestBallDiameter / 2.0) / (right - left);

            if (_testWheel < deadzone)
            {
                _testWheel = 0.0;
                PositionBarBall();
            }
        }

        private void PositionPadBall()
        {
            Canvas canvas = GForceTestPadCanvas;
            double size = canvas.Width;
            double travel = (size - TestPadInset * 2.0 - TestBallDiameter) / 2.0;
            double centre = size / 2.0;

            Canvas.SetLeft(_testPadBall, centre + _testPadX * travel - TestBallDiameter / 2.0);
            Canvas.SetTop(_testPadBall, centre - _testPadY * travel - TestBallDiameter / 2.0);
        }

        private void PositionBarBall()
        {
            Canvas canvas = GForceTestBarCanvas;
            double left = TestPadInset + TestBallDiameter / 2.0;
            double right = canvas.Width - TestPadInset - TestBallDiameter / 2.0;

            Canvas.SetLeft(_testBarBall, left + _testWheel * (right - left) - TestBallDiameter / 2.0);
            Canvas.SetTop(_testBarBall, (canvas.Height - TestBallDiameter) / 2.0);
        }
    }
}
