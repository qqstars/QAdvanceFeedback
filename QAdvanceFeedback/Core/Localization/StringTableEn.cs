using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Localization
{
    /// <summary>English string table - the source of truth for the key set every other table must
    /// match (see the key-parity test).</summary>
    public static class StringTableEn
    {
        public static Dictionary<string, string> Create() => new Dictionary<string, string>
        {
            ["Tab.WheelLock"] = "Wheel Lock",
            ["Tab.WheelSlip"] = "Wheel Slip",
            ["Tab.GForce"] = "G-Force",
            ["Tab.General"] = "General",

            ["Common.Apply"] = "Apply",
            ["Common.DialogTitle"] = "QAdvanceFeedback",
            ["Common.RestoreAllDefaults"] = "Restore all default settings",
            ["Common.RestoreAllConfirm"] = "This resets every setting on the Wheel Lock, Wheel Slip, G-Force and General tabs back to its shipped default - including any of your own tuning - and cannot be undone. Continue?",

            ["Group.Sources"] = "Sources",
            ["Group.Curve"] = "Output shaping",
            ["Group.Pulse"] = "Pulse at maximum",
            ["Group.GForceMaxima"] = "Maximum G",
            ["Group.GForceSustain"] = "Sustained feel",
            ["Group.GForceMotion"] = "Motion feel",
            ["Group.GForceLateral"] = "Left/right direction",
            ["Group.GForceShake"] = "Wheel lock/slip shake",
            ["Group.General"] = "General",

            // ---- Sources (the four wheel rows, Wheel Lock and Wheel Slip tabs) ----
            ["Sources.Note"] = "Each wheel reads Layer 3's own Raw property by default. Only the four wheels are configurable here - All/Front/Rear/Left/Right are always aggregated from them.",
            ["Sources.ContractNote"] = "Each field accepts a plain SimHub property name, or a SimHub expression (NCalc/JavaScript) if the script editor/evaluator is available on this SimHub install. Values use a 0-100 scale.",
            ["Sources.ContractNote.NoEvaluator"] = "Each field accepts a plain SimHub property name only - the expression evaluator is not available on this SimHub install. Values use a 0-100 scale.",
            ["Sources.FrontLeft"] = "Front left",
            ["Sources.FrontRight"] = "Front right",
            ["Sources.RearLeft"] = "Rear left",
            ["Sources.RearRight"] = "Rear right",
            ["Sources.ResetToDefault"] = "Reset to default",
            ["Sources.EditScriptButton"] = "Edit script…",
            ["Curve.PickButton"] = "Pick…",
            ["Sources.ScriptType.TooltipJavaScript"] = "JavaScript - click to switch to NCalc",
            ["Sources.ScriptType.TooltipNCalc"] = "NCalc - click to switch to JavaScript",
            ["Sources.ScriptType.TooltipPlain"] = "Plain property - click to switch to JavaScript",

            // ---- Source mode toggle: ShakeIt Plugin Output Properties (default) vs. Manual. ALWAYS
            // visible and switchable, at the top of the Sources section, regardless of whether SimHub
            // currently reports the four expected properties - see SettingsControl.xaml.cs's own remarks. ----
            ["Sources.Mode.Label"] = "Source:",
            ["Sources.Mode.Manual"] = "Manual",
            ["Sources.Mode.ShakeIt"] = "ShakeIt Plugin Output Properties",
            ["Sources.ShakeItSetup.Lock"] = "Quick setup: in SimHub, enable the \"ShakeIt Motors\" plugin, create a profile, add a \"Wheels lock\" effect (group \"Slip and lock\"), tick \"Use legacy IRacing algorythm\", tick \"Export output value as a property\" and set \"Property name\" to exactly \"WheelLock.IRacing\", then tick \"Disable output\" so it drives no hardware. Full steps: docs\\shakeit-export-guide.md.",
            ["Sources.ShakeItSetup.Slip"] = "Quick setup: in SimHub, enable the \"ShakeIt Motors\" plugin, create a profile, add a \"Wheels slip\" effect (group \"Slip and lock\"), tick \"Use legacy IRacing algorythm\", tick \"Export output value as a property\" and set \"Property name\" to exactly \"WheelSlip.IRacing\", then tick \"Disable output\" so it drives no hardware. Full steps: docs\\shakeit-export-guide.md.",
            ["Sources.ShakeItUnavailable.Note"] = "Not published yet: SimHub is not currently reporting all four of ShakeIt Motors' exported wheel properties for this channel - follow the steps above, then reopen this tab. This channel keeps working meanwhile: it falls back to its own Raw values automatically until the export appears.",
            ["Sources.Mode.ManualNote.Lock"] = "Manual mode: point each wheel's Source field at any SimHub property that reports a 0-100 value for that wheel - 0 means the wheel isn't locking at all, 100 means it's fully locked.",
            ["Sources.Mode.ManualNote.Slip"] = "Manual mode: point each wheel's Source field at any SimHub property that reports a 0-100 value for that wheel - 0 means the wheel isn't slipping at all, 100 means it's fully spinning.",

            // ---- Pedal-pressed thresholds (owner-requested, deliberately deviates from SimHub's own hard-coded Brake>20/Throttle>40 - applies in BOTH Manual and ShakeIt mode) ----
            ["Sources.Threshold.LockBrake"] = "Brake pedal threshold (%)",
            ["Sources.Threshold.SlipBrake"] = "Brake pedal threshold (%)",
            ["Sources.Threshold.SlipThrottle"] = "Throttle pedal threshold (%)",
            ["Sources.Threshold.Lock.Note"] = "The wheel is only considered to be locking once the brake pedal is pressed past this percentage. Default 20% (matches SimHub's own built-in value).",
            ["Sources.Threshold.Slip.Note"] = "Slip checks the brake threshold FIRST: if the brake pedal is pressed past its own threshold, that takes priority and slip reads the same braking value Lock does. Only if brake is below its threshold is throttle checked against its own threshold (and only while the clutch is under 5%, SimHub's own fixed rule). Defaults: brake 100% (so brake practically never wins and slip is throttle-only, as intended) and throttle 40% (matches SimHub's own built-in value).",

            // ---- Curve editor (shared layout, per-channel defaults/wording) ----
            ["Curve.Preset.Label"] = "Preset:",
            ["Curve.Preset.Linear"] = "Linear",
            ["Curve.Preset.Curve"] = "Curve",
            ["Curve.Preset.Custom"] = "Custom",
            ["Curve.Column.RawValue"] = "raw value",
            ["Curve.Column.OutputValue"] = "output value",
            ["Curve.LiveOutputFormat"] = "at this input, the curve currently sends {0}",

            ["Curve.StartPoint.Label"] = "Start value:",
            ["Curve.StartPoint.AlwaysZero"] = "(always 0)",
            ["Curve.EndPoint.Label"] = "End value:",
            ["Curve.EndPoint.AlwaysFull"] = "(always 100)",

            ["Curve.Anchor.Slightly"] = "Slightly",
            ["Curve.Anchor.Ideal"] = "Ideal",
            ["Curve.Anchor.Critical"] = "Critical",

            // Band meaning these anchors are built around (verified numerically, see
            // docs\refinements-report.md): 0-30 light/margin available, 30-60 the IDEAL working
            // range (best performance - hold it here), 60-80 starting to lock/spin - NOT yet fully
            // locked/spinning but very close, reduce brake/throttle immediately - 100 fully
            // locked/spinning. The anchor INPUT positions (30/60/80/100) are shared by both channels;
            // only the OUTPUT feel differs (Slip is gentler throughout, matching its own curve).
            ["Curve.Lock.AnchorNote"] = "Each row reads \"when the lock value reaches this raw value, send this output\". Curve (the default) sends 30 to 10, 60 to 30 and 80 to 80 - almost nothing during light braking, a clear buzz in the ideal zone, then a hard ramp as the tyre approaches lock.",
            ["Curve.Lock.StartPoint.Desc"] = "Nothing below this point produces any output. Raise it if light braking buzzes more than you want.",
            ["Curve.Lock.Slightly.Desc"] = "You're just entering the tyre's working range - below this you still have margin, above this you're using real grip. Keep this low to feel nothing during ordinary, gentle braking.",
            ["Curve.Lock.Ideal.Desc"] = "You're at the edge of the ideal braking zone - maximum effective braking, right before the wheel starts to lock. This is the sweet spot: hold the brake here for the fastest stop.",
            ["Curve.Lock.Critical.Desc"] = "The wheel is right on the verge of locking - not locked yet, but only moments away. Ease off the brake immediately.",
            ["Curve.Lock.EndPoint.Desc"] = "At and above this point the wheel is fully locked and sliding - the output is always full strength.",

            ["Curve.Slip.AnchorNote"] = "Each row reads \"when the slip value reaches this raw value, send this output\". Curve (the default) sends 30 to 8, 60 to 20 and 80 to 75 - the same band boundaries as the lock curve, but gentler throughout: almost nothing during gentle throttle, a clear cue in the ideal zone, then a hard ramp as the tyre approaches the traction limit.",
            ["Curve.Slip.StartPoint.Desc"] = "Nothing below this point produces any output. Raise it if light throttle buzzes more than you want.",
            ["Curve.Slip.Slightly.Desc"] = "The tyre is just starting to work under power - below this you still have margin, above this you're really putting power down. Keep this low to feel nothing under gentle, controlled throttle.",
            ["Curve.Slip.Ideal.Desc"] = "You're at the edge of the ideal traction zone - putting down maximum power, right before the tyre starts to spin. This is the sweet spot for the fastest exit without wheelspin.",
            ["Curve.Slip.Critical.Desc"] = "The wheel is right on the verge of spinning - not spinning yet, but only moments away. Ease off the throttle immediately.",
            ["Curve.Slip.EndPoint.Desc"] = "At and above this point the wheel is fully spinning and drive is being wasted - the output is always full strength.",

            // ---- Pulse ----
            ["Pulse.Enable"] = "Pulse instead of holding flat at maximum",
            ["Pulse.GapMs"] = "Gap (ms, one half-cycle)",
            ["Pulse.MinValue"] = "Minimum value during a pulse",
            ["Pulse.FloorNote"] = "The gap can never go below 200 ms (5 Hz) - this floor is enforced by the plugin itself, not only by this spinner.",

            // ---- G-Force tab ----
            ["GForce.Maxima.Note"] = "Each maximum binds separately per game AND per car - switching cars relearns its own maximum under Auto rather than sharing one number.",
            ["GForce.AccelMax.Label"] = "Maximum acceleration G",
            ["GForce.DecelMax.Label"] = "Maximum braking G",
            ["GForce.Mode.Fixed"] = "Fixed",
            ["GForce.Mode.Auto"] = "Auto (learn while driving)",
            ["GForce.LearnedValueFormat"] = "Learned for this car: {0:0.00} g",
            ["GForce.RecommendedHz.Note"] = "Recommended pad frequency for configuring your ShakeIt channels: {0:0} Hz at value 0, down to {1:0} Hz at value 100.",

            ["GForce.Sustain.Note"] = "Under sustained hard braking or acceleration, the trailing pads keep a weaker vibration instead of fading to nothing, so the feel stays continuous. Defaults are derived from this engine's own model, not arbitrary numbers - 0% reproduces the old fade-to-nothing behaviour.",
            ["GForce.Motion.Note"] = "Braking/acceleration feedback separates the STEADY level you're holding from the MOTION of getting there: a quick stab feels like an obvious kick, holding steady settles into a gentle background hum. These three numbers control that feel - higher time constants feel slower/smoother, a higher gain makes sudden changes hit harder.",
            ["GForce.SustainTau.Label"] = "Level response time (s)",
            ["GForce.TransientTau.Label"] = "Kick smoothing time (s)",
            ["GForce.TransientGain.Label"] = "Kick strength",
            ["GForce.Sustain.BrakeBottomRear"] = "Braking - Bottom Rear sustain (%)",
            ["GForce.Sustain.BrakeBackLow"] = "Braking - Back Low sustain (%)",
            ["GForce.Sustain.AccelBottomRear"] = "Acceleration - Bottom Rear sustain (%)",
            ["GForce.Sustain.AccelBackLow"] = "Acceleration - Back Low sustain (%)",

            ["GForce.LateralDirection.Label"] = "Lateral feedback direction",
            ["GForce.LateralDirection.Normal"] = "Normal",
            ["GForce.LateralDirection.Reversed"] = "Reversed",
            ["GForce.LateralDirection.Note"] = "Normal: when you turn left, the vibration travels to and settles on the RIGHT pads (the car's own reaction to cornering loads that side). Reversed: the vibration instead travels to and settles on the LEFT pads when you turn left - pick whichever matches how the corner actually feels to you.",

            // ---- G-Force wheel lock/slip shake ----
            ["GForce.Shake.Enable"] = "Integrate Wheel Lock and Slip",
            ["GForce.Shake.On"] = "On",
            ["GForce.Shake.Off"] = "Off",
            ["GForce.Shake.Note"] = "When on, wheel lock/slip modulates every left/right pad pair as an alternating shake superimposed on the current G-force level (the pair's width, not its centre, grows with how hard the wheel is locking/slipping) - off by default, since it changes the existing G-force feel. The settings below are disabled while this is off.",
            ["GForce.Shake.Frequency.Label"] = "Shake frequency (Hz, 5-20)",
            ["GForce.Shake.LockScale.Label"] = "Wheel Lock scale",
            ["GForce.Shake.SlipScale.Label"] = "Wheel Slip scale",

            // ---- General tab ----
            ["General.EnableDiagnostics"] = "Enable diagnostics",
            ["General.EnableDiagnostics.Note"] = "When off (default), only the Raw/Normalized/Projected wheel values and the 8 G-force channels are published, and nothing extra is logged. When on, internal/diagnostic properties (learned grip state, measured direction, G-force learners) are also published, for troubleshooting in SimHub's property picker or a dashboard.",
            ["General.EnableDiagnostics.RestartNote"] = "SimHub registers properties once at startup - this box needs a SimHub restart to take effect, not just Apply.",
            ["General.ExportCsv"] = "Export session to CSV",
            ["General.ExportCsv.Note"] = "When on, every property (including diagnostics) is written to a CSV file for troubleshooting, in the same folder as this plugin's settings. When off, nothing is written. Takes effect immediately - no restart needed.",
        };
    }
}
