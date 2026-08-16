using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.Normalized;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Registers and serves every property this plugin publishes: Raw/Normalized/Projected for both Wheel
    /// Lock and Wheel Slip (54 properties), the 8 G-force channels, and - only when
    /// <see cref="Settings.GeneralSettings.EnableDiagnostics"/> is on - the internal/diagnostic
    /// properties. Values are held in fields and served through <c>AttachDelegate</c>, so publishing
    /// costs nothing extra in the per-frame path; <see cref="AllPublishedProperties"/> is the single
    /// source of truth for WHICH names exist and the diagnostics gate (unit-tested there, SimHub-free)
    /// - this class is only the thin loop that dispatches each one to the matching backing field.
    /// <para/>
    /// CRITICAL (has bitten this project family twice - read before changing the signature of
    /// <see cref="Register{TPlugin}"/>): <c>IPluginExtensions.AttachDelegate&lt;T,U&gt;</c> infers
    /// <c>T</c> from the STATIC type of its receiver, not <c>GetType()</c>. This method is therefore
    /// generic in the concrete plugin type (<typeparamref name="TPlugin"/>) rather than taking a
    /// plain <see cref="IPlugin"/> parameter - a fixed <c>IPlugin</c> parameter would make every
    /// property register under "IPlugin.*" regardless of what the plugin class is named, silently
    /// defeating the entire "QAdvanceFeedback." prefix this plugin class's own name is supposed to
    /// supply (see <c>PluginManager.GetName</c>, decompiled and confirmed - <c>QAdvanceFeedback.cs</c>'s
    /// own remarks). This is mutation (b) in the report.
    /// </summary>
    public sealed class PropertyPublisher
    {
        private const int TargetCount = 9;

        // Raw (Layer 3), Normalized (Layer 4), Projected (Layer 5) - indexed exactly as
        // PublishedPropertyNames.Targets: FrontLeft, FrontRight, RearLeft, RearRight, Front, Rear,
        // Left, Right, All.
        private readonly double[] _lockRaw = new double[TargetCount];
        private readonly double[] _slipRaw = new double[TargetCount];
        private readonly double[] _lockNormalized = new double[TargetCount];
        private readonly double[] _slipNormalized = new double[TargetCount];
        private readonly double[] _lockProjected = new double[TargetCount];
        private readonly double[] _slipProjected = new double[TargetCount];

        // G-force - indexed exactly as GForcePublishedNames.AllNames()'s own order (Bottom x4, then
        // Back x4). Nullable: null must publish as a real null (no G data this frame), never a 0.
        private readonly double?[] _gforce = new double?[8];

        // Diagnostics - only ever read back when EnableDiagnostics is on (see Register), but kept
        // updated unconditionally so toggling the setting and restarting always shows current state.
        private string _direction = "Unknown";
        private string _motionLevel = "Unavailable";
        private double _motionMagnitudeG;
        private double _lockLearnedPeakG;
        private double _lockLearnerConfidence;
        private double _slipLearnedPeakG;
        private double _slipLearnerConfidence;
        private double _gforceLearnedAccelMaxG;
        private double _gforceLearnedDecelMaxG;

        // Diag.Source.* (docs\raw-gap-and-pad-balance-report.md) - the RESOLVED SOURCE value Layer 4
        // actually consumed this frame, per wheel plus the same Front/Rear/Left/Right/All aggregates
        // every other tier publishes - indexed exactly like _lockRaw/_slipRaw (PublishedPropertyNames.
        // Targets order). Added so a future "does our Raw match the configured source" investigation
        // never again needs to invert the Normalized transform to recover this value.
        private readonly double[] _sourceLock = new double[TargetCount];
        private readonly double[] _sourceSlip = new double[TargetCount];

        // WheelLock/WheelSlip.ProjectedWithoutPulse.* (docs\raw-gap-and-pad-balance-report.md, the
        // pulse-into-shake fix) - internal/troubleshooting only, gated the same as every other
        // diagnostic name (see Register). Indexed exactly like _lockProjected/_slipProjected.
        private readonly double[] _lockProjectedWithoutPulse = new double[TargetCount];
        private readonly double[] _slipProjectedWithoutPulse = new double[TargetCount];

        /// <summary>
        /// Attaches every property this plugin publishes this session. <paramref name="diagnosticsEnabled"/>
        /// gates the diagnostic set only (see <see cref="AllPublishedProperties.DiagnosticNames"/>) -
        /// the 62 product properties are ALWAYS attached, unconditionally. SimHub registers properties
        /// once at Init, so toggling the diagnostics setting only takes effect after a SimHub restart -
        /// the settings UI says so next to the checkbox.
        /// </summary>
        public void Register<TPlugin>(TPlugin plugin, bool diagnosticsEnabled) where TPlugin : IPlugin
        {
            AttachTier(plugin, PublishedPropertyNames.LockPrefix, _lockRaw);
            AttachTier(plugin, PublishedPropertyNames.SlipPrefix, _slipRaw);
            AttachTier(plugin, AllPublishedProperties.NormalizedLockPrefix, _lockNormalized);
            AttachTier(plugin, AllPublishedProperties.NormalizedSlipPrefix, _slipNormalized);
            AttachTier(plugin, AllPublishedProperties.ProjectedLockPrefix, _lockProjected);
            AttachTier(plugin, AllPublishedProperties.ProjectedSlipPrefix, _slipProjected);

            string[] gforceNames = new System.Collections.Generic.List<string>(GForcePublishedNames.AllNames()).ToArray();
            for (int i = 0; i < gforceNames.Length; i++)
            {
                int index = i;
                plugin.AttachDelegate(gforceNames[index], () => (object)_gforce[index]);
            }

            if (!diagnosticsEnabled) return;

            plugin.AttachDelegate("Diag.Direction", () => _direction);
            plugin.AttachDelegate("Diag.MotionLevel", () => _motionLevel);
            plugin.AttachDelegate("Diag.MotionMagnitudeG", () => _motionMagnitudeG);
            plugin.AttachDelegate("Diag.Lock.LearnedPeakG", () => _lockLearnedPeakG);
            plugin.AttachDelegate("Diag.Lock.LearnerConfidence", () => _lockLearnerConfidence);
            plugin.AttachDelegate("Diag.Slip.LearnedPeakG", () => _slipLearnedPeakG);
            plugin.AttachDelegate("Diag.Slip.LearnerConfidence", () => _slipLearnerConfidence);
            plugin.AttachDelegate("Diag.GForce.LearnedAccelMaxG", () => _gforceLearnedAccelMaxG);
            plugin.AttachDelegate("Diag.GForce.LearnedDecelMaxG", () => _gforceLearnedDecelMaxG);

            AttachTier(plugin, AllPublishedProperties.SourceLockPrefix, _sourceLock);
            AttachTier(plugin, AllPublishedProperties.SourceSlipPrefix, _sourceSlip);

            AttachTier(plugin, AllPublishedProperties.ProjectedLockWithoutPulsePrefix, _lockProjectedWithoutPulse);
            AttachTier(plugin, AllPublishedProperties.ProjectedSlipWithoutPulsePrefix, _slipProjectedWithoutPulse);
        }

        private static void AttachTier<TPlugin>(TPlugin plugin, string prefix, double[] values) where TPlugin : IPlugin
        {
            for (int i = 0; i < PublishedPropertyNames.Targets.Length; i++)
            {
                int index = i; // capture per iteration
                plugin.AttachDelegate(prefix + PublishedPropertyNames.Targets[index], () => values[index]);
            }
        }

        public void UpdateRaw(LegacyWheelLockSlipResult result) => Fill(_lockRaw, _slipRaw, result);

        public void UpdateNormalized(NormalizedWheelLockSlipResult result)
        {
            _lockNormalized[0] = result.LockWheels.FrontLeft;
            _lockNormalized[1] = result.LockWheels.FrontRight;
            _lockNormalized[2] = result.LockWheels.RearLeft;
            _lockNormalized[3] = result.LockWheels.RearRight;
            _lockNormalized[4] = result.LockFront;
            _lockNormalized[5] = result.LockRear;
            _lockNormalized[6] = result.LockLeft;
            _lockNormalized[7] = result.LockRight;
            _lockNormalized[8] = result.LockAll;

            _slipNormalized[0] = result.SlipWheels.FrontLeft;
            _slipNormalized[1] = result.SlipWheels.FrontRight;
            _slipNormalized[2] = result.SlipWheels.RearLeft;
            _slipNormalized[3] = result.SlipWheels.RearRight;
            _slipNormalized[4] = result.SlipFront;
            _slipNormalized[5] = result.SlipRear;
            _slipNormalized[6] = result.SlipLeft;
            _slipNormalized[7] = result.SlipRight;
            _slipNormalized[8] = result.SlipAll;
        }

        public void UpdateProjected(ProjectedWheelLockSlipResult result)
        {
            _lockProjected[0] = result.LockWheels.FrontLeft;
            _lockProjected[1] = result.LockWheels.FrontRight;
            _lockProjected[2] = result.LockWheels.RearLeft;
            _lockProjected[3] = result.LockWheels.RearRight;
            _lockProjected[4] = result.LockFront;
            _lockProjected[5] = result.LockRear;
            _lockProjected[6] = result.LockLeft;
            _lockProjected[7] = result.LockRight;
            _lockProjected[8] = result.LockAll;

            _slipProjected[0] = result.SlipWheels.FrontLeft;
            _slipProjected[1] = result.SlipWheels.FrontRight;
            _slipProjected[2] = result.SlipWheels.RearLeft;
            _slipProjected[3] = result.SlipWheels.RearRight;
            _slipProjected[4] = result.SlipFront;
            _slipProjected[5] = result.SlipRear;
            _slipProjected[6] = result.SlipLeft;
            _slipProjected[7] = result.SlipRight;
            _slipProjected[8] = result.SlipAll;

            _lockProjectedWithoutPulse[0] = result.LockWheelsWithoutPulse.FrontLeft;
            _lockProjectedWithoutPulse[1] = result.LockWheelsWithoutPulse.FrontRight;
            _lockProjectedWithoutPulse[2] = result.LockWheelsWithoutPulse.RearLeft;
            _lockProjectedWithoutPulse[3] = result.LockWheelsWithoutPulse.RearRight;
            _lockProjectedWithoutPulse[4] = result.LockFrontWithoutPulse;
            _lockProjectedWithoutPulse[5] = result.LockRearWithoutPulse;
            _lockProjectedWithoutPulse[6] = result.LockLeftWithoutPulse;
            _lockProjectedWithoutPulse[7] = result.LockRightWithoutPulse;
            _lockProjectedWithoutPulse[8] = result.LockAllWithoutPulse;

            _slipProjectedWithoutPulse[0] = result.SlipWheelsWithoutPulse.FrontLeft;
            _slipProjectedWithoutPulse[1] = result.SlipWheelsWithoutPulse.FrontRight;
            _slipProjectedWithoutPulse[2] = result.SlipWheelsWithoutPulse.RearLeft;
            _slipProjectedWithoutPulse[3] = result.SlipWheelsWithoutPulse.RearRight;
            _slipProjectedWithoutPulse[4] = result.SlipFrontWithoutPulse;
            _slipProjectedWithoutPulse[5] = result.SlipRearWithoutPulse;
            _slipProjectedWithoutPulse[6] = result.SlipLeftWithoutPulse;
            _slipProjectedWithoutPulse[7] = result.SlipRightWithoutPulse;
            _slipProjectedWithoutPulse[8] = result.SlipAllWithoutPulse;
        }

        /// <summary>
        /// Diag.Source.* (docs\raw-gap-and-pad-balance-report.md): records exactly the per-wheel
        /// Corners Layer 4 (<c>NormalizedWheelLockSlipEngine.Compute</c>) was actually called with THIS
        /// frame - <c>lockSources</c>/<c>slipSources</c> in <c>QAdvanceFeedback.cs</c>'s own
        /// <c>DataUpdate</c>, i.e. our own Layer 3 Raw by default, or whatever the driver's Source
        /// fields resolved to instead (a ShakeIt export, a Manual property/expression, or the Layer 3
        /// fallback on any resolution failure - see <c>WheelSourceResolver</c>). Front/Rear/Left/Right/
        /// All are aggregated with the SAME owner-configured scheme every other tier uses for this
        /// channel (docs\aggregation-report.md), so this reads exactly like the Raw/Normalized/Projected
        /// tiers a driver already knows how to interpret.
        /// </summary>
        public void UpdateSource(Corners lockSources, Corners slipSources, AggregationWeights lockWeights, AggregationWeights slipWeights)
        {
            FillSource(_sourceLock, lockSources, lockWeights);
            FillSource(_sourceSlip, slipSources, slipWeights);
        }

        private static void FillSource(double[] values, Corners wheels, AggregationWeights weights)
        {
            values[0] = wheels.FrontLeft;
            values[1] = wheels.FrontRight;
            values[2] = wheels.RearLeft;
            values[3] = wheels.RearRight;
            WheelAggregate aggregate = Aggregator.Compute(wheels, weights);
            values[4] = aggregate.Front;
            values[5] = aggregate.Rear;
            values[6] = aggregate.Left;
            values[7] = aggregate.Right;
            values[8] = aggregate.All;
        }

        public void UpdateGForce(GForceOutput output)
        {
            _gforce[0] = output.BottomFrontLeft;
            _gforce[1] = output.BottomFrontRight;
            _gforce[2] = output.BottomRearLeft;
            _gforce[3] = output.BottomRearRight;
            _gforce[4] = output.BackLowLeft;
            _gforce[5] = output.BackLowRight;
            _gforce[6] = output.BackTopLeft;
            _gforce[7] = output.BackTopRight;
        }

        public void UpdateDiagnostics(
            LongitudinalMotionState direction, AchievedMotion.SignalLevel motionLevel, double motionMagnitudeG,
            double lockLearnedPeakG, double lockLearnerConfidence,
            double slipLearnedPeakG, double slipLearnerConfidence,
            double gforceLearnedAccelMaxG, double gforceLearnedDecelMaxG)
        {
            _direction = direction.ToString();
            _motionLevel = motionLevel.ToString();
            _motionMagnitudeG = motionMagnitudeG;
            _lockLearnedPeakG = lockLearnedPeakG;
            _lockLearnerConfidence = lockLearnerConfidence;
            _slipLearnedPeakG = slipLearnedPeakG;
            _slipLearnerConfidence = slipLearnerConfidence;
            _gforceLearnedAccelMaxG = gforceLearnedAccelMaxG;
            _gforceLearnedDecelMaxG = gforceLearnedDecelMaxG;
        }

        /// <summary>
        /// Every value this plugin currently holds, in EXACTLY the same order as
        /// <c>AllPublishedProperties.ProductNames()</c> followed by
        /// <c>AllPublishedProperties.DiagnosticNames()</c> - i.e. always ALL of them, regardless of
        /// whether diagnostics are actually published to SimHub, since "Export CSV" writes every
        /// property including internals whenever it is on (see <c>GeneralSettings.ExportCsv</c>'s
        /// remarks) independently of <c>GeneralSettings.EnableDiagnostics</c>.
        /// </summary>
        public object[] SnapshotAllValuesForCsv()
        {
            var values = new System.Collections.Generic.List<object>(62 + 9);
            foreach (double v in _lockRaw) values.Add(v);
            foreach (double v in _slipRaw) values.Add(v);
            foreach (double v in _lockNormalized) values.Add(v);
            foreach (double v in _slipNormalized) values.Add(v);
            foreach (double v in _lockProjected) values.Add(v);
            foreach (double v in _slipProjected) values.Add(v);
            foreach (double? v in _gforce) values.Add(v);

            values.Add(_direction);
            values.Add(_motionLevel);
            values.Add(_motionMagnitudeG);
            values.Add(_lockLearnedPeakG);
            values.Add(_lockLearnerConfidence);
            values.Add(_slipLearnedPeakG);
            values.Add(_slipLearnerConfidence);
            values.Add(_gforceLearnedAccelMaxG);
            values.Add(_gforceLearnedDecelMaxG);

            foreach (double v in _sourceLock) values.Add(v);
            foreach (double v in _sourceSlip) values.Add(v);

            foreach (double v in _lockProjectedWithoutPulse) values.Add(v);
            foreach (double v in _slipProjectedWithoutPulse) values.Add(v);

            return values.ToArray();
        }

        // Snapshot accessors for CSV export (Core/GForce/etc. types stay out of the CSV writer itself).
        public double[] LockRawSnapshot => (double[])_lockRaw.Clone();
        public double[] SlipRawSnapshot => (double[])_slipRaw.Clone();
        public double[] LockNormalizedSnapshot => (double[])_lockNormalized.Clone();
        public double[] SlipNormalizedSnapshot => (double[])_slipNormalized.Clone();
        public double[] LockProjectedSnapshot => (double[])_lockProjected.Clone();
        public double[] SlipProjectedSnapshot => (double[])_slipProjected.Clone();
        public double?[] GForceSnapshot => (double?[])_gforce.Clone();
        public string DirectionSnapshot => _direction;
        public string MotionLevelSnapshot => _motionLevel;
        public double MotionMagnitudeGSnapshot => _motionMagnitudeG;
        public double LockLearnedPeakGSnapshot => _lockLearnedPeakG;
        public double LockLearnerConfidenceSnapshot => _lockLearnerConfidence;
        public double SlipLearnedPeakGSnapshot => _slipLearnedPeakG;
        public double SlipLearnerConfidenceSnapshot => _slipLearnerConfidence;
        public double GForceLearnedAccelMaxGSnapshot => _gforceLearnedAccelMaxG;
        public double GForceLearnedDecelMaxGSnapshot => _gforceLearnedDecelMaxG;
        public double[] SourceLockSnapshot => (double[])_sourceLock.Clone();
        public double[] SourceSlipSnapshot => (double[])_sourceSlip.Clone();
        public double[] LockProjectedWithoutPulseSnapshot => (double[])_lockProjectedWithoutPulse.Clone();
        public double[] SlipProjectedWithoutPulseSnapshot => (double[])_slipProjectedWithoutPulse.Clone();

        private static void Fill(double[] lockValues, double[] slipValues, LegacyWheelLockSlipResult result)
        {
            lockValues[0] = result.LockWheels.FrontLeft;
            lockValues[1] = result.LockWheels.FrontRight;
            lockValues[2] = result.LockWheels.RearLeft;
            lockValues[3] = result.LockWheels.RearRight;
            lockValues[4] = result.LockFront;
            lockValues[5] = result.LockRear;
            lockValues[6] = result.LockLeft;
            lockValues[7] = result.LockRight;
            lockValues[8] = result.LockAll;

            slipValues[0] = result.SlipWheels.FrontLeft;
            slipValues[1] = result.SlipWheels.FrontRight;
            slipValues[2] = result.SlipWheels.RearLeft;
            slipValues[3] = result.SlipWheels.RearRight;
            slipValues[4] = result.SlipFront;
            slipValues[5] = result.SlipRear;
            slipValues[6] = result.SlipLeft;
            slipValues[7] = result.SlipRight;
            slipValues[8] = result.SlipAll;
        }
    }
}
