namespace QAdvanceFeedback.Core.Viper
{
    /// <summary>
    /// The exact SimHub property names published by viper4gh's <b>CalcLngWheelSlip</b> plugin
    /// (https://github.com/viper4gh/SimHub-Plugin-CalcLngWheelSlip), and the NCalc expressions this
    /// plugin uses to read them as a Wheel Lock / Wheel Slip source.
    /// <para/>
    /// THE ORIGINAL viper4gh PLUGIN ONLY. A community fork exists; it registers its properties under a
    /// DIFFERENT plugin type name and is deliberately not supported here - the owner's own scoping.
    /// Pointing this mode at the fork would silently read nothing (see the property-name note below for
    /// how that failure actually presents, which is NOT an error).
    /// <para/>
    /// THE PROPERTY NAME IS THE WHOLE FEATURE, and getting it wrong is silent. SimHub's NCalc resolves
    /// an UNKNOWN property to NULL, not to 0 and not to an exception - and this channel's
    /// <c>max(0.0, ...)</c> then absorbs that null into a clean 0.0 (see <see cref="GetScript"/>). So a
    /// wrong plugin prefix does not fail, it reads 0 on every frame, forever.
    /// <see cref="WheelSourceResolver"/>'s fallback cannot help either: it only triggers when evaluation
    /// FAILS, and this evaluation succeeds. Diagnosed from the owner's own capture, where an entire
    /// session ran with every <c>Diag.Source.*</c> reading exactly 0.00 while the rig still vibrated
    /// (the engine's Raw divergence fallback was carrying it), and the only symptom was that nothing
    /// was ever learned. That is why this mode exists as a preset rather than as documentation telling
    /// a driver to type the name themselves.
    /// <para/>
    /// WHEEL SUFFIXES ARE VIPER'S, NOT OURS. The plugin abbreviates (FL/FR/RL/RR) where this project
    /// spells them out (FrontLeft/...), so the two must be mapped rather than concatenated -
    /// see <see cref="ViperWheelSuffixFor"/>.
    /// </summary>
    public static class ViperPropertyNames
    {
        /// <summary>viper4gh's plugin class name, as it appears in every property it registers.</summary>
        public const string PluginTypeName = "ViperDataPlugin";

        /// <summary>Everything before the wheel suffix, e.g.
        /// <c>ViperDataPlugin.CalcLngWheelSlip.Computed.LngWheelSlip_</c>.</summary>
        public const string ComputedPrefix = PluginTypeName + ".CalcLngWheelSlip.Computed.LngWheelSlip_";

        /// <summary>The full property name for one wheel, using VIPER's own suffix (FL/FR/RL/RR).</summary>
        public static string GetRawPropertyName(string viperWheelSuffix) => ComputedPrefix + viperWheelSuffix;

        /// <summary>Maps this project's wheel suffix (<c>FrontLeft</c>) to Viper's (<c>FL</c>). Returns
        /// null for anything unrecognised rather than guessing, so a caller cannot silently build a
        /// property name that will read 0 forever.</summary>
        public static string ViperWheelSuffixFor(string wheelSuffix)
        {
            switch (wheelSuffix)
            {
                case MotorsExport.MotorsExportPropertyNames.FrontLeft: return "FL";
                case MotorsExport.MotorsExportPropertyNames.FrontRight: return "FR";
                case MotorsExport.MotorsExportPropertyNames.RearLeft: return "RL";
                case MotorsExport.MotorsExportPropertyNames.RearRight: return "RR";
                default: return null;
            }
        }

        /// <summary>
        /// The NCalc expression this plugin configures for one wheel of one channel.
        /// <para/>
        /// SIGN CONVENTION (the owner's own, confirmed against their plugin): <c>LngWheelSlip_XX</c> is
        /// POSITIVE for LOCK (0 to 1, where 1 is fully locked) and NEGATIVE for SLIP. So Lock reads the
        /// value directly and Slip negates it first; everything outside its own half is floored to 0 by
        /// the <c>max(0, ...)</c>, so the two channels never bleed into each other.
        /// <para/>
        /// THE SHAPE IS THE OWNER'S OWN <c>min</c>/<c>max</c> CLAMP, restored at their explicit
        /// instruction (2026-09-29) after a period in nested-<c>if()</c> form. The two shapes compute
        /// the same numbers; the only substantive difference is how each behaves when the property is
        /// ABSENT, which is written out below so nobody "fixes" this back again without knowing.
        /// <para/>
        /// EVERY NUMERIC LITERAL MUST BE WRITTEN AS A DOUBLE (<c>0.0</c>, not <c>0</c>). This is not
        /// style - with <c>min</c>/<c>max</c> it is the difference between a proportional signal and an
        /// on/off flag, and it cost a full diagnosis to find.
        /// <para/>
        /// DECOMPILED FROM SimHub's OWN NCalc.dll (<c>NCalc.Numbers.Max</c>): <c>Max</c> and <c>Min</c>
        /// switch on the type of their FIRST argument and convert the second to it -
        /// <code>
        ///   TypeCode.Int32  => Math.Max((int)a,    Convert.ToInt32(b))
        ///   TypeCode.Double => Math.Max((double)a, Convert.ToDouble(b))
        /// </code>
        /// So <c>max(0, x)</c> takes the Int32 branch, and <c>Convert.ToInt32</c> ROUNDS: 0.45 becomes
        /// 0, 0.60 becomes 1. The expression then collapses to a step at slip ratio 0.5 - everything
        /// below is silence, everything above is full scale. Measured on the owner's own F1 25 capture
        /// before this was understood: 777 nonzero wheel-frames, EVERY one exactly 100, not a single
        /// value in between, across all eight per-wheel columns. Their reported symptom - "vibration
        /// only when the wheel FULLY locked" - was this step. <c>max(0.0, ...)</c> takes the Double
        /// branch and is correct; <c>min(1.0, ...)</c> was always safe for the same reason.
        /// <para/>
        /// WHAT THIS SHAPE COSTS, AND IT IS ASYMMETRIC BETWEEN THE TWO CHANNELS. A property SimHub does
        /// not know does not evaluate to 0 - <c>NCalcEngineBase.EvaluateParameter</c> leaves
        /// <c>args.Result</c> null and only forces <c>HasResult</c> - and <c>NCalc.Numbers.Max</c> opens
        /// with <c>if (b == null) return a;</c>. So:
        /// <list type="bullet">
        /// <item>LOCK - <c>max(0.0, null)</c> quietly yields 0.0. Evaluation SUCCEEDS, so
        /// <see cref="SimHubExpressionEvaluator.TryEvaluate"/> returns true and
        /// <see cref="WheelSourceResolver"/> never falls back: a missing Viper plugin reads as a
        /// confident "no lock" forever rather than handing the wheel to Raw.</item>
        /// <item>SLIP - the negation runs FIRST, and <c>-1.0 * null</c> dereferences its operand
        /// (<c>Numbers.Multiply</c> does <c>a.GetType()</c>), so it THROWS, <c>TryEvaluate</c> returns
        /// false, and the wheel DOES fall back to Raw.</item>
        /// </list>
        /// The <see cref="ViperDocument"/> supported-game list and the settings page's own
        /// "reading Plugin Internal on this game" shadow are what cover the Lock case instead: the
        /// channel is switched off Viper before the expression is ever evaluated, rather than relying on
        /// the expression to fail. <c>isnull(...)</c> is deliberately NOT present - it would make no
        /// difference to Lock (<c>max</c> already absorbs the null) and would DESTROY Slip's fallback by
        /// converting the throw into a confident zero.
        /// <para/>
        /// THE PLUGIN'S RANGE IS -1..+1 AND ALWAYS WAS, confirmed from viper4gh's own two dashboards
        /// ("Viper - Lng Wheel Slip", "Lock and Spin of Wheels"): both bind the colour ramp with
        /// <c>StartColorValue -1.0 / MiddleColorValue 0.0 / EndColorValue 1.0</c>, and the gauge runs
        /// 0..1 over the absolute value. Positive is lock, negative is slip, exactly as this expression
        /// assumes - so the <c>min(1.0, ...)</c> ceiling only ever trims a transient overshoot.
        /// </summary>
        public static string GetScript(bool isLockChannel, string wheelSuffix)
        {
            string viperWheel = ViperWheelSuffixFor(wheelSuffix);
            if (viperWheel == null) return string.Empty;

            string p = "[" + GetRawPropertyName(viperWheel) + "]";

            // EVERY NUMERIC LITERAL IS A DOUBLE - 0.0, never 0. See the remarks above: with min/max a
            // bare integer first argument silently collapses the whole signal to a step at 0.5.
            // Lock keeps the positive half clamped to 0..1; Slip negates first, which also gives that
            // channel a working null-fallback that Lock does not have.
            return isLockChannel
                ? "min(1.0, max(0.0, " + p + ")) * 100.0"
                : "min(1.0, max(0.0, -1.0 * " + p + ")) * 100.0";
        }
    }
}
