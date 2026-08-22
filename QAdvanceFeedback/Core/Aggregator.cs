using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Combines one channel's four per-wheel Raw values into Front/Rear/Left/Right/All - the owner's
    /// own physically-motivated scheme (docs\aggregation-report.md), supplied and seat-tested by the
    /// plugin's owner, REPLACING the previous generic p-norm/<c>GroupMode</c> scheme (retired - see this
    /// class's own remarks at the bottom for why).
    /// <para/>
    /// WHY: the old scheme was a single p-norm, identical for every group (Front/Rear/Left/Right/All)
    /// and both channels, which ignores weight transfer - the dominant real effect. Under braking, load
    /// shifts forward, so the FRONT wheels carry the grip and matter most; under power, the DRIVEN
    /// wheels are the ones that spin. This scheme encodes that directly, per channel.
    /// <para/>
    /// THE SCHEME - two weighted blend stages (see <see cref="AggregationWeights"/> for the five
    /// configurable numbers), plus one optional floor stage:
    /// <list type="number">
    /// <item><b>AXLE (Front/Rear).</b> <c>Front = Max(FL,FR)*WMax + Min(FL,FR)*WMin</c>;
    /// <c>Rear = Max(RL,RR)*WMax + Min(RL,RR)*WMin</c> - ORDER-INDEPENDENT (it does not matter which
    /// physical wheel on the axle is stronger). Models both wheels on an axle sharing the load, not
    /// just whichever one happens to read worse this frame.</item>
    /// <item><b>SIDE (Left/Right) and CAR (All).</b> <c>Left = FL*WFront + RL*WRear</c>;
    /// <c>Right = FR*WFront + RR*WRear</c>; <c>All = Front*WFront + Rear*WRear</c> (using the AXLE-
    /// blended Front/Rear from stage 1, not the four raw wheels directly - so a car-level reading
    /// already reflects the same front/rear bias driving Left/Right/Front/Rear). ORDER-DEPENDENT (front
    /// is always front) - this is where weight transfer is actually modelled: Lock ships
    /// WFront=0.90/WRear=0.10 (braking load-shifts forward); Slip ships WFront=0.65/WRear=0.35 (gentler,
    /// since which axle is DRIVEN varies by car - a FWD car would invert this bias entirely, which is
    /// exactly why these are driver-configurable, not hard-coded).</item>
    /// <item><b>SLIP FLOOR</b> (by shipped default - see <see cref="AggregationWeights.SlipFloorFactor"/>).
    /// Applied independently to All/Front/Rear/Left/Right AFTER stages 1-2, as
    /// <c>result = Max(result, Max(participating wheels) * SlipFloorFactor)</c> - "participating
    /// wheels" is all four for All, the axle pair for Front/Rear, the side pair for Left/Right. This is
    /// what stops a single strongly-spinning wheel being averaged away: WITHOUT it, Slip's own default
    /// axle blend (WMax=0.55) alone would still let a lone spinning wheel read only 55% of its own
    /// value at the axle level, before front/rear blending dilutes it further. THE MECHANISM ITSELF IS
    /// GENERIC - any non-zero <see cref="AggregationWeights.SlipFloorFactor"/> applies it, on either
    /// channel - only the SHIPPED DEFAULT makes it Slip-only (Lock ships the factor at 0, which
    /// disables this stage entirely).
    /// <para/>
    /// LOCK DOES NOT NEED THE EQUIVALENT (verified, docs\aggregation-report.md; axle numbers REVISED,
    /// docs\slip-source-consistency-report.md - Lock's own axle blend is now WMax=0.75/WMin=0.25, the
    /// stronger wheel dominating rather than the weaker one, matching the newer owner-tested default):
    /// Lock's extreme front bias (WFront=0.90) already carries a front lock-up
    /// through strongly at the car level without any floor. Lock-ups from this engine's own algorithm
    /// are also car-level/axle-symmetric far more often than power slip is (an open diff spins ONE
    /// wheel routinely; the "Braking vs speed" branch this plugin's Lock channel uses has no per-wheel
    /// term at all - see <c>RawCalculatorEngine</c>'s own remarks - so in practice all four wheels
    /// already read the same value and a floor would be a no-op most of the time). The mechanism is
    /// still generic and available (a driver could set Lock's own floor factor above 0 if their own car
    /// disagrees), it simply ships at 0.
    /// </item>
    /// </list>
    /// <para/>
    /// CONTINUITY (verified in AggregatorTests, swept numerically across a crossover): both blend
    /// stages are weighted sums of <c>Math.Max</c>/<c>Math.Min</c>/plain values - all continuous
    /// functions of their inputs - so the combined result is continuous as any two wheel values cross,
    /// UNLIKE a bare <c>Max</c> (which steps discontinuously and is felt as a click through a shaker -
    /// the reason the OLD default was p-norm rather than Max). The floor stage (a further <c>Math.Max</c>
    /// of two already-continuous quantities) does not reintroduce a step either.
    /// <para/>
    /// BOUNDS: every input is clamped to 0-100 before blending, and every output (Front/Rear/Left/
    /// Right/All) is clamped to 0-100 again after both blend stages and the floor - see
    /// <see cref="AggregationWeights"/>'s own remarks on why weights only need to be non-negative, not
    /// sum to 1: this FINAL clamp (not a fragile invariant on the weights themselves) is what actually
    /// guarantees the 0-100 bound, for any non-negative weight, however large.
    /// <para/>
    /// RETIRED: the previous <c>GroupMode</c> enum (Max/Mean/WeightedMean/PNorm/Min) and the
    /// Pair/Quad-based instance API are both gone. <c>GroupMode</c> was NEVER exposed to the settings UI
    /// or persisted - every call site hard-coded <c>GroupMode.PNorm</c> at construction - so there is no
    /// persisted driver setting to migrate; retiring it removes a second, unused aggregation surface
    /// that would otherwise sit alongside the owner's new scheme and invite confusion about which one is
    /// actually in effect. This class is now static and stateless (see <see cref="Compute"/>) rather than
    /// an instance wrapping one fixed set of weights, since the weights must now be re-readable from
    /// settings every frame WITHOUT rebuilding any engine (the owner's explicit "tune without a
    /// rebuild" requirement) - passing <see cref="AggregationWeights"/> straight into a static method
    /// achieves that with no extra allocation.
    /// </summary>
    public static class Aggregator
    {
        /// <summary>Computes Front/Rear/Left/Right/All from one frame's four per-wheel values and this
        /// channel's own configured weights - see this class's own remarks for the full scheme.</summary>
        public static WheelAggregate Compute(Corners wheels, AggregationWeights weights)
        {
            double fl = ClampMath.To0100(wheels.FrontLeft);
            double fr = ClampMath.To0100(wheels.FrontRight);
            double rl = ClampMath.To0100(wheels.RearLeft);
            double rr = ClampMath.To0100(wheels.RearRight);

            double front = AxleBlend(fl, fr, weights);
            double rear = AxleBlend(rl, rr, weights);
            double left = SideBlend(fl, rl, weights);
            double right = SideBlend(fr, rr, weights);
            double all = SideBlend(front, rear, weights);

            if (weights.SlipFloorFactor > 0.0)
            {
                front = ApplyFloor(front, Math.Max(fl, fr), weights);
                rear = ApplyFloor(rear, Math.Max(rl, rr), weights);
                left = ApplyFloor(left, Math.Max(fl, rl), weights);
                right = ApplyFloor(right, Math.Max(fr, rr), weights);
                all = ApplyFloor(all, Max4(fl, fr, rl, rr), weights);
            }

            return new WheelAggregate(
                ClampMath.To0100(front), ClampMath.To0100(rear),
                ClampMath.To0100(left), ClampMath.To0100(right), ClampMath.To0100(all));
        }

        /// <summary>Front or Rear: the stronger wheel on that axle weighted by WMax, the weaker by
        /// WMin - order-independent (see this class's own remarks).</summary>
        private static double AxleBlend(double a, double b, AggregationWeights weights)
        {
            double max = Math.Max(a, b);
            double min = Math.Min(a, b);
            return max * weights.WMax + min * weights.WMin;
        }

        /// <summary>Left/Right (raw per-side wheel pair) or All (axle-blended Front/Rear): the
        /// front-position value weighted by WFront, the rear-position value by WRear - order-dependent
        /// (see this class's own remarks).</summary>
        private static double SideBlend(double frontPosition, double rearPosition, AggregationWeights weights)
            => frontPosition * weights.WFront + rearPosition * weights.WRear;

        private static double ApplyFloor(double value, double participatingMax, AggregationWeights weights)
            => Math.Max(value, participatingMax * weights.SlipFloorFactor);

        private static double Max4(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

        /// <summary>
        /// ABSENT-VS-ZERO AGGREGATION (telemetry-integrity pass, item 1): the per-wheel equivalent of
        /// <see cref="Compute"/>, but taking each wheel as a NULLABLE reading (null = this wheel reported
        /// nothing usable this frame - see <c>RawCalculatorEngine</c>'s own per-branch signal-availability
        /// check) and combining only the wheels that actually reported, rather than treating a missing
        /// wheel as a real zero. THE DEFECT THIS FIXES: a wheel with no telemetry this frame is
        /// indistinguishable from a fully-unlocked (Lock) or non-spinning (Slip) wheel if its absence is
        /// silently coalesced to 0.0 before blending - two genuinely-reporting wheels would then be
        /// diluted by two SILENT zeros exactly as if all four had reported a real "nothing happening"
        /// reading, understating a real event by up to half. This method instead RENORMALISES each
        /// pairwise blend (axle, side, car) down to whichever of its own two participants actually has a
        /// reading: both present blends exactly as <see cref="Compute"/> already does; exactly one
        /// present passes that ONE wheel's own reading straight through (weight 1, not diluted by the
        /// other wheel's absent-as-zero); neither present leaves the result absent too (propagated, not
        /// invented). <paramref name="hasValue"/> reports, in <see cref="PublishedPropertyNames.Targets"/>
        /// order (FrontLeft, FrontRight, RearLeft, RearRight, Front, Rear, Left, Right, All) with the
        /// first four copied straight from the inputs, whether each of the 9 published values actually has
        /// something to say this frame.
        /// </summary>
        public static WheelAggregate ComputeAvailable(
            double? frontLeft, double? frontRight, double? rearLeft, double? rearRight, AggregationWeights weights,
            out bool[] hasValue)
        {
            double? fl = frontLeft.HasValue ? ClampMath.To0100(frontLeft.Value) : (double?)null;
            double? fr = frontRight.HasValue ? ClampMath.To0100(frontRight.Value) : (double?)null;
            double? rl = rearLeft.HasValue ? ClampMath.To0100(rearLeft.Value) : (double?)null;
            double? rr = rearRight.HasValue ? ClampMath.To0100(rearRight.Value) : (double?)null;

            double? front = BlendPair(fl, fr, (a, b) => Math.Max(a, b) * weights.WMax + Math.Min(a, b) * weights.WMin);
            double? rear = BlendPair(rl, rr, (a, b) => Math.Max(a, b) * weights.WMax + Math.Min(a, b) * weights.WMin);
            double? left = BlendPair(fl, rl, (a, b) => a * weights.WFront + b * weights.WRear);
            double? right = BlendPair(fr, rr, (a, b) => a * weights.WFront + b * weights.WRear);
            double? all = BlendPair(front, rear, (a, b) => a * weights.WFront + b * weights.WRear);

            if (weights.SlipFloorFactor > 0.0)
            {
                front = ApplyFloorAvailable(front, MaxOfPresent(fl, fr), weights);
                rear = ApplyFloorAvailable(rear, MaxOfPresent(rl, rr), weights);
                left = ApplyFloorAvailable(left, MaxOfPresent(fl, rl), weights);
                right = ApplyFloorAvailable(right, MaxOfPresent(fr, rr), weights);
                all = ApplyFloorAvailable(all, MaxOfPresent(fl, fr, rl, rr), weights);
            }

            hasValue = new[]
            {
                fl.HasValue, fr.HasValue, rl.HasValue, rr.HasValue,
                front.HasValue, rear.HasValue, left.HasValue, right.HasValue, all.HasValue
            };

            return new WheelAggregate(
                ClampMath.To0100(front ?? 0.0), ClampMath.To0100(rear ?? 0.0),
                ClampMath.To0100(left ?? 0.0), ClampMath.To0100(right ?? 0.0), ClampMath.To0100(all ?? 0.0));
        }

        /// <summary>Both present: the ordinary blend. Exactly one present: that wheel's own reading,
        /// unchanged (not diluted by treating the missing one as zero). Neither present: absent.</summary>
        private static double? BlendPair(double? a, double? b, Func<double, double, double> bothPresent)
        {
            if (a.HasValue && b.HasValue) return bothPresent(a.Value, b.Value);
            if (a.HasValue) return a.Value;
            if (b.HasValue) return b.Value;
            return null;
        }

        private static double? ApplyFloorAvailable(double? value, double? participatingMax, AggregationWeights weights)
        {
            if (!value.HasValue) return null; // nothing to floor
            if (!participatingMax.HasValue) return value; // should not happen (value present implies a participant was too), defensive
            return Math.Max(value.Value, participatingMax.Value * weights.SlipFloorFactor);
        }

        private static double? MaxOfPresent(double? a, double? b)
        {
            if (a.HasValue && b.HasValue) return Math.Max(a.Value, b.Value);
            return a ?? b;
        }

        private static double? MaxOfPresent(double? a, double? b, double? c, double? d)
        {
            double? ab = MaxOfPresent(a, b);
            double? cd = MaxOfPresent(c, d);
            return MaxOfPresent(ab, cd);
        }
    }
}
