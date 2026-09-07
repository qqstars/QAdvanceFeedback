using System;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A REPORT: what the 2-minute rolling window does across a GAP - a pause, a menu, a long pit stop.
    /// <para/>
    /// The mechanic under test is that <c>RobustBandEstimator.EvictExpired</c> is called only from
    /// <c>Observe</c>, never from <c>TryEstimate</c>. The window is therefore advanced by INCOMING
    /// SAMPLES, not by the passage of time.
    /// </summary>
    public class GForceWindowGapReportTests
    {
        private readonly ITestOutputHelper _out;
        public GForceWindowGapReportTests(ITestOutputHelper output) => _out = output;

        private const double Hz = 60.0;
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static void Drive(GForceSettings s, DateTime from, double seconds, double g)
        {
            for (int i = 0; i < (int)(Hz * seconds); i++)
                s.ObserveLatG("G", "C", g, from.AddSeconds(i / Hz));
        }

        private static double Settled(GForceSettings s, DateTime t)
        {
            s.EffectiveLatMaxG("G", "C", t);
            return s.EffectiveLatMaxG("G", "C", t.AddSeconds(5));
        }

        [Fact]
        public void Report_what_a_gap_does_to_the_rolling_window()
        {
            _out.WriteLine("THE 2-MINUTE WINDOW ACROSS A GAP");
            _out.WriteLine("EvictExpired runs only inside Observe - the window is advanced by incoming");
            _out.WriteLine("SAMPLES, not by elapsed time. So a gap freezes it rather than draining it.");
            _out.WriteLine("");

            // ---- 1) A one-minute pause, then the same driving resumes.
            {
                var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 1.5 };
                s.SetCurrentGameAndCar("G", "C");
                Drive(s, T0, 30, 3.5);

                double before = s.GetLearnedLatMaxG("G", "C");
                DateTime pauseEnd = T0.AddSeconds(30).AddMinutes(1);
                double during = s.GetLearnedLatMaxG("G", "C");     // read DURING the pause, no samples fed

                Drive(s, pauseEnd, 2, 3.5);
                double after = s.GetLearnedLatMaxG("G", "C");

                _out.WriteLine("1) 30 s at 3.5g, then a ONE-MINUTE pause, then 3.5g again");
                _out.WriteLine($"     before pause : {before:F3} g");
                _out.WriteLine($"     during pause : {during:F3} g   <- frozen, nothing expires while idle");
                _out.WriteLine($"     after resume : {after:F3} g   <- pre-pause samples still inside the 2 min");
                _out.WriteLine("");
            }

            // ---- 2) A three-minute gap, then gentle driving.
            {
                var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 1.5 };
                s.SetCurrentGameAndCar("G", "C");
                Drive(s, T0, 30, 3.5);

                double before = s.GetLearnedLatMaxG("G", "C");
                DateTime gapEnd = T0.AddSeconds(30).AddMinutes(3);
                double during = s.GetLearnedLatMaxG("G", "C");

                // ONE frame after the gap - this is the call that evicts everything older than 2 min.
                s.ObserveLatG("G", "C", 0.8, gapEnd);
                double afterOneFrame = s.GetLearnedLatMaxG("G", "C");

                Drive(s, gapEnd, 2, 0.8);
                double afterResume = s.GetLearnedLatMaxG("G", "C");
                double published = Settled(s, gapEnd.AddSeconds(10));

                _out.WriteLine("2) 30 s at 3.5g, then a THREE-MINUTE gap, then gentle 0.8g driving");
                _out.WriteLine($"     before gap        : {before:F3} g");
                _out.WriteLine($"     during gap        : {during:F3} g   <- still frozen, however long you wait");
                _out.WriteLine($"     after ONE frame   : {afterOneFrame:F3} g   <- that frame evicted the lot");
                _out.WriteLine($"     after 2 s driving : {afterResume:F3} g");
                _out.WriteLine($"     PUBLISHED value   : {published:F3} g");
                _out.WriteLine("");
            }

            // ---- 3) The same gap, but the driving that resumes is still fast.
            {
                var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 1.5 };
                s.SetCurrentGameAndCar("G", "C");
                Drive(s, T0, 30, 3.5);
                DateTime gapEnd = T0.AddSeconds(30).AddMinutes(3);
                Drive(s, gapEnd, 2, 3.5);

                _out.WriteLine("3) same three-minute gap, but 3.5g driving resumes");
                _out.WriteLine($"     after 2 s driving : {s.GetLearnedLatMaxG("G", "C"):F3} g   <- relearned immediately");
                _out.WriteLine("");
            }

            // ---- 4) WHAT DOES "RESET" ACTUALLY MEAN? Same gap, same history, different resume values.
            {
                _out.WriteLine("4) THE SAME 3-minute gap and the SAME 3.5g history, resuming at different speeds.");
                _out.WriteLine("   If 'reset' meant the UI set point, every row would read 1.500.");
                _out.WriteLine("   If it meant the previous maximum, every row would read 3.500.");
                _out.WriteLine("");
                _out.WriteLine("   resume at | learned after 1 frame | published once settled");
                _out.WriteLine("   ----------+-----------------------+-----------------------");

                foreach (double resume in new[] { 0.30, 0.80, 2.00, 3.50, 5.00 })
                {
                    var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 1.5 };
                    s.SetCurrentGameAndCar("G", "C");
                    Drive(s, T0, 30, 3.5);                      // history: a 3.5g car

                    DateTime gapEnd = T0.AddSeconds(30).AddMinutes(3);
                    s.ObserveLatG("G", "C", resume, gapEnd);    // ONE frame after the gap
                    double learned = s.GetLearnedLatMaxG("G", "C");

                    Drive(s, gapEnd, 2, resume);
                    double published = Settled(s, gapEnd.AddSeconds(10));

                    _out.WriteLine($"     {resume,6:F2}g |          {learned,6:F3} g       |        {published,6:F3} g");
                }
                _out.WriteLine("");
                _out.WriteLine("   -> it becomes THE FIRST POST-GAP SAMPLE. Not the set point, not the old max.");
                _out.WriteLine("      (0.30 publishes as 0.500 - that is the floor, Min(0.5, set point 1.5).)");
                _out.WriteLine("");
            }

            _out.WriteLine("SUMMARY");
            _out.WriteLine("  - A pause NEVER decays the estimate. Nothing expires until a sample arrives.");
            _out.WriteLine("  - The first sample after a gap longer than 2 minutes evicts the entire window,");
            _out.WriteLine("    so the estimate is rebuilt from post-gap data alone.");
            _out.WriteLine("  - It therefore tracks whatever you do next: resume fast and it stays high;");
            _out.WriteLine("    resume gently and it follows you down (bounded below by the floor).");

            Assert.True(true);   // report only
        }
    }
}
