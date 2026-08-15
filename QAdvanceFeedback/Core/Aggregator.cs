using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>How <see cref="Aggregator"/> combines 2 or 4 wheel values into one.</summary>
    public enum GroupMode { Max, Mean, WeightedMean, PNorm, Min }

    /// <summary>
    /// Combines per-wheel Raw values into the Front/Rear/Left/Right pairs and the single All value.
    /// Ported from the sibling ReliableWheelLockSlip project's Core/Aggregator.cs (same author, same
    /// problem: SimHub's legacy algorithm is car-level except for the left/right lateral halving -
    /// see <see cref="LegacySlipAlgorithm"/> - so aggregation is genuinely needed to produce
    /// Front/Rear/Left/Right/All, they are not natively provided). Default is p-norm rather than
    /// Max: Max steps discontinuously when the worst wheel changes, which is felt as a click through
    /// a shaker.
    /// <para/>
    /// Trimmed to just the Pair/Quad wheel-combining behaviour this project currently needs (no
    /// car-level/blend concept exists yet in Layers 1-3) - the full GroupMode surface is kept so a
    /// later settings layer can expose the choice, exactly as the sibling project does.
    /// </summary>
    public sealed class Aggregator
    {
        private readonly GroupMode _mode;
        private readonly double _p;
        private readonly Corners _weights;

        public Aggregator(GroupMode mode, double p, Corners weights)
        {
            _mode = mode;
            _p = Math.Max(1.0, p);
            _weights = weights;
        }

        public double Pair(int wheelA, int wheelB, Corners values)
            => Combine(values[wheelA], _weights[wheelA], values[wheelB], _weights[wheelB], 0.0, 0.0, 0.0, 0.0, 2);

        public double Quad(Corners values)
            => Combine(values[0], _weights[0], values[1], _weights[1],
                       values[2], _weights[2], values[3], _weights[3], 4);

        private double Combine(double v0, double w0, double v1, double w1,
                               double v2, double w2, double v3, double w3, int count)
        {
            v0 = ClampMath.To0100(v0); v1 = ClampMath.To0100(v1);
            v2 = ClampMath.To0100(v2); v3 = ClampMath.To0100(v3);

            switch (_mode)
            {
                case GroupMode.Max:
                {
                    double m = v0;
                    if (v1 > m) m = v1;
                    if (count > 2) { if (v2 > m) m = v2; if (v3 > m) m = v3; }
                    return ClampMath.To0100(m);
                }
                case GroupMode.Min:
                {
                    double m = v0;
                    if (v1 < m) m = v1;
                    if (count > 2) { if (v2 < m) m = v2; if (v3 < m) m = v3; }
                    return ClampMath.To0100(m);
                }
                case GroupMode.Mean:
                {
                    double sum = v0 + v1 + (count > 2 ? v2 + v3 : 0.0);
                    return ClampMath.To0100(sum / count);
                }
                case GroupMode.WeightedMean:
                {
                    double totalWeight = w0 + w1 + (count > 2 ? w2 + w3 : 0.0);
                    if (totalWeight <= 0.0) goto case GroupMode.Mean;
                    double sum = v0 * w0 + v1 * w1 + (count > 2 ? v2 * w2 + v3 * w3 : 0.0);
                    return ClampMath.To0100(sum / totalWeight);
                }
                default: // PNorm
                {
                    double totalWeight = w0 + w1 + (count > 2 ? w2 + w3 : 0.0);
                    if (totalWeight <= 0.0)
                    {
                        // Neutralise weights while preserving the p-norm shape (unlike WeightedMean,
                        // which falls back to plain mean). Keeps aggregation continuous: zero
                        // weights must not silently switch the aggregation method.
                        w0 = w1 = w2 = w3 = 1.0;
                        totalWeight = count;
                    }

                    double acc = w0 * Math.Pow(v0, _p) + w1 * Math.Pow(v1, _p);
                    if (count > 2) acc += w2 * Math.Pow(v2, _p) + w3 * Math.Pow(v3, _p);

                    double result = Math.Pow(acc / totalWeight, 1.0 / _p);
                    return ClampMath.To0100(result);
                }
            }
        }
    }
}
