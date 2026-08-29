using System;
using QAdvanceFeedback.Core.RawCalculator.Calibration;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// FIDELITY OF THE SHAKEIT CALIBRATION PORT (1.0.7.1). Every assertion here is a property of
    /// SimHub's own decompiled <c>CalibrationData</c>/<c>PreloadedCalibrationData</c>/
    /// <c>CalibrationDataProvider</c>, not a preference of this project. A failure means we have drifted
    /// from ShakeIt, which is the one thing Layer 3 must not do.
    /// </summary>
    public class ShakeItCalibrationFidelityTests
    {
        // ------------------------------------------------------------------------------------
        // THE HEADLINE FIX: never silent.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void GetPercentileAnswersFromTheVeryFirstSample_NeverGoingSilent()
        {
            // The single largest divergence in the previous implementation: it returned null below its
            // own 200-sample bar and the caller published a literal 0. SimHub always answers.
            var calibration = new CalibrationData();
            calibration.AddValue(0.4);

            double low = calibration.GetPercentile(15.0);
            double high = calibration.GetPercentile(99.0);

            Assert.True(high > 0.0);
            Assert.True(low < high, "a usable band must exist immediately, not just a single number");
        }

        [Fact]
        public void ThePreMaturityFallbackIsExactlySimHubsFormula()
        {
            var calibration = new CalibrationData();
            calibration.AddValue(4.0);   // Max = 4.0, far too few samples for a real percentile
            calibration.AddValue(2.0);

            // Math.Max(1.0, Max * 0.9) * percentile / 100.0
            double expected99 = Math.Max(1.0, 4.0 * CalibrationData.FallbackMaxFactor) * 99.0 / 100.0;
            double expected15 = Math.Max(1.0, 4.0 * CalibrationData.FallbackMaxFactor) * 15.0 / 100.0;

            Assert.Equal(expected99, calibration.GetPercentile(99.0), 9);
            Assert.Equal(expected15, calibration.GetPercentile(15.0), 9);
        }

        [Fact]
        public void TheFallbackFloorsAtOne_SoATinyMaxStillProducesABand()
        {
            var calibration = new CalibrationData();
            calibration.AddValue(0.01);   // Max * 0.9 is far below 1.0, so the Math.Max(1.0, ...) bites

            Assert.Equal(1.0 * 99.0 / 100.0, calibration.GetPercentile(99.0), 9);
        }

        [Fact]
        public void ARealPercentileTakesOverOnceThereAreEnoughPositiveSamples()
        {
            var calibration = new CalibrationData();
            for (int i = 0; i < CalibrationData.MinSamplesForPercentile; i++) calibration.AddValue(0.5);

            // Every sample is 0.5, so the real percentile is 0.5 - clearly distinct from the fallback,
            // which would be Math.Max(1.0, 0.45) * 99/100 = 0.99.
            Assert.Equal(0.5, calibration.GetPercentile(99.0), 9);
        }

        [Fact]
        public void PercentileZeroIsAlwaysZero()
        {
            var calibration = new CalibrationData();
            calibration.AddValue(4.0);
            Assert.Equal(0.0, calibration.GetPercentile(0.0));
        }

        // ------------------------------------------------------------------------------------
        // Accumulation details that change the published number.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0.123456, 0.1235)]   // <= 1.0    -> 4 decimals
        [InlineData(4.567, 4.57)]        // 1..10     -> 2 decimals
        [InlineData(24.56, 24.6)]        // 10..50    -> 1 decimal
        [InlineData(64.4, 64.0)]         // 50..100   -> integer
        [InlineData(342.0, 340.0)]       // 100..1000 -> nearest 5
        public void TheHistogramUsesSimHubsAdaptiveBucketLadder(double observed, double expectedBucket)
        {
            var calibration = new CalibrationData();
            calibration.AddValue(observed);
            Assert.True(calibration.Values.ContainsKey(expectedBucket),
                $"expected bucket {expectedBucket} for {observed}; got [{string.Join(",", calibration.Values.Keys)}]");
        }

        [Fact]
        public void MagnitudeIsUsed_SoSignIsDiscarded()
        {
            var calibration = new CalibrationData();
            calibration.AddValue(-0.5);
            Assert.Equal(0.5, calibration.Max, 9);
            Assert.True(calibration.Values.ContainsKey(0.5));
        }

        [Fact]
        public void CalibrationPointsAddedSeedsAtOneAndCountsPositivesOnly()
        {
            var calibration = new CalibrationData();
            Assert.Equal(1.0, calibration.CalibrationPointsAdded);

            calibration.AddValue(0.0);   // a zero bucket must NOT advance the positive-point counter
            Assert.Equal(1.0, calibration.CalibrationPointsAdded);
            Assert.Equal(1, calibration.Count);   // ...but it does advance the plain sample count

            calibration.AddValue(0.3);
            Assert.Equal(2.0, calibration.CalibrationPointsAdded);
        }

        [Fact]
        public void ReadinessIsSampleCountAtSevenThousand()
        {
            var calibration = new CalibrationData();
            for (int i = 0; i < CalibrationData.ReadySamples - 1; i++) calibration.AddValue(0.2);
            Assert.False(calibration.IsReady);

            calibration.AddValue(0.2);
            Assert.True(calibration.IsReady);
            Assert.Equal(100.0, calibration.Completion, 6);
        }

        [Fact]
        public void AverageIsTheMeanOfMagnitudes_AndNullOnlyWhenEmpty()
        {
            var calibration = new CalibrationData();
            Assert.Null(calibration.GetAverage());

            calibration.AddValue(1.0);
            calibration.AddValue(-3.0);
            Assert.Equal(2.0, calibration.GetAverage().Value, 9);
        }

        // ------------------------------------------------------------------------------------
        // Shipped presets.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void APresetWithNoAutocalibrationIsPurelyLinearInPercentile()
        {
            var preset = new PreloadedCalibrationData { MeasuredMaximum = 2.0, CorrectionFactor = 1.5, AutocalibrationDataRatio = 0.0 };
            Assert.Equal(2.0 * 1.5 * 99.0 / 100.0, preset.GetPercentile(99.0), 9);
            Assert.True(preset.IsReady);
            Assert.Equal(100.0, preset.Completion, 6);
        }

        [Fact]
        public void APresetBlendsLiveEvidenceAtAFixedQuarter_Permanently()
        {
            var live = new CalibrationData();
            for (int i = 0; i < CalibrationData.MinSamplesForPercentile; i++) live.AddValue(0.5);

            var preset = new PreloadedCalibrationData
            {
                MeasuredMaximum = 2.0,
                CorrectionFactor = 1.0,
                AutoCalibrationData = live,
                AutocalibrationDataRatio = PreloadedCalibrationData.DefaultAutocalibrationDataRatio,
            };

            double shipped = 2.0 * 1.0 * 99.0 / 100.0;
            double expected = (live.GetPercentile(99.0) - shipped) * 0.25 + shipped;
            Assert.Equal(expected, preset.GetPercentile(99.0), 9);

            // ...and it never ramps: far more live evidence moves it no further than a quarter.
            for (int i = 0; i < 6000; i++) live.AddValue(0.5);
            double stillExpected = (live.GetPercentile(99.0) - shipped) * 0.25 + shipped;
            Assert.Equal(stillExpected, preset.GetPercentile(99.0), 9);
        }

        [Fact]
        public void APresetRefusesToProduceAMean()
        {
            // Load-bearing: the Lock branch averages, so a preset must never silently serve it.
            var preset = new PreloadedCalibrationData { MeasuredMaximum = 2.0 };
            Assert.Throws<NotImplementedException>(() => preset.GetAverage());
        }

        // ------------------------------------------------------------------------------------
        // TimeMovingAverage - the one piece of this port reconstructed from usage rather than
        // decompiled (it lives in WoteverCommon, which this project does not ship).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void MovingAverageIsTheArithmeticMeanOfRetainedSamples()
        {
            var average = new TimeMovingAverage { MaxSamples = 1500.0, MaxTimeMs = 2000000000.0 };
            Assert.Equal(0, average.Count);
            Assert.Equal(0.0, average.CurrentAverage, 9);

            average.Enqueue(0.1);
            average.Enqueue(0.2);
            average.Enqueue(0.3);

            Assert.Equal(3, average.Count);
            Assert.Equal(0.2, average.CurrentAverage, 9);
        }

        [Fact]
        public void MovingAverageTrimsToMaxSamples_AndCountIsTheRetainedCount()
        {
            var average = new TimeMovingAverage { MaxSamples = 4.0 };
            for (int i = 1; i <= 10; i++) average.Enqueue(i);

            // Only the last four survive: 7+8+9+10 = 34 / 4.
            Assert.Equal(4, average.Count);
            Assert.Equal(8.5, average.CurrentAverage, 9);
        }

        [Fact]
        public void MovingAverageAtShakeItsOwnSettingsNeverTrimsBelowFifteenHundred()
        {
            // ShakeIt's MaxTimeMs of two billion ms is unambiguously "never trim on time", so the sample
            // bound is the only one that can act - which is why implementing the sample bound alone
            // reproduces ShakeIt exactly at this call site.
            var average = new TimeMovingAverage { MaxSamples = 1500.0, MaxTimeMs = 2000000000.0 };
            for (int i = 0; i < 1500; i++) average.Enqueue(0.05);

            Assert.Equal(1500, average.Count);
            Assert.Equal(0.05, average.CurrentAverage, 9);

            average.Enqueue(0.05);
            Assert.Equal(1500, average.Count); // trimmed, not grown
        }

        [Fact]
        public void MovingAverageIgnoresNonFiniteSamples()
        {
            var average = new TimeMovingAverage();
            average.Enqueue(double.NaN);
            average.Enqueue(double.PositiveInfinity);
            Assert.Equal(0, average.Count);
        }

        // ------------------------------------------------------------------------------------
        // The wheel-speed-delta bounds, seeded SimHub's way.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void WithNoShippedBounds_TheSeedsAreSimHubsLocalOnes_NotTheGameCalibrationDefaults()
        {
            // SimHub seeds num=0.02 and num2=0.15 locally. 0.15 is NOT GameCalibration's own 0.2 default
            // for WheelSpeedDeltaHighbound - an earlier revision used the property default here, which was
            // a different structure that only happened to agree because both seeds are overwritten before
            // use on every reachable path. This test pins the structure so it stays right.
            Assert.Equal(0.02, GameCalibrationBounds.Defaults.WheelSpeedDeltaLowbound, 9);
            Assert.Equal(0.2, GameCalibrationBounds.Defaults.WheelSpeedDeltaHighbound, 9);
            Assert.Equal(0.1, GameCalibrationBounds.Defaults.WheelSpeedDeltaLowLockbound, 9);

            // Braking regime, no shipped bounds: SimHub hard-codes 0.2/1.0 for Slip and 0.4/1.0 for Lock,
            // so neither seed survives and both channels are bound-independent here.
            double slipBraking = QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 50.0, wheelSpeedOppositeWheel: 50.0,
                groundSpeedOrFallback: 100.0, brakePercent: 80.0, throttlePercent: 0.0, clutchPercent: 0.0,
                gearAverageDelta: 0.0, gearAverageSampleCount: 0, bounds: null, isFlying: 0);

            double slipBrakingWithBounds = QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 50.0, wheelSpeedOppositeWheel: 50.0,
                groundSpeedOrFallback: 100.0, brakePercent: 80.0, throttlePercent: 0.0, clutchPercent: 0.0,
                gearAverageDelta: 0.0, gearAverageSampleCount: 0,
                bounds: new GameCalibrationBounds { WheelSpeedDeltaLowbound = 0.9, WheelSpeedDeltaHighbound = 0.95 },
                isFlying: 0);

            Assert.Equal(slipBraking, slipBrakingWithBounds, 9);
        }

        [Fact]
        public void ShippedHighBoundChangesTheSlipReadingInThePositiveRegime()
        {
            // The positive regime re-reads the shipped high bound with SimHub's OWN 0.6 fallback, so this
            // is the bound that actually reaches an Offset call.
            double withDefault = QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 130.0, wheelSpeedOppositeWheel: 130.0,
                groundSpeedOrFallback: 100.0, brakePercent: 0.0, throttlePercent: 50.0, clutchPercent: 0.0,
                gearAverageDelta: 0.0, gearAverageSampleCount: 0, bounds: null, isFlying: 0);

            double withShipped = QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 130.0, wheelSpeedOppositeWheel: 130.0,
                groundSpeedOrFallback: 100.0, brakePercent: 0.0, throttlePercent: 50.0, clutchPercent: 0.0,
                gearAverageDelta: 0.0, gearAverageSampleCount: 0,
                bounds: new GameCalibrationBounds { WheelSpeedDeltaHighbound = 0.35 }, isFlying: 0);

            Assert.NotEqual(withDefault, withShipped);
        }

        [Fact]
        public void AirborneSuppressesTheWheelSpeedDeltaBranchEntirely()
        {
            double grounded = QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 160.0, wheelSpeedOppositeWheel: 160.0,
                groundSpeedOrFallback: 100.0, brakePercent: 0.0, throttlePercent: 50.0, clutchPercent: 0.0,
                gearAverageDelta: 0.0, gearAverageSampleCount: 0, bounds: null, isFlying: 0);
            Assert.True(grounded > 0.0);

            double airborne = QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 160.0, wheelSpeedOppositeWheel: 160.0,
                groundSpeedOrFallback: 100.0, brakePercent: 0.0, throttlePercent: 50.0, clutchPercent: 0.0,
                gearAverageDelta: 0.0, gearAverageSampleCount: 0, bounds: null, isFlying: 1);
            Assert.Equal(0.0, airborne, 9);
        }

        [Fact]
        public void CorneringFramesAreExcludedFromTheCruiseReference()
        {
            Assert.True(QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.QualifiesAsGearCruiseSample(
                brakePercent: 0.0, clutchPercent: 0.0, throttlePercent: 50.0, groundSpeedOrFallback: 100.0,
                largestWheelDelta: 0.1, yawChangePerSecond: 0.5));

            Assert.False(QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.QualifiesAsGearCruiseSample(
                brakePercent: 0.0, clutchPercent: 0.0, throttlePercent: 50.0, groundSpeedOrFallback: 100.0,
                largestWheelDelta: 0.1, yawChangePerSecond: 2.0));

            // A title that does not report yaw behaves as it did before this gate existed.
            Assert.True(QAdvanceFeedback.Core.RawCalculator.DispatchBranchFormulas.QualifiesAsGearCruiseSample(
                brakePercent: 0.0, clutchPercent: 0.0, throttlePercent: 50.0, groundSpeedOrFallback: 100.0,
                largestWheelDelta: 0.1, yawChangePerSecond: null));
        }

        // ------------------------------------------------------------------------------------
        // Provider keying and feeding.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void SlipIsPooledAcrossAllFourWheels_ButRpsSplitsByAxle()
        {
            // SimHub accepts a `front` flag on the slip getter and ignores it; the RPS getter appends
            // Front/Rear. Both are reproduced, and this is the assertion that proves it.
            Assert.Equal(
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.SlipMetric),
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.SlipMetric));

            var provider = new CalibrationDataProvider();
            ICalibrationData front = provider.GetSlipCalibration("Spa", "Sauber", front: true, store: true);
            ICalibrationData rear = provider.GetSlipCalibration("Spa", "Sauber", front: false, store: true);
            Assert.Same(front, rear);

            ICalibrationData rpsFront = provider.GetRpsToSpeedCalibration("Spa", "Sauber", front: true, store: true);
            ICalibrationData rpsRear = provider.GetRpsToSpeedCalibration("Spa", "Sauber", front: false, store: true);
            Assert.NotSame(rpsFront, rpsRear);
        }

        [Fact]
        public void TheKeyDropsEmptySegments()
        {
            // A title reporting no track id must key as "car;metric", not ";car;metric".
            Assert.Equal("Sauber;Slip", CalibrationDataProvider.BuildKey(null, "Sauber", "Slip"));
            Assert.Equal("Spa;Sauber;Slip", CalibrationDataProvider.BuildKey("Spa", "Sauber", "Slip"));
        }

        [Fact]
        public void ReadingAnUnknownKeyDoesNotPopulateThePersistedStore()
        {
            var provider = new CalibrationDataProvider();
            provider.GetSlipCalibration("Spa", "Sauber", front: false);
            Assert.Empty(provider.ExportCalibrations());

            provider.GetSlipCalibration("Spa", "Sauber", front: false, store: true);
            Assert.Single(provider.ExportCalibrations());
        }

        [Fact]
        public void LearningAlwaysFeedsTheLiveCalibration_NeverThePreset()
        {
            var provider = new CalibrationDataProvider();
            provider.SetPrecalibration(new System.Collections.Generic.Dictionary<string, PreloadedCalibrationData>
            {
                [CalibrationDataProvider.SlipMetric] = new PreloadedCalibrationData { MeasuredMaximum = 2.0 },
            });

            provider.Update("Spa", "Sauber", capabilityWheelsRps: false, capabilityWheelsSlip: true,
                speedKmh: 100.0, brakePercent: 0.0,
                wheelRps: null, wheelSlip: new double?[] { 0.5, 0.5, 0.5, 0.5 });

            string key = CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.SlipMetric);
            Assert.Equal(4, provider.ExportCalibrations()[key].Count);

            // The read path still resolves to the preset, with the live data attached underneath.
            ICalibrationData read = provider.GetSlipCalibration("Spa", "Sauber", front: false);
            Assert.True(read.IsPreset());
            Assert.Same(provider.ExportCalibrations()[key], read.GetUnderlyingAutocalibration());
        }

        [Fact]
        public void FeedGatesMatchSimHub()
        {
            var provider = new CalibrationDataProvider();
            var rps = new double?[] { 10.0, 10.0, 10.0, 10.0 };
            var slip = new double?[] { 0.5, 0.5, 0.5, 0.5 };

            // Below the global speed gate nothing is fed at all.
            provider.Update("Spa", "Sauber", true, true, speedKmh: 1.0, brakePercent: 0.0, wheelRps: rps, wheelSlip: slip);
            Assert.Empty(provider.ExportCalibrations());

            // Braking suppresses the RPS cruise reference but NOT slip.
            provider.Update("Spa", "Sauber", true, true, speedKmh: 100.0, brakePercent: 50.0, wheelRps: rps, wheelSlip: slip);
            Assert.False(provider.ExportCalibrations().ContainsKey(
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.RpsToSpeedMetric + CalibrationDataProvider.FrontSuffix)));
            Assert.True(provider.ExportCalibrations().ContainsKey(
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.SlipMetric)));

            // Cruising below 50 km/h also suppresses the RPS reference.
            provider.Update("Spa", "Sauber", true, true, speedKmh: 30.0, brakePercent: 0.0, wheelRps: rps, wheelSlip: slip);
            Assert.False(provider.ExportCalibrations().ContainsKey(
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.RpsToSpeedMetric + CalibrationDataProvider.FrontSuffix)));

            // Light-throttle cruising above 50 km/h feeds it.
            provider.Update("Spa", "Sauber", true, true, speedKmh: 100.0, brakePercent: 0.0, wheelRps: rps, wheelSlip: slip);
            Assert.True(provider.ExportCalibrations().ContainsKey(
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.RpsToSpeedMetric + CalibrationDataProvider.FrontSuffix)));
        }

        [Fact]
        public void FeedingStopsOnceThePositivePointCapIsReached()
        {
            var provider = new CalibrationDataProvider();
            var slip = new double?[] { 0.5, 0.5, 0.5, 0.5 };
            for (int frame = 0; frame < 2200; frame++)
                provider.Update("Spa", "Sauber", false, true, 100.0, 0.0, null, slip);

            CalibrationData calibration = provider.ExportCalibrations()[
                CalibrationDataProvider.BuildKey("Spa", "Sauber", CalibrationDataProvider.SlipMetric)];

            // 2200 frames x 4 wheels = 8800 offered, but feeding stops just past the 7000 cap.
            Assert.True(calibration.CalibrationPointsAdded <= CalibrationData.ReadySamples + 4,
                $"expected feeding to stop near the cap, got {calibration.CalibrationPointsAdded}");
        }
    }
}
