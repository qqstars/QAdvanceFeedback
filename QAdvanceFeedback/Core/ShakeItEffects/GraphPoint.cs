namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>A point on a graph canvas (y grows downwards, as in WPF).</summary>
    public readonly struct GraphPoint
    {
        public GraphPoint(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
    }
}
