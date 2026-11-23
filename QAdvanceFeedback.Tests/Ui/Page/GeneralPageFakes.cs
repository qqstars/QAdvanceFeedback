using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>The page host plus the General tab's own members (<see cref="IGeneralPageHost"/>):
    /// delegates the page host to a <see cref="FakeSettingsPageHost"/>, records imports, and serves a
    /// health list the test controls (never the process-wide registry, which other tests share).</summary>
    public sealed class FakeGeneralPageHost : ISettingsPageHost, IGeneralPageHost
    {
        public FakeGeneralPageHost(QAdvanceFeedbackSettings settings = null)
        {
            Inner = new FakeSettingsPageHost(settings);
        }

        public FakeSettingsPageHost Inner { get; }

        // ---- IGeneralPageHost ----
        public List<HealthEntry> Health { get; } = new List<HealthEntry>();
        public int HealthReads { get; private set; }
        public IReadOnlyList<HealthEntry> HealthSnapshot() { HealthReads++; return Health.ToArray(); }

        public string PluginVersion { get; set; } = "1.2.3.4";

        public List<bool> Imports { get; } = new List<bool>();
        public Func<bool, global::QAdvanceFeedback.ShakeItConversionOutcome> OnImport { get; set; }
        public global::QAdvanceFeedback.ShakeItConversionOutcome ImportShakeItPrecalibration(bool overrideExisting)
        {
            Imports.Add(overrideExisting);
            return OnImport?.Invoke(overrideExisting);
        }

        // ---- ISettingsPageHost (delegated) ----
        public QAdvanceFeedbackSettings Settings => Inner.Settings;
        public SettingsHub Hub => Inner.Hub;
        public string CurrentGameId => Inner.CurrentGameId;
        public QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness => Inner.CurrentLockNativeness;
        public QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness => Inner.CurrentSlipNativeness;
        public int KeyDataRevision => Inner.KeyDataRevision;
        public bool ViperAvailable => Inner.ViperAvailable;
        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) => Inner.LearnedKeyData(isLock, sourceIdentity);
        public void ApplySettings() => Inner.ApplySettings();
        public QAdvanceFeedbackSettings CreateDefaultSettings() => Inner.CreateDefaultSettings();
        public event EventHandler PluginStateChanged
        {
            add => Inner.PluginStateChanged += value;
            remove => Inner.PluginStateChanged -= value;
        }
        public void LogError(string message) => Inner.LogError(message);
        public TestEffectSession Session => Inner.Session;
        public void PublishGForceTest(GForceOutput outputOrNull) { }
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) { }
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) { }
    }

    /// <summary>A clipboard another process holds locked.</summary>
    public sealed class LockedClipboard : IClipboard
    {
        public int Attempts { get; private set; }
        public void SetText(string text) { Attempts++; throw new InvalidOperationException("clipboard locked"); }
    }
}
