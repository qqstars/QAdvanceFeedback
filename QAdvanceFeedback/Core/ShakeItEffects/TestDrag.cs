namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// Press-and-hold state of a Test Effect drag target (the G-Force pad, the Lock/Slip and Slip Angle
    /// bars; owner, 2026-11-20): the ball is held ONLY while the left button is down. It is let go the
    /// moment the button is released - by the button-up itself, by a mouse move that finds the button
    /// already up (the button-up went to someone else), or by a lost mouse capture - and each let-go
    /// reports exactly once, so the snap runs once.
    /// </summary>
    public sealed class TestDragHold
    {
        public bool IsHeld { get; private set; }

        public void Press() => IsHeld = true;

        /// <summary>True when the ball should follow this move; a move with the button up ends the hold
        /// (check <see cref="IsHeld"/> afterwards, or call <see cref="Release"/> to get the one let-go).</summary>
        public bool Move(bool leftButtonDown)
        {
            if (!IsHeld) return false;
            return leftButtonDown;
        }

        /// <summary>Ends the hold. True only for the call that actually let the ball go.</summary>
        public bool Release()
        {
            bool was = IsHeld;
            IsHeld = false;
            return was;
        }
    }

    /// <summary>Geometry of the Test Effect drag targets: positions past the edge stay at the furthest
    /// reachable position, and a release near the rest position snaps to it.</summary>
    public static class TestDragMath
    {
        /// <summary>One pad axis, -1..+1: <paramref name="offsetPx"/> from the centre over the ball's
        /// travel, clamped (outside the pad the ball stays at the edge).</summary>
        public static double PadAxis(double offsetPx, double travelPx) =>
            travelPx > 0.0 && ClampMath.IsFinite(offsetPx) ? ClampMath.Clamp(offsetPx / travelPx, -1.0, 1.0) : 0.0;

        /// <summary>A release within one ball radius of the centre (on both axes) snaps to the centre.</summary>
        public static bool PadSnapsToCentre(double x, double y, double ballRadiusPx, double travelPx)
        {
            if (!(travelPx > 0.0)) return true;
            double deadzone = ballRadiusPx / travelPx;
            return System.Math.Abs(x) < deadzone && System.Math.Abs(y) < deadzone;
        }

        /// <summary>The bar value 0..1 at <paramref name="px"/>, clamped to the bar's ends.</summary>
        public static double Bar01(double px, double left0Px, double right1Px)
        {
            double span = right1Px - left0Px;
            return span > 0.0 ? ClampMath.To01((px - left0Px) / span) : 0.0;
        }

        /// <summary>A release within one ball radius of the left end snaps to 0.</summary>
        public static double BarSnap(double value, double ballRadiusPx, double spanPx) =>
            spanPx > 0.0 && value < ballRadiusPx / spanPx ? 0.0 : value;
    }
}
