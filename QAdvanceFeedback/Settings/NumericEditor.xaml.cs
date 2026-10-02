using System;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

// ROOT NAMESPACE, DELIBERATELY OUTSIDE QAdvanceFeedback.* - and this is forced, not a preference.
// The plugin's own class is QAdvanceFeedback.QAdvanceFeedback, and a TYPE shadows a NAMESPACE of the
// same name during C# lookup. So inside namespace QAdvanceFeedback.Settings, the generated XAML field
// declaration "QAdvanceFeedback.Settings.NumericEditor" resolves its first identifier to the plugin
// CLASS and then looks for a member called Settings on it - which exists, as a property - giving
// CS0426. The WPF generator emits unqualified names, so there is no global:: escape available. Any
// namespace under QAdvanceFeedback.* fails identically; a separate root is the fix.
namespace QAdvanceFeedbackControls
{
    /// <summary>
    /// THE NUMERIC EDITOR USED EVERYWHERE ON THE SETTINGS PAGE (owner, 2026-09-28: "if you disable the
    /// numeric editor, both of the textbox and +- will be disabled ... wrap the Textbox and the +- button
    /// as a control, to control the disable together, so ALL controls will apply the same thing").
    /// <para/>
    /// WHY A CONTROL RATHER THAN A HELPER FUNCTION. A helper only works if every call site remembers to
    /// use it, and the failure it prevents is silent: a half-disabled editor whose +/- are dead but which
    /// still accepts typed input looks enabled to the driver and ignores them. Wrapping makes the
    /// guarantee structural - <see cref="UIElement.IsEnabled"/> on this control is coerced down onto the
    /// single inner <see cref="NumericUpDown"/> by WPF itself, so the two halves cannot disagree, and
    /// there is no second way to spell it. The owner considered and rejected the read-only-text-box case
    /// ("I don't see the scenario that need to make textbox uneditable but can use +- button"), so this
    /// deliberately exposes no <c>IsReadOnly</c> at all.
    /// <para/>
    /// SURFACE IS INTENTIONALLY MINIMAL - only what the page actually binds. Everything forwards to the
    /// inner control; nothing is re-implemented, so MahApps keeps owning the behaviour and the look.
    /// <para/>
    /// <see cref="Value"/> IS NULLABLE, and that is load-bearing rather than incidental: null renders the
    /// watermark, which is how the page shows "---" for a key data point that has no honest number yet
    /// (an unrecognised source nobody has measured). A non-nullable double would turn that into a
    /// confident 0.
    /// </summary>
    public partial class NumericEditor : UserControl
    {
        public NumericEditor()
        {
            InitializeComponent();
            Inner.ValueChanged += OnInnerValueChanged;
        }

        /// <summary>Raised when the value changes, from a user edit or a programmatic assignment - the
        /// same contract <see cref="NumericUpDown.ValueChanged"/> has, since the page's dirty tracking
        /// relies on it firing for both and suppresses the programmatic case with its own loading
        /// guard.</summary>
        public event EventHandler<RoutedPropertyChangedEventArgs<double?>> ValueChanged;

        /// <summary>
        /// THE WRITE-BACK. Without it this control is one-way, and that was a defect that silently
        /// discarded every edit a driver made anywhere on the settings page (owner-reported,
        /// 2026-10-01: "CHANGE NUMBER, EXIT, RESTART, KeyPoints RESTORED ... CONFIG FILE DOES NOT HAVE
        /// UPDATED VALUE").
        /// <para/>
        /// <see cref="OnValueChanged"/> pushes the DP DOWN into the inner control, which is the path a
        /// programmatic assignment takes. The opposite direction - a driver typing, or clicking +/- -
        /// changes the INNER control only. The DP kept its old number, so every handler that reads
        /// <see cref="Value"/> (the whole page: PersistChannelIfManual, SaveChannel, SaveToSettings)
        /// read the STALE value and dutifully saved it. The display showed the new number while the
        /// model kept the old one, so an edit looked accepted and then "reverted" on the next load.
        /// <para/>
        /// THE DP IS UPDATED BEFORE THE EVENT IS RAISED, deliberately: handlers read
        /// <see cref="Value"/>, so raising first would hand them the stale number and reintroduce the
        /// bug one level up. <see cref="OnValueChanged"/>'s own equality check absorbs the echo this
        /// causes, so the two directions cannot loop.
        /// </summary>
        private void OnInnerValueChanged(object sender, RoutedPropertyChangedEventArgs<double?> e)
        {
            if (!NullableEquals(Value, e.NewValue)) Value = e.NewValue;
            ValueChanged?.Invoke(this, e);
        }

        /// <summary>Exact comparison, NaN included - these are UI numbers echoed between two controls,
        /// not measurements, so "the same bits" is the right test and avoids an endless ping-pong on a
        /// value that cannot compare equal to itself.</summary>
        private static bool NullableEquals(double? a, double? b)
            => a.HasValue == b.HasValue
               && (!a.HasValue || a.Value.Equals(b.Value));

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double?), typeof(NumericEditor),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        /// <summary>The edited number, or null for "no value" - which renders as the watermark.</summary>
        public double? Value
        {
            get => (double?)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = (NumericEditor)d;
            var next = (double?)e.NewValue;
            // Guard against the echo: assigning the inner control raises its ValueChanged, which writes
            // back here (see OnInnerValueChanged) and would otherwise recurse.
            if (!NullableEquals(editor.Inner.Value, next)) editor.Inner.Value = next;
        }

        public double Minimum
        {
            get => Inner.Minimum;
            set => Inner.Minimum = value;
        }

        public double Maximum
        {
            get => Inner.Maximum;
            set => Inner.Maximum = value;
        }

        public double Interval
        {
            get => Inner.Interval;
            set => Inner.Interval = value;
        }

        public string StringFormat
        {
            get => Inner.StringFormat;
            set => Inner.StringFormat = value;
        }

        /// <summary>The "---" placeholder shown when <see cref="Value"/> is null. Forwards to MahApps'
        /// own attached watermark on the inner control, which is where the page used to set it.</summary>
        public string Watermark
        {
            get => TextBoxHelper.GetWatermark(Inner);
            set => TextBoxHelper.SetWatermark(Inner, value);
        }
    }
}
