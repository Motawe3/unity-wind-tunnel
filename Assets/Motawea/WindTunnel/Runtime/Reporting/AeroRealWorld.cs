using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>One practical consequence derived from a comparison delta.</summary>
    public class RealWorldReading
    {
        public string label;
        public string value;
        /// <summary>Where the number comes from — printed next to it, never hidden.</summary>
        public string basis;
        /// <summary>True for values measured by the solver; false for derived estimates.</summary>
        public bool measured;
        /// <summary>+1 = this reading is an improvement (B over A), −1 = a regression,
        /// 0 = neutral/informational. Drives the colour coding in the export.</summary>
        public int better;
    }

    /// <summary>The real-world consequences block of a comparison.</summary>
    public class RealWorldImpact
    {
        /// <summary>False when the comparison cannot honestly carry consequences at all
        /// (not comparable, no CdA, wrong medium/class).</summary>
        public bool applicable;
        /// <summary>True when the CdA delta is inside the noise band — the section then
        /// says so instead of listing consequences.</summary>
        public bool withinNoise;
        public float cdaDeltaPct;      // B − A, % of A
        public float noiseBandPct;
        public List<RealWorldReading> readings = new List<RealWorldReading>();
        public List<string> assumptions = new List<string>();
    }

    /// <summary>
    /// Turns a CdA difference into the quantities a buyer actually asks about — fuel,
    /// CO₂, EV range — using standard road-load arithmetic with every assumption stated.
    ///
    /// Two rules keep this honest. First: consequences are only derived from a delta
    /// that cleared the measurement's own noise band; inside it, the block says "no
    /// claim" in so many words. Second: the tool's measured tendency to exaggerate
    /// pairwise deltas (up to ~25 %, worst on the bluffest shapes — see the
    /// cross-vehicle validation table) is folded in as a range, not ignored: every
    /// derived figure spans [0.75×, 1.0×] of the measured delta.
    /// </summary>
    public static class AeroRealWorld
    {
        static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // Measured bias envelope: the cross-vehicle validation (locked cell size; see
        // aero_compare_live.txt) finds pairwise CdA deltas exaggerated by 4–27 %.
        // Consequences are therefore quoted for 0.7–1.0× of the measured delta.
        const float BiasLow = 0.7f, BiasHigh = 1.0f;

        // Aerodynamic share of road-load energy for passenger cars / light trucks on a
        // level road (standard road-load decomposition; the rest is rolling resistance
        // and drivetrain): highway ≈ half, mixed ≈ a third, urban ≈ a sixth.
        const float HighwayShareLo = 0.45f, HighwayShareHi = 0.55f;
        const float MixedShareLo = 0.30f, MixedShareHi = 0.40f;
        const float UrbanShareLo = 0.10f, UrbanShareHi = 0.20f;
        // EVs regenerate braking losses, so aero is a larger slice of highway energy.
        const float EvHighwayShareLo = 0.55f, EvHighwayShareHi = 0.65f;

        // Yearly figures need a usage profile; these are stated on the page and meant
        // to be mentally rescaled to the reader's fleet.
        const float AnnualKm = 15000f;
        const float MixedLPer100Km = 9.0f;      // light truck / SUV ballpark
        const float Co2KgPerLitre = 2.31f;      // petrol, tank-to-wheel

        public static RealWorldImpact Derive(AeroComparisonReport r)
        {
            var impact = new RealWorldImpact();
            if (r == null || !r.Valid || !r.comparable) return impact;

            // Fuel/range arithmetic below is road-vehicle physics in air. Watercraft
            // and aircraft comparisons keep the measured rows only.
            bool roadVehicle =
                r.sessionA.fluidMedium == AeroFluidMedium.Air &&
                r.sessionB.fluidMedium == AeroFluidMedium.Air &&
                r.sessionA.vehicleClass != AeroVehicleClass.Aircraft &&
                r.sessionB.vehicleClass != AeroVehicleClass.Aircraft;

            var cda = FindRow(r, "CdA (drag area)");
            if (cda == null || !cda.hasDeltaPct) return impact;

            impact.applicable = true;
            impact.cdaDeltaPct = cda.deltaPct;
            impact.noiseBandPct = r.noiseBandPct;

            if (cda.withinNoise)
            {
                impact.withinNoise = true;
                return impact;
            }

            // ---- measured rows first: no assumptions in these two ----
            impact.readings.Add(new RealWorldReading
            {
                label = "Drag area (CdA)",
                value = string.Format(Ic, "{0:+0.000;-0.000} m² ({1:+0.0;-0.0} %)", cda.delta, cda.deltaPct),
                basis = $"measured, uncertainty ±{r.noiseBandPct.ToString("0.0", Ic)} %",
                measured = true,
                better = cda.delta < 0f ? 1 : -1   // less drag area is always the win
            });

            var power = FindRow(r, "Aero power");
            var speed = FindRow(r, "Test speed");
            if (power != null && Mathf.Abs(power.delta) > 1e-4f)
                impact.readings.Add(new RealWorldReading
                {
                    label = speed != null
                        ? string.Format(Ic, "Aero power at {0:0.#} km/h", speed.a)
                        : "Aero power at the test speed",
                    value = string.Format(Ic, "{0:+0.00;-0.00} kW", power.delta),
                    basis = "measured: the engine power the air difference costs or saves at that speed",
                    measured = true,
                    better = power.delta < 0f ? 1 : -1
                });

            if (!roadVehicle) return impact;

            // ---- derived estimates: range = share × [0.7, 1.0] × measured delta ----
            float d = cda.deltaPct;   // signed, B − A; less drag = better everywhere below
            int lessDragBetter = d < 0f ? 1 : -1;
            void Derived(string label, float shareLo, float shareHi, string basis)
            {
                float lo = d * shareLo * BiasLow;
                float hi = d * shareHi * BiasHigh;
                impact.readings.Add(new RealWorldReading
                {
                    label = label,
                    value = FormatRangePct(lo, hi),
                    basis = basis,
                    measured = false,
                    better = lessDragBetter
                });
            }

            Derived("Highway fuel (~110 km/h)", HighwayShareLo, HighwayShareHi,
                "aero is ~45–55 % of road load at highway speed");
            Derived("Mixed driving fuel", MixedShareLo, MixedShareHi,
                "aero is ~30–40 % of road load in mixed driving");
            Derived("Urban fuel", UrbanShareLo, UrbanShareHi,
                "aero is ~10–20 % of road load in town — shape changes barely matter here");

            float litresBase = AnnualKm / 100f * MixedLPer100Km;
            float litresLo = litresBase * Mathf.Abs(d) / 100f * MixedShareLo * BiasLow;
            float litresHi = litresBase * Mathf.Abs(d) / 100f * MixedShareHi * BiasHigh;
            string gainOrCost = d < 0f ? "saved" : "added";
            impact.readings.Add(new RealWorldReading
            {
                label = "Fuel per year (mixed use)",
                value = string.Format(Ic, "{0:0}–{1:0} L {2}", litresLo, litresHi, gainOrCost),
                basis = string.Format(Ic, "at {0:0,0} km/yr, {1:0.#} L/100 km — rescale to your fleet", AnnualKm, MixedLPer100Km),
                measured = false,
                better = lessDragBetter
            });
            impact.readings.Add(new RealWorldReading
            {
                label = "CO₂ per year (mixed use)",
                value = string.Format(Ic, "{0:0}–{1:0} kg {2}", litresLo * Co2KgPerLitre, litresHi * Co2KgPerLitre, gainOrCost),
                basis = string.Format(Ic, "{0:0.00} kg CO₂ per litre of petrol", Co2KgPerLitre),
                measured = false,
                better = lessDragBetter
            });

            float evLo = -d * EvHighwayShareLo * BiasLow;   // less drag = MORE range
            float evHi = -d * EvHighwayShareHi * BiasHigh;
            impact.readings.Add(new RealWorldReading
            {
                label = "EV highway range",
                value = FormatRangePct(evLo, evHi),
                basis = "aero is ~55–65 % of an EV's highway energy",
                measured = false,
                better = lessDragBetter
            });

            impact.assumptions.Add(string.Format(Ic,
                "Derived figures span 0.7–1.0× of the measured CdA delta: this tool's cross-vehicle " +
                "validation measured pairwise deltas exaggerated by up to ~30 %, worst on bluff shapes."));
            impact.assumptions.Add(
                "Road-load shares assume a passenger car / light truck on a level road; they scale " +
                "with speed squared, so higher cruise speeds push every figure toward its upper end.");
            impact.assumptions.Add(string.Format(Ic,
                "Yearly figures assume {0:0,0} km/yr at {1:0.#} L/100 km mixed — linear in both, so rescaling is safe.",
                AnnualKm, MixedLPer100Km));

            return impact;
        }

        static ComparisonRow FindRow(AeroComparisonReport r, string labelSuffix) =>
            r.rows.Find(x => x.label.EndsWith(labelSuffix, StringComparison.Ordinal));

        static string FormatRangePct(float lo, float hi)
        {
            if (Mathf.Abs(lo) > Mathf.Abs(hi)) (lo, hi) = (hi, lo);
            return string.Format(Ic, "{0:+0.0;-0.0} to {1:+0.0;-0.0} %", lo, hi);
        }
    }
}
