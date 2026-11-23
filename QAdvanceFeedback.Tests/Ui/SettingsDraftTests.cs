using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The page's draft / baseline / live merge (docs\full-mvvm-plan.md §3.1): the page edits a draft; an
    /// external change (ShakeIt panel, file, runtime) is adopted leaf by leaf and wins over an unapplied
    /// page edit of the same leaf; Apply writes only the leaves the page edited, into the live object in
    /// place.
    /// </summary>
    public class SettingsDraftTests
    {
        private readonly QAdvanceFeedbackSettings _live = new QAdvanceFeedbackSettings();

        private SettingsDraft NewDraft() => new SettingsDraft(() => _live);

        private static string Json(object o) => JsonConvert.SerializeObject(o);

        [Fact]
        public void Draft_starts_as_an_exact_copy_of_live_and_is_a_different_instance()
        {
            _live.Lock.BrakeThresholdPercent = 33;
            _live.Lock.KeyDataPoints.Values["g|x"] = new KeyDataPointEntry { SMax = 7, S90 = 5, S75 = 4 };

            SettingsDraft d = NewDraft();

            Assert.NotSame(_live, d.Settings);
            Assert.NotSame(_live.Lock, d.Settings.Lock);
            Assert.Equal(Json(_live), Json(d.Settings));
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void Runtime_read_only_values_never_make_the_draft_dirty_and_Apply_stays_clean()
        {
            // Final review C1: GForceSettings.CurrentLearned*MaxG are get-only reads of the live learners.
            // They serialise, the draft copy cannot hold them, so the page read "unapplied changes" forever.
            SettingsDraft d = NewDraft();
            _live.GForce.SetCurrentGameAndCar("G", "C");
            for (int i = 0; i < 800; i++) _live.GForce.ObserveLatG("G", "C", 1.35);
            Assert.True(_live.GForce.CurrentLearnedLatMaxG > 1.0);

            d.AdoptExternal();
            Assert.False(d.IsDirty);

            d.Apply();
            d.AdoptExternal();
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void Default_settings_round_trip_exactly_so_a_fresh_draft_is_clean()
        {
            // Newtonsoft populates initialiser-created objects/dictionaries (ObjectCreationHandling.Auto);
            // a property that duplicated on a round trip would make every fresh page dirty.
            var d = new SettingsDraft(() => QAdvanceFeedbackSettings.CreateDefault());
            Assert.Equal(Json(QAdvanceFeedbackSettings.CreateDefault()), Json(d.Settings));
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void An_edit_makes_the_draft_dirty_and_editing_back_makes_it_clean()
        {
            SettingsDraft d = NewDraft();
            int raised = 0;
            d.DirtyChanged += (s, e) => raised++;
            double original = d.Settings.Lock.BrakeThresholdPercent;

            d.Settings.Lock.BrakeThresholdPercent = original + 5;
            d.MarkEdited();
            Assert.True(d.IsDirty);
            Assert.Equal(1, raised);
            Assert.Equal(original, _live.Lock.BrakeThresholdPercent);   // the live object is untouched

            d.MarkEdited();   // no change: no second event
            Assert.Equal(1, raised);

            d.Settings.Lock.BrakeThresholdPercent = original;
            d.MarkEdited();
            Assert.False(d.IsDirty);
            Assert.Equal(2, raised);
        }

        [Fact]
        public void An_external_change_of_an_unedited_leaf_is_adopted_and_the_draft_stays_clean()
        {
            SettingsDraft d = NewDraft();
            QAdvanceFeedbackSettings draftObject = d.Settings;
            DraftReloadedEventArgs reloaded = null;
            d.Reloaded += (s, e) => reloaded = e;

            _live.GForce.FixedAccelMaxG = 2.75;
            IReadOnlyList<string> paths = d.AdoptExternal();

            Assert.Same(draftObject, d.Settings);
            Assert.Equal(2.75, d.Settings.GForce.FixedAccelMaxG);
            Assert.False(d.IsDirty);
            Assert.Equal(new[] { "GForce.FixedAccelMaxG" }, paths);
            Assert.NotNull(reloaded);
            Assert.Equal(paths, reloaded.Paths);
            Assert.True(reloaded.Touches("GForce"));
            Assert.True(reloaded.Touches("GForce.FixedAccelMaxG"));
            Assert.False(reloaded.Touches("GForce.FixedDecelMaxG"));
            Assert.False(reloaded.Touches("GForce.FixedAccelMaxGx"));
        }

        [Fact]
        public void Nothing_changed_externally_adopts_nothing_and_raises_no_reload()
        {
            SettingsDraft d = NewDraft();
            bool reloaded = false;
            d.Reloaded += (s, e) => reloaded = true;
            Assert.Empty(d.AdoptExternal());
            Assert.False(reloaded);
        }

        [Fact]
        public void An_external_change_of_the_same_leaf_replaces_the_unapplied_edit_ShakeIt_wins()
        {
            SettingsDraft d = NewDraft();
            d.Settings.Lock.BrakeThresholdPercent = 30;
            d.MarkEdited();
            Assert.True(d.IsDirty);

            _live.Lock.BrakeThresholdPercent = 40;
            d.AdoptExternal();

            Assert.Equal(40, d.Settings.Lock.BrakeThresholdPercent);
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void An_external_change_of_another_leaf_keeps_the_unapplied_edit()
        {
            SettingsDraft d = NewDraft();
            d.Settings.Lock.BrakeThresholdPercent = 30;
            d.MarkEdited();

            _live.Slip.ThrottleThresholdPercent = 12;
            d.AdoptExternal();

            Assert.Equal(30, d.Settings.Lock.BrakeThresholdPercent);
            Assert.Equal(12, d.Settings.Slip.ThrottleThresholdPercent);
            Assert.True(d.IsDirty);
        }

        [Fact]
        public void Apply_writes_only_the_edited_leaves_and_never_reverts_an_unadopted_live_change()
        {
            SettingsDraft d = NewDraft();
            d.Settings.Lock.BrakeThresholdPercent = 30;
            d.MarkEdited();
            // Changed live AFTER the draft's last sync, never adopted, never edited on the page.
            _live.Slip.ThrottleThresholdPercent = 12;

            IReadOnlyList<string> written = d.Apply();

            Assert.Equal(new[] { "Lock.BrakeThresholdPercent" }, written);
            Assert.Equal(30, _live.Lock.BrakeThresholdPercent);
            Assert.Equal(12, _live.Slip.ThrottleThresholdPercent);
            Assert.False(d.IsDirty);

            // ... and the next sync brings it into the draft.
            d.AdoptExternal();
            Assert.Equal(12, d.Settings.Slip.ThrottleThresholdPercent);
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void Apply_mutates_the_live_object_in_place()
        {
            WheelChannelSettings lockBefore = _live.Lock;
            KeyDataPointSettings keyDataBefore = _live.Lock.KeyDataPoints;
            Dictionary<string, KeyDataPointEntry> valuesBefore = _live.Lock.KeyDataPoints.Values;
            SettingsDraft d = NewDraft();
            d.Settings.Lock.BrakeThresholdPercent = 31;
            d.Settings.Lock.KeyDataPoints.Values["g|x"] = new KeyDataPointEntry { SMax = 8, S90 = 6, S75 = 5 };
            d.MarkEdited();

            d.Apply();

            Assert.Same(lockBefore, _live.Lock);
            Assert.Same(keyDataBefore, _live.Lock.KeyDataPoints);
            Assert.Same(valuesBefore, _live.Lock.KeyDataPoints.Values);
            Assert.Equal(31, _live.Lock.BrakeThresholdPercent);
            Assert.Equal(8, _live.Lock.KeyDataPoints.Values["g|x"].SMax);
        }

        [Fact]
        public void Dictionary_entries_added_and_removed_round_trip_both_ways()
        {
            _live.Lock.KeyDataPoints.Values["Old|Key"] = new KeyDataPointEntry { SMax = 3, S90 = 2, S75 = 1 };
            SettingsDraft d = NewDraft();

            // Page: remove one key, add another.
            d.Settings.Lock.KeyDataPoints.Values.Remove("Old|Key");
            d.Settings.Lock.KeyDataPoints.Values["New|Key"] = new KeyDataPointEntry { SMax = 9, S90 = 7, S75 = 6 };
            d.MarkEdited();
            Assert.True(d.IsDirty);
            d.Apply();
            Assert.False(_live.Lock.KeyDataPoints.Values.ContainsKey("Old|Key"));
            Assert.Equal(9, _live.Lock.KeyDataPoints.Values["New|Key"].SMax);
            // Still the case-insensitive dictionary the settings class created.
            Assert.True(_live.Lock.KeyDataPoints.Values.ContainsKey("new|key"));

            // External: remove the page's key, add one.
            _live.Lock.KeyDataPoints.Values.Remove("New|Key");
            _live.Lock.KeyDataPoints.Values["Ext|Key"] = new KeyDataPointEntry { SMax = 4, S90 = 3, S75 = 2 };
            IReadOnlyList<string> adopted = d.AdoptExternal();
            Assert.False(d.Settings.Lock.KeyDataPoints.Values.ContainsKey("New|Key"));
            Assert.Equal(4, d.Settings.Lock.KeyDataPoints.Values["Ext|Key"].SMax);
            Assert.True(d.Settings.Lock.KeyDataPoints.Values.ContainsKey("ext|key"));
            Assert.Equal(2, adopted.Count);
            Assert.False(d.IsDirty);
            Assert.Equal(Json(_live), Json(d.Settings));
        }

        [Fact]
        public void A_whole_sub_object_set_to_null_and_back_is_adopted()
        {
            SettingsDraft d = NewDraft();
            _live.Lock.ShakeItAlgorithm = null;
            d.AdoptExternal();
            Assert.Null(d.Settings.Lock.ShakeItAlgorithm);
            Assert.False(d.IsDirty);

            _live.Lock.ShakeItAlgorithm = new ShakeItAlgorithmSettings { LockOnly = true };
            d.AdoptExternal();
            Assert.NotNull(d.Settings.Lock.ShakeItAlgorithm);
            Assert.True(d.Settings.Lock.ShakeItAlgorithm.LockOnly);
            Assert.NotSame(_live.Lock.ShakeItAlgorithm, d.Settings.Lock.ShakeItAlgorithm);
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void ReplaceDraft_then_Apply_restores_the_defaults_into_live()
        {
            _live.Lock.BrakeThresholdPercent = 3;
            _live.GForce.FixedLatMaxG = 3.3;
            _live.General.EnableDiagnostics = !_live.General.EnableDiagnostics;
            SettingsDraft d = NewDraft();
            QAdvanceFeedbackSettings draftObject = d.Settings;
            DraftReloadedEventArgs reloaded = null;
            d.Reloaded += (s, e) => reloaded = e;

            QAdvanceFeedbackSettings defaults = QAdvanceFeedbackSettings.CreateDefault();
            d.ReplaceDraft(defaults);

            Assert.Same(draftObject, d.Settings);
            Assert.NotSame(defaults.Lock, d.Settings.Lock);   // a copy, not the caller's object
            Assert.True(d.IsDirty);
            Assert.NotNull(reloaded);
            Assert.Contains("Lock.BrakeThresholdPercent", reloaded.Paths);

            d.Apply();
            Assert.Equal(Json(defaults), Json(_live));
            Assert.False(d.IsDirty);
        }

        [Fact]
        public void A_missing_live_settings_object_is_tolerated()
        {
            var d = new SettingsDraft(() => null);
            Assert.NotNull(d.Settings);
            d.Settings.Lock.BrakeThresholdPercent = 44;
            d.MarkEdited();
            Assert.True(d.IsDirty);
            Assert.Empty(d.AdoptExternal());
            Assert.Empty(d.Apply());
            Assert.True(d.IsDirty);   // nothing was written, so the edit is still pending
        }

        [Fact]
        public void A_live_object_that_appears_later_is_adopted_whole()
        {
            QAdvanceFeedbackSettings live = null;
            var d = new SettingsDraft(() => live);
            live = new QAdvanceFeedbackSettings();
            live.Lock.BrakeThresholdPercent = 21;

            d.AdoptExternal();

            Assert.Equal(21, d.Settings.Lock.BrakeThresholdPercent);
            Assert.Equal(Json(live), Json(d.Settings));
            Assert.False(d.IsDirty);
        }

        // ---- arrays (no settings property is a list today; the merge must still treat one as a leaf) ----

        public sealed class Holder
        {
            public List<int> Items { get; set; } = new List<int> { 1, 2 };
            public Inner Inner { get; set; } = new Inner();
        }

        public sealed class Inner
        {
            public int[] Numbers { get; set; } = { 5 };
            public string Name { get; set; } = "a";
        }

        [Fact]
        public void Arrays_are_leaves_compared_and_copied_whole()
        {
            var live = new Holder();
            var draft = SettingsJsonLeaves.DeepCopy(live);
            Assert.Equal(new[] { 1, 2 }, draft.Items);   // not duplicated by populating the initialiser's list

            Newtonsoft.Json.Linq.JToken baseline = SettingsJsonLeaves.ToJson(draft);
            draft.Items.Add(3);
            draft.Inner.Numbers = new[] { 6, 7 };
            List<SettingsJsonLeaves.LeafChange> changes = SettingsJsonLeaves.Diff(baseline, SettingsJsonLeaves.ToJson(draft));
            Assert.Equal(new[] { "Items", "Inner.Numbers" }, changes.Select(c => c.Path).ToArray());

            List<int> itemsBefore = live.Items;
            Inner innerBefore = live.Inner;
            SettingsJsonLeaves.Write(live, changes, SettingsJsonLeaves.ToJson(draft));

            Assert.Equal(new[] { 1, 2, 3 }, live.Items);         // replaced, not appended to
            Assert.Equal(new[] { 6, 7 }, live.Inner.Numbers);
            Assert.Same(innerBefore, live.Inner);                // objects are still mutated in place
            Assert.NotSame(itemsBefore, live.Items);             // a leaf array is replaced whole
        }
    }
}
