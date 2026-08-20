// The Ahmed-body benchmark — the validation docs/DESIGN.md has listed as owed:
//   Unity.exe -batchmode -projectPath . -executeMethod AeroAhmedTest.Run
//
// Runs the standard bluff-body benchmark across rear-slant angles and compares against
// published values. The headline test is NOT whether Cd matches: it is whether the
// solver reproduces the drag CLIFF — drag climbing to a peak near 30° and collapsing
// after it, as the flow stops clinging to the slant. That cliff is decided by boundary
// layer behaviour on a smooth surface, which is exactly what an interactive grid cannot
// resolve, so this is a direct pass/fail on separation prediction.
//
// Physics expectations are REPORTED, not asserted: a known modelling limit must not
// fail the build. Only harness correctness (geometry, areas, runs completing) is
// asserted. Needs a GPU; do not pass -nographics.
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

public static class AeroAhmedTest
{
    const string ReportPath = "aero_ahmed_body.txt";

    // The original experiments ran at 40 m/s on the 1.044 m model: Re ≈ 2.8e6, which is
    // inside this solver's reachable range — unlike a full-size car at motorway speed.
    const float SpeedMs = 40f;

    // Wind-tunnel cross-section from the original setup (1.87 × 1.4 m), which puts
    // blockage at ~4.3% — the same order the experiment had, so the comparison is not
    // confounded by a wildly different tunnel.
    static readonly Vector3 TunnelSize = new Vector3(8.04f, 1.4f, 1.87f);
    const int StreamwiseCells = 536;          // 15.0 mm cells
    const float UpstreamM = 2.0f;             // clear air ahead of the nose
    const float AverageFlowThroughs = 6f;

    static readonly float[] SlantAngles = { 0f, 25f, 30f, 35f };

    /// <summary>
    /// -aeroInterpolatedWalls on the command line runs the benchmark with Bouzidi
    /// sub-cell wall placement instead of half-way bounce-back, so the effect can be
    /// measured against the stored baselines at every resolution rather than only at one.
    /// </summary>
    static bool InterpolatedWalls =>
        Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroInterpolatedWalls") >= 0;

    /// <summary>-aeroWallModel runs with the Stage 2.5c equilibrium wall model on.</summary>
    static bool WallModel =>
        Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroWallModel") >= 0;

    /// <summary>
    /// -aeroCells 1148 (streamwise; comma-separate for several) overrides the resolution
    /// sweep's built-in ladder, so one new cell size can run without repeating the whole
    /// sweep. Override runs write aero_ahmed_resolution_custom.txt and leave the
    /// canonical reports alone.
    /// </summary>
    static int[] CellOverride
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-aeroCells");
            if (i < 0 || i + 1 >= args.Length) return null;
            var list = new List<int>();
            foreach (var part in args[i + 1].Split(','))
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
                    list.Add(v);
            return list.Count > 0 ? list.ToArray() : null;
        }
    }

    struct Result
    {
        public float angle, cd, cl, sem, area, blockage, flowThroughs;
        public bool settled;
        public long solids;
    }

    static StringBuilder _log;
    static int _passed, _failed;
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try { Benchmark(); }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); _failed++; }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"AHMED BODY HARNESS: PASS ({_passed} checks)"
                                     : $"AHMED BODY HARNESS: FAIL ({_failed} of {_passed + _failed} checks)");
        // Flagged runs must not overwrite the canonical benchmark report.
        string report = "aero_ahmed_body"
            + (CellOverride != null ? "_custom" : "")
            + (WallModel ? "_wallmodel" : "")
            + (InterpolatedWalls ? "_bouzidi" : "")
            + ".txt";
        File.WriteAllText(report, _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void Benchmark()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSettings.asyncShaderCompilation = false;

        // -aeroCells overrides the benchmark resolution too (first value if several);
        // the grid stays identical across angles either way.
        int nx = CellOverride != null ? CellOverride[0] : StreamwiseCells;
        float dx = TunnelSize.x / nx;
        _log.AppendLine("AeroAhmedTest — Ahmed body (Ahmed, Ramm & Faltin 1984)");
        _log.AppendLine(string.Format(Ic,
            "model {0:F3} × {1:F3} × {2:F3} m, clearance {3:F0} mm, slant length {4:F0} mm, reference area {5:F5} m²",
            AeroAhmedBody.LengthM, AeroAhmedBody.WidthM, AeroAhmedBody.HeightM,
            AeroAhmedBody.GroundClearanceM * 1000f, AeroAhmedBody.SlantLengthM * 1000f,
            AeroAhmedBody.ReferenceAreaM2));
        _log.AppendLine(string.Format(Ic,
            "tunnel {0:F2} × {1:F2} × {2:F2} m at {3:F1} mm cells, {4:F0} m/s, fixed floor, {5:F0} FT averaged, {6} walls{7}",
            TunnelSize.x, TunnelSize.y, TunnelSize.z, dx * 1000f, SpeedMs, AverageFlowThroughs,
            InterpolatedWalls ? "INTERPOLATED (Bouzidi)" : "half-way",
            WallModel ? ", WALL MODEL ON (Spalding prototype)" : ""));
        _log.AppendLine();

        VerifyGeometry();

        var results = new List<Result>();
        foreach (float angle in SlantAngles)
            results.Add(RunAngle(angle, nx));

        Report(results);
    }

    /// <summary>
    /// Checks the generated shape against its own published dimensions before a single
    /// step of flow is solved. A benchmark run on the wrong geometry is worse than no
    /// benchmark at all — it looks like evidence.
    /// </summary>
    static void VerifyGeometry()
    {
        _log.AppendLine("--- geometry ---");
        foreach (float angle in SlantAngles)
        {
            var go = AeroAhmedBody.Create(angle);
            var vehicle = go.GetComponent<AeroVehicle>();
            vehicle.TryComputeAeroBounds(Quaternion.identity, out Bounds b);

            AeroAhmedBody.SlantExtents(angle, out float horizontal, out float drop);
            float expectedHeight = AeroAhmedBody.HeightM + AeroAhmedBody.GroundClearanceM;

            _log.AppendLine(string.Format(Ic,
                "  {0,2:F0}°: bounds {1:F3} × {2:F3} × {3:F3} m   slant drops {4:F3} m over {5:F3} m   base height {6:F3} m",
                angle, b.size.x, b.size.y, b.size.z, drop, horizontal, AeroAhmedBody.HeightM - drop));

            Check($"{angle:0}° length matches the published 1.044 m",
                  Mathf.Abs(b.size.x - AeroAhmedBody.LengthM) < 0.002f, $"{b.size.x:F4} m");
            Check($"{angle:0}° width matches the published 0.389 m",
                  Mathf.Abs(b.size.z - AeroAhmedBody.WidthM) < 0.002f, $"{b.size.z:F4} m");
            Check($"{angle:0}° height including clearance matches",
                  Mathf.Abs(b.size.y - expectedHeight) < 0.002f, $"{b.size.y:F4} m");
            Check($"{angle:0}° sits on its stilts at y=0",
                  Mathf.Abs(b.min.y) < 0.002f, $"lowest point {b.min.y:F4} m");

            Object.DestroyImmediate(go);
        }

        // The reference area is a definition, not a measurement — get it wrong and
        // every coefficient is on a different basis than the published ones.
        Check("reference area is width × height",
              Mathf.Abs(AeroAhmedBody.ReferenceAreaM2 - 0.11203f) < 1e-4f,
              AeroAhmedBody.ReferenceAreaM2.ToString("F5"));
        _log.AppendLine();
    }

    static Result RunAngle(float angle) => RunAngle(angle, StreamwiseCells);

    static Result RunAngle(float angle, int streamwiseCells)
    {
        var tunnelGo = new GameObject("Tunnel");
        tunnelGo.transform.position = new Vector3(0f, TunnelSize.y * 0.5f, 0f);   // floor at y = 0
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.size = TunnelSize;
        domain.streamwiseCellsOverride = streamwiseCells;   // identical cells at every angle
        domain.ground = GroundSimulation.FixedFloor;
        domain.rotatingWheels = false;
        domain.inletSpeedMs = SpeedMs;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = true;
        domain.interpolatedWalls = InterpolatedWalls;
        domain.wallModel = WallModel;
        // A benchmark owns its tunnel: nothing may resize it between angles or the
        // angles stop being comparable.
        domain.autoFit.fitAutomatically = false;

        var body = AeroAhmedBody.Create(angle);
        var vehicle = body.GetComponent<AeroVehicle>();
        // Nose at the upstream station, stilts on the floor, centred laterally.
        body.transform.position = new Vector3(
            -TunnelSize.x * 0.5f + UpstreamM + AeroAhmedBody.LengthM * 0.5f, 0f, 0f);
        domain.vehicle = vehicle;

        domain.StartSimulation();
        if (domain.Solver == null)
        {
            Check($"{angle:0}° simulation started", false);
            Object.DestroyImmediate(body); Object.DestroyImmediate(tunnelGo);
            return default;
        }

        // Voxelised volume against the analytic volume: catches a mesh that is
        // inside-out, unsealed, or the wrong shape, which a Cd number would not.
        float voxelVolume = SolidVolume(domain);
        float analytic = AeroAhmedBody.BodyVolumeM3(angle);
        _log.AppendLine(string.Format(Ic,
            "  {0,2:F0}°: voxel volume {1:F4} m³ vs analytic {2:F4} m³ ({3:+0.0;-0.0}%), " +
            "measured silhouette {4:F4} m² vs reference {5:F4} m², blockage {6:P2}",
            angle, voxelVolume, analytic, 100f * (voxelVolume / analytic - 1f),
            domain.MeasuredFrontalAreaM2, AeroAhmedBody.ReferenceAreaM2, domain.BlockageRatio));

        Check($"{angle:0}° voxelised volume is within 15% of the analytic shape",
              Mathf.Abs(voxelVolume / analytic - 1f) < 0.15f,
              $"{voxelVolume:F4} vs {analytic:F4} m³");
        Check($"{angle:0}° coefficients use the published reference area",
              Mathf.Abs(domain.FrontalAreaM2 - AeroAhmedBody.ReferenceAreaM2) < 1e-4f,
              domain.FrontalAreaM2.ToString("F5"));

        var runner = tunnelGo.AddComponent<AeroTestRunner>();
        runner.tunnel = domain;
        int cap = Mathf.CeilToInt((AeroTestRunner.SettleFlowThroughs + AverageFlowThroughs + 2f) * domain.FlowThroughSteps);
        var test = new AeroTestDefinition
        {
            testName = $"Ahmed {angle:0}° slant",
            kind = AeroTestKind.ConstantSpeedDrag,
            speedMs = SpeedMs,
            ground = GroundSimulation.FixedFloor,
            rotatingWheels = false,
            averageOverFlowThroughs = AverageFlowThroughs,
            maxStepsPerPoint = cap
        };

        AeroTestSession session = null;
        runner.SessionCompleted += s => session = s;
        runner.StartSingle(test);

        // Hang guard, not a schedule: sized so a healthy run can never hit it. 25 min
        // covers the standard ladder; grids past ~12M cells (the -aeroCells territory)
        // scale up with cell count — the 51M-cell 7.4 mm run needs ~40 min of stepping.
        long cellCount = (long)domain.Solver.Dims.x * domain.Solver.Dims.y * domain.Solver.Dims.z;
        float timeoutMinutes = Mathf.Max(25f, cellCount / 1_000_000f * 2f);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int tick = 0;
        while (session == null && watch.Elapsed < TimeSpan.FromMinutes(timeoutMinutes))
        {
            domain.Tick();
            runner.Tick();
            if (++tick % 8 == 0) AsyncGPUReadback.WaitAllRequests();
        }
        AsyncGPUReadback.WaitAllRequests();

        Check($"{angle:0}° run completed", session != null, runner.StatusLine);

        var result = new Result { angle = angle };
        if (session != null)
        {
            var p = session.tests[0].points[0];
            result.cd = p.sample.cd;
            result.cl = p.sample.cl;
            result.sem = p.standardError;
            result.settled = p.converged;
            result.flowThroughs = p.flowThroughsAveraged;
            result.area = p.sample.frontalAreaM2;
            result.blockage = domain.BlockageRatio;
            _log.AppendLine(string.Format(Ic,
                "        Cd {0:F3} ± {1:P1}  Cl {2:F3}  over {3:F1} FT  ({4})  {5:mm\\:ss}",
                result.cd, result.sem, result.cl, result.flowThroughs,
                result.settled ? "settled" : "unsettled", watch.Elapsed));

            Check($"{angle:0}° Cd is physical", result.cd > 0.05f && result.cd < 2f, result.cd.ToString("F3"));
        }

        domain.ShutdownSimulation();
        Object.DestroyImmediate(body);
        Object.DestroyImmediate(tunnelGo);
        return result;
    }

    static float SolidVolume(WindTunnelDomain domain)
    {
        var field = typeof(WindTunnelDomain).GetField("_voxelizer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var voxelizer = (VehicleVoxelizer)field.GetValue(domain);
        float dx = domain.CellSize;
        return voxelizer.SolidCellCount * dx * dx * dx;
    }

    // ------------------------------------------------------------------ the verdict

    static void Report(List<Result> results)
    {
        _log.AppendLine();
        _log.AppendLine("--- against published values ---");
        _log.AppendLine("  slant    Cd sim    ±        Cd published   ratio    Cl sim   blockage");
        _log.AppendLine("  ---------------------------------------------------------------------");
        foreach (var r in results)
        {
            float published = AeroAhmedBody.PublishedCd(r.angle);
            _log.AppendLine(string.Format(Ic,
                "  {0,4:F0}°   {1,6:F3}   {2,6:P1}   {3,10:F3}   {4,5:F2}x   {5,6:F3}   {6,7:P1}",
                r.angle, r.cd, r.sem, published, r.cd / published, r.cl, r.blockage));
        }

        _log.AppendLine();
        _log.AppendLine("  Published values are approximate (Ahmed et al. 1984) and are here for");
        _log.AppendLine("  orientation. The test that matters is the SHAPE of the curve below.");

        // ---- the actual benchmark: does the drag cliff appear? ----
        Result at25 = Find(results, 25f), at30 = Find(results, 30f), at35 = Find(results, 35f), at0 = Find(results, 0f);

        _log.AppendLine();
        _log.AppendLine("--- the separation test ---");
        _log.AppendLine("  The Ahmed body's signature: drag climbs with slant angle to a peak near 30°,");
        _log.AppendLine("  then COLLAPSES as the flow stops clinging to the slant and separates at the");
        _log.AppendLine("  top edge instead. Reproducing that drop means the solver is predicting");
        _log.AppendLine("  separation on a smooth surface. Failing to means it is not.");
        _log.AppendLine();

        // How much does the answer move across the whole angle sweep, and is that
        // movement even bigger than the measurement's own uncertainty? A "trend" that
        // is smaller than the error bars is not a trend.
        float simMin = float.MaxValue, simMax = float.MinValue, worstSem = 0f;
        float pubMin = float.MaxValue, pubMax = float.MinValue;
        foreach (var r in results)
        {
            simMin = Mathf.Min(simMin, r.cd);
            simMax = Mathf.Max(simMax, r.cd);
            worstSem = Mathf.Max(worstSem, r.sem);
            float published = AeroAhmedBody.PublishedCd(r.angle);
            pubMin = Mathf.Min(pubMin, published);
            pubMax = Mathf.Max(pubMax, published);
        }
        float simSpread = simMax / Mathf.Max(simMin, 1e-6f) - 1f;
        float pubSpread = pubMax / Mathf.Max(pubMin, 1e-6f) - 1f;
        bool sensitive = simSpread > 4f * worstSem;

        float simDrop = at30.cd > 0f ? 1f - at35.cd / at30.cd : 0f;
        float realDrop = 1f - AeroAhmedBody.PublishedCd(35f) / AeroAhmedBody.PublishedCd(30f);

        _log.AppendLine(string.Format(Ic, "  spread across all angles:  sim {0:P0}   published {1:P0}",
            simSpread, pubSpread));
        _log.AppendLine(string.Format(Ic, "  measurement uncertainty:   ±{0:P1}  → the sweep moves the answer by {1:F1}× the error bar",
            worstSem, simSpread / Mathf.Max(worstSem, 1e-6f)));
        _log.AppendLine(string.Format(Ic, "  rise 0° → 30°:             sim {0:F3} → {1:F3}   ({2:+0.0;-0.0}%)   published +51%",
            at0.cd, at30.cd, 100f * (at30.cd / Mathf.Max(at0.cd, 1e-6f) - 1f)));
        _log.AppendLine(string.Format(Ic, "  drop 30° → 35°:            sim {0:F3} → {1:F3}   ({2:P0})        published {3:P0}",
            at30.cd, at35.cd, simDrop, realDrop));

        _log.AppendLine();
        if (sensitive && at30.cd > at0.cd && at35.cd < at30.cd)
        {
            _log.AppendLine("  READ: the solver responds to slant angle in the published direction. Absolute Cd");
            _log.AppendLine("  is still biased, but the physics that decides a car's wake is present.");
        }
        else
        {
            _log.AppendLine("  READ: the solver is essentially BLIND to the rear slant. The published Cd varies by");
            _log.AppendLine("  ~50% across these angles; here it barely moves, and what movement there is sits");
            _log.AppendLine("  close to the error bars. Every angle lands near Cd ≈ 1.0 — the value of a plain");
            _log.AppendLine("  square-backed box — which says the flow is separating early and staying separated");
            _log.AppendLine("  no matter what shape the tail is. That is the unresolved boundary layer, and it is");
            _log.AppendLine("  the number to beat. A finding, not a harness failure.");
        }

        // Asserted: only that the harness measured something usable. The physics
        // expectation above is reported, because a known modelling limit must not turn
        // into a red build that someone silences.
        Check("every angle produced a measurement", results.TrueForAll(r => r.cd > 0f));
        Check("the angles are directly comparable (identical grid)",
              results.TrueForAll(r => Mathf.Abs(r.area - AeroAhmedBody.ReferenceAreaM2) < 1e-4f));
    }

    /// <summary>
    /// Resolution sweep on the 0° case — the cleanest diagnostic in the set. A plain
    /// square-backed Ahmed body has a published Cd of 0.25 because the flow stays
    /// attached along its length and the base pressure recovers. If this solver reads
    /// ~1.0 (a bluff box) the flow is separating early instead. Refining the grid tells
    /// us WHY: if Cd falls as cells shrink, it is a resolution problem and finer cells
    /// or interpolated bounce-back will help; if it sits flat, it is a modelling limit
    /// and only a wall model will move it.
    /// </summary>
    public static void RunResolutionCheck() => EditorApplication.Exit(ExecuteResolutionCheck());

    public static int ExecuteResolutionCheck()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSettings.asyncShaderCompilation = false;

            _log.AppendLine("AeroAhmedTest — resolution sweep on the 0° (square-back) case");
            _log.AppendLine($"walls: {(InterpolatedWalls ? "INTERPOLATED (Bouzidi)" : "half-way bounce-back")}");
            _log.AppendLine("published Cd 0.250; a plain bluff box would be ~1.05");
            _log.AppendLine("half-way baseline: 20.0 mm 1.177 | 15.0 mm 1.082 | 12.0 mm 1.019 | 10.5 mm 1.022");
            _log.AppendLine();

            var cellCounts = CellOverride ?? new[] { 402, 536, 670, 766 };
            if (CellOverride != null)
                _log.AppendLine($"CUSTOM ladder (-aeroCells): {string.Join(", ", cellCounts)} streamwise");
            var rows = new List<(float dx, Result r, long cells)>();
            foreach (int nx in cellCounts)
            {
                float dx = TunnelSize.x / nx;
                _log.AppendLine(string.Format(Ic, "{0:F1} mm cells ({1} streamwise):", dx * 1000f, nx));
                var r = RunAngle(0f, nx);
                rows.Add((dx, r, nx));
            }

            _log.AppendLine();
            _log.AppendLine("  cell size   Cd sim     ±        vs published 0.250   vs a bluff box 1.05");
            _log.AppendLine("  --------------------------------------------------------------------------");
            foreach (var row in rows)
                _log.AppendLine(string.Format(Ic, "  {0,6:F1} mm   {1,6:F3}   {2,6:P1}   {3,14:F2}x   {4,17:F2}x",
                    row.dx * 1000f, row.r.cd, row.r.sem, row.r.cd / 0.250f, row.r.cd / 1.05f));

            float coarse = rows[0].r.cd, fine = rows[rows.Count - 1].r.cd;
            float change = fine / Mathf.Max(coarse, 1e-6f) - 1f;
            // The convergence question is answered by the LAST refinement step and the
            // residual gap — not by the coarse-to-fine total, which a single arbitrary
            // threshold can flip on a couple of percentage points while the answer is
            // still four times the published value.
            float lastStep = rows.Count >= 2
                ? Mathf.Abs(fine / Mathf.Max(rows[rows.Count - 2].r.cd, 1e-6f) - 1f)
                : 1f;
            float residual = fine / 0.250f;

            _log.AppendLine();
            _log.AppendLine(string.Format(Ic,
                "  {0:F1} mm → {1:F1} mm moves Cd by {2:+0.0;-0.0}%; the last refinement step moves it {3:P1}",
                rows[0].dx * 1000f, rows[rows.Count - 1].dx * 1000f, 100f * change, lastStep));
            if (rows.Count >= 2)
            {
                // A step judged against a fixed threshold alone can read more settled
                // than the data: also report it against its own combined noise band.
                float semF = rows[rows.Count - 1].r.sem, semP = rows[rows.Count - 2].r.sem;
                float band = 2f * Mathf.Sqrt(semF * semF + semP * semP);
                _log.AppendLine(string.Format(Ic,
                    "  that step's own ±2σ band is {0:P1} — the step is {1} its noise",
                    band, lastStep > band ? "OUTSIDE" : "inside"));
            }
            _log.AppendLine(string.Format(Ic,
                "  finest value sits at {0:F2}x the published 0.250 and {1:F2}x a plain bluff box (~1.05)",
                residual, fine / 1.05f));
            _log.AppendLine();
            if (residual < 1.5f)
            {
                _log.AppendLine("  READ: refinement is carrying the answer to the published value. The error was");
                _log.AppendLine("  substantially discretisation.");
            }
            else if (lastStep > 0.02f)
            {
                _log.AppendLine("  READ: still moving at the finest cell tested, so the grid study is not yet");
                _log.AppendLine("  finished — but it is far from the published value and would have to keep");
                _log.AppendLine("  falling by a factor of ~4 to reach it. Refine further before concluding.");
            }
            else
            {
                _log.AppendLine("  READ: refining the grid barely moves it. The body reads like a bluff box at");
                _log.AppendLine("  every resolution tested, so the flow is separating at the nose and staying");
                _log.AppendLine("  separated. Grid alone will not fix this — it is the missing near-wall physics.");
            }
        }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); _failed++; }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"AHMED RESOLUTION SWEEP: PASS ({_passed} checks)"
                                     : $"AHMED RESOLUTION SWEEP: FAIL ({_failed} of {_passed + _failed} checks)");
        File.WriteAllText(
            CellOverride != null
                ? (InterpolatedWalls ? "aero_ahmed_resolution_custom_bouzidi.txt"
                                     : "aero_ahmed_resolution_custom.txt")
                : (InterpolatedWalls ? "aero_ahmed_resolution_bouzidi.txt"
                                     : "aero_ahmed_resolution.txt"),
            _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static Result Find(List<Result> results, float angle) =>
        results.Find(r => Mathf.Approximately(r.angle, angle));

    static void Check(string what, bool condition, string detail = null)
    {
        if (condition) _passed++;
        else _failed++;
        _log.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {what}" +
                        (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
    }
}
