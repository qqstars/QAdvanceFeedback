using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QAdvanceFeedback;
using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE VIPER SOURCE (owner, 2026-09-25 - v1.1.0): a third <see cref="SourceMode"/> reading
    /// viper4gh's CalcLngWheelSlip plugin through a shipped NCalc preset, with its own cold-start SMax
    /// references measured from the owner's own capture.
    /// <para/>
    /// THE ORIGINAL viper4gh PLUGIN ONLY - the community fork uses a different plugin name and is
    /// explicitly out of scope, which several of these tests pin so it cannot creep back in.
    /// </summary>
    public class ViperSourceTests
    {
        private readonly ITestOutputHelper _out;
        public ViperSourceTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void The_property_names_are_the_original_viper_plugins()
        {
            // The whole feature is this string being exactly right - a wrong prefix does not fail, it
            // evaluates cleanly to 0 forever. See ViperPropertyNames' own remarks.
            Assert.Equal("ViperDataPlugin", ViperPropertyNames.PluginTypeName);
            Assert.Equal("ViperDataPlugin.CalcLngWheelSlip.Computed.LngWheelSlip_", ViperPropertyNames.ComputedPrefix);
            Assert.Equal("ViperDataPlugin.CalcLngWheelSlip.Computed.LngWheelSlip_FL", ViperPropertyNames.GetRawPropertyName("FL"));

            // The fork's name must appear nowhere - it registers under its own prefix and its scale is
            // not what the shipped SMax constants were measured against.
            foreach (string wheel in MotorsExportPropertyNames.WheelSuffixes)
                foreach (bool isLock in new[] { true, false })
                    Assert.DoesNotContain("WheelSlipLearner", ViperPropertyNames.GetScript(isLock, wheel));
        }

        [Fact]
        public void Wheel_suffixes_are_mapped_to_vipers_abbreviations_not_concatenated()
        {
            Assert.Equal("FL", ViperPropertyNames.ViperWheelSuffixFor(MotorsExportPropertyNames.FrontLeft));
            Assert.Equal("FR", ViperPropertyNames.ViperWheelSuffixFor(MotorsExportPropertyNames.FrontRight));
            Assert.Equal("RL", ViperPropertyNames.ViperWheelSuffixFor(MotorsExportPropertyNames.RearLeft));
            Assert.Equal("RR", ViperPropertyNames.ViperWheelSuffixFor(MotorsExportPropertyNames.RearRight));
            // Never guessed at: an unrecognised suffix must not produce a name that reads 0 forever.
            Assert.Null(ViperPropertyNames.ViperWheelSuffixFor("Spare"));
            Assert.Equal(string.Empty, ViperPropertyNames.GetScript(true, "Spare"));
        }

        [Fact]
        public void Lock_reads_the_positive_half_and_slip_the_negated_half()
        {
            // The owner's own sign convention: LngWheelSlip is POSITIVE for lock, NEGATIVE for slip.
            string lockScript = ViperPropertyNames.GetScript(true, MotorsExportPropertyNames.FrontLeft);
            string slipScript = ViperPropertyNames.GetScript(false, MotorsExportPropertyNames.FrontLeft);
            _out.WriteLine(lockScript);
            _out.WriteLine(slipScript);

            // THE OWNER'S OWN CLAMP, verbatim except that every literal is a double (2026-09-29:
            // "PLEASE USE MIN/MAX THAT I PROVIDED ... SIMPLY MAKE SURE ALWAYS USE DECIMAL NUMBER").
            const string p = "[ViperDataPlugin.CalcLngWheelSlip.Computed.LngWheelSlip_FL]";
            Assert.Equal($"min(1.0, max(0.0, {p})) * 100.0", lockScript);
            Assert.Equal($"min(1.0, max(0.0, -1.0 * {p})) * 100.0", slipScript);

            // Each channel floors the other's half at zero, so neither can ever see it.
            Assert.Contains("max(0.0,", lockScript);
            Assert.Contains("max(0.0,", slipScript);
        }

        [Fact]
        public void No_game_running_is_NOT_an_unsupported_game()
        {
            // THE DEFECT THIS CLOSES, owner-reported 2026-09-30 ("Viper support warning will always be
            // displayed, and always use Raw" / "Viper Detection incorrect"), reproduced by driving the
            // real settings control.
            //
            // DataUpdate returns early unless GameRunning && !GamePaused && !GameInMenu, so the 1 Hz
            // push that sets the page's _currentGameId NEVER runs at the SimHub menu - which is exactly
            // where a driver configures a plugin. _currentGameId stayed "", IsSupported("") is false,
            // and the channel presented as permanently unsupported: amber warning, source rows disabled
            // showing Raw, and - the damaging part - the page re-keyed its key data points to RAW's
            // identity, so every number typed while configuring was filed under the wrong source.
            //
            // IsSupported itself is right to say false for "" - it is asked "is this game in the list".
            // The judgement about an ABSENT game belongs to the caller, so the guard lives there.
            var shipped = ViperSupportedGames.CreateShippedDocument();
            Assert.False(ViperSupportedGames.IsSupported("", shipped.SupportedGames));
            Assert.False(ViperSupportedGames.IsSupported(null, shipped.SupportedGames));

            string plugin = File.ReadAllText(Path.Combine(RepoRoot(), "QAdvanceFeedback", "QAdvanceFeedback.cs"));
            int start = plugin.IndexOf("public bool ViperSourceBlocked", System.StringComparison.Ordinal);
            Assert.True(start > 0, "ViperSourceBlocked not found");
            string body = plugin.Substring(start, plugin.IndexOf(';', start) - start);

            Assert.Contains("!string.IsNullOrWhiteSpace(gameId)", body);
            _out.WriteLine(body);
        }

        [Fact]
        public void The_games_the_owners_own_rig_reports_are_in_the_shipped_list()
        {
            // Taken from the real parameter file captured on the owner's rig, whose learner keys are
            // "game|#|car|#|source|#|surface" - so these are SimHub's OWN GameName values, not names
            // guessed from Viper's source. F12025 is the one the Viper sessions were recorded under.
            //
            // NOTE THE SHAPE: "F12025", with no space. A display name like "F1 25" would NOT match, and
            // that distinction is the whole reason this test exists.
            var shipped = ViperSupportedGames.CreateShippedDocument();
            Assert.True(ViperSupportedGames.IsSupported("F12025", shipped.SupportedGames));
            Assert.True(ViperSupportedGames.IsSupported("EAWRC23", shipped.SupportedGames));
            Assert.False(ViperSupportedGames.IsSupported("F1 25", shipped.SupportedGames));
            // Forza Horizon 6 also appears on that rig and Viper genuinely does not support it.
            Assert.False(ViperSupportedGames.IsSupported("FH6", shipped.SupportedGames));
        }

        [Fact]
        public void Plugin_availability_is_re_sampled_while_driving_not_once_at_construction()
        {
            // The other half of "Viper Detection incorrect": _viperAvailable was readonly, sampled once
            // in the settings control's constructor. viper4gh publishes nothing until a supported game
            // is running, and the page is normally opened BEFORE that - so "Plugin not detected" stuck
            // on for the whole session however long the driver drove.
            string code = File.ReadAllText(Path.Combine(
                RepoRoot(), "QAdvanceFeedback", "Settings", "SettingsControl.xaml.cs"));

            Assert.DoesNotContain("private readonly bool _viperAvailable", code);
            Assert.DoesNotContain("private readonly bool _lockMotorsExportAvailable", code);
            Assert.Contains("private void RefreshSourceAvailability()", code);

            // Wired into the 1 Hz push, which is the only thing that runs while actually driving.
            int push = code.IndexOf("public void UpdateLearnedKeyDataPoints", System.StringComparison.Ordinal);
            int pushEnd = code.IndexOf("\n        /// <summary>", push, System.StringComparison.Ordinal);
            Assert.Contains("RefreshSourceAvailability()", code.Substring(push, pushEnd - push));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            return dir.FullName;
        }

        [Fact]
        public void No_isnull_because_it_would_destroy_slips_fallback()
        {
            // A property SimHub does not know evaluates to NULL, not 0 (NCalcEngineBase.EvaluateParameter
            // never assigns args.Result and only forces HasResult). What happens next differs per channel,
            // and isnull would flatten the difference the wrong way:
            //   - LOCK  max(0.0, null) -> 0.0, because NCalc.Numbers.Max opens `if (b == null) return a;`.
            //           Evaluation SUCCEEDS, so there is no fallback. The supported-game list covers this
            //           case instead, by switching the channel off Viper before it is ever evaluated.
            //   - SLIP  -1.0 * null THROWS first (Numbers.Multiply does a.GetType()), TryEvaluate returns
            //           false, and WheelSourceResolver substitutes Raw for that wheel.
            // Adding isnull(x, 0.0) would change nothing for Lock and would silently convert Slip's throw
            // into a confident zero - losing the one fallback path this shape still has.
            foreach (bool isLock in new[] { true, false })
                foreach (string wheel in MotorsExportPropertyNames.WheelSuffixes)
                    Assert.DoesNotContain("isnull", ViperPropertyNames.GetScript(isLock, wheel));

            // The asymmetry above is a property of the shape, so pin the shape: Slip must negate OUTSIDE
            // the max() - `max(0.0, -1.0 * p)` throws on null, `-1.0 * max(0.0, p)` would not.
            string slip = ViperPropertyNames.GetScript(false, MotorsExportPropertyNames.FrontLeft);
            Assert.Contains("max(0.0, -1.0 * [", slip);
        }

        [Fact]
        public void Every_numeric_literal_is_a_double()
        {
            // THE DEFECT THIS GUARDS, and it is invisible by inspection. Decompiled from SimHub's own
            // NCalc.dll, NCalc.Numbers.Max switches on the type of its FIRST argument and converts the
            // second to it - so max(0, x) takes the Int32 branch and Convert.ToInt32 ROUNDS x. The
            // expression then collapses to a step at slip ratio 0.5: 0.45 -> 0, 0.60 -> 100. Measured on
            // the owner's F1 25 capture before this was understood: 777 nonzero wheel-frames, every one
            // exactly 100, none in between - and the reported symptom was "vibration only when the wheel
            // FULLY locked".
            //
            // A future edit "tidying" 0.0 back to 0 would silently reintroduce it: the expression still
            // reads correctly and still evaluates without error. Hence a test that scans the literals.
            foreach (bool isLock in new[] { true, false })
                foreach (string wheel in MotorsExportPropertyNames.WheelSuffixes)
                {
                    string script = ViperPropertyNames.GetScript(isLock, wheel);

                    // Strip the property reference first - it carries digits of its own (F1 25 etc.).
                    string withoutProperty = Regex.Replace(script, @"\[[^\]]*\]", "PROP");

                    foreach (Match m in Regex.Matches(withoutProperty, @"-?\d+(\.\d+)?"))
                        Assert.True(m.Value.Contains("."),
                            $"literal '{m.Value}' in the {(isLock ? "Lock" : "Slip")} {wheel} script is an "
                            + "integer - NCalc's Max/Min take their type from the FIRST argument, so a bare "
                            + $"integer collapses the signal to a step at 0.5. Script: {script}");
                }
        }

        [Fact]
        public void Selecting_the_mode_fills_all_four_wheels_and_forces_NCalc()
        {
            // The script type is the half a driver cannot be left to set: on Plain, SimHub would look up
            // a property literally named "min(1.0, max(0, ..." and silently fall back to Raw forever.
            foreach (bool isLock in new[] { true, false })
            {
                var channel = new WheelChannelSettings { SourceMode = SourceMode.Viper };
                channel.ResetSourcesForCurrentMode(isLock);

                Assert.Equal(SourceMode.Viper, channel.SourceMode);
                Assert.Equal(ScriptType.NCalc, channel.ScriptTypeFrontLeft);
                Assert.Equal(ScriptType.NCalc, channel.ScriptTypeFrontRight);
                Assert.Equal(ScriptType.NCalc, channel.ScriptTypeRearLeft);
                Assert.Equal(ScriptType.NCalc, channel.ScriptTypeRearRight);

                foreach (string src in new[] { channel.SourceFrontLeft, channel.SourceFrontRight, channel.SourceRearLeft, channel.SourceRearRight })
                {
                    Assert.Contains(ViperPropertyNames.ComputedPrefix, src);
                    Assert.NotEqual(string.Empty, src);
                }

                // All four must be DISTINCT - a copy/paste slip that pointed every wheel at FL would
                // publish a plausible-looking but uniform signal with no per-wheel information at all.
                var distinct = new HashSet<string> { channel.SourceFrontLeft, channel.SourceFrontRight, channel.SourceRearLeft, channel.SourceRearRight };
                Assert.Equal(4, distinct.Count);
            }
        }

        [Fact]
        public void The_shipped_preset_is_recognised_by_the_cold_start_table()
        {
            // Viper is matched by whole-identity equality against a recomputed identity, because
            // SourceIdentity HASHES scripted sources - there is no property name left to substring on.
            foreach (bool isLock in new[] { true, false })
            {
                var channel = new WheelChannelSettings { SourceMode = SourceMode.Viper };
                channel.ResetSourcesForCurrentMode(isLock);

                string identity = SourceIdentity.Compute(
                    channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                    channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                    channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                    channel.SourceRearRight, channel.ScriptTypeRearRight.ToString());

                _out.WriteLine($"isLock={isLock} identity={identity}");
                Assert.StartsWith("NCalc:", identity);   // scripted sources are hashed, by design
                Assert.Equal(KnownFeedbackSource.ViperLngWheelSlip, KnownSourceColdStartReference.Classify(identity, isLock));

                Assert.True(KnownSourceColdStartReference.TryGetSMax(identity, isLock, out double smax));
                Assert.Equal(isLock ? 15.0 : 10.0, smax, 6);
            }
        }

        [Fact]
        public void The_SMax_references_are_pinned()
        {
            // OWNER-SET (2026-09-28), replacing the values measured through the integer-literal defect.
            // Viper publishes a raw longitudinal slip RATIO, so the limit sits around 10-20 on this
            // preset's 0-100 mapping rather than the 64-71 the severity-scaled sources use - see the
            // constants' own remarks.
            Assert.Equal(15.0, KnownSourceColdStartReference.LockViperSMax, 6);
            Assert.Equal(10.0, KnownSourceColdStartReference.SlipViperSMax, 6);

            // Slip below Lock: a driven wheel breaks traction at a smaller ratio than a braked wheel
            // needs to pass its friction peak.
            Assert.True(KnownSourceColdStartReference.SlipViperSMax < KnownSourceColdStartReference.LockViperSMax);
        }

        [Fact]
        public void An_edited_preset_stops_being_recognised_rather_than_keeping_a_calibration()
        {
            // A driver who changes the expression is no longer running the source these constants were
            // measured on, so falling out of the table is correct - the alternative is a calibration
            // that silently no longer describes their signal.
            var channel = new WheelChannelSettings { SourceMode = SourceMode.Viper };
            channel.ResetSourcesForCurrentMode(true);
            channel.SourceFrontLeft += " ";   // one trailing space

            string identity = SourceIdentity.Compute(
                channel.SourceFrontLeft, "NCalc", channel.SourceFrontRight, "NCalc",
                channel.SourceRearLeft, "NCalc", channel.SourceRearRight, "NCalc");

            Assert.Equal(KnownFeedbackSource.Unknown, KnownSourceColdStartReference.Classify(identity, true));
            Assert.False(KnownSourceColdStartReference.TryGetSMax(identity, true, out _));
        }

        [Fact]
        public void The_other_two_sources_are_still_classified_correctly()
        {
            // Viper is matched FIRST, so this guards against it shadowing the pre-existing two.
            var raw = new WheelChannelSettings();
            raw.ResetSourcesToDefault(true);
            string rawIdentity = SourceIdentity.Compute(
                raw.SourceFrontLeft, "Plain", raw.SourceFrontRight, "Plain",
                raw.SourceRearLeft, "Plain", raw.SourceRearRight, "Plain");
            Assert.Equal(KnownFeedbackSource.QAdvanceFeedbackRaw, KnownSourceColdStartReference.Classify(rawIdentity, true));

            var shakeIt = new WheelChannelSettings();
            shakeIt.ApplyMotorsExportDefaults(true);
            string shakeItIdentity = SourceIdentity.Compute(
                shakeIt.SourceFrontLeft, "Plain", shakeIt.SourceFrontRight, "Plain",
                shakeIt.SourceRearLeft, "Plain", shakeIt.SourceRearRight, "Plain");
            Assert.Equal(KnownFeedbackSource.ShakeItMotorsExport, KnownSourceColdStartReference.Classify(shakeItIdentity, true));
        }

        [Fact]
        public void Availability_needs_all_four_wheels_and_tolerates_a_genuine_zero()
        {
            var published = new Dictionary<string, object>();
            foreach (string w in new[] { "FL", "FR", "RL", "RR" })
                published[ViperPropertyNames.GetRawPropertyName(w)] = 0.0;

            // A wheel that is neither locking nor spinning genuinely reads 0 for most of a lap, so
            // "available" must mean the property EXISTS - not that it is nonzero.
            Assert.True(ViperAvailabilityResolver.IsAvailable(n => published.TryGetValue(n, out object v) ? v : null));

            // A partial match is never a partially-working mode.
            published.Remove(ViperPropertyNames.GetRawPropertyName("RR"));
            Assert.False(ViperAvailabilityResolver.IsAvailable(n => published.TryGetValue(n, out object v) ? v : null));

            // Nothing installed at all, and a reader that throws, both resolve to "not available".
            Assert.False(ViperAvailabilityResolver.IsAvailable(n => null));
            Assert.False(ViperAvailabilityResolver.IsAvailable(n => throw new System.InvalidOperationException()));
            Assert.False(ViperAvailabilityResolver.IsAvailable(null));
        }

        [Fact]
        public void Switching_away_from_viper_restores_the_other_modes_defaults()
        {
            // The mode must be reversible - a driver trying Viper and going back must not be left with
            // NCalc expressions in their Raw fields.
            var channel = new WheelChannelSettings { SourceMode = SourceMode.Viper };
            channel.ResetSourcesForCurrentMode(true);
            Assert.Contains(ViperPropertyNames.ComputedPrefix, channel.SourceFrontLeft);

            channel.SourceMode = SourceMode.Manual;
            channel.ResetSourcesForCurrentMode(true);

            Assert.DoesNotContain(ViperPropertyNames.ComputedPrefix, channel.SourceFrontLeft);
            Assert.Equal(ScriptType.Plain, channel.ScriptTypeFrontLeft);
        }
    }
}
