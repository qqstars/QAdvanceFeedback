using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
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
        };

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            if (member?.DeclaringType == null) return property;

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

        /// <summary>The settings both the writer and the reader must use. Kept as one shared instance so
        /// they can never drift apart - a mismatch would read every calibration back as empty.</summary>
        public static JsonSerializerSettings Settings(Formatting formatting) => new JsonSerializerSettings
        {
            ContractResolver = Instance,
            Formatting = formatting,
        };
    }
}
