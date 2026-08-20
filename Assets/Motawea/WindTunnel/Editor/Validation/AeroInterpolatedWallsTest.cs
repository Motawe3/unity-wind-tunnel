// Stage 1 A/B: interpolated (Bouzidi) bounce-back vs half-way bounce-back.
//   Unity.exe -batchmode -projectPath . -executeMethod AeroInterpolatedWallsTest.Run
//
// Runs each case twice — identical geometry, tunnel, grid and averaging, with only
// `interpolatedWalls` flipped — so the difference IS the feature. Absolute values here
// are not meant to match docs/IMPROVEMENT-PLAN.md §4 exactly (shorter averaging, and the
// reference bodies use their own tunnel); the A/B delta is the measurement.
//
// What each case is for:
//   plate, cube  — MUST NOT move. Separation is pinned at a sharp edge, so sub-cell wall
//                  placement has nothing to correct. Movement here means the force
//                  integration (wall position in the momentum exchange) is wrong.
//   sphere       — the primary target. Its published excess is largely voxel staircasing
//                  on a curved surface, which is exactly what this removes.
//   Ahmed 0°     — the benchmark case, in both soft-voxel states. With soft voxels ON the
//                  skin is already gray-blended so little is expected; with them OFF the
//                  wall is a hard staircase and Bouzidi is the whole treatment.
// Needs a GPU; do not pass -nographics.
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

public static class AeroInterpolatedWallsTest
{
    const string ReportPath = "aero_interpolated_walls.txt";

    // Reference bodies: the free-air tunnel AeroValidate uses, so the numbers are
    // comparable to its published table.
    static readonly Vector3 RefTunnel = new Vector3(12f, 5f, 5f);
    const TunnelResolution RefResolution = TunnelResolution.Fine;   // 46.9 mm
    const float RefSpeed = 30f;

    // Ahmed body: the benchmark's own tunnel and grid (docs/IMPROVEMENT-PLAN.md §4.1).
    static readonly Vector3 AhmedTunnel = new Vector3(8.04f, 1.4f, 1.87f);
    const int AhmedCells = 536;                                     // 15.0 mm
    const float AhmedSpeed = 40f;

    const float AverageFlowThroughs = 5f;

    class Case
    {
        public string label;
        public string reference;          // published value, for context
        public float published;
        public bool softVoxels;
        public bool mustNotMove;          // sharp-edged: a change here is a bug, not a result
        public Func<GameObject> build;    // returns a root carrying an AeroVehicle
        public bool ahmed;                // uses the Ahmed tunnel instead of the reference one
    }

    struct Measurement
    {
        public float cd, sem;
        public bool ok;
    }

    static StringBuilder _log;
    static int _passed, _failed;
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try { AB(); }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); _failed++; }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"INTERPOLATED WALLS: PASS ({_passed} checks)"
                                     : $"INTERPOLATED WALLS: FAIL ({_failed} of {_passed + _failed} checks)");
        File.WriteAllText(ReportPath, _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void AB()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSettings.asyncShaderCompilation = false;

        _log.AppendLine("Stage 1 A/B — interpolated (Bouzidi) bounce-back");
        _log.AppendLine(string.Format(Ic,
            "reference bodies: {0} @ {1} ({2:F1} mm), {3:F0} m/s, free air | Ahmed: {4} @ {5} ({6:F1} mm), {7:F0} m/s, fixed floor",
            RefTunnel, (int)RefResolution, RefTunnel.x / (int)RefResolution * 1000f, RefSpeed,
            AhmedTunnel, AhmedCells, AhmedTunnel.x / AhmedCells * 1000f, AhmedSpeed));
        _log.AppendLine($"averaging {AverageFlowThroughs:F0} flow-throughs per run; each case run twice, toggle flipped");
        _log.AppendLine();

        var cases = new List<Case>
        {
            new Case { label = "plate (soft off)", published = 1.17f, reference = "1.17", softVoxels = false,
                       mustNotMove = true, build = () => Primitive(PrimitiveType.Cube, new Vector3(0.02f, 1f, 1f)) },
            new Case { label = "cube (soft off)", published = 1.05f, reference = "1.05", softVoxels = false,
                       mustNotMove = true, build = () => Primitive(PrimitiveType.Cube, Vector3.one) },
            new Case { label = "sphere (soft off)", published = 0.45f, reference = "~0.45", softVoxels = false,
                       build = () => Primitive(PrimitiveType.Sphere, Vector3.one) },
            new Case { label = "sphere (soft on)", published = 0.45f, reference = "~0.45", softVoxels = true,
                       build = () => Primitive(PrimitiveType.Sphere, Vector3.one) },
            new Case { label = "Ahmed 0° (soft on)", published = 0.250f, reference = "0.250", softVoxels = true,
                       ahmed = true, build = () => AeroAhmedBody.Create(0f) },
            new Case { label = "Ahmed 0° (soft off)", published = 0.250f, reference = "0.250", softVoxels = false,
                       ahmed = true, build = () => AeroAhmedBody.Create(0f) },
        };

        var results = new List<(Case c, Measurement off, Measurement on)>();
        foreach (var c in cases)
        {
            _log.AppendLine($"--- {c.label} ---");
            Measurement off = Measure(c, false);
            Measurement on = Measure(c, true);
            results.Add((c, off, on));
            _log.AppendLine();
        }

        // ---- table ----
        _log.AppendLine("case                     half-way        Bouzidi         change    published   ratio off  ratio on");
        _log.AppendLine("------------------------------------------------------------------------------------------------------");
        foreach (var (c, off, on) in results)
        {
            if (!off.ok || !on.ok) { _log.AppendLine($"{c.label,-24} (incomplete)"); continue; }
            float change = on.cd / Mathf.Max(off.cd, 1e-6f) - 1f;
            _log.AppendLine(string.Format(Ic,
                "{0,-24} {1,6:F3} ±{2,4:P0}   {3,6:F3} ±{4,4:P0}   {5,8:P1}   {6,8}   {7,7:F2}x  {8,7:F2}x",
                c.label, off.cd, off.sem, on.cd, on.sem, change, c.reference,
                off.cd / c.published, on.cd / c.published));
        }

        // ---- verdict ----
        _log.AppendLine();
        _log.AppendLine("--- verdict ---");
        foreach (var (c, off, on) in results)
        {
            if (!off.ok || !on.ok) continue;
            float change = on.cd / Mathf.Max(off.cd, 1e-6f) - 1f;
            float band = Mathf.Max(off.sem, on.sem) * 2f;   // both runs carry an error

            if (c.mustNotMove)
            {
                // A sharp edge fixes separation geometrically. Sub-cell wall placement
                // still shifts the wall slightly, so allow the combined uncertainty plus
                // a little; a LARGE move means the momentum-exchange wall position is wrong.
                Check($"{c.label} did not move materially", Mathf.Abs(change) < Mathf.Max(band, 0.04f),
                      $"{change:P1} (band ±{band:P1})");
            }
            else
            {
                bool moved = Mathf.Abs(change) > band;
                bool better = Mathf.Abs(on.cd - c.published) < Mathf.Abs(off.cd - c.published);
                _log.AppendLine(string.Format(Ic,
                    "  {0,-24} {1} by {2:P1} (band ±{3:P1}) — {4}",
                    c.label, change < 0f ? "fell" : "rose", Mathf.Abs(change), band,
                    !moved ? "inside the noise, no effect"
                           : better ? "TOWARD the published value" : "AWAY from the published value"));
            }
        }
        _log.AppendLine();
        _log.AppendLine("  Read: the sharp-edged cases are the correctness check (they must not move);");
        _log.AppendLine("  the sphere and Ahmed cases are the hypothesis under test.");
    }

    static Measurement Measure(Case c, bool interpolated)
    {
        var tunnelGo = new GameObject("Tunnel");
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.autoFit.fitAutomatically = false;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = c.softVoxels;
        domain.interpolatedWalls = interpolated;

        GameObject body = c.build();

        if (c.ahmed)
        {
            tunnelGo.transform.position = new Vector3(0f, AhmedTunnel.y * 0.5f, 0f);
            domain.size = AhmedTunnel;
            domain.streamwiseCellsOverride = AhmedCells;
            domain.ground = GroundSimulation.FixedFloor;
            domain.rotatingWheels = false;
            domain.inletSpeedMs = AhmedSpeed;
            body.transform.position = new Vector3(
                -AhmedTunnel.x * 0.5f + 2f + AeroAhmedBody.LengthM * 0.5f, 0f, 0f);
        }
        else
        {
            tunnelGo.transform.position = new Vector3(0f, 2.5f, 0f);
            domain.size = RefTunnel;
            domain.resolution = RefResolution;
            domain.ground = GroundSimulation.OpenFloor;
            domain.rotatingWheels = false;
            domain.inletSpeedMs = RefSpeed;
            body.transform.position = tunnelGo.transform.position + new Vector3(-RefTunnel.x * 0.15f, 0f, 0f);
        }

        var vehicle = body.GetComponent<AeroVehicle>();
        domain.vehicle = vehicle;
        domain.StartSimulation();

        var result = new Measurement();
        if (domain.Solver == null)
        {
            Check($"{c.label} ({(interpolated ? "Bouzidi" : "half-way")}) started", false);
            Object.DestroyImmediate(body); Object.DestroyImmediate(tunnelGo);
            return result;
        }

        var runner = tunnelGo.AddComponent<AeroTestRunner>();
        runner.tunnel = domain;
        int cap = Mathf.CeilToInt((AeroTestRunner.SettleFlowThroughs + AverageFlowThroughs + 2f) * domain.FlowThroughSteps);
        AeroTestSession session = null;
        runner.SessionCompleted += s => session = s;
        runner.StartSingle(new AeroTestDefinition
        {
            testName = c.label,
            kind = AeroTestKind.ConstantSpeedDrag,
            speedMs = domain.inletSpeedMs,
            ground = domain.ground,
            rotatingWheels = false,
            averageOverFlowThroughs = AverageFlowThroughs,
            maxStepsPerPoint = cap
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        int tick = 0;
        while (session == null && watch.Elapsed < TimeSpan.FromMinutes(20))
        {
            domain.Tick();
            runner.Tick();
            if (++tick % 8 == 0) AsyncGPUReadback.WaitAllRequests();
        }
        AsyncGPUReadback.WaitAllRequests();

        if (session != null)
        {
            var p = session.tests[0].points[0];
            result.cd = p.sample.cd;
            result.sem = Mathf.Max(p.standardError, 0f);
            result.ok = true;
            _log.AppendLine(string.Format(Ic, "  {0,-9}: Cd {1:F3} ± {2:P1}   A {3:F4} m²   {4:mm\\:ss}",
                interpolated ? "Bouzidi" : "half-way", result.cd, result.sem,
                p.sample.frontalAreaM2, watch.Elapsed));
        }
        Check($"{c.label} ({(interpolated ? "Bouzidi" : "half-way")}) completed", session != null, runner.StatusLine);

        domain.ShutdownSimulation();
        Object.DestroyImmediate(body);
        Object.DestroyImmediate(tunnelGo);
        return result;
    }

    /// <summary>A primitive of the given size carrying an AeroVehicle, 1 m reference area.</summary>
    static GameObject Primitive(PrimitiveType type, Vector3 scale)
    {
        var root = new GameObject($"{type} rig");
        var vehicle = root.AddComponent<AeroVehicle>();
        vehicle.vehicleClass = AeroVehicleClass.ReferenceBody;

        var mesh = GameObject.CreatePrimitive(type);
        mesh.transform.SetParent(root.transform, false);
        mesh.transform.localScale = scale;
        var collider = mesh.GetComponent<Collider>();
        if (collider != null) Object.DestroyImmediate(collider);
        return root;
    }

    static void Check(string what, bool condition, string detail = null)
    {
        if (condition) _passed++;
        else _failed++;
        _log.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {what}" +
                        (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
    }
}
