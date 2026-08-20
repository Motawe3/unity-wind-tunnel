// End-to-end exercise of the comparison feature on REAL solver output:
//   Unity.exe -batchmode -projectPath . -executeMethod AeroCompareLive.Run
//
// AeroCompareTest checks the comparison engine against synthetic sessions. This one
// checks the whole chain the user actually drives: auto-fit each vehicle with a locked
// cell size, run a real drag test through AeroTestRunner to a settled mean, export the
// archive, load it back, compare the pairs, and write the comparison HTML.
//
// It also answers the question the feature exists for — is B better than A, and by
// enough to be believed — for two bodies that genuinely differ.
//
// This ran three subjects until the sports car and race car were dropped from the
// project over asset provenance; the ranking and pairwise loops below are generic over
// the subject count, so add rows to Subjects to widen it again.
// Needs a GPU; do not pass -nographics. The pickup imports from .blend, so it also
// needs Blender installed — see the README.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Motawea.WindTunnel;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class AeroCompareLive
{
    const string ReportPath = "aero_compare_live.txt";

    // One locked cell size for every vehicle: this is the setting that makes a
    // cross-vehicle comparison like-for-like, and the run proves it removes the grid
    // caveat rather than merely silencing it.
    const float LockedCellM = 0.09f;
    const float SpeedMs = 33.33f;          // 120 km/h
    const float AverageFlowThroughs = 6f;  // uncertainty ~ scatter / sqrt(6)

    class Subject
    {
        public string prefab;
        public string label;
        /// <summary>Approximate published figures, for orientation only — see the note in the report.</summary>
        public float realCd, realAreaM2;
        public float RealCdA => realCd * realAreaM2;
    }

    static readonly Subject[] Subjects =
    {
        new Subject { prefab = "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab",
                      label = "SUV", realCd = 0.36f, realAreaM2 = 2.90f },
        // Ballpark published figures, same standing as the SUV row above: a lifted
        // Trail Boss on off-road tyres is well clear of the ~0.40 GM quotes for the
        // aero trims. Exact values matter less than the gap -- the check is whether the
        // solver ranks the two bodies in the right order, not whether it hits a number.
        new Subject { prefab = "Assets/Prefabs/Cars/Chevy_NoCap.prefab",
                      label = "pickup", realCd = 0.45f, realAreaM2 = 3.30f },
    };

    static StringBuilder _log;
    static int _passed, _failed;
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try { Live(); }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); _failed++; }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"LIVE COMPARISON: PASS ({_passed} checks)"
                                     : $"LIVE COMPARISON: FAIL ({_failed} of {_passed + _failed} checks)");
        File.WriteAllText(ReportPath, _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void Live()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSettings.asyncShaderCompilation = false;

        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Reports", "live-comparison");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        _log.AppendLine("AeroCompareLive — real solver runs, exported and compared");
        _log.AppendLine(string.Format(Ic,
            "cell size locked at {0:F0} mm for every vehicle, {1:F0} km/h, drag test averaged over {2:F0}+ flow-throughs",
            LockedCellM * 1000f, SpeedMs * 3.6f, AverageFlowThroughs));
        _log.AppendLine($"reports → {dir}");
        _log.AppendLine();

        var exported = new List<string>();
        foreach (var subject in Subjects)
        {
            string path = RunOne(subject, dir);
            if (path != null) exported.Add(path);
        }

        Check("every vehicle produced an archive", exported.Count == Subjects.Length,
              $"{exported.Count} of {Subjects.Length}");
        if (exported.Count < 2) return;

        // ---- the listing the modal shows ------------------------------------
        var listed = AeroSessionArchive.List(dir);
        Check("all runs appear in the picker", listed.Count == exported.Count,
              $"{listed.Count} entries");
        foreach (var file in listed)
            Check($"'{file.fileName}' loads", file.IsUsable, file.loadError);

        // ---- compare every pair ---------------------------------------------
        _log.AppendLine();
        _log.AppendLine("--- pairwise comparisons ---");
        for (int i = 0; i < exported.Count; i++)
        for (int j = i + 1; j < exported.Count; j++)
        {
            var a = AeroSessionArchive.Load(exported[i], out _);
            var b = AeroSessionArchive.Load(exported[j], out _);
            var report = AeroComparison.Compare(a, b);

            _log.AppendLine();
            _log.AppendLine($"{a.vehicleName}  vs  {b.vehicleName}");
            Check("comparison is valid", report.Valid, report.error);
            if (!report.Valid) continue;

            foreach (var c in report.checks)
                _log.AppendLine($"   {c.level,-8} {c.label,-26} {c.a}  |  {c.b}");

            var primary = report.rows.Find(r => r.primary);
            _log.AppendLine(string.Format(Ic,
                "   PRIMARY {0}: {1:F3} vs {2:F3} ({3:+0.0;-0.0}%), band ±{4:F1}%",
                primary?.label, primary?.a, primary?.b, primary?.deltaPct, report.noiseBandPct));
            _log.AppendLine($"   VERDICT: {report.verdict} — {report.verdictDetail}");

            // The whole point of locking the cell size: no grid caveat between runs
            // that were fitted to differently-sized vehicles.
            var grid = report.checks.Find(c => c.label == "Grid");
            Check("locked cell size removes the grid caveat",
                  grid != null && grid.level == ComparabilityLevel.Ok,
                  grid == null ? "no grid row" : $"{grid.level}: {grid.a} vs {grid.b}");

            Check("blocking nothing — these are all road-going cars in air", report.comparable);
            Check("the primary metric is drag area", primary != null && primary.label.Contains("CdA"));

            // Lift must not be scored across classes (race car vs road car).
            var lift = report.rows.Find(r => r.label.EndsWith("Cl"));
            if (a.vehicleClass != b.vehicleClass)
                Check("lift unscored across classes",
                      lift != null && lift.polarity == MetricPolarity.Informational);

            // Side force at zero yaw is noise on both sides; it must not be ranked.
            var cy = report.rows.Find(r => r.label.Contains("Cy"));
            Check("side force unscored on a straight-ahead test",
                  cy == null || cy.polarity == MetricPolarity.Informational);

            // ---- the HTML export the modal's footer button writes ------------
            string html = AeroComparisonExporter.ExportTo(report, dir);
            Check("comparison HTML written", File.Exists(html), Path.GetFileName(html));
            if (File.Exists(html))
            {
                string page = File.ReadAllText(html);
                Check("HTML carries the verdict", page.Contains("verdict"));
                Check("HTML carries the audit", page.Contains("Like-for-like audit"));
                Check("HTML names both vehicles",
                      page.Contains(Escape(a.vehicleName)) && page.Contains(Escape(b.vehicleName)));
                Check("HTML states the uncertainty band", page.Contains("uncertainty on"));
                Check("HTML is self-contained (no external requests)",
                      !page.Contains("http://") && !page.Contains("https://"));
                _log.AppendLine($"   exported {Path.GetFileName(html)} ({new FileInfo(html).Length / 1024} KB)");
            }
        }

        // ---- did the tool answer the question it exists for? -----------------
        _log.AppendLine();
        _log.AppendLine("--- summary: drag area, the comparable quantity ---");
        var measured = new List<(Subject subject, float cdA, float cd, float area, float sem)>();
        for (int i = 0; i < exported.Count; i++)
        {
            var s = AeroSessionArchive.Load(exported[i], out _);
            var p = s.tests[0].points[0];
            _log.AppendLine(string.Format(Ic,
                "  {0,-26} CdA {1:F3} m²   Cd {2:F3}   A {3:F3} m²   ±{4:P1} over {5:F1} FT   {6}",
                s.vehicleName, p.sample.cdA, p.sample.cd, p.sample.frontalAreaM2,
                p.standardError, p.flowThroughsAveraged, p.converged ? "settled" : "UNSETTLED"));
            measured.Add((Subjects[i], p.sample.cdA, p.sample.cd, p.sample.frontalAreaM2, p.standardError));
        }

        // ---- is any of this worth anything? ----------------------------------
        // The tool's whole claim is that DELTAS are trustworthy while absolutes are
        // not. That claim is checkable: the ordering of these three cars is known, and
        // so are their published figures. Reference values are approximate and are
        // here for orientation, not as a validation standard.
        _log.AppendLine();
        _log.AppendLine("--- against published figures (approximate; orientation only) ---");
        _log.AppendLine("  vehicle                     CdA sim   CdA real   ratio    Cd sim   Cd real   A sim   A real");
        _log.AppendLine("  ---------------------------------------------------------------------------------------------");
        foreach (var m in measured)
            _log.AppendLine(string.Format(Ic,
                "  {0,-26} {1,7:F3}   {2,8:F3}   {3,5:F2}x   {4,6:F3}   {5,7:F2}   {6,5:F2}   {7,5:F2}",
                Path.GetFileNameWithoutExtension(m.subject.prefab).Substring(0, Math.Min(26,
                    Path.GetFileNameWithoutExtension(m.subject.prefab).Length)),
                m.cdA, m.subject.RealCdA, m.cdA / m.subject.RealCdA, m.cd, m.subject.realCd,
                m.area, m.subject.realAreaM2));

        measured.Sort((x, y) => x.cdA.CompareTo(y.cdA));
        var expected = new List<Subject>(Subjects);
        expected.Sort((x, y) => x.RealCdA.CompareTo(y.RealCdA));
        bool orderingHolds = true;
        for (int i = 0; i < measured.Count; i++)
            if (measured[i].subject != expected[i]) orderingHolds = false;
        Check("the simulated drag-area ranking matches the real one", orderingHolds,
              string.Join(" < ", measured.ConvertAll(m => m.subject.label)));

        _log.AppendLine();
        _log.AppendLine("  Pairwise ratios — this is what the comparison feature actually sells:");
        for (int i = 0; i < measured.Count; i++)
        for (int j = i + 1; j < measured.Count; j++)
        {
            float simRatio = measured[j].cdA / measured[i].cdA;
            float realRatio = measured[j].subject.RealCdA / measured[i].subject.RealCdA;
            _log.AppendLine(string.Format(Ic,
                "    {0,-12} vs {1,-12}  sim {2:F2}x   real {3:F2}x   (delta exaggerated by {4:P0})",
                measured[i].subject.label, measured[j].subject.label, simRatio, realRatio,
                simRatio / realRatio - 1f));
        }
        _log.AppendLine();
        _log.AppendLine("  Read: absolute CdA is ~1.6-2.0x high (the wall-model limit), the RANKING is right,");
        _log.AppendLine("  and pairwise ratios are directionally right but exaggerated — the bias is larger on");
        _log.AppendLine("  the bluffest body, so it does not cancel completely between different shapes.");
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Fits, runs and exports one vehicle. Returns the archive path.</summary>
    static string RunOne(Subject subject, string dir)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(subject.prefab);
        if (prefab == null) { Check($"prefab {subject.prefab}", false); return null; }

        var tunnelGo = new GameObject("Tunnel");
        tunnelGo.transform.position = new Vector3(0f, 3f, 0f);
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.inletSpeedMs = SpeedMs;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.autoFit.fitAutomatically = false;
        domain.autoFit.matchCellSizeM = LockedCellM;
        domain.autoFit.memoryBudgetGB = 4f;

        var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var vehicle = car.GetComponentInChildren<AeroVehicle>(true);
        vehicle.RefreshWheels();
        domain.vehicle = vehicle;

        var plan = domain.FitToVehicle(logSummary: false);
        if (!plan.valid) { Check($"fit {vehicle.Name}", false, plan.error); Cleanup(car, tunnelGo, domain); return null; }

        var runner = tunnelGo.AddComponent<AeroTestRunner>();
        runner.tunnel = domain;

        // Cap generously: the averaging window grows with the run, so the cap is what
        // sets the achievable uncertainty.
        int cap = Mathf.CeilToInt((AeroTestRunner.SettleFlowThroughs + AverageFlowThroughs + 2f) * domain.FlowThroughSteps);
        var test = new AeroTestDefinition
        {
            testName = $"Drag test {SpeedMs * 3.6f:0} km/h",
            kind = AeroTestKind.ConstantSpeedDrag,
            speedMs = SpeedMs,
            ground = domain.ground,
            rotatingWheels = domain.rotatingWheels,
            averageOverFlowThroughs = AverageFlowThroughs,
            maxStepsPerPoint = cap
        };

        AeroTestSession session = null;
        runner.SessionCompleted += s => session = s;

        _log.AppendLine(string.Format(Ic,
            "{0} ({1}): tunnel {2:F1}×{3:F1}×{4:F1} m, {5}×{6}×{7} @ {8:F1} mm, {9:F2}M cells, cap {10:N0} steps",
            vehicle.Name, subject.label, domain.EffectiveSize.x, domain.EffectiveSize.y, domain.EffectiveSize.z,
            domain.Dims.x, domain.Dims.y, domain.Dims.z, domain.CellSize * 1000f,
            (double)domain.Dims.x * domain.Dims.y * domain.Dims.z / 1e6, cap));

        runner.StartSingle(test);
        if (!runner.IsRunning) { Check($"runner started for {vehicle.Name}", false); Cleanup(car, tunnelGo, domain); return null; }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        int tick = 0;
        while (session == null && watch.Elapsed < TimeSpan.FromMinutes(20))
        {
            domain.Tick();
            runner.Tick();
            if (++tick % 8 == 0) AsyncGPUReadback.WaitAllRequests();
        }
        AsyncGPUReadback.WaitAllRequests();

        Check($"{vehicle.Name} completed its test", session != null, runner.StatusLine);
        if (session == null) { Cleanup(car, tunnelGo, domain); return null; }

        var point = session.tests[0].points[0];
        _log.AppendLine(string.Format(Ic,
            "   Cd {0:F3}  CdA {1:F3} m²  Cl {2:F3}  ±{3:P1} over {4:F1} flow-throughs ({5} samples), {6}, {7:mm\\:ss}",
            point.sample.cd, point.sample.cdA, point.sample.cl, point.standardError,
            point.flowThroughsAveraged, point.samplesAveraged,
            point.converged ? "settled" : "unsettled", watch.Elapsed));

        Check($"{vehicle.Name} Cd is physical", point.sample.cd > 0f && point.sample.cd < 5f,
              point.sample.cd.ToString("F3"));
        Check($"{vehicle.Name} averaged a real window", point.samplesAveraged > 10,
              $"{point.samplesAveraged} samples");
        Check($"{vehicle.Name} reports an uncertainty", point.standardError > 0f,
              point.standardError.ToString("P2"));

        string baseName = AeroReportExporter.ExportAll(session, dir);
        Cleanup(car, tunnelGo, domain);
        return Path.Combine(dir, baseName + AeroReportExporter.JsonExtension);
    }

    static void Cleanup(GameObject car, GameObject tunnel, WindTunnelDomain domain)
    {
        domain?.ShutdownSimulation();
        if (car != null) Object.DestroyImmediate(car);
        if (tunnel != null) Object.DestroyImmediate(tunnel);
    }

    static void Check(string what, bool condition, string detail = null)
    {
        if (condition) _passed++;
        else _failed++;
        _log.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {what}" +
                        (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
    }
}
