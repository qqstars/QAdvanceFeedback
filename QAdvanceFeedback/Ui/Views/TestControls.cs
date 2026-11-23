using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedbackViews
{
    /// <summary>Shared look of the Test Effect drag targets (ported from the plugin page).</summary>
    internal static class TestLook
    {
        public const double BallDiameter = 22.0;
        public const double Inset = 18.0;   // keeps the ball inside the drawn frame

        public static Ellipse MakeBall() => new Ellipse
        {
            Width = BallDiameter,
            Height = BallDiameter,
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
            IsHitTestVisible = false,
        };

        public static Brush FrameStroke => new SolidColorBrush(Color.FromRgb(0x3F, 0x51, 0xB5));

        // Looked up again until found: Gong may load after the first control (the plugin page first).
        private static DependencyProperty _dragSourceIgnore;

        /// <summary>
        /// Marks <paramref name="control"/> (and, the property inherits, its ball and frame) as
        /// GongSolutions' DragSourceIgnore (owner, 2026-11-20): SimHub's ShakeIt effect list is a Gong drag
        /// source, and without this a press on the pad or bar starts a row drag after a few pixels, which
        /// takes the mouse away from the control. Gong decides at the press, so moves outside the control
        /// cannot start one either. Gong ships only inside SimHub, so the property is found by name; without
        /// it (the plugin page outside SimHub) this does nothing.
        /// </summary>
        public static void IgnoreListDrag(DependencyObject control)
        {
            DependencyProperty p = _dragSourceIgnore ?? (_dragSourceIgnore = FindDragSourceIgnore());
            if (p != null) control.SetValue(p, true);
        }

        private static DependencyProperty FindDragSourceIgnore()
        {
            try
            {
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType("GongSolutions.Wpf.DragDrop.DragDrop", false);
                    if (t?.GetField("DragSourceIgnoreProperty")?.GetValue(null) is DependencyProperty p) return p;
                }
            }
            catch (Exception) { }
            return null;
        }
    }

    /// <summary>
    /// Backstop so a release OUTSIDE the pad or bar always ends the drag (owner, 2026-11-20). The press
    /// captures the mouse, so the control normally gets the button-up wherever the cursor is; if the capture
    /// is refused or taken away, the window's own button-up (tunnelling, seen first) or the window losing
    /// focus ends it instead. Watched only while a drag is held.
    /// </summary>
    internal sealed class WindowReleaseWatch
    {
        private Window _window;
        private Action _end;

        public void Start(DependencyObject control, Action end)
        {
            Stop();
            _window = Window.GetWindow(control);
            if (_window == null) return;
            _end = end;
            _window.PreviewMouseLeftButtonUp += OnButtonUp;
            _window.Deactivated += OnDeactivated;
        }

        public void Stop()
        {
            if (_window == null) return;
            _window.PreviewMouseLeftButtonUp -= OnButtonUp;
            _window.Deactivated -= OnDeactivated;
            _window = null;
        }

        private void OnButtonUp(object sender, MouseButtonEventArgs e) => _end?.Invoke();
        private void OnDeactivated(object sender, EventArgs e) => _end?.Invoke();
    }

    /// <summary>
    /// The G-ball pad: drag the ball to set <see cref="PadX"/> / <see cref="PadY"/> (-1..+1, two-way).
    /// Top = braking, bottom = accelerating, left = turning right. Dropped inside the ball's own
    /// footprint, it snaps back to the centre.
    /// </summary>
    public sealed class GBallPad : Canvas
    {
        public static readonly DependencyProperty PadXProperty = DependencyProperty.Register(nameof(PadX), typeof(double), typeof(GBallPad),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((GBallPad)d).PositionBall()));
        public static readonly DependencyProperty PadYProperty = DependencyProperty.Register(nameof(PadY), typeof(double), typeof(GBallPad),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((GBallPad)d).PositionBall()));

        public double PadX { get => (double)GetValue(PadXProperty); set => SetValue(PadXProperty, value); }
        public double PadY { get => (double)GetValue(PadYProperty); set => SetValue(PadYProperty, value); }

        private const double Size = 260.0;
        private readonly Ellipse _ball = TestLook.MakeBall();
        private readonly TestDragHold _hold = new TestDragHold();
        private readonly WindowReleaseWatch _release = new WindowReleaseWatch();

        public GBallPad()
        {
            Width = Size;
            Height = Size;
            Background = Brushes.Transparent;
            double inner = Size - TestLook.Inset * 2.0;

            var frame = new Rectangle
            {
                Width = inner, Height = inner, RadiusX = 6, RadiusY = 6,
                Stroke = TestLook.FrameStroke, StrokeThickness = 2,
                Fill = new LinearGradientBrush(Color.FromRgb(0xFA, 0xFB, 0xFF), Color.FromRgb(0xEC, 0xEF, 0xF8), 90.0),
            };
            SetLeft(frame, TestLook.Inset);
            SetTop(frame, TestLook.Inset);
            Children.Add(frame);

            AddAxis(TestLook.Inset, Size / 2.0, Size - TestLook.Inset, Size / 2.0);
            AddAxis(Size / 2.0, TestLook.Inset, Size / 2.0, Size - TestLook.Inset);

            AddLabel(Strings.Get("GForce.Test.Axis.TurnRight"), TestLook.Inset + 3, Size / 2.0 - 16, rightAligned: false);
            AddLabel(Strings.Get("GForce.Test.Axis.TurnLeft"), Size - TestLook.Inset - 3, Size / 2.0 - 16, rightAligned: true);
            AddLabel(Strings.Get("GForce.Test.Axis.Decel"), Size / 2.0 + 5, TestLook.Inset + 2, rightAligned: false);
            AddLabel(Strings.Get("GForce.Test.Axis.Accel"), Size / 2.0 + 5, Size - TestLook.Inset - 19, rightAligned: false);

            Children.Add(_ball);
            PositionBall();
            TestLook.IgnoreListDrag(this);
        }

        private double Travel => (Size - TestLook.Inset * 2.0 - TestLook.BallDiameter) / 2.0;

        // Held only while the left button is down (TestDrag); outside the pad the ball stays at the edge.
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _hold.Press();
            CaptureMouse();
            _release.Start(this, EndDrag);   // a release outside the control still ends the drag
            DragTo(e.GetPosition(this));
            e.Handled = true;   // the surrounding ShakeIt list must not take the press (and the capture)
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_hold.IsHeld) return;
            if (_hold.Move(e.LeftButton == MouseButtonState.Pressed)) DragTo(e.GetPosition(this));
            else EndDrag();   // the button-up went elsewhere
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;   // ours even when the window watch already ended the drag
            EndDrag();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            EndDrag();
        }

        private void EndDrag()
        {
            if (!_hold.Release()) return;
            _release.Stop();
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (TestDragMath.PadSnapsToCentre(PadX, PadY, TestLook.BallDiameter / 2.0, Travel)) { PadX = 0.0; PadY = 0.0; }
        }

        private void DragTo(Point p)
        {
            double centre = Size / 2.0;
            PadX = TestDragMath.PadAxis(p.X - centre, Travel);
            PadY = TestDragMath.PadAxis(centre - p.Y, Travel);   // up is positive
        }

        private void PositionBall()
        {
            double centre = Size / 2.0;
            SetLeft(_ball, centre + PadX * Travel - TestLook.BallDiameter / 2.0);
            SetTop(_ball, centre - PadY * Travel - TestLook.BallDiameter / 2.0);
        }

        private void AddAxis(double x1, double y1, double x2, double y2) => Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)),
            StrokeThickness = 1.5,
        });

        private void AddLabel(string text, double x, double top, bool rightAligned)
        {
            var label = new TextBlock { Text = text, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)) };
            if (rightAligned)
            {
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                x -= label.DesiredSize.Width;
            }
            SetLeft(label, x);
            SetTop(label, top);
            Children.Add(label);
        }
    }

    /// <summary>The Lock/Slip (and Slip Angle) bar: drag the ball to set <see cref="Value"/> (0..1, two-way).
    /// Released near the left end, it snaps to 0.</summary>
    public sealed class WheelBar : Canvas
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(WheelBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((WheelBar)d).PositionBall()));

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

        private const double BarWidth = 320.0, BarHeight = 60.0, TrackHeight = 26.0;
        private readonly Ellipse _ball = TestLook.MakeBall();
        private readonly TestDragHold _hold = new TestDragHold();
        private readonly WindowReleaseWatch _release = new WindowReleaseWatch();

        public WheelBar()
        {
            Width = BarWidth;
            Height = BarHeight;
            Background = Brushes.Transparent;
            var bar = new Rectangle
            {
                Width = BarWidth - TestLook.Inset * 2.0, Height = TrackHeight,
                RadiusX = TrackHeight / 2.0, RadiusY = TrackHeight / 2.0,
                Stroke = TestLook.FrameStroke, StrokeThickness = 2,
                Fill = new LinearGradientBrush(Color.FromRgb(0xFA, 0xFB, 0xFF), Color.FromRgb(0xE6, 0xEA, 0xF6), 90.0),
            };
            SetLeft(bar, TestLook.Inset);
            SetTop(bar, (BarHeight - TrackHeight) / 2.0);
            Children.Add(bar);
            Children.Add(_ball);
            PositionBall();
            TestLook.IgnoreListDrag(this);
        }

        private double Left0 => TestLook.Inset + TestLook.BallDiameter / 2.0;
        private double Right1 => BarWidth - TestLook.Inset - TestLook.BallDiameter / 2.0;

        // Same press-and-hold rules as the pad (TestDrag); past either end the ball stays at that end.
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _hold.Press();
            CaptureMouse();
            _release.Start(this, EndDrag);   // a release outside the control still ends the drag
            DragTo(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_hold.IsHeld) return;
            if (_hold.Move(e.LeftButton == MouseButtonState.Pressed)) DragTo(e.GetPosition(this));
            else EndDrag();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;   // ours even when the window watch already ended the drag
            EndDrag();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            EndDrag();
        }

        private void EndDrag()
        {
            if (!_hold.Release()) return;
            _release.Stop();
            if (IsMouseCaptured) ReleaseMouseCapture();
            Value = TestDragMath.BarSnap(Value, TestLook.BallDiameter / 2.0, Right1 - Left0);
        }

        private void DragTo(Point p) => Value = TestDragMath.Bar01(p.X, Left0, Right1);

        private void PositionBall()
        {
            SetLeft(_ball, Left0 + Value * (Right1 - Left0) - TestLook.BallDiameter / 2.0);
            SetTop(_ball, (BarHeight - TestLook.BallDiameter) / 2.0);
        }
    }

    /// <summary>
    /// The read-only "Source value -> Projected value" graph: grid, the green dashed curve
    /// (<see cref="Curve"/>, value coordinates 0..100) and the Test Effect balls (<see cref="Balls"/>).
    /// </summary>
    public sealed class SourceProjectedGraph : Canvas
    {
        public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(nameof(Curve), typeof(IReadOnlyList<GraphPoint>),
            typeof(SourceProjectedGraph), new PropertyMetadata(null, (d, e) => ((SourceProjectedGraph)d).Redraw()));
        public static readonly DependencyProperty BallsProperty = DependencyProperty.Register(nameof(Balls), typeof(IReadOnlyList<TestBall>),
            typeof(SourceProjectedGraph), new PropertyMetadata(null, (d, e) => ((SourceProjectedGraph)d).Redraw()));

        public IReadOnlyList<GraphPoint> Curve { get => (IReadOnlyList<GraphPoint>)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
        public IReadOnlyList<TestBall> Balls { get => (IReadOnlyList<TestBall>)GetValue(BallsProperty); set => SetValue(BallsProperty, value); }

        private const double W = 420, H = 160, BallSize = 13.0;

        public SourceProjectedGraph()
        {
            Width = W;
            Height = H;
            Background = Brushes.White;
            ClipToBounds = true;
            Redraw();
        }

        private void Redraw()
        {
            Children.Clear();
            GraphFurniture.AddGrid(Children, W, H);

            IReadOnlyList<GraphPoint> curve = Curve;
            if (curve != null && curve.Count > 0)
            {
                var points = new PointCollection();
                foreach (GraphPoint p in curve) points.Add(new Point(p.X / 100.0 * W, H - p.Y / 100.0 * H));
                Children.Add(new Polyline { Points = points, Stroke = Brushes.Green, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 6, 3 } });
            }

            IReadOnlyList<TestBall> balls = Balls;
            if (balls == null) return;
            foreach (TestBall b in balls)
            {
                Color c = BaseColour(b.Colour);
                var ball = new Ellipse
                {
                    Width = BallSize, Height = BallSize,
                    Stroke = new SolidColorBrush(Darken(c)), StrokeThickness = 1,
                    Fill = new RadialGradientBrush(Lighten(c), c) { GradientOrigin = new Point(0.35, 0.30), RadiusX = 0.75, RadiusY = 0.75 },
                };
                SetLeft(ball, ClampMath.To0100(b.Source0100) / 100.0 * W - BallSize / 2.0);
                SetTop(ball, H - ClampMath.To0100(b.Projected0100) / 100.0 * H - BallSize / 2.0);
                Children.Add(ball);
            }
        }

        private static Color BaseColour(BallColour c)
        {
            switch (c)
            {
                case BallColour.BrightRed: return Color.FromRgb(0xFF, 0x52, 0x52);
                case BallColour.DarkRed: return Color.FromRgb(0xB7, 0x1C, 0x1C);
                case BallColour.BrightBlue: return Color.FromRgb(0x64, 0xB5, 0xF6);
                case BallColour.DarkBlue: return Color.FromRgb(0x0D, 0x47, 0xA1);
                case BallColour.Red: return Color.FromRgb(0xE5, 0x39, 0x35);
                case BallColour.Blue: return Color.FromRgb(0x1E, 0x88, 0xE5);
                case BallColour.BrightGrey: return Color.FromRgb(0xD0, 0xD0, 0xD0);
                case BallColour.DarkGrey: return Color.FromRgb(0x61, 0x61, 0x61);
                default: return Color.FromRgb(0xFB, 0x8C, 0x00);
            }
        }

        private static Color Lighten(Color c) => Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));
        private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6));
    }
}
