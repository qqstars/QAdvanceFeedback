using System;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// One row of the G-Force tab's Maximum G group (acceleration, braking or cornering): the Auto
    /// toggle, the max-G box and the readout beside it. Both inputs are page edits on the DRAFT (applied
    /// with Apply), unlike the ShakeIt panel's <see cref="GForceAxisViewModel"/>, which applies at once.
    /// <list type="bullet">
    /// <item>THE BOX IS ALWAYS EDITABLE (docs\robust-auto-gforce-report.md): under Auto it is the default
    /// Auto falls back to, so it is never disabled or overwritten with the learned value.</item>
    /// <item>THE READOUT (old <c>RefreshGForceLearnedText</c>, <see cref="GForceReadout.Build"/>): the
    /// page's own Auto toggle, with the RUNNING plugin's fixed value and detection - it is the only
    /// thing that tells Auto's state. Rebuilt when the toggle moves, after a reload / Apply, and by
    /// <see cref="RefreshReadout"/> (the tab's one-second timer).</item>
    /// </list>
    /// </summary>
    public sealed class GForcePageAxisViewModel : PageSectionViewModel
    {
        private readonly SettingField _modeField;
        private string _readout = string.Empty;

        public GForcePageAxisViewModel(SettingsPageContext ctx, SettingField modeField) : base(ctx)
        {
            if (modeField != SettingField.AccelMaxMode && modeField != SettingField.DecelMaxMode && modeField != SettingField.LatMaxMode)
                throw new ArgumentOutOfRangeException(nameof(modeField));
            _modeField = modeField;
            // Apply leaves the draft clean and the live settings changed: the readout follows at once.
            Ctx.Draft.DirtyChanged += (s, e) => RefreshReadout();
            RefreshReadout();
        }

        private GForceSettings G => S.GForce;

        /// <summary>The Auto toggle (two-way): a page edit; the readout follows at once.</summary>
        public bool Auto
        {
            get => Mode == GMaxMode.Auto;
            set
            {
                if (Edit(Mode, value ? GMaxMode.Auto : GMaxMode.Fixed, v => Mode = v)) RefreshReadout();
            }
        }

        /// <summary>The max-G box (two-way). Empty keeps the value.</summary>
        public double? Max
        {
            get => Fixed;
            set { if (value.HasValue) Edit(Fixed, value.Value, v => Fixed = v); }
        }

        /// <summary>"Fixed: 1.50G" / "Default: 1.50G. Auto detected: 2.30G" / "... (no data yet)".</summary>
        public string Readout { get => _readout; private set => SetProperty(ref _readout, value); }

        /// <summary>Re-query the running plugin's learner (the learned max moves while driving).</summary>
        public void RefreshReadout()
        {
            GForceSettings live = Ctx.Host.Settings?.GForce;
            if (live == null) { Readout = string.Empty; return; }
            bool auto = Auto;
            double detected;
            switch (_modeField)
            {
                case SettingField.AccelMaxMode:
                    Readout = GForceReadout.Build(auto, live.FixedAccelMaxG, live.TryGetCurrentAccelAutoDetected(out detected), detected);
                    break;
                case SettingField.DecelMaxMode:
                    Readout = GForceReadout.Build(auto, live.FixedDecelMaxG, live.TryGetCurrentDecelAutoDetected(out detected), detected);
                    break;
                default:
                    Readout = GForceReadout.Build(auto, live.FixedLatMaxG, live.TryGetCurrentLatAutoDetected(out detected), detected);
                    break;
            }
        }

        protected override void OnDraftReloaded(DraftReloadedEventArgs e)
        {
            base.OnDraftReloaded(e);
            RefreshReadout();
        }

        private GMaxMode Mode
        {
            get
            {
                switch (_modeField)
                {
                    case SettingField.AccelMaxMode: return G.AccelMaxMode;
                    case SettingField.DecelMaxMode: return G.DecelMaxMode;
                    default: return G.LatMaxMode;
                }
            }
            set
            {
                switch (_modeField)
                {
                    case SettingField.AccelMaxMode: G.AccelMaxMode = value; break;
                    case SettingField.DecelMaxMode: G.DecelMaxMode = value; break;
                    default: G.LatMaxMode = value; break;
                }
            }
        }

        private double Fixed
        {
            get
            {
                switch (_modeField)
                {
                    case SettingField.AccelMaxMode: return G.FixedAccelMaxG;
                    case SettingField.DecelMaxMode: return G.FixedDecelMaxG;
                    default: return G.FixedLatMaxG;
                }
            }
            set
            {
                switch (_modeField)
                {
                    case SettingField.AccelMaxMode: G.FixedAccelMaxG = value; break;
                    case SettingField.DecelMaxMode: G.FixedDecelMaxG = value; break;
                    default: G.FixedLatMaxG = value; break;
                }
            }
        }
    }
}
