using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Runtime;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Core.RawCalculator.Calibration;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Serialisation rules for the ShakeIt calibration types, applied when reading and writing
    /// <c>QAdvanceFeedback.Parameters.json</c>.
    /// <para/>
    /// WHY A RESOLVER RATHER THAN ATTRIBUTES. <c>Core\</c> deliberately references no serialiser at all -
    /// every type there is a plain POCO and all (de)serialisation happens out here in
    /// <see cref="RuntimeStore"/>. Putting <c>[JsonProperty]</c> on the calibration types would break
    /// that. A resolver keeps the naming decision in the serialiser layer where it belongs, and keeps it
    /// in ONE place rather than scattered across three types.
    /// <para/>
    /// TWO JOBS:
    /// <list type="bullet">
    /// <item>SHORTEN. The calibration histogram is by far the largest thing in the parameters file - one
    /// entry per (track, car, metric), each holding a bucket dictionary - and it is rewritten on a timer
    /// while driving. Two-letter names measurably shrink the file and the parse.</item>
    /// <item>OMIT WHAT MUST NOT PERSIST. <see cref="PreloadedCalibrationData.AutoCalibrationData"/> is a
    /// LIVE OBJECT REFERENCE the provider re-points every frame; serialising it would duplicate a whole
    /// histogram inside every preset and restore a stale copy pointing at nothing. The computed
    /// <c>IsReady</c>/<c>Completion</c> getters are derived and would only bloat the file.</item>
    /// </list>
    /// CHANGING A NAME HERE IS A BREAKING FILE CHANGE - an existing parameters file would silently lose
    /// that field and the calibration would restart. Add a new name only alongside a Version bump.
    /// </summary>
    public sealed class ShakeItCalibrationContractResolver : DefaultContractResolver
    {
        public static readonly ShakeItCalibrationContractResolver Instance = new ShakeItCalibrationContractResolver();

        private static readonly Dictionary<Type, Dictionary<string, string>> ShortNames =
            new Dictionary<Type, Dictionary<string, string>>
            {
                [typeof(CalibrationData)] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Max"] = "mx",
                    ["Values"] = "v",
                    ["Sum"] = "s",
                    ["Count"] = "c",
                    ["CalibrationPointsAdded"] = "p",
                    ["PercentileTable"] = "pt",   // Version 17-19's compact form - read only, never written
                    ["Percentile15"] = "p15",     // Version 20: a finished Slip entry keeps only these two
                    ["Percentile99"] = "p99",
                },
                [typeof(PreloadedCalibrationData)] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MeasuredMaximum"] = "mm",
                    ["CorrectionFactor"] = "cf",
                },
                [typeof(GameCalibrationBounds)] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["WheelSpeedDeltaLowbound"] = "lo",
                    ["WheelSpeedDeltaHighbound"] = "hi",
                    ["WheelSpeedDeltaLowLockbound"] = "ll",
                },
            };

        private static readonly HashSet<string> NeverPersisted = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PreloadedCalibrationData.AutoCalibrationData),
            nameof(PreloadedCalibrationData.AutocalibrationDataRatio),
            "IsReady",
            "Completion",
            nameof(CalibrationData.IsFinished),
            nameof(CalibrationData.IsCompact),
            nameof(CalibrationData.KeepsPercentiles),
            nameof(CalibrationData.Revision),
        };

        /// <summary>A key that was never taught is not saved at all (<see cref="NeverFedKeysOmitted"/>) - set on the
        /// dictionary TYPE, not the document's property, because the parameters file is written section by section (each
        /// section's value serialised on its own) as well as whole.</summary>
        protected override JsonContract CreateContract(Type objectType)
        {
            JsonContract contract = base.CreateContract(objectType);
            if (objectType == typeof(Dictionary<string, SlipAngleKeyState>)) contract.Converter = NeverFedKeysOmitted.Instance;
            return contract;
        }

        /// <summary>
        /// LEAN SLIP ANGLE STATES (owner, 2026-11-25, parameters Version 19). These types are saved with every field, most
        /// of them still at their starting value (null curve points, zero counters). A field is now written only when it
        /// differs from the same field of a FRESHLY CONSTRUCTED instance - not from zero: a few start elsewhere (the 5 / 8 / 10
        /// windows, PriorScale 1) - and a missing field is filled by that same constructor on load, so nothing is lost and an
        /// older plugin reads the same state.
        /// </summary>
        private static readonly HashSet<Type> LeanTypes = new HashSet<Type>
        {
            typeof(SlipAngleKeyState), typeof(SlipAngleLearnerState), typeof(GripCurveSummary), typeof(GripCurveBinsState),
            typeof(SlipAngleBalanceState), typeof(SlipAngleSpeedBandSetState), typeof(SlipAngleSpeedBandState),
            typeof(CorroboratedLevelState),
        };

        private static readonly ConcurrentDictionary<Type, object> FreshInstances = new ConcurrentDictionary<Type, object>();

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            if (member?.DeclaringType == null) return property;

            if (LeanTypes.Contains(member.DeclaringType) && property.Readable && property.ValueProvider != null)
            {
                object fresh = FreshInstances.GetOrAdd(member.DeclaringType, t => Activator.CreateInstance(t));
                IValueProvider values = property.ValueProvider;
                object start = values.GetValue(fresh);
                Predicate<object> existing = property.ShouldSerialize;
                property.ShouldSerialize = o => (existing == null || existing(o)) && !Equals(values.GetValue(o), start);
                return property;
            }


            // Only a compact Slip entry has the two percentiles: leave them out rather than writing null.
            if (member.DeclaringType == typeof(CalibrationData)
                && (member.Name == nameof(CalibrationData.Percentile15) || member.Name == nameof(CalibrationData.Percentile99)))
                property.NullValueHandling = NullValueHandling.Ignore;

            if (!ShortNames.TryGetValue(member.DeclaringType, out Dictionary<string, string> map)) return property;

            if (NeverPersisted.Contains(member.Name))
            {
                property.Ignored = true;
                property.ShouldSerialize = _ => false;
                return property;
            }

            if (map.TryGetValue(member.Name, out string shortName)) property.PropertyName = shortName;
            return property;
        }

        /// <summary>
        /// The Slip Angle learner dictionary, WITHOUT the keys that were created but never taught (owner, 2026-11-25,
        /// parameters Version 19): a key whose saved state equals <see cref="KeyedSlipAngleLearner.FreshState"/> for its
        /// angle source (e.g. the car-angle estimate's front axle, which never learns). It is rebuilt the same way when it is
        /// needed. Reading is the ordinary dictionary read.
        /// </summary>
        private sealed class NeverFedKeysOmitted : JsonConverter
        {
            public static readonly NeverFedKeysOmitted Instance = new NeverFedKeysOmitted();

            private readonly ConcurrentDictionary<string, JToken> _fresh = new ConcurrentDictionary<string, JToken>(StringComparer.Ordinal);

            public override bool CanRead => false;

            public override bool CanConvert(Type objectType) => objectType == typeof(Dictionary<string, SlipAngleKeyState>);

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
                throw new NotSupportedException();

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var states = (Dictionary<string, SlipAngleKeyState>)value;
                writer.WriteStartObject();
                foreach (KeyValuePair<string, SlipAngleKeyState> kv in states)
                {
                    if (kv.Value == null) continue;
                    JToken state = JToken.FromObject(kv.Value, serializer);
                    if (KeyedSlipAngleLearner.TryParseKey(kv.Key, out _, out _, out string source, out _, out _)
                        && JToken.DeepEquals(state, _fresh.GetOrAdd(source, s => JToken.FromObject(KeyedSlipAngleLearner.FreshState(s), serializer))))
                        continue;
                    writer.WritePropertyName(kv.Key);
                    state.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
        }

        /// <summary>The settings both the writer and the reader must use. Kept as one shared instance so
        /// they can never drift apart - a mismatch would read every calibration back as empty.</summary>
        public static JsonSerializerSettings Settings(Formatting formatting) => new JsonSerializerSettings
        {
            ContractResolver = Instance,
            Formatting = formatting,
        };
    }
}
