using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Runtime;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// The Slip Angle learners are saved LEAN (owner, 2026-11-25, parameters Version 19): a field only when it differs from
    /// a fresh instance's, and a key that was never taught not at all - and every state comes back exactly.
    /// </summary>
    public sealed class SlipAngleLeanPersistenceTests
    {
        private const string Game = "RFactor2";
        private const string Car = "Car1";
        private static readonly string Fallback = SlipAngleFrameProcessor.FallbackIdentity;
        private static readonly ImpactVerdict Allowed = new ImpactVerdict(true, false, false, 0.0, 0.0);
        private static readonly JsonSerializerSettings Lean = ShakeItCalibrationContractResolver.Settings(Formatting.None);

        /// <summary>Like the car-angle estimate: the rear axle and "all" learn, the front never does (NaN).</summary>
        private static Dictionary<string, SlipAngleKeyState> Driven()
        {
            var k = new KeyedSlipAngleLearner();
            SurfaceBlend blend = SurfaceBlend.Only(SurfaceBucket.Sealed);
            for (int i = 0; i < 4000; i++)
            {
                double a = 2.0 + 6.0 * Math.Abs(Math.Sin(i / 90.0));
                double g = Math.Min(1.4, a / 6.0);
                k.Observe(Game, Car, Fallback, blend, double.NaN, a, a, g, Allowed, true, 1.0 / 60.0);
            }
            return k.Export();
        }

        private static string Full(object o) => JsonConvert.SerializeObject(o);   // every field, no resolver

        private static RuntimeDocument RoundTrip(RuntimeDocument d) =>
            JsonConvert.DeserializeObject<RuntimeDocument>(JsonConvert.SerializeObject(d, Lean), Lean);

        [Fact]
        public void A_key_that_was_never_taught_is_not_saved()
        {
            Dictionary<string, SlipAngleKeyState> states = Driven();
            string front = KeyedSlipAngleLearner.MakeKey(Game, Car, Fallback, SlipAngleTarget.Front, SurfaceBucket.Sealed);
            string rear = KeyedSlipAngleLearner.MakeKey(Game, Car, Fallback, SlipAngleTarget.Rear, SurfaceBucket.Sealed);
            Assert.True(states.ContainsKey(front));
            // The live never-fed key is exactly what FreshState describes...
            Assert.Equal(Full(KeyedSlipAngleLearner.FreshState(Fallback)), Full(states[front]));

            JObject saved = JObject.Parse(JsonConvert.SerializeObject(new RuntimeDocument { SlipAngleLearners = states }, Lean));
            var learners = (JObject)saved[nameof(RuntimeDocument.SlipAngleLearners)];
            Assert.Null(learners[front]);       // ...so it is left out
            Assert.NotNull(learners[rear]);     // a taught key is kept
        }

        [Fact]
        public void The_parameters_file_written_section_by_section_leaves_the_never_taught_key_out_too()
        {
            // RuntimeStore writes each section's value on its own - the omission must hold on that path as well.
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qaf-lean-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Dictionary<string, SlipAngleKeyState> states = Driven();
                string front = KeyedSlipAngleLearner.MakeKey(Game, Car, Fallback, SlipAngleTarget.Front, SurfaceBucket.Sealed);
                string rear = KeyedSlipAngleLearner.MakeKey(Game, Car, Fallback, SlipAngleTarget.Rear, SurfaceBucket.Sealed);
                using (var store = new RuntimeStore(path, flushInterval: TimeSpan.FromMinutes(10)))
                {
                    store.SaveSlipAngleLearners(states);
                    store.Flush();
                }
                var learners = (JObject)JObject.Parse(System.IO.File.ReadAllText(path))[nameof(RuntimeDocument.SlipAngleLearners)];
                Assert.Null(learners[front]);
                Assert.NotNull(learners[rear]);
                using (var reopened = new RuntimeStore(path, flushInterval: TimeSpan.FromMinutes(10)))
                {
                    reopened.LoadSlipAngleLearners(out Dictionary<string, SlipAngleKeyState> back);
                    Assert.Equal(Full(states[rear]), Full(back[rear]));
                }
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
            }
        }

        [Fact]
        public void A_taught_key_comes_back_field_for_field()
        {
            Dictionary<string, SlipAngleKeyState> states = Driven();
            RuntimeDocument back = RoundTrip(new RuntimeDocument { SlipAngleLearners = states });
            foreach (KeyValuePair<string, SlipAngleKeyState> kv in back.SlipAngleLearners)
                Assert.Equal(Full(states[kv.Key]), Full(kv.Value));
            Assert.Equal(states.Count - 1, back.SlipAngleLearners.Count);   // only the never-taught front key is missing
        }

        [Fact]
        public void An_older_plugin_reading_the_lean_file_gets_the_same_state()
        {
            Dictionary<string, SlipAngleKeyState> states = Driven();
            string lean = JsonConvert.SerializeObject(new RuntimeDocument { SlipAngleLearners = states }, Lean);
            // An older plugin's reader knows nothing of the lean form: a missing field takes the constructor's value.
            RuntimeDocument old = JsonConvert.DeserializeObject<RuntimeDocument>(lean);
            foreach (KeyValuePair<string, SlipAngleKeyState> kv in old.SlipAngleLearners)
                Assert.Equal(Full(states[kv.Key]), Full(kv.Value));
        }

        [Fact]
        public void A_field_set_back_to_zero_from_a_non_zero_start_is_still_saved()
        {
            SlipAngleKeyState state = Driven().Values.First(s => s.Learner.Observations > 0);
            Assert.Equal(-1, new GripCurveSummary().FirstBin);   // a field that starts at -1, not 0
            state.Learner.Curve.FirstBin = 0;                    // ...set to 0: must still be written
            state.Learner.PriorScale = 0.0;
            state.Learner.PriorMin = 0.0;
            var d = new RuntimeDocument { SlipAngleLearners = new Dictionary<string, SlipAngleKeyState> { ["G|#|C|#|" + Fallback + "/all|#|sealed"] = state } };
            SlipAngleKeyState back = RoundTrip(d).SlipAngleLearners.Values.Single();
            Assert.Equal(0, back.Learner.Curve.FirstBin);
            Assert.Equal(0.0, back.Learner.PriorScale);
            Assert.Equal(0.0, back.Learner.PriorMin);
            Assert.Equal(Full(state), Full(back));
        }

        [Fact]
        public void The_lean_section_is_a_fraction_of_the_full_one()
        {
            // Heavily taught keys (most fields hold real values) still save at least a third smaller; the whole section,
            // with its never-taught key left out, smaller still.
            Dictionary<string, SlipAngleKeyState> states = Driven();
            int lean = JsonConvert.SerializeObject(states.Values.ToList(), Lean).Length;
            int full = JsonConvert.SerializeObject(states.Values.ToList()).Length;
            Assert.True(lean * 3 < full * 2, lean + " vs " + full);
            int leanSection = JsonConvert.SerializeObject(new RuntimeDocument { SlipAngleLearners = states }, Lean).Length;
            int fullSection = JsonConvert.SerializeObject(new RuntimeDocument { SlipAngleLearners = states }).Length;
            Assert.True(leanSection < lean + (fullSection - full), leanSection + " vs " + fullSection);
        }
    }
}
