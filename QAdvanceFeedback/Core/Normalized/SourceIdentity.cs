using System.Text;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Derives a stable, human-readable "which signal is currently feeding this channel" identity from
    /// one channel's four per-wheel source configurations - the third dimension
    /// <see cref="KeyedGripLearner"/> now keys its learners by (see that class's own remarks for the
    /// full "why"). Pure and SimHub-free: takes plain strings (never the <c>Settings.ScriptType</c>
    /// enum directly, so Core never has to reference the Settings layer above it) and a string name for
    /// each wheel's script type, produced by the caller via a plain <c>ToString()</c>.
    /// <para/>
    /// THE OWNER'S OWN SUGGESTION, taken literally: "the source property name acts as the category."
    /// A <see cref="ScriptType.Plain"/> source is used VERBATIM (the resolved property name itself -
    /// human-readable, exactly what the owner asked for: "ShakeIT...WheelLock.IRacing.FrontLeft" reads
    /// as itself in a diagnostic dump or a persisted JSON key, not an opaque hash). A scripted source
    /// (JavaScript/NCalc) is HASHED instead - the owner's own alternative for exactly this case - since
    /// an expression can be arbitrarily long, contain characters this class's key separator would
    /// collide with, or simply be unwieldy to embed verbatim in a persisted key.
    /// <para/>
    /// PER-CHANNEL, NOT PER-WHEEL (see <see cref="KeyedGripLearner"/>'s own remarks for the full
    /// justification): all four wheels' own identities are combined into ONE composite string for the
    /// channel, since the learner itself only ever tracks one scalar (a car-level G magnitude) per
    /// channel, not four independent per-wheel quantities. The four wheels ARE included independently
    /// (not reduced to e.g. "are all four the same" or just the first wheel) specifically because the
    /// brief flags they "could in principle point at different providers" - a change to ANY ONE wheel's
    /// source is a genuine change to what THIS CHANNEL'S composite signal is, and must isolate its own
    /// learning session accordingly.
    /// <para/>
    /// STABLE ACROSS RESTARTS: every input here is either a literal property name (already stable) or a
    /// deterministic hash of literal expression text (FNV-1a, a fixed, well-known, allocation-light
    /// non-cryptographic hash - stable for the SAME input on any machine/run, unlike
    /// <see cref="object.GetHashCode"/>, which .NET explicitly does not guarantee is stable across
    /// processes/versions).
    /// </summary>
    public static class SourceIdentity
    {
        private const string WheelSeparator = "~";
        private const string FieldSeparator = ":";

        /// <summary>Combines this channel's four per-wheel source configurations into one composite
        /// identity string. <paramref name="scriptTypeXxx"/> parameters are the plain string NAME of
        /// each wheel's <c>Settings.ScriptType</c> (e.g. "Plain", "JavaScript", "NCalc") - the caller's
        /// own <c>ToString()</c>, so Core has no upward dependency on the Settings layer.</summary>
        public static string Compute(
            string sourceFrontLeft, string scriptTypeFrontLeft,
            string sourceFrontRight, string scriptTypeFrontRight,
            string sourceRearLeft, string scriptTypeRearLeft,
            string sourceRearRight, string scriptTypeRearRight)
        {
            var sb = new StringBuilder(128);
            AppendWheel(sb, sourceFrontLeft, scriptTypeFrontLeft);
            sb.Append(WheelSeparator);
            AppendWheel(sb, sourceFrontRight, scriptTypeFrontRight);
            sb.Append(WheelSeparator);
            AppendWheel(sb, sourceRearLeft, scriptTypeRearLeft);
            sb.Append(WheelSeparator);
            AppendWheel(sb, sourceRearRight, scriptTypeRearRight);
            return sb.ToString();
        }

        private static void AppendWheel(StringBuilder sb, string source, string scriptType)
        {
            string type = string.IsNullOrEmpty(scriptType) ? "Plain" : scriptType;
            sb.Append(type).Append(FieldSeparator);

            if (string.IsNullOrWhiteSpace(source))
            {
                sb.Append("(empty)");
                return;
            }

            // "Plain" - a straight property reference - is kept human-readable verbatim (the owner's
            // own suggestion: the property name itself IS the category). Anything else (a script/
            // expression) is hashed - see this class's own remarks.
            if (string.Equals(type, "Plain", System.StringComparison.OrdinalIgnoreCase))
                sb.Append(source);
            else
                sb.Append(Fnv1aHex(source));
        }

        /// <summary>FNV-1a, 32-bit, rendered as 8 lowercase hex digits - a fixed, deterministic,
        /// non-cryptographic hash (not .NET's own <see cref="string.GetHashCode"/>, which is explicitly
        /// documented as unstable across processes) so the SAME expression text always produces the
        /// SAME identity, on any machine, across any restart.</summary>
        private static string Fnv1aHex(string text)
        {
            unchecked
            {
                const uint fnvOffsetBasis = 2166136261;
                const uint fnvPrime = 16777619;
                uint hash = fnvOffsetBasis;
                foreach (char c in text)
                {
                    hash ^= c;
                    hash *= fnvPrime;
                }
                return hash.ToString("x8");
            }
        }
    }
}
