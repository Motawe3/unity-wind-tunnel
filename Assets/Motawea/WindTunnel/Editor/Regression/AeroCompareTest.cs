// Headless regression test for the result-comparison feature (update 2):
//   Unity.exe -batchmode -projectPath . -executeMethod AeroCompareTest.Run
// Builds synthetic sessions, round-trips them through the JSON archive and the CSV
// fallback, and checks that the comparison engine blocks what must be blocked, calls
// the winner it must call, and refuses to call one inside the convergence noise band.
// No GPU work — this one runs anywhere.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Motawea.WindTunnel;
using Motawea.WindTunnel.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;   // Query() is an extension method: the using is required

public static class AeroCompareTest
{
    const string ReportPath = "aero_compare_test.txt";

    static StringBuilder _log;
    static int _passed, _failed;

    public static void Run()
    {
        int exit = Execute();
        EditorApplication.Exit(exit);
    }

    /// <summary>Runs the checks and returns a process exit code. Also called by AeroUpdateTests.</summary>
    public static int Execute()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try
        {
            Cases();
        }
        catch (Exception e)
        {
            _log.AppendLine("EXCEPTION: " + e);
            _failed++;
        }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"COMPARE TESTS: PASS ({_passed} checks)"
                                     : $"COMPARE TESTS: FAIL ({_failed} of {_passed + _failed} checks)");
        File.WriteAllText(ReportPath, _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void Cases()
    {
        string dir = Path.Combine(Path.GetTempPath(), "windtunnel-compare-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        _log.AppendLine($"AeroCompareTest — scratch folder {dir}");

        // ---- 1. archive round trip -----------------------------------------
        var baseline = DragSession("Baseline SUV", AeroVehicleClass.RoadVehicle, cd: 0.400f, area: 2.50f);
        string stem = AeroReportExporter.ExportAll(baseline, dir);
        _log.AppendLine($"exported {stem}");

        string jsonPath = Path.Combine(dir, stem + AeroReportExporter.JsonExtension);
        Check("JSON archive written", File.Exists(jsonPath));
        Check("HTML written", File.Exists(Path.Combine(dir, stem + ".html")));
        Check("CSV written", File.Exists(Path.Combine(dir, stem + ".csv")));

        var reloaded = AeroSessionArchive.Load(jsonPath, out string loadError);
        Check("JSON reloads", reloaded != null, loadError);
        if (reloaded != null)
        {
            Check("vehicle name survives", reloaded.vehicleName == baseline.vehicleName);
            Check("vehicle class survives", reloaded.vehicleClass == baseline.vehicleClass);
            Check("reference area survives", Mathf.Approximately(reloaded.frontalAreaM2, baseline.frontalAreaM2));
            Check("grid settings survive",
                  reloaded.softVoxels == baseline.softVoxels &&
                  Mathf.Approximately(reloaded.cellSizeM, baseline.cellSizeM));
            Check("Cd survives", Mathf.Abs(reloaded.tests[0].points[0].sample.cd - 0.400f) < 1e-4f,
                  reloaded.tests[0].points[0].sample.cd.ToString("0.0000"));
            Check("metadata marked complete", reloaded.metadataComplete);
        }

        // The CSV fallback keeps the numbers but cannot know the configuration.
        var fromCsv = AeroSessionArchive.Load(Path.Combine(dir, stem + ".csv"), out string csvError);
        Check("CSV fallback loads", fromCsv != null, csvError);
        if (fromCsv != null)
        {
            Check("CSV keeps Cd", Mathf.Abs(fromCsv.tests[0].points[0].sample.cd - 0.400f) < 1e-3f);
            Check("CSV flagged as incomplete metadata", !fromCsv.metadataComplete);
        }

        // Listing hides a CSV that has a JSON twin, so one session is one entry.
        var listed = AeroSessionArchive.List(dir);
        Check("listing collapses the twin formats", listed.Count == 1, $"{listed.Count} entries");

        // ---- 2. a real improvement is called ---------------------------------
        var improved = DragSession("Improved SUV", AeroVehicleClass.RoadVehicle, cd: 0.372f, area: 2.50f);
        var report = AeroComparison.Compare(baseline, improved);
        Check("comparable pair", report.Valid && report.comparable, report.error);
        Check("B wins on a 7% CdA reduction", report.winner > 0, $"winner {report.winner}: {report.verdict}");
        var primary = report.rows.Find(r => r.primary);
        Check("primary metric is CdA for a road vehicle", primary != null && primary.label.Contains("CdA"),
              primary?.label);
        Check("delta is B − A", primary != null && primary.delta < 0f, primary?.delta.ToString("0.000"));
        Check("clean pair has no caveats", report.cleanPair);
        _log.AppendLine($"  verdict: {report.verdict} — {report.verdictDetail}");

        // ---- 3. a difference inside the noise band is not a result -----------
        var noise = DragSession("Barely different SUV", AeroVehicleClass.RoadVehicle, cd: 0.4008f, area: 2.50f);
        var noisyReport = AeroComparison.Compare(baseline, noise);
        Check("0.2% delta is inside the noise band", noisyReport.winner == 0, noisyReport.verdict);
        Check("row flagged as within noise", noisyReport.rows.Find(r => r.primary)?.withinNoise == true);

        // ---- 3b. averaging is what buys the resolution to see a small change ---
        // These runs scatter 4% sample-to-sample but their MEANS are good to 0.4%.
        // Judging a delta against the raw scatter would bury a real 2% improvement;
        // judging it against the uncertainty on the means resolves it.
        var smallGain = DragSession("Slightly better SUV", AeroVehicleClass.RoadVehicle, cd: 0.392f, area: 2.50f);
        var smallReport = AeroComparison.Compare(baseline, smallGain);
        Check("noise band comes from the uncertainty on the means, not sample scatter",
              smallReport.noiseBandPct < 1f, $"band ±{smallReport.noiseBandPct:0.00}%");
        Check("a 2% improvement is resolvable", smallReport.winner > 0, smallReport.verdict);
        _log.AppendLine($"  small-gain verdict: {smallReport.verdict} — {smallReport.verdictDetail}");

        // ---- 4. mismatched procedures and classes are blocked ----------------
        var yaw = YawSession("Yaw SUV", AeroVehicleClass.RoadVehicle);
        var kindMismatch = AeroComparison.Compare(baseline, yaw);
        Check("drag vs yaw has no counterpart test", !kindMismatch.Valid, kindMismatch.error);

        var aircraft = DragSession("Drone", AeroVehicleClass.Aircraft, cd: 0.080f, area: 0.35f,
                                   areaMode: AeroReferenceAreaMode.Planform, cl: 0.55f);
        var classMismatch = AeroComparison.Compare(baseline, aircraft);
        Check("road vs aircraft is blocked", !classMismatch.comparable);
        Check("blocked comparison gives no verdict", classMismatch.winner == 0, classMismatch.verdict);
        bool blockedOnClass = classMismatch.checks.Exists(c =>
            c.label == "Vehicle class" && c.level == ComparabilityLevel.Blocking);
        bool blockedOnBasis = classMismatch.checks.Exists(c =>
            c.label == "Reference area basis" && c.level == ComparabilityLevel.Blocking);
        Check("class mismatch flagged", blockedOnClass);
        Check("reference-area basis mismatch flagged", blockedOnBasis);

        // ---- 4b. a road car against a race car IS comparable ------------------
        // Both normalize by frontal silhouette and both want less drag; only the lift
        // objective differs. Blocking this would be useless strictness.
        var raceCar = DragSession("GT race car", AeroVehicleClass.Motorsport, cd: 0.372f, area: 2.10f, cl: -0.90f);
        var mixed = AeroComparison.Compare(baseline, raceCar);
        Check("road vehicle vs motorsport is comparable", mixed.comparable, mixed.verdict);
        Check("class difference is a note, not a caveat or a block", mixed.checks.Exists(c =>
            c.label == "Vehicle class" && c.level == ComparabilityLevel.Note));
        // A note says "this difference does not affect the comparison", so it must not
        // drag the verdict's confidence down with it.
        Check("a note leaves the pair clean", mixed.cleanPair);
        // A race car's downforce against a road car's lift satisfies "lower is better"
        // while meaning nothing — they are not competing at the same task.
        Check("lift is unscored across different classes",
              mixed.rows.Find(r => r.label.EndsWith("Cl"))?.polarity == MetricPolarity.Informational);
        Check("but drag is still scored across them",
              mixed.rows.Find(r => r.primary)?.polarity == MetricPolarity.LowerIsBetter);
        // Same class, shared objective: lift is the design target and gets scored.
        var raceVsRace = AeroComparison.Compare(
            DragSession("GT race car A", AeroVehicleClass.Motorsport, cd: 0.400f, area: 2.10f, cl: -1.20f),
            DragSession("GT race car B", AeroVehicleClass.Motorsport, cd: 0.400f, area: 2.10f, cl: -1.80f));
        Check("lift IS scored between two of the same class",
              raceVsRace.rows.Find(r => r.label.EndsWith("Cl"))?.polarity == MetricPolarity.LowerIsBetter);
        Check("more downforce wins for motorsport",
              raceVsRace.rows.Find(r => r.label.EndsWith("Cl"))?.better > 0);

        // Side force at zero yaw is wake jitter around zero on both sides; ranking a
        // 27% difference between two ~0.03 readings would be ranking noise.
        var cyRow = mixed.rows.Find(r => r.label.Contains("Cy"));
        Check("peak |Cy| is unscored outside a yaw sweep",
              cyRow == null || cyRow.polarity == MetricPolarity.Informational);
        Check("verdict carries no caveat for a noted difference",
              !mixed.verdictDetail.Contains("audit"), mixed.verdictDetail);
        Check("mixed classes still produce a verdict", mixed.winner != 0, mixed.verdict);
        Check("primary metric is still CdA", mixed.rows.Find(r => r.primary)?.label.Contains("CdA") == true);
        // Road cars want less lift, race cars want more downforce: no shared objective.
        var liftRow = mixed.rows.Find(r => r.label.EndsWith("Cl"));
        Check("lift is left unscored when the objectives disagree",
              liftRow != null && liftRow.polarity == MetricPolarity.Informational);
        _log.AppendLine($"  mixed-class verdict: {mixed.verdict} — {mixed.verdictDetail}");

        // A hand-set reference area only rescales Cd; CdA = F/q is untouched, so it is
        // a caveat rather than a block.
        var manualArea = DragSession("Baseline SUV (published area)", AeroVehicleClass.RoadVehicle,
                                     cd: 0.372f, area: 2.90f, areaMode: AeroReferenceAreaMode.Manual);
        var manualCmp = AeroComparison.Compare(baseline, manualArea);
        Check("manual reference area is a caveat, not a block", manualCmp.comparable &&
              manualCmp.checks.Exists(c => c.label == "Reference area basis" &&
                                           c.level == ComparabilityLevel.Warning));

        // ---- 5. aircraft are scored on lift/drag -----------------------------
        var betterDrone = DragSession("Drone B", AeroVehicleClass.Aircraft, cd: 0.070f, area: 0.35f,
                                      areaMode: AeroReferenceAreaMode.Planform, cl: 0.55f);
        var droneReport = AeroComparison.Compare(aircraft, betterDrone);
        var dronePrimary = droneReport.rows.Find(r => r.primary);
        Check("primary metric is L/D for an aircraft",
              dronePrimary != null && dronePrimary.label.Contains("Lift / drag"), dronePrimary?.label);
        Check("higher L/D wins", droneReport.winner > 0, droneReport.verdict);
        _log.AppendLine($"  drone verdict: {droneReport.verdict} — {droneReport.verdictDetail}");

        // ---- 6. settings that bias a comparison are caveated, not hidden -----
        var coarser = DragSession("Improved SUV (coarse grid)", AeroVehicleClass.RoadVehicle, cd: 0.372f, area: 2.50f);
        coarser.cellSizeM = baseline.cellSizeM * 2f;
        coarser.softVoxels = !baseline.softVoxels;
        var caveated = AeroComparison.Compare(baseline, coarser);
        Check("different grid warns but still compares", caveated.comparable && !caveated.cleanPair);
        Check("grid difference flagged", caveated.checks.Exists(c =>
            c.label == "Grid" && c.level == ComparabilityLevel.Warning));
        Check("soft-voxel difference flagged", caveated.checks.Exists(c =>
            c.label == "Soft voxels" && c.level == ComparabilityLevel.Warning));
        // The caveat has to name what was flagged — "the audit flagged something" trains
        // the reader to skip caveats; "grid, soft voxels were flagged" does not.
        Check("verdict names the flagged checks",
              caveated.verdictDetail.Contains("grid") && caveated.verdictDetail.Contains("soft voxels"),
              caveated.verdictDetail);

        // ---- 7. sweeps line up point by point --------------------------------
        var yawB = YawSession("Yaw SUV B", AeroVehicleClass.RoadVehicle, cdScale: 0.9f);
        var sweepReport = AeroComparison.Compare(yaw, yawB);
        Check("sweep matched point by point", sweepReport.sweep.Count == yaw.tests[0].points.Count,
              $"{sweepReport.sweep.Count} matched rows");
        Check("sweep rows differ in the right direction",
              sweepReport.sweep.Count > 0 && sweepReport.sweep[0].cdB < sweepReport.sweep[0].cdA);
        Check("peak |Cy| is reported for a yaw sweep",
              sweepReport.rows.Exists(r => r.label.Contains("Cy")));
        Check("and scored, because a yaw sweep is where side force means something",
              sweepReport.rows.Find(r => r.label.Contains("Cy"))?.polarity == MetricPolarity.LowerIsBetter);

        // ---- 8. the modal builds and renders what the engine produced ---------
        string improvedStem = AeroReportExporter.ExportAll(improved, dir);
        var view = new AeroComparisonView(dir);
        Check("modal lists both results", CountFileButtons(view) == 2, $"{CountFileButtons(view)} entries");

        bool selected = view.SelectPaths(Path.Combine(dir, stem + AeroReportExporter.JsonExtension),
                                         Path.Combine(dir, improvedStem + AeroReportExporter.JsonExtension));
        Check("modal selects both sides", selected);
        Check("modal produced a report", view.CurrentReport != null && view.CurrentReport.Valid);
        Check("modal report names the same winner", view.CurrentReport != null && view.CurrentReport.winner > 0,
              view.CurrentReport?.verdict);
        int labels = view.Query<Label>().ToList().Count;
        Check("modal rendered the tables", labels > 40, $"{labels} labels rendered");

        // ---- 9. real-world consequences derive only from defensible deltas ----
        // baseline 0.400 vs improved 0.372 is −7.0 % CdA on a ±0.4 % band.
        var impact = AeroRealWorld.Derive(report);
        Check("impact applies to a comparable drag pair", impact.applicable && !impact.withinNoise);
        Check("impact carries the measured CdA delta", Mathf.Abs(impact.cdaDeltaPct + 7f) < 0.1f,
              impact.cdaDeltaPct.ToString("0.00"));
        Check("measured rows come before estimates",
              impact.readings.Count >= 2 && impact.readings[0].measured);
        var highway = impact.readings.Find(x => x.label.StartsWith("Highway fuel"));
        // −7 % × share 0.45–0.55 × bias 0.7–1.0 → −2.2 to −3.9 %, both negative (a saving).
        Check("highway fuel estimate exists and spans the honest range",
              highway != null && highway.value.Contains("-2.2") && highway.value.Contains("-3.9"),
              highway?.value);
        Check("estimates are marked as estimates, not measurements",
              highway != null && !highway.measured);
        Check("assumptions are stated", impact.assumptions.Count >= 3);
        Check("an improvement colour-codes every reading as better",
              impact.readings.TrueForAll(x => x.better > 0));
        var reversed = AeroRealWorld.Derive(AeroComparison.Compare(improved, baseline));
        Check("the reverse comparison colour-codes every reading as worse",
              reversed.applicable && !reversed.withinNoise && reversed.readings.TrueForAll(x => x.better < 0),
              $"{reversed.readings.Count} readings");

        var noisyPair = AeroComparison.Compare(baseline,
            DragSession("Nearly Identical SUV", AeroVehicleClass.RoadVehicle, cd: 0.3995f, area: 2.50f));
        var noisyImpact = AeroRealWorld.Derive(noisyPair);
        Check("a delta inside the noise band produces NO consequences",
              noisyImpact.applicable && noisyImpact.withinNoise && noisyImpact.readings.Count == 0,
              $"{noisyImpact.readings.Count} readings on a {noisyImpact.cdaDeltaPct:0.00}% delta");

        var planes = AeroComparison.Compare(
            DragSession("Plane A", AeroVehicleClass.Aircraft, cd: 0.050f, area: 20f),
            DragSession("Plane B", AeroVehicleClass.Aircraft, cd: 0.045f, area: 20f));
        var planeImpact = AeroRealWorld.Derive(planes);
        Check("aircraft get measured rows only — road fuel arithmetic does not apply",
              !planeImpact.withinNoise && planeImpact.readings.TrueForAll(x => x.measured),
              $"{planeImpact.readings.Count} readings");

        var blockedImpact = AeroRealWorld.Derive(classMismatch);
        Check("a non-comparable pair carries no real-world section", !blockedImpact.applicable);

        // ---- 10. the exported pages carry the impact section and the purpose block ----
        string cmpPath = AeroComparisonExporter.Export(report, Path.Combine(dir, "impact-test.html"));
        string cmpHtml = File.ReadAllText(cmpPath);
        Check("comparison page has the real-world section", cmpHtml.Contains("Real-world impact"));
        Check("comparison page states the assumptions", cmpHtml.Contains("Assumptions, stated so they can be challenged"));
        Check("comparison page ends with the purpose block", cmpHtml.Contains("What this page is"));
        Check("comparison page wears the console theme", cmpHtml.Contains("#0e1116"));
        string sessionHtml = File.ReadAllText(Path.Combine(dir, improvedStem + ".html"));
        Check("session report ends with the purpose block", sessionHtml.Contains("What this page is"));
        Check("session report wears the console theme", sessionHtml.Contains("#0e1116"));

        Directory.Delete(dir, true);
    }

    static int CountFileButtons(AeroComparisonView view)
    {
        int n = 0;
        foreach (var button in view.Query<Button>().ToList())
            if (button.ClassListContains("aero-cmp-file")) n++;
        return n / 2;   // the same list is built for both sides
    }

    // ------------------------------------------------------------------ fixtures

    static AeroTestSession NewSession(string vehicle, AeroVehicleClass cls, AeroReferenceAreaMode areaMode, float area)
    {
        return new AeroTestSession
        {
            vehicleName = vehicle,
            vehicleClass = cls,
            vehicleClassLabel = cls.ToString(),
            packageVersion = WindTunnelVersion.Value,
            startedAtIso = DateTime.Now.ToString("s"),
            finishedAtIso = DateTime.Now.ToString("s"),
            fluidMedium = AeroFluidMedium.Air,
            airDensity = 1.225f,
            airTemperatureC = 15f,
            frontalAreaM2 = area,
            referenceAreaMode = areaMode,
            referenceAreaBasis = AeroVehicleProfile.AreaBasisLabel(areaMode),
            measuredFrontalAreaM2 = area,
            blockageRatio = 0.04f,
            gridInfo = "384×120×160 @ 68 mm",
            tunnelSizeM = new Vector3(26f, 8f, 11f),
            resolutionTier = TunnelResolution.Ultra,
            cellSizeM = 0.068f,
            cellCount = 7_372_800,
            softVoxels = true,
            sealOpenModels = true,
            lesCw = 0.5f,
            convergenceTolerance = 0.01f,
            reynoldsEffective = 3.5e6f
        };
    }

    static AeroSample Sample(float cd, float cl, float cy, float area)
    {
        const float q = 0.5f * 1.225f * 30f * 30f;
        return new AeroSample
        {
            cd = cd,
            cl = cl,
            clFront = cl * 0.45f,
            clRear = cl * 0.55f,
            cy = cy,
            cdA = cd * area,
            frontalAreaM2 = area,
            dragForceN = cd * q * area,
            liftForceN = cl * q * area,
            sideForceN = cy * q * area,
            aeroPowerW = cd * q * area * 30f,
            airSpeedMs = 30f,
            airDensity = 1.225f,
            reynoldsEffective = 3.5e6f,
            liftSplitValid = true,
            solverStep = 20000
        };
    }

    static AeroTestSession DragSession(string vehicle, AeroVehicleClass cls, float cd, float area,
                                       AeroReferenceAreaMode areaMode = AeroReferenceAreaMode.FrontalSilhouette,
                                       float cl = 0.10f)
    {
        var session = NewSession(vehicle, cls, areaMode, area);
        var test = new AeroTestResult
        {
            testName = "Drag test 108 km/h",
            kind = AeroTestKind.ConstantSpeedDrag,
            parameterName = "-",
            speedMs = 30f,
            ground = cls == AeroVehicleClass.Aircraft ? GroundSimulation.OpenFloor : GroundSimulation.FixedFloor,
            rotatingWheels = cls != AeroVehicleClass.Aircraft
        };
        test.points.Add(new AeroTestPointResult
        {
            parameter = 0f,
            sample = Sample(cd, cl, 0f, area),
            converged = true,
            convergenceCv = 0.04f,
            standardError = 0.004f,
            samplesAveraged = 144,
            flowThroughsAveraged = 3f,
            solverSteps = 20000
        });
        session.tests.Add(test);
        return session;
    }

    static AeroTestSession YawSession(string vehicle, AeroVehicleClass cls, float cdScale = 1f)
    {
        const float area = 2.5f;
        var session = NewSession(vehicle, cls, AeroReferenceAreaMode.FrontalSilhouette, area);
        var test = new AeroTestResult
        {
            testName = "Yaw sweep ±15°",
            kind = AeroTestKind.YawSweep,
            parameterName = "Yaw angle (deg)",
            speedMs = 30f,
            ground = GroundSimulation.FixedFloor,
            rotatingWheels = true
        };

        var angles = new List<float> { -15f, -7.5f, 0f, 7.5f, 15f };
        foreach (float psi in angles)
        {
            float cd = (0.40f + 0.0006f * psi * psi) * cdScale;
            test.points.Add(new AeroTestPointResult
            {
                parameter = psi,
                sample = Sample(cd, 0.10f, 0.02f * psi, area),
                converged = true,
                convergenceCv = 0.04f,
                standardError = 0.004f,
                samplesAveraged = 144,
                flowThroughsAveraged = 3f,
                solverSteps = 20000
            });
        }
        session.tests.Add(test);
        return session;
    }

    // ------------------------------------------------------------------ harness

    static void Check(string what, bool condition, string detail = null)
    {
        if (condition) _passed++;
        else _failed++;
        _log.AppendLine($"[{(condition ? "PASS" : "FAIL")}] {what}" +
                        (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
    }
}
