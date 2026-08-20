using System;
using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>How badly a difference between two runs undermines their comparison.</summary>
    public enum ComparabilityLevel
    {
        [Tooltip("The two runs agree on this.")]
        Ok,
        [Tooltip("They differ, but in a way that does not affect the comparison — worth stating, not worth worrying about.")]
        Note,
        [Tooltip("They differ in a way that biases the comparison — the delta is still shown, with the caveat.")]
        Warning,
        [Tooltip("They differ in a way that makes the numbers different quantities — no verdict is given.")]
        Blocking
    }

    /// <summary>One line of the like-for-like audit run before any numbers are differenced.</summary>
    public class ComparabilityCheck
    {
        public string label;
        public string a;
        public string b;
        public ComparabilityLevel level;
        public string note;
    }

    public enum MetricPolarity { LowerIsBetter, HigherIsBetter, Informational }

    /// <summary>One metric compared across the two runs.</summary>
    public class ComparisonRow
    {
        public string label;
        public string unit;
        public string format = "0.000";
        public float a, b;
        public float delta;      // b - a
        public float deltaPct;   // relative to |a|
        public MetricPolarity polarity = MetricPolarity.Informational;
        /// <summary>-1 = A is better, +1 = B is better, 0 = tie, no call, or inside the noise band.</summary>
        public int better;
        public bool withinNoise;
        public bool primary;
        public bool hasDeltaPct = true;
    }

    /// <summary>One matched measurement point of a sweep.</summary>
    public class ComparisonSweepRow
    {
        public float parameter;
        public float cdA, cdB;
        public float clA, clB;
        public float cyA, cyB;
        public bool convergedA = true, convergedB = true;
    }

    /// <summary>The whole comparison: audit, metric table, sweep table and verdict.</summary>
    public class AeroComparisonReport
    {
        public AeroTestSession sessionA, sessionB;
        public AeroTestResult testA, testB;
        public string labelA = "A", labelB = "B";

        public List<ComparabilityCheck> checks = new List<ComparabilityCheck>();
        public List<ComparisonRow> rows = new List<ComparisonRow>();
        public List<ComparisonSweepRow> sweep = new List<ComparisonSweepRow>();

        /// <summary>False when a blocking difference means the two numbers are not the same quantity.</summary>
        public bool comparable;
        /// <summary>True when nothing at all was flagged — a clean, like-for-like pair.</summary>
        public bool cleanPair;

        /// <summary>-1 = A wins, +1 = B wins, 0 = too close to call / no verdict.</summary>
        public int winner;
        public string verdict = "";
        /// <summary>The winning vehicle's name alone, for callers that draw the side chip themselves.</summary>
        public string winnerName = "";
        public string verdictDetail = "";
        /// <summary>Uncertainty band (%) a delta must clear to mean anything.</summary>
        public float noiseBandPct = 1f;

        public string error;
        public bool Valid => string.IsNullOrEmpty(error);
    }

    /// <summary>
    /// Compares two exported test sessions the way an aerodynamicist would: first
    /// establish that the two runs are the same experiment on the same basis, then
    /// difference the coefficients, and only call a winner when the difference is
    /// bigger than the uncertainty on the two numbers being differenced.
    /// </summary>
    public static class AeroComparison
    {
        /// <summary>
        /// Index of the test in <paramref name="other"/> that best matches
        /// <paramref name="test"/>: same kind, then the closest name.
        /// Returns -1 when the other session has no test of that kind.
        /// </summary>
        public static int MatchTest(AeroTestResult test, AeroTestSession other)
        {
            if (test == null || other == null) return -1;
            int fallback = -1;
            for (int i = 0; i < other.tests.Count; i++)
            {
                if (other.tests[i].kind != test.kind) continue;
                if (string.Equals(other.tests[i].testName, test.testName, StringComparison.OrdinalIgnoreCase))
                    return i;
                if (fallback < 0) fallback = i;
            }
            return fallback;
        }

        public static AeroComparisonReport Compare(AeroTestSession a, AeroTestSession b,
                                                   int testIndexA = 0, int testIndexB = -1)
        {
            var report = new AeroComparisonReport { sessionA = a, sessionB = b };

            if (a == null || b == null)
            {
                report.error = "pick two result files";
                return report;
            }
            if (a.tests.Count == 0 || b.tests.Count == 0)
            {
                report.error = "one of the sessions contains no test results";
                return report;
            }

            testIndexA = Mathf.Clamp(testIndexA, 0, a.tests.Count - 1);
            report.testA = a.tests[testIndexA];
            if (testIndexB < 0) testIndexB = MatchTest(report.testA, b);
            if (testIndexB < 0)
            {
                report.error = $"'{b.vehicleName}' has no {report.testA.kind} test to compare against";
                report.checks.Add(new ComparabilityCheck
                {
                    label = "Test procedure",
                    a = report.testA.kind.ToString(),
                    b = "none of this kind",
                    level = ComparabilityLevel.Blocking,
                    note = "only two runs of the same procedure can be differenced"
                });
                return report;
            }
            report.testB = b.tests[Mathf.Clamp(testIndexB, 0, b.tests.Count - 1)];

            report.labelA = $"{a.vehicleName} · {report.testA.testName}";
            report.labelB = $"{b.vehicleName} · {report.testB.testName}";

            BuildChecks(report);
            report.comparable = true;
            report.cleanPair = true;
            foreach (var check in report.checks)
            {
                if (check.level == ComparabilityLevel.Blocking) report.comparable = false;
                // Notes do not dirty the pair — they state a difference that does not
                // change what the numbers mean.
                if (check.level == ComparabilityLevel.Blocking || check.level == ComparabilityLevel.Warning)
                    report.cleanPair = false;
            }

            var aggA = Aggregate.From(report.testA);
            var aggB = Aggregate.From(report.testB);
            report.noiseBandPct = NoiseBand(a, b, aggA, aggB);

            BuildRows(report, aggA, aggB);
            BuildSweep(report);
            BuildVerdict(report, aggA, aggB);
            return report;
        }

        // ------------------------------------------------------------------ audit

        static void BuildChecks(AeroComparisonReport r)
        {
            AeroTestSession a = r.sessionA, b = r.sessionB;

            Add(r, "Test procedure", r.testA.kind.ToString(), r.testB.kind.ToString(),
                r.testA.kind == r.testB.kind ? ComparabilityLevel.Ok : ComparabilityLevel.Blocking,
                "only two runs of the same procedure can be differenced");

            string classA = ClassLabel(a), classB = ClassLabel(b);
            if (a.vehicleClass == b.vehicleClass)
            {
                Add(r, "Vehicle class", classA, classB, ComparabilityLevel.Ok, null);
            }
            else
            {
                // Two different classes are still the same experiment as long as they
                // measure the same quantity. What actually breaks a comparison is a
                // different reference-area convention (a car's frontal silhouette
                // against a wing's planform), not the label on the vehicle — a road car
                // against a race car is an entirely reasonable thing to want.
                var profileA = AeroVehicleProfile.For(a.vehicleClass, a.watercraftMode);
                var profileB = AeroVehicleProfile.For(b.vehicleClass, b.watercraftMode);
                bool sameArea = profileA.AreaMode == profileB.AreaMode;
                bool sameObjective = profileA.LiftObjective == profileB.LiftObjective;

                // A note, not a caveat: if the conventions match, nothing about the
                // comparison is compromised, and flagging it amber would tell the
                // reader to distrust a number that is perfectly sound.
                Add(r, "Vehicle class", classA, classB,
                    sameArea ? ComparabilityLevel.Note : ComparabilityLevel.Blocking,
                    sameArea
                        ? (sameObjective
                            ? "different classes, but the same reference-area convention and the same objective — drag is comparable"
                            : "different classes with opposite lift objectives, so lift is reported but not scored; drag is comparable")
                        : "these classes normalize by different kinds of area, so their coefficients are not the same quantity");
            }

            // Cd is force / (q · A_ref), so a different *kind* of reference area makes
            // the two Cd numbers different quantities. A hand-set area is only a
            // scaling difference — and CdA = F/q, the primary metric, is immune to it
            // either way — so that is a caveat on Cd, not a block.
            bool basisEqual = a.referenceAreaMode == b.referenceAreaMode;
            bool eitherManual = a.referenceAreaMode == AeroReferenceAreaMode.Manual ||
                                b.referenceAreaMode == AeroReferenceAreaMode.Manual;
            Add(r, "Reference area basis", a.referenceAreaBasis, b.referenceAreaBasis,
                basisEqual ? ComparabilityLevel.Ok
                    : eitherManual ? ComparabilityLevel.Warning
                    : ComparabilityLevel.Blocking,
                eitherManual
                    ? "one side normalizes by a hand-set area, so its Cd is on a different scale; CdA = F/q is unaffected"
                    : "coefficients divided by different kinds of area are not the same quantity");

            Add(r, "Working fluid", a.fluidMedium.ToString(), b.fluidMedium.ToString(),
                a.fluidMedium == b.fluidMedium ? ComparabilityLevel.Ok : ComparabilityLevel.Blocking,
                "forces in air and in water are not comparable");

            float speedA = r.testA.speedMs, speedB = r.testB.speedMs;
            Add(r, "Test speed", $"{speedA * 3.6f:0.#} km/h", $"{speedB * 3.6f:0.#} km/h",
                RelDiff(speedA, speedB) < 0.01f ? ComparabilityLevel.Ok : ComparabilityLevel.Warning,
                "coefficients are speed-independent in theory, but not at different Reynolds numbers on this solver");

            Add(r, "Ground simulation",
                $"{r.testA.ground}{(r.testA.rotatingWheels ? " + rotating wheels" : "")}",
                $"{r.testB.ground}{(r.testB.rotatingWheels ? " + rotating wheels" : "")}",
                r.testA.ground == r.testB.ground && r.testA.rotatingWheels == r.testB.rotatingWheels
                    ? ComparabilityLevel.Ok : ComparabilityLevel.Warning,
                "a valid experiment on its own, but then the ground simulation is what is being compared");

            if (!a.metadataComplete || !b.metadataComplete)
            {
                Add(r, "Archive completeness",
                    a.metadataComplete ? "full" : "CSV — settings unknown",
                    b.metadataComplete ? "full" : "CSV — settings unknown",
                    ComparabilityLevel.Warning,
                    "a CSV never recorded the grid, soft-voxel state or vehicle class, so those could not be checked");
            }
            else
            {
                Add(r, "Grid", GridLabel(a), GridLabel(b),
                    RelDiff(a.cellSizeM, b.cellSizeM) < 0.02f ? ComparabilityLevel.Ok : ComparabilityLevel.Warning,
                    "absolute coefficients shift with cell size — never compare across resolutions");

                Add(r, "Soft voxels", a.softVoxels ? "on" : "off", b.softVoxels ? "on" : "off",
                    a.softVoxels == b.softVoxels ? ComparabilityLevel.Ok : ComparabilityLevel.Warning,
                    "sub-cell coverage changes the effective surface roughness — never compare across the toggle");

                Add(r, "Solver version", Version(a), Version(b),
                    string.Equals(Version(a), Version(b), StringComparison.Ordinal)
                        ? ComparabilityLevel.Ok : ComparabilityLevel.Warning,
                    "the physics may have changed between package versions");
            }

            float blockA = a.blockageRatio, blockB = b.blockageRatio;
            bool blockageHigh = blockA > WindTunnelDomain.BlockageWarningRatio ||
                                blockB > WindTunnelDomain.BlockageWarningRatio;
            Add(r, "Blockage", $"{blockA:P1}", $"{blockB:P1}",
                blockageHigh ? ComparabilityLevel.Warning : ComparabilityLevel.Ok,
                $"above {WindTunnelDomain.BlockageWarningRatio:P0} the tunnel walls inflate the coefficients");

            // Report what the runs are actually worth: the uncertainty on each mean.
            // "Unsettled" is no longer "unknown error" — the mean is still the
            // measurement, it just carries a wider band, and that band is stated.
            bool allA = AllConverged(r.testA), allB = AllConverged(r.testB);
            Add(r, "Measurement uncertainty", UncertaintyLabel(r.testA), UncertaintyLabel(r.testB),
                allA && allB ? ComparabilityLevel.Ok : ComparabilityLevel.Warning,
                "a point that hit the step cap before its mean settled carries the wider band shown — " +
                "a delta smaller than that is not a result");
        }

        static void Add(AeroComparisonReport r, string label, string a, string b,
                        ComparabilityLevel level, string note)
        {
            r.checks.Add(new ComparabilityCheck
            {
                label = label,
                a = string.IsNullOrEmpty(a) ? "—" : a,
                b = string.IsNullOrEmpty(b) ? "—" : b,
                level = level,
                note = level == ComparabilityLevel.Ok ? null : note
            });
        }

        static string ClassLabel(AeroTestSession s) =>
            string.IsNullOrEmpty(s.vehicleClassLabel) ? s.vehicleClass.ToString() : s.vehicleClassLabel;

        static string GridLabel(AeroTestSession s) =>
            string.IsNullOrEmpty(s.gridInfo) ? $"{s.cellSizeM * 1000f:0} mm cells" : s.gridInfo;

        static string Version(AeroTestSession s) =>
            string.IsNullOrEmpty(s.packageVersion) ? "unknown" : s.packageVersion;

        static bool AllConverged(AeroTestResult t)
        {
            foreach (var p in t.points)
                if (!p.converged) return false;
            return t.points.Count > 0;
        }

        /// <summary>"±0.4% of Cd, settled" — the run's own statement of what it is worth.</summary>
        static string UncertaintyLabel(AeroTestResult t)
        {
            float worst = -1f;
            bool settled = true;
            float flowThroughs = 0f;
            foreach (var p in t.points)
            {
                if (p.standardError >= 0f) worst = Mathf.Max(worst, p.standardError);
                if (!p.converged) settled = false;
                flowThroughs = Mathf.Max(flowThroughs, p.flowThroughsAveraged);
            }

            if (worst < 0f) return settled ? "settled (band not recorded)" : "unsettled (band not recorded)";
            string span = flowThroughs > 0f ? $" over {flowThroughs:0.#} flow-throughs" : "";
            return $"±{worst:P1} of Cd{span}{(settled ? ", settled" : ", unsettled")}";
        }

        static float RelDiff(float a, float b)
        {
            float scale = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            return scale > 1e-6f ? Mathf.Abs(a - b) / scale : 0f;
        }

        // ------------------------------------------------------------------ metrics

        /// <summary>Sweep-averaged view of one side. A single-point test averages one point.</summary>
        struct Aggregate
        {
            public float cd, cdA, cl, clFront, clRear, cyAbsMax, dragN, powerKw, area, speedMs;
            public float worstCv;
            /// <summary>Worst standard error of the mean across the points, as a fraction.</summary>
            public float worstStandardError;
            public int points;

            public float LiftToDrag => Mathf.Abs(cd) > 1e-5f ? cl / cd : 0f;
            public float FrontBalancePct
            {
                get
                {
                    float total = Mathf.Abs(clFront) + Mathf.Abs(clRear);
                    return total > 1e-6f ? 100f * Mathf.Abs(clFront) / total : 50f;
                }
            }

            public static Aggregate From(AeroTestResult test)
            {
                var agg = new Aggregate { worstCv = -1f, worstStandardError = -1f };
                if (test == null || test.points.Count == 0) return agg;

                foreach (var p in test.points)
                {
                    var s = p.sample;
                    agg.cd += s.cd;
                    agg.cdA += s.cdA;
                    agg.cl += s.cl;
                    agg.clFront += s.clFront;
                    agg.clRear += s.clRear;
                    agg.dragN += s.dragForceN;
                    agg.powerKw += s.aeroPowerW / 1000f;
                    agg.area += s.frontalAreaM2;
                    agg.speedMs += s.airSpeedMs;
                    agg.cyAbsMax = Mathf.Max(agg.cyAbsMax, Mathf.Abs(s.cy));
                    if (p.convergenceCv >= 0f) agg.worstCv = Mathf.Max(agg.worstCv, p.convergenceCv);
                    if (p.standardError >= 0f) agg.worstStandardError = Mathf.Max(agg.worstStandardError, p.standardError);
                }

                float n = test.points.Count;
                agg.points = test.points.Count;
                agg.cd /= n; agg.cdA /= n; agg.cl /= n; agg.clFront /= n; agg.clRear /= n;
                agg.dragN /= n; agg.powerKw /= n; agg.area /= n; agg.speedMs /= n;
                return agg;
            }
        }

        /// <summary>
        /// The band a delta must clear to mean anything.
        ///
        /// The right quantity is the uncertainty on the numbers being differenced — the
        /// standard error of each run's mean — not the scatter of the individual samples
        /// that went into it. Averaging is precisely what buys the resolution to see a
        /// small difference; judging against raw scatter would throw that away and call
        /// every real 2% improvement "noise". Older sessions that recorded no standard
        /// error fall back to the sample scatter, then to the tolerance they ran at.
        /// The 0.2% floor is round-off and lattice-quantisation honesty.
        /// </summary>
        static float NoiseBand(AeroTestSession a, AeroTestSession b, Aggregate aggA, Aggregate aggB)
        {
            float band = Mathf.Max(aggA.worstStandardError, aggB.worstStandardError);
            if (band < 0f) band = Mathf.Max(aggA.worstCv, aggB.worstCv);
            if (band < 0f) band = Mathf.Max(a.convergenceTolerance, b.convergenceTolerance);
            return Mathf.Max(band, 0.002f) * 100f;
        }

        static void BuildRows(AeroComparisonReport r, Aggregate a, Aggregate b)
        {
            var cls = r.sessionA.vehicleClass;
            bool aircraft = cls == AeroVehicleClass.Aircraft;
            var profile = AeroVehicleProfile.For(cls, r.sessionA.watercraftMode);
            var profileB = AeroVehicleProfile.For(r.sessionB.vehicleClass, r.sessionB.watercraftMode);

            // Lift is scored only between two vehicles of the SAME class. Two classes
            // can share a "lower is better" rule and still be chasing different things:
            // a race car's −3.8 downforce against a road car's +3.3 lift satisfies the
            // rule while meaning nothing — they are not competing at the same task, and
            // road-car lift does not even converge with grid (see the crash course,
            // Part VIII §2b). Drag stays comparable across classes; lift does not.
            bool sameClass = r.sessionA.vehicleClass == r.sessionB.vehicleClass;
            MetricPolarity liftPolarity =
                !sameClass || profile.LiftObjective != profileB.LiftObjective
                    ? MetricPolarity.Informational
                    : profile.LiftObjective switch
                    {
                        AeroLiftObjective.LowerIsBetter => MetricPolarity.LowerIsBetter,
                        AeroLiftObjective.HigherIsBetter => MetricPolarity.HigherIsBetter,
                        _ => MetricPolarity.Informational
                    };

            bool sweep = r.testA.kind != AeroTestKind.ConstantSpeedDrag;
            string prefix = sweep ? "Mean " : "";

            Row(r, prefix + "Cd", "", a.cd, b.cd, MetricPolarity.LowerIsBetter, "0.000");
            Row(r, prefix + "CdA (drag area)", "m²", a.cdA, b.cdA, MetricPolarity.LowerIsBetter, "0.000",
                primary: !aircraft);
            Row(r, prefix + "Cl", "", a.cl, b.cl, liftPolarity, "0.000");
            Row(r, prefix + "Cl front", "", a.clFront, b.clFront, MetricPolarity.Informational, "0.000");
            Row(r, prefix + "Cl rear", "", a.clRear, b.clRear, MetricPolarity.Informational, "0.000");
            Row(r, "Front lift share", "%", a.FrontBalancePct, b.FrontBalancePct, MetricPolarity.Informational, "0.0");
            Row(r, "Lift / drag", "", a.LiftToDrag, b.LiftToDrag,
                aircraft && sameClass ? MetricPolarity.HigherIsBetter : MetricPolarity.Informational, "0.00",
                primary: aircraft);
            if (a.cyAbsMax > 1e-4f || b.cyAbsMax > 1e-4f)
            {
                // Side force is a crosswind result. At zero yaw a symmetric body should
                // read zero, and both sides do — scoring a 27% difference between two
                // numbers that are both ~0.03 would be ranking wake jitter. Only a
                // sweep that actually yaws the vehicle produces a Cy worth scoring.
                bool yawed = r.testA.kind == AeroTestKind.YawSweep && r.testB.kind == AeroTestKind.YawSweep;
                Row(r, "Peak |Cy|", "", a.cyAbsMax, b.cyAbsMax,
                    yawed ? MetricPolarity.LowerIsBetter : MetricPolarity.Informational, "0.000");
            }
            Row(r, prefix + "Drag force", "N", a.dragN, b.dragN, MetricPolarity.LowerIsBetter, "0.#");
            Row(r, prefix + "Aero power", "kW", a.powerKw, b.powerKw, MetricPolarity.LowerIsBetter, "0.00");
            Row(r, "Reference area", "m²", a.area, b.area, MetricPolarity.Informational, "0.000");
            Row(r, "Test speed", "km/h", a.speedMs * 3.6f, b.speedMs * 3.6f, MetricPolarity.Informational, "0.#");
        }

        static void Row(AeroComparisonReport r, string label, string unit, float a, float b,
                        MetricPolarity polarity, string format, bool primary = false)
        {
            float delta = b - a;
            float scale = Mathf.Abs(a);
            bool hasPct = scale > 1e-6f;
            float pct = hasPct ? 100f * delta / scale : 0f;

            var row = new ComparisonRow
            {
                label = label,
                unit = unit,
                a = a,
                b = b,
                delta = delta,
                deltaPct = pct,
                hasDeltaPct = hasPct,
                polarity = polarity,
                format = format,
                primary = primary
            };

            // A change smaller than the uncertainty on the two means is not a result.
            row.withinNoise = hasPct
                ? Mathf.Abs(pct) < r.noiseBandPct
                : Mathf.Abs(delta) < 1e-4f;

            if (polarity == MetricPolarity.Informational || row.withinNoise) row.better = 0;
            else if (polarity == MetricPolarity.LowerIsBetter) row.better = delta < 0f ? 1 : -1;
            else row.better = delta > 0f ? 1 : -1;

            r.rows.Add(row);
        }

        // ------------------------------------------------------------------ sweep

        static void BuildSweep(AeroComparisonReport r)
        {
            if (r.testA.kind == AeroTestKind.ConstantSpeedDrag) return;

            foreach (var pa in r.testA.points)
            {
                AeroTestPointResult best = null;
                float bestDist = float.MaxValue;
                foreach (var pb in r.testB.points)
                {
                    float d = Mathf.Abs(pb.parameter - pa.parameter);
                    if (d < bestDist) { bestDist = d; best = pb; }
                }
                // Points must line up: a 15° station is not a comparison for a 10° one.
                float span = Mathf.Max(Mathf.Abs(pa.parameter), 1f);
                if (best == null || bestDist > 0.05f * span) continue;

                r.sweep.Add(new ComparisonSweepRow
                {
                    parameter = pa.parameter,
                    cdA = pa.sample.cd,
                    cdB = best.sample.cd,
                    clA = pa.sample.cl,
                    clB = best.sample.cl,
                    cyA = pa.sample.cy,
                    cyB = best.sample.cy,
                    convergedA = pa.converged,
                    convergedB = best.converged
                });
            }
        }

        // ------------------------------------------------------------------ verdict

        static void BuildVerdict(AeroComparisonReport r, Aggregate a, Aggregate b)
        {
            if (!r.comparable)
            {
                r.winner = 0;
                r.verdict = "Not comparable";
                r.verdictDetail = "These two runs do not measure the same quantity — see the audit above. " +
                                  "Nothing is scored.";
                return;
            }

            ComparisonRow primary = null;
            foreach (var row in r.rows)
                if (row.primary) { primary = row; break; }
            if (primary == null)
            {
                r.winner = 0;
                r.verdict = "No verdict";
                r.verdictDetail = "No primary metric for this vehicle class.";
                return;
            }

            // Name what was actually flagged. "Settings the two runs did not share" is
            // wrong when the flag is the runs' own uncertainty, and a caveat that
            // misdescribes itself teaches the reader to ignore caveats.
            string caveat = "";
            if (!r.cleanPair)
            {
                var flagged = new List<string>();
                foreach (var check in r.checks)
                    if (check.level == ComparabilityLevel.Warning)
                        flagged.Add(check.label.ToLowerInvariant());
                caveat = flagged.Count == 0
                    ? " Treat the size of the difference with care — see the audit above."
                    : $" Treat the size of the difference with care: {string.Join(", ", flagged)} " +
                      (flagged.Count == 1 ? "was flagged above." : "were flagged above.");
            }

            if (primary.withinNoise || primary.better == 0)
            {
                r.winner = 0;
                r.verdict = "Too close to call";
                r.verdictDetail =
                    $"{primary.label} differs by {Mathf.Abs(primary.deltaPct):0.0}%, inside the ±{r.noiseBandPct:0.0}% " +
                    "uncertainty on these two means. Run longer or average more flow-throughs to resolve it." + caveat;
                return;
            }

            r.winner = primary.better;
            string winnerName = primary.better > 0 ? r.sessionB.vehicleName : r.sessionA.vehicleName;
            string winnerSide = primary.better > 0 ? "B" : "A";
            r.winnerName = winnerName;
            float better = primary.better > 0 ? primary.b : primary.a;
            float worse = primary.better > 0 ? primary.a : primary.b;

            r.verdict = $"{winnerSide} — {winnerName}";
            r.verdictDetail =
                $"{primary.label} {better.ToString(primary.format)} vs {worse.ToString(primary.format)} " +
                $"{primary.unit} ({Mathf.Abs(primary.deltaPct):0.0}% {(primary.polarity == MetricPolarity.LowerIsBetter ? "lower" : "higher")}), " +
                $"beyond the ±{r.noiseBandPct:0.0}% uncertainty on these two means." + caveat;
        }
    }
}
