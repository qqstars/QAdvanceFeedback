using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// WHAT ONE HERTZ MEANS (owner, 2026-09-05): "a channel getting from Max-Min-Max took one loop,
    /// 10Hz means one channel getting from Max-Min-Max for 10 times but not halved as 5 times."
    /// <para/>
    /// The concern is real for any design where the two pads alternate: it would be easy to build one
    /// in which a full LEFT-RIGHT-LEFT pan counts as the cycle, so each individual pad only completes
    /// Max-Min-Max half as often as the configured frequency. These tests pin that this is NOT what
    /// happens - each pad, on its own, completes one full travel per 1/f second - and they cover every
    /// mode at once because all of them route through <c>GForceShake.ApplyBand</c>.
    /// </summary>
    public class ShakeFrequencyDefinitionTests
    {
        /// <summary>Counts upward crossings of the series' own mean - one per Max-Min-Max cycle,
        /// robust to the trapezoid's flat tops in a way that peak-spotting is not.</summary>
        private static int CyclesIn(List<double> series) => (int)Math.Round(1.0 / MeanPeriod(series, 1));

        /// <summary>
        /// Mean seconds between successive upward crossings of the series' own mean - i.e. the measured
        /// period of one Max-Min-Max travel.
        /// <para/>
        /// Measured as a PERIOD rather than by counting crossings inside a fixed window: a count over
        /// exactly one second lands on 9 or 10 purely according to where the wave's phase happens to sit
        /// at the window edges, which says nothing about the rate. The period is phase-independent.
        /// </summary>
        private static double MeanPeriod(List<double> series, int sampleHzUnused)
        {
            double sum = 0.0;
            foreach (double v in series) sum += v;
            double mean = sum / series.Count;

            var crossingIndices = new List<int>();
            for (int i = 1; i < series.Count; i++)
                if (series[i - 1] <= mean && series[i] > mean) crossingIndices.Add(i);

            Assert.True(crossingIndices.Count >= 3, "not enough cycles sampled to measure a period");
            double spanSamples = crossingIndices[crossingIndices.Count - 1] - crossingIndices[0];
            double periods = crossingIndices.Count - 1;
            return spanSamples / periods / SampleRate;
        }

        private const double SampleRate = 2000.0;

        [Theory]
        [InlineData(5.0)]
        [InlineData(10.0)]
        [InlineData(23.0)]
        public void Each_pad_completes_one_full_travel_per_period_on_the_lock_slip_wave(double hz)
        {
            const int sampleHz = 4000;   // two seconds at SampleRate
            var left = new List<double>();
            var right = new List<double>();

            for (int i = 0; i < sampleHz; i++)   // exactly one second
            {
                GForceShake.ApplyLockSlipOnly(
                    0.7, hz, i / SampleRate, 0.4, ShakeFeeling.OppositePhase,
                    out double l, out double r);
                left.Add(l);
                right.Add(r);
            }

            Assert.Equal((int)hz, CyclesIn(left));
            Assert.Equal((int)hz, CyclesIn(right));
        }

        [Theory]
        [InlineData(5.0)]
        [InlineData(10.0)]
        [InlineData(23.0)]
        public void Each_pad_completes_one_full_travel_per_period_on_the_G_force_band(double hz)
        {
            // GForceShake.Apply is the band used by PerChannel and AllChannelsGForce; it shares
            // ApplyBand with the lock/slip wave, so this covers those modes' time base too.
            const int sampleHz = 4000;   // two seconds at SampleRate
            var left = new List<double>();
            var right = new List<double>();

            for (int i = 0; i < sampleHz; i++)
            {
                GForceShake.Apply(
                    60.0, 0.7, hz, i / SampleRate, 0.4, ShakeFeeling.OppositePhase,
                    out double l, out double r);
                left.Add(l);
                right.Add(r);
            }

            Assert.Equal((int)hz, CyclesIn(left));
            Assert.Equal((int)hz, CyclesIn(right));
        }

        [Theory]
        [InlineData(ShakeFeeling.OppositePhase)]
        [InlineData(ShakeFeeling.SamePhase)]
        [InlineData(ShakeFeeling.Blending)]
        public void The_feeling_changes_the_relationship_but_never_the_per_pad_rate(ShakeFeeling feeling)
        {
            // A feeling decides how the two pads relate. It must not halve or double how often either
            // one travels - Same Phase moves them together, Opposite Phase opposes them, Blending
            // offsets them, and in every case each pad completes ten Max-Min-Max cycles per second.
            const int sampleHz = 4000;   // two seconds at SampleRate
            var left = new List<double>();

            for (int i = 0; i < sampleHz; i++)
            {
                GForceShake.ApplyLockSlipOnly(
                    0.7, 10.0, i / SampleRate, 0.4, feeling, out double l, out _);
                left.Add(l);
            }

            Assert.Equal(10, CyclesIn(left));
        }

        [Fact]
        public void The_underlying_wave_itself_is_one_full_period_per_1_over_f()
        {
            // The definition at its source: SineHoldWave runs MAX -> MIN -> MAX exactly once per 1/f.
            const int sampleHz = 8000;
            var series = new List<double>();
            for (int i = 0; i < sampleHz; i++)
                series.Add(GForceShake.SineHoldWave(10.0, i / SampleRate, 0.4));

            Assert.Equal(10, CyclesIn(series));
        }
    }
}
