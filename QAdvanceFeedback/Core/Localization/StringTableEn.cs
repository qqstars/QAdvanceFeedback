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

            ["Group.TriggerThreshold"] = "Trigger threshold",
            ["Group.Sources"] = "Sources",
            ["Group.Aggregation"] = "Wheel combination",
            ["Group.Curve"] = "Output data and shaping",
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
            ["Sources.Mode.Manual"] = "Plugin Internal",
            ["Sources.Mode.ShakeIt"] = "ShakeIt Plugin Output Properties",
            ["Sources.ShakeItSetup.Lock"] = "Quick setup: in SimHub, enable the \"ShakeIt Motors\" plugin, create a profile, add a \"Wheels lock\" effect (group \"Slip and lock\"), tick \"Use legacy IRacing algorythm\", tick \"Export output value as a property\" and set \"Property name\" to exactly \"WheelLock.IRacing\", then tick \"Disable output\" so it drives no hardware. Full steps: docs\\shakeit-export-guide.md.",
            ["Sources.ShakeItSetup.Slip"] = "Quick setup: in SimHub, enable the \"ShakeIt Motors\" plugin, create a profile, add a \"Wheels slip\" effect (group \"Slip and lock\"), tick \"Use legacy IRacing algorythm\", tick \"Export output value as a property\" and set \"Property name\" to exactly \"WheelSlip.IRacing\", then tick \"Disable output\" so it drives no hardware. Full steps: docs\\shakeit-export-guide.md.",
            ["Sources.ShakeItUnavailable.Note"] = "Not published yet: SimHub is not currently reporting all four of ShakeIt Motors' exported wheel properties for this channel - follow the steps above, then reopen this tab. This channel keeps working meanwhile: it falls back to its own Raw values automatically until the export appears.",
            ["Sources.Mode.ManualNote.Lock"] = "Plugin Internal: each wheel reads this plugin's own computed value by default. You can still point any wheel's Source field at another SimHub property that reports a 0-100 value for that wheel - 0 means the wheel isn't locking at all, 100 means it's fully locked.",
            ["Sources.Mode.ManualNote.Slip"] = "Plugin Internal: each wheel reads this plugin's own computed value by default. You can still point any wheel's Source field at another SimHub property that reports a 0-100 value for that wheel - 0 means the wheel isn't slipping at all, 100 means it's fully spinning.",

            // ---- TRIGGER THRESHOLD (its own section, above Sources - owner-requested restructure;
            // was previously inside "Sources". Deliberately deviates from SimHub's own hard-coded
            // Brake>20/Throttle>40. Gates the WHOLE channel - Raw AND Normalized both read 0 below
            // threshold - not just the pedal-driven part of the underlying algorithm, and applies no
            // matter which Source (below) is selected: our own Raw, a ShakeIt export, or a Manual
            // property/expression - see the notes below for the ShakeIt-specific composition caveat.) ----
            ["Sources.Threshold.LockBrake"] = "Brake pedal threshold (%)",
            ["Sources.Threshold.SlipBrake"] = "Brake pedal threshold (%)",
            ["Sources.Threshold.SlipThrottle"] = "Throttle pedal threshold (%)",
            ["Sources.Threshold.Lock.Note"] = "Below this brake percentage, Wheel Lock is silent: the Normalized and Projected values read 0, whichever Source is selected. Default 20%. NOTE the Raw values are deliberately NOT gated by this on most titles - Raw exists to reproduce SimHub ShakeIt exactly, and ShakeIt has no pedal threshold of its own, so gating it would make Raw and ShakeIt disagree. Raw is gated only on the simple pedal-and-speed fallback algorithms, where this threshold replaces the fixed 20% ShakeIt hard-codes there. Everything you actually drive from - Normalized, Projected, and the ShakeIt effects fed by them - respects this setting on every title. If Source is set to ShakeIt Plugin Output Properties, ShakeIt also applies its OWN internal gate before publishing, so this threshold can only make the channel MORE restrictive, never less - lowering it will not make ShakeIt's values appear any earlier.",
            ["Sources.Threshold.Slip.Note"] = "Slip checks the brake threshold FIRST: if the brake pedal is pressed past its own threshold, that takes priority and slip reads the same braking value Lock does. Only if brake is below its threshold is throttle checked against its own threshold (and only while the clutch is under 5%, SimHub's own fixed rule). Below whichever threshold applies, Wheel Slip is silent: the Normalized and Projected values read 0, whichever Source is selected - see Wheel Lock's own note above for why the Raw values are deliberately not gated the same way, and for the ShakeIt composition caveat. Default: brake pedal pressed 100% (which means only throttle pedal pressed, checked against its own 40% default, will trigger Wheel Slip) - a deliberate choice so Slip is throttle-only out of the box, even though SimHub's own built-in legacy algorithm applies the identical brake gate to Slip as it does to Lock. Lower the brake threshold toward 20% (matching Wheel Lock's own default) if you prefer Slip to respond to braking the same way SimHub's own algorithm and Wheel Lock do.",
            ["Sources.Threshold.LockSensibility"] = "Lock sensitivity",
            ["Sources.Threshold.LockSensibility.Note"] = "Matches SimHub's own \"Lock sensitivity\" setting exactly (0-100, default 50). Higher values respond sooner to a firm brake, but also cap how strong the reading can ever get - only the default (50) can reach a genuine full-strength reading; values above 50 trade top-end strength for an earlier response.",

            // ---- AGGREGATION (docs\aggregation-report.md) - how the four wheels above combine into
            // Front/Rear/Left/Right/All. The owner's own physically-motivated Max/Min axle blend +
            // Front/Rear weight-transfer scheme, replacing the previous generic aggregation - every
            // weight is independently configurable per channel; only Wheel Slip has a floor control. ----
            ["Aggregation.Lock.Note"] = "How the four wheels above combine into Front/Rear/Left/Right/All. Under braking, load shifts forward, so the front wheels carry the grip and matter most - that weight transfer is what these numbers encode.",
            ["Aggregation.Slip.Note"] = "How the four wheels above combine into Front/Rear/Left/Right/All. Under power, the driven wheels are the ones that spin - which axle that is depends on your car's drivetrain, so these numbers are tunable rather than fixed.",
            ["Aggregation.WMax.Label"] = "Max wheel weight (per axle)",
            ["Aggregation.WMin.Label"] = "Min wheel weight (per axle)",
            ["Aggregation.WFront.Label"] = "Front axle weight (car level)",
            ["Aggregation.WRear.Label"] = "Rear axle weight (car level)",
            ["Aggregation.AxleHelp"] = "Max/Min blend the two wheels on the SAME axle (e.g. front-left and front-right) into one Front or Rear value - continuously, not with a hard cutoff, so one wheel taking over from the other is never felt as a click.",
            ["Aggregation.SideHelp.Lock"] = "Front/Rear weight decides how much the front axle counts against the rear at the Left/Right/All level - under braking the front wheels carry the load, so they dominate the combined value by default (0.90 vs 0.10).",
            ["Aggregation.SideHelp.Slip"] = "Front/Rear weight decides how much the front axle counts against the rear at the Left/Right/All level - under power the driven wheels are the ones that spin, so raise the rear weight (or swap the two entirely) if your car is rear- or four-wheel drive.",
            ["Aggregation.FloorFactor.Label"] = "Slip floor (fraction of the strongest wheel)",
            ["Aggregation.FloorFactor.Note"] = "Keeps a single strongly-spinning wheel from being averaged away: the combined value can never read below this fraction of whichever wheel feeding it is spinning hardest. Wheel Lock has no equivalent control - a locked wheel already carries through strongly via the front weight above, without needing a floor.",

            // ---- Curve editor (shared layout, per-channel defaults/wording) ----
            ["Curve.Preset.Label"] = "Preset:",
            ["Curve.Preset.Linear"] = "Linear",
            ["Curve.Preset.Curve"] = "Curve",
            ["Curve.Preset.Custom"] = "Custom",
            ["Curve.Preset.LinearCustom"] = "Linear (customised)",
            ["Curve.Preset.CurveCustom"] = "Curve (customised)",
            ["Curve.Column.RawValue"] = "source value",
            ["Curve.Graph.Source"] = "Source value -> Projected value",
            ["KeyData.Marker.SMax"] = "SMax",
            ["KeyData.Marker.S90"] = "S90",
            ["KeyData.Marker.S75"] = "S75",
            ["KeyData.Marker.Perfect"] = "Perfect",
            ["KeyData.Marker.Great"] = "Great",
            ["KeyData.Marker.Good"] = "Good",
            ["Curve.Graph.Normalized"] = "Normalized value -> Final Projected value",
            ["Curve.Column.OutputValue"] = "output value",
            // PRE-RELEASE ADDITION (configurable per-setpoint flatten ranges): a third NumericUpDown
            // column per setpoint row, defaulting to 3/2/2 for Powerful/Ideal/Max Grip - see
            // OutputProjector.AcceptSetpointWithFlatten's own remarks for the exact mechanism.
            ["Curve.Column.FlattenRange"] = "flatten range",
            ["Curve.Column.FlattenRange.Note"] = "How far either side of this set point (in raw input units) stays close to its own output, forming a near-flat platform instead of a sharp ramp through it. 0 disables flattening for this set point. Ships at 3/2/2 for the Powerful/Ideal/Max Grip set points by default, so the Ideal and Max Grip plateaus land exactly on the shared 60 and 80 band boundaries.",
            // Replaces the old per-row LIVE readout ("at this input, the curve currently sends
            // {0}") - redundant with the spinner textboxes right next to it, which already show the
            // current values. Instead states the SHIPPED DEFAULT input->output mapping for this
            // anchor, which the spinners cannot show once a driver has edited them. The live value is
            // NOT dropped outright, though: Curve.Anchor.LiveDiffersFormat is appended alongside it
            // whenever the curve's actual output at the driver's own typed input differs from what
            // they typed (an anchor at/below Start, at/above End, or reordered past a neighbour gets
            // dropped/clamped by OutputProjector - see its own remarks) - see
            // docs\curve-help-text-report.md for why a silent restatement was rejected.
            ["Curve.Anchor.DefaultFormat"] = "under default input value {0}, curve will return {1}",
            ["Curve.Anchor.LiveDiffersFormat"] = "note: the curve actually returns {0} here, not what you typed - it may be clamped or dropped",

            ["Curve.StartPoint.Label"] = "Start value:",
            ["Curve.EndPoint.Label"] = "End value:",
            // PRE-RELEASE ADDITION: the old static "(always 0)"/"(always 100)" text is replaced by real
            // editable Start/End output spinners (Curve.StartOutput/Curve.EndOutput in the XAML) - these
            // are their tooltips. See ProjectorSettings.StartOutput/EndOutput's own remarks for the
            // continuous-floor/ceiling design and the "permanent baseline hum" consequence.
            ["Curve.StartOutput.Help"] = "The output held at and below the Start raw value, to the left - a continuous baseline, not a one-time floor: once the channel engages, this level is felt for as long as the input stays at or below Start. 0 by default (no output at all before Start). Raising it is useful if a motor's own dead zone would otherwise swallow a small value, but it becomes a permanent hum for the entire time the channel is engaged, not merely a raised floor on the ramp.",
            ["Curve.EndOutput.Help"] = "The output held at and above the End raw value, to the left - a hard ceiling on the maximum strength the effect can ever reach. 100 by default (full strength). Lower it to cap the effect below full strength.",

            // RENAMED (v1.0.6.9 rework, Goal 4 - docs\v1068-rework-report.md), only after Goal 3's
            // 30/60 anchors were verified: "Slightly" -> "Powerful". New meaning, matching the owner's
            // own final specification: near-30 marks the start of a POWERFUL brake/throttle application -
            // good enough, but not yet ideal; holding in the 30-60 band gives a GOOD brake/throttle;
            // holding in the 60-80 band gives the IDEAL brake/throttle and the best result.
            //
            // PRE-RELEASE ADDITION: each label now carries a parenthesised NUMBER - the NORMALIZED BAND
            // value the anchor's own flatten-range plateau reaches (30/60/80), deliberately DIFFERENT
            // from the curve editor's own "raw value" column for Ideal/Max Grip (62/78 - the underlying
            // threshold, moved there so the plateau EDGES land on 60/80 - see ProjectorSettings'
            // Change-2c remarks). This is deliberately STATIC text, not generated from the threshold
            // field, per the owner's own explicit instruction (auto-generating it would show 62/78 and
            // defeat the point of the parenthesised number).
            //
            // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - these labels now LIVE-SWITCH
            // with Wheel Lock's own Normalize Pattern selector (Wheel Slip has no selector and keeps a
            // single, fixed set always - see the "Curve.Anchor.Slip.*" keys below, which restore the
            // ORIGINAL "Slightly"/"Ideal" wording an earlier rename had incorrectly applied to Slip too,
            // since Slip shared the same keys Lock's Mapping-mode rename touched):
            // <list type="bullet">
            // <item>Mapping (default): "Powerful (30)" / "Perfect (60)" / "Max Grip (80)" - the v1.0.6.9
            // rework's own wording above, with "Ideal" renamed to "Perfect" to free up "Ideal" for
            // Max-Grip Only mode below.</item>
            // <item>Max-Grip Only: "Slightly (30)" / "Ideal (60)" / "Max Grip (80)" - the ORIGINAL,
            // pre-rework wording (see docs\release-1060-report.md's own 1.0.5 comparison), since under
            // this pattern only the Max-Grip anchor's own output is guaranteed reliable and the simpler,
            // older framing fits that better.</item>
            // </list>
            // The THIRD anchor ("Max Grip (80)") is IDENTICAL text in both modes - only the first two
            // change - so it keeps a single shared key.
            ["Curve.Anchor.Lock.Mapping.Slightly"] = "Powerful (30)",
            ["Curve.Anchor.Lock.Mapping.Ideal"] = "Perfect (60)",
            ["Curve.Anchor.Lock.MaxGripOnly.Slightly"] = "Slightly (30)",
            ["Curve.Anchor.Lock.MaxGripOnly.Ideal"] = "Ideal (60)",
            ["Curve.Anchor.Slip.Slightly"] = "Slightly (30)",
            ["Curve.Anchor.Slip.Ideal"] = "Ideal (60)",
            ["Curve.Anchor.Critical"] = "Max Grip (80)",

            // ---- Normalize Pattern selector (1.0.6.0, docs\release-1060-report.md, Part 2's UI half) -
            // Wheel Lock ONLY, at the top of the "Output data and shaping" section. Wheel Slip has no
            // equivalent control at all; it always uses the Mapping-equivalent formula it always has.
            // Default is Mapping (matches NormalizedWheelLockSlipEngine.LockNormalizePattern's own
            // field-initialiser default and WheelChannelSettings.NormalizePattern's own default).
            ["Curve.NormalizePattern.Label"] = "Normalize Pattern:",
            ["Curve.NormalizePattern.MaxGripOnly"] = "Max-Grip Only",
            ["Curve.NormalizePattern.Mapping"] = "Max-Grip/S90/S75 Mapping",
            ["Curve.NormalizePattern.MaxGripOnly.Desc"] = "Only applies general scaling based on the Max-Grip point. Only the Max-Grip normalized output is guaranteed reliable.",
            ["Curve.NormalizePattern.Mapping.Desc"] = "Applies scaling based on the 100%/90%/75% points of Max-Grip, each range scaled individually. The 100%/90%/75% normalized outputs are guaranteed reliable.",
            // ---- KEY DATA POINTS (v1.0.7.2) ----
            ["KeyData.Section.Lock"] = "Key Data Points Settings - SMax/S90/S75",
            ["KeyData.Section.Slip"] = "Key Data Points Settings - Perfect/Great/Good",
            ["KeyData.AutoGenerate.Label"] = "Auto Generate",
            ["KeyData.AutoGenerate.Desc"] = "Learn these points from your own driving. Learning always runs; turning this off only makes the plugin publish your configured values instead.",
            ["KeyData.PerGame.Label"] = "Per-Game",
            ["KeyData.PerGame.Desc"] = "Keep a separate set of values for each game, instead of one set shared by all of them.",
            ["KeyData.Lock.SMax.Label"] = "Source value of Max-Grip",
            ["KeyData.Lock.S90.Label"] = "Source value of 90% Grip",
            ["KeyData.Lock.S75.Label"] = "Source value of 75% Grip",
            ["KeyData.Slip.SMax.Label"] = "Source value of Perfect point",
            ["KeyData.Slip.S90.Label"] = "Source value of Great point",
            ["KeyData.Slip.S75.Label"] = "Source value of Good point",
            ["KeyData.Lock.SMax.Desc"] = "The source reading at which the wheel is fully at its locking limit.",
            ["KeyData.Lock.S90.Desc"] = "The source reading at 90% of available grip.",
            ["KeyData.Lock.S75.Desc"] = "The source reading at 75% of available grip.",
            ["KeyData.Slip.SMax.Desc"] = "The source reading at which wheelspin is at its limit.",
            ["KeyData.Slip.S90.Desc"] = "The source reading for a great, near-limit launch.",
            ["KeyData.Slip.S75.Desc"] = "The source reading for a good, controlled launch.",
            ["KeyData.LearnedPrefix"] = "[Learned Value: {0}]  ",
            ["KeyData.LearnedPending"] = "[Learned Value:  --.-]  ",
            ["KeyData.Waiting"] = "Warming up - your values apply once this session has finished calibrating and you have driven for 30 seconds.",
            ["KeyData.Invalid"] = "Needs Max-Grip >= 90% >= 75%, and every value between 0 and 100.",
            ["KeyData.Invalid.Slip"] = "Needs Perfect >= Great >= Good, and every value between 0 and 100.",
            ["KeyData.ResetToDefault"] = "Reset to default",
            ["Curve.NormalizePattern.Slip.MaxGripOnly"] = "Perfect Point Only",
            ["Curve.NormalizePattern.Slip.Mapping"] = "Perfect/Great/Good Mapping",

            // Band meaning these anchors are built around (verified numerically, see
            // docs\refinements-report.md; the anchor rescale that moved the grip-limit anchor onto
            // this top anchor's own input position is documented in docs\anchor-rescale-report.md):
            // 0-30 light/margin available, 30-60 power braking/acceleration working up toward ideal,
            // 60-80 the ideal working range (best performance - hold it here), up to the measured
            // grip limit AT 80 - the maximum braking/traction the tyre can deliver - 80-100 past the
            // limit, the wheel is locking/spinning, reduce brake/throttle immediately. The anchor
            // BAND positions (30/60/80/100) are shared by both channels; only the OUTPUT feel
            // differs (Slip is gentler throughout, matching its own curve). PRE-RELEASE Change 2c:
            // the Ideal/Max Grip curve editor THRESHOLDS moved to 62/78 (paired with flatten ranges of
            // 2 each) so each plateau's own edge still lands exactly on the shared 60/80 boundary -
            // "raw value" 62/78 below is what the curve editor's own column shows.
            // 1.0.6.0 (docs\release-1060-report.md, Part 4d): the "78 to 80" figure below became stale
            // once Lock's own Critical/Max Grip OUTPUT moved 80 -> 60 - corrected to "78 to 60", and the
            // "reaching full strength right at the limit" claim (which was only ever true while Critical
            // output was 80) is removed since Max Grip no longer reads full strength.
            ["Curve.Lock.AnchorNote"] = "Each row reads \"when the lock value reaches this raw value, send this output\". Curve (the default) sends 30 to 10, 62 to 30 and 78 to 60 - almost nothing during light braking, a clear buzz in the ideal zone, then a strong cue as the tyre approaches lock. Each anchor's own flatten range widens it into a brief plateau instead of a sharp ramp through it - by default the Ideal and Max Grip plateaus land exactly on the shared 60 and 80 band boundaries (60-64 and 76-80), smooth in between and ramping quickly outside that 60-80 band.",
            ["Curve.Lock.StartPoint.Desc"] = "Below this raw value the output holds steady at the Start output shown to the right (0 by default - no output at all). Raise the raw value if light braking buzzes more than you want. Ships at {0} by default.",

            // 1.0.6.0 (docs\release-1060-report.md, Part 2's UI half) - Lock's Slightly/Ideal
            // descriptions now LIVE-SWITCH with the Normalize Pattern selector; see
            // "Curve.Anchor.Lock.*" above for the matching label rename. Mapping mode keeps the
            // v1.0.6.9 rework's own "Powerful"/"Perfect" wording (renamed from "Ideal" only in display
            // text; the underlying band meaning is unchanged); Max-Grip Only mode restores the ORIGINAL,
            // pre-rework 1.0.5 wording verbatim (extracted from that release's own shipped source), since
            // under this pattern only the Max-Grip anchor's own output is guaranteed reliable and the
            // simpler, older framing fits that better.
            ["Curve.Lock.Mapping.Slightly.Desc"] = "This is where a POWERFUL brake application starts, at raw value 30 - good enough, but not yet ideal. Holding the brake in the band above this (up to Perfect) gives a good, solid stop; holding it in the Perfect-to-Max-Grip band gives the ideal brake and the best result. Keep this low to feel nothing during ordinary, gentle braking.",
            ["Curve.Lock.Mapping.Ideal.Desc"] = "You're at the edge of the perfect braking zone, starting at raw value 60 - maximum effective braking, right before the wheel starts to lock. This is the sweet spot: hold the brake here for the fastest stop.",
            // VERBATIM from QAdvanceFeedback_1.0.5.zip's own StringTableEn.cs (Curve.Lock.Slightly.Desc/
            // Curve.Lock.Ideal.Desc, before the v1.0.6.9 rework and the 62/78 threshold move) - no raw
            // value called out, since 1.0.5 predates both.
            ["Curve.Lock.MaxGripOnly.Slightly.Desc"] = "You're just entering the tyre's working range - below this you still have margin, above this you're using real grip. Keep this low to feel nothing during ordinary, gentle braking.",
            ["Curve.Lock.MaxGripOnly.Ideal.Desc"] = "You're at the edge of the ideal braking zone - maximum effective braking, right before the wheel starts to lock. This is the sweet spot: hold the brake here for the fastest stop.",
            ["Curve.Lock.Critical.Desc"] = "This is the measured grip limit, at raw value 80 - the maximum braking force the tyre can deliver. Above this point you are past the limit and the wheel is locking. Release the brake immediately.",
            ["Curve.Lock.EndPoint.Desc"] = "At and above this raw value the wheel is fully locked and sliding, and the output holds steady at the End output shown to the right (100 by default - full strength). Lower the End output to cap how strong the cue is ever allowed to get. Ships at {0} by default.",

            ["Curve.Slip.AnchorNote"] = "Each row reads \"when the slip value reaches this raw value, send this output\". Curve (the default) sends 30 to 10, 62 to 35 and 78 to 70 - the same band boundaries as the lock curve, and now much closer to it in the ideal zone too: almost nothing during gentle throttle, a clear cue in the ideal zone, then a strong cue as the tyre approaches the traction limit. Each anchor's own flatten range widens it into a brief plateau instead of a sharp ramp through it - by default the Ideal and Max Grip plateaus land exactly on the shared 60 and 80 band boundaries (60-64 and 76-80), smooth in between and ramping quickly outside that 60-80 band.",
            ["Curve.Slip.StartPoint.Desc"] = "Below this raw value the output holds steady at the Start output shown to the right (0 by default - no output at all). Raise the raw value if light throttle buzzes more than you want. Ships at {0} by default.",
            ["Curve.Slip.Slightly.Desc"] = "This is where a POWERFUL throttle application starts, at raw value 30 - good enough, but not yet ideal. Holding the throttle in the band above this (up to Ideal) gives a good, solid launch; holding it in the Ideal-to-Max-Grip band gives the ideal throttle and the best result. Keep this low to feel nothing under gentle, controlled throttle.",
            ["Curve.Slip.Ideal.Desc"] = "You're at the edge of the ideal traction zone, starting at raw value 60 - putting down maximum power, right before the tyre starts to spin. This is the sweet spot for the fastest exit without wheelspin.",
            ["Curve.Slip.Critical.Desc"] = "This is the measured grip limit, at raw value 80 - the maximum traction the tyre can deliver. Above this point you are past the limit and the wheel is spinning. Release the throttle immediately.",
            ["Curve.Slip.EndPoint.Desc"] = "At and above this raw value the wheel is fully spinning and drive is being wasted, and the output holds steady at the End output shown to the right (100 by default - full strength). Lower the End output to cap how strong the cue is ever allowed to get. Ships at {0} by default.",

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
            ["GForce.Mode.Auto.Short"] = "Auto",
            ["GForce.Readout.Fixed"] = "Fixed: {0:0.0}G",
            ["GForce.Readout.Auto.Detected"] = "Default: {0:0.0}G. Auto detected: {1:0.0}G",
            ["GForce.Readout.Auto.NoDataYet"] = "Default: {0:0.0}G. Auto: still using default (no data yet)",
            ["GForce.RecommendedHz.Note"] = "Recommended pad frequency for configuring your ShakeIt channels: {0:0} Hz at value 0, down to {1:0} Hz at value 100.",

            ["GForce.Sustain.Note"] = "Under sustained hard braking or acceleration, the trailing pads keep a weaker vibration instead of fading to nothing, so the feel stays continuous. Defaults are derived from this engine's own model, not arbitrary numbers - 0% reproduces the old fade-to-nothing behaviour.",
            ["GForce.Motion.Note"] = "Braking/acceleration feedback travels through three stages (far pad, then middle pad, then the pad closest to the direction of force) before settling - a hard stamp on the pedal sweeps through quickly and strongly, a gentle change sweeps slowly and gently. The STEADY level you're holding (how far each pad settles) is separate from this travel. These three numbers control the feel - a higher level response time feels slower/smoother once settled, a higher sweep speed makes a sudden change travel faster. To make the sweep feel slower, lower Sweep speed (it travels more gradually across the pads) and raise Sweep smoothing time (it eases in rather than snaps). Sweep speed is capped internally, so past a point turning it up further won't make the sweep any faster. Raising Sweep speed also lowers the input rate at which that cap kicks in, so above that point gentle and violent changes start to feel more alike.",
            ["GForce.SustainTau.Label"] = "Level response time (s)",
            ["GForce.TransientTau.Label"] = "Sweep smoothing time (s)",
            ["GForce.TransientGain.Label"] = "Sweep speed",
            ["GForce.TransitionScale.Auto.Label"] = "Transition scale (Auto mode)",
            ["GForce.TransitionScale.Fixed.Label"] = "Transition scale (Fixed mode)",
            ["GForce.TransitionScale.Note"] = "Amplifies the travelling sensation of the sweep so a low-grip car still produces a full-feeling transition, without changing how hard the sustained level says you are actually braking or accelerating relative to the car's own capability - that always stays true to the real g. Two separate scales apply depending on whether the axis is set to Auto or Fixed above (blended smoothly, never a sudden jump, whenever the axis switches between using its default and using a detected value). 1.0 reproduces the original, unscaled sweep exactly; 0 turns the extra amplification off entirely without touching the sustain.",
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
            ["GForce.Shake.Note"] = "When on, wheel lock/slip modulates every left/right pad pair as an alternating shake superimposed on the current G-force level (the pair's width, not its centre, grows with how hard the wheel is locking/slipping) - on by default so a fresh install feels this without hunting for the toggle; it stays inert with no lock/slip signal wired up. Uncheck to fall back to the plain G-force feel. The settings below are disabled while this is off.",
            ["GForce.Shake.Frequency.Label"] = "Shake frequency (Hz, 1-20)",
            ["GForce.Shake.LockScale.Label"] = "Wheel Lock scale (1.0 = 100%)",
            ["GForce.Shake.SlipScale.Label"] = "Wheel Slip scale (1.0 = 100%)",

            // ---- General tab ----
            ["General.EnableDiagnostics"] = "Enable diagnostics",
            ["General.EnableDiagnostics.Note"] = "When off (default), only the Raw/Normalized/Projected wheel values and the 8 G-force channels are published, and nothing extra is logged. When on, internal/diagnostic properties (learned grip state, measured direction, G-force learners) are also published, for troubleshooting in SimHub's property picker or a dashboard.",
            ["General.EnableDiagnostics.RestartNote"] = "SimHub registers properties once at startup - this box needs a SimHub restart to take effect, not just Apply.",
            ["General.ExportCsv"] = "Export session to CSV",
            ["General.ExportCsv.Note"] = "When on, every property (including diagnostics) is written to a CSV file for troubleshooting, in the same folder as this plugin's settings. When off, nothing is written. Takes effect immediately - no restart needed.",
            // 1.0.6.0 (docs\release-1060-report.md, Part 4f) - {0} is read from the running assembly's
            // own AssemblyFileVersion at display time, never hardcoded - see SettingsControl.xaml.cs's
            // own remarks on why (a hand-typed version string cannot be caught by the compiler when it
            // drifts from the DLL actually shipped, the same lesson docs\curve-help-text-report.md
            // already drew for the curve editor's own help text).
            ["General.Version.Label"] = "QAdvanceFeedback version: {0}",
            ["General.ShakeItImport.Group"] = "ShakeIt Reference Data",
            ["General.ShakeItImport.Note"] = "This plugin's Raw values reproduce SimHub's ShakeIt Motors arithmetic exactly. The one input it cannot ship is SimHub's own per-game calibration file, which SimHub is licensed to distribute and this plugin is not. Importing it from your SimHub installation makes the Raw values match ShakeIt's for every game that file covers. This runs automatically the first time the plugin starts with no calibration of its own, so you normally never need this button.",
            ["General.ShakeItImport.Override"] = "Override current data if exists?",
            ["General.ShakeItImport.Override.Note"] = "Off (default): where this plugin has already learned or imported a calibration for a game, that entry is kept and the imported one is skipped. On: the imported SimHub values replace this plugin's own for every matching entry. Leave it off unless you specifically want to discard what this plugin has learned and go back to SimHub's shipped numbers.",
            ["General.ShakeItImport.Button"] = "Convert SimHub ShakeIt Reference Data",

            // ---- Plugin health section (General tab) - invisible/one-line when nothing is wrong;
            // see Core.Health.HealthRegistry. ----
            ["Group.Health"] = "Plugin health",
            ["Health.AllGood"] = "All systems normal - nothing to report.",
            ["Health.CopyDetails"] = "Copy details for a bug report",
            ["Health.CopyDetails.Done"] = "Copied to clipboard.",
            ["Health.SimHubUpdateNeeded"] = "This feature needs an update for your SimHub version.",

            ["Health.Subsystem.ScriptEditor"] = "Script editor",
            ["Health.Subsystem.PropertyPicker"] = "Property picker",
            ["Health.Subsystem.ExpressionEvaluator"] = "Expression evaluator",
            ["Health.Subsystem.ShakeItExport"] = "ShakeIt Motors export reading",
            ["Health.Subsystem.CapabilityDetection"] = "Capability detection",
            ["Health.Subsystem.ConfigPersistence"] = "Settings file",
            ["Health.Subsystem.RuntimePersistence"] = "Learned-calibration file",
            ["Health.Subsystem.CsvExport"] = "CSV export",
            ["Health.Subsystem.BackgroundFlush"] = "Background save",
            ["Health.Subsystem.TelemetryAdapter"] = "Telemetry processing",
            ["Health.Subsystem.PropertyPublish"] = "Property publishing",
            ["Health.Subsystem.Init"] = "Plugin startup",
            ["Health.Subsystem.Shutdown"] = "Plugin shutdown",
            ["Health.Subsystem.SettingsUi"] = "Settings screen",

            ["Health.Impact.ScriptEditor"] = "The built-in script editor button is unavailable. You can still type a plain SimHub property name or a formula by hand into the Source field.",
            ["Health.Impact.PropertyPicker"] = "The property picker ('Pick...') button is unavailable. You can still type a plain SimHub property name by hand into the Source field.",
            ["Health.Impact.ExpressionEvaluator"] = "JavaScript/NCalc expressions cannot be evaluated this session. Configured sources fall back to plain SimHub property names only.",
            ["Health.Impact.ShakeItExport"] = "Reading ShakeIt Motors' exported wheel properties failed unexpectedly. This channel falls back to its own Raw values automatically.",
            ["Health.Impact.CapabilityDetection"] = "Diagnostic wheel-telemetry/capability properties are unavailable this session. The main Wheel Lock/Slip and G-Force outputs are unaffected.",
            ["Health.Impact.ConfigPersistence"] = "Your settings could not be loaded or saved; shipped defaults are being used instead. Re-apply your tuning once this is resolved.",
            ["Health.Impact.RuntimePersistence"] = "Learned calibration data could not be loaded or saved; the plugin keeps learning fresh this session but may not remember it next time.",
            ["Health.Impact.CsvExport"] = "CSV session export stopped; every other feature is unaffected.",
            ["Health.Impact.BackgroundFlush"] = "Learned calibration could not be saved in the background; it will still be saved once when SimHub closes, if possible.",
            ["Health.Impact.TelemetryAdapter"] = "One or more telemetry frames were skipped; output pauses briefly rather than showing an incorrect value.",
            ["Health.Impact.PropertyPublish"] = "One of this plugin's published SimHub properties briefly reported a neutral value instead of its real reading.",
            ["Health.Impact.Init"] = "The plugin did not finish starting up this session; its outputs may be inactive until SimHub is restarted.",
            ["Health.Impact.Shutdown"] = "The plugin could not fully save state while SimHub was closing.",
            ["Health.Impact.SettingsUi"] = "The settings screen failed to load; see the SimHub log for details.",
        };
    }
}
