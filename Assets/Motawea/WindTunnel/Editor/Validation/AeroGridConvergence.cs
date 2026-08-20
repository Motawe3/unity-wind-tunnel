// Grid-convergence study (the validation DESIGN.md lists as still owed):
//   Unity.exe -batchmode -projectPath . -executeMethod AeroGridConvergence.Run
// Fits the tunnel to the vehicle ONCE, then re-solves the identical domain at every
// resolution tier. Only the cell size changes, so the trend is a grid effect and not a
// domain effect — which is what made this study hard to do before the auto-fit existed.
//
// Reports CdA as well as Cd: CdA = F/q is independent of the measured reference area,
// so it separates "the force changed" from "the voxel silhouette changed".
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

public static class AeroGridConvergence
{
    const string ReportPath = "aero_grid_convergence.txt";
    const string PrefabPath = "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab";

    // Published figures for the vehicle under test.
    const float RealCd = 0.36f;
    const float RealAreaM2 = 2.9f;

    const int FlowThroughs = 10;
    const int AverageOverLast = 3;

    static readonly TunnelResolution[] Tiers =
    {
        TunnelResolution.Coarse, TunnelResolution.Medium, TunnelResolution.Fine,
        TunnelResolution.Ultra, TunnelResolution.Extreme
    };

    static StringBuilder _log;
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        int exit = 0;
        try { Study(); }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); exit = 1; }
        File.WriteAllText(ReportPath, _log.ToString());
        return exit;
    }

    struct Point
    {
        public TunnelResolution tier;
        public Vector3Int dims;
        public long cells;
        public float cellMm, areaM2, blockage, cd, cdStd, cdA, cl, cv, cellsOnBody;
    }

    static void Study()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSettings.asyncShaderCompilation = false;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { _log.AppendLine("prefab not found"); return; }

        _log.AppendLine("AeroGridConvergence — Range Rover Sport SVR, auto-fitted domain held fixed,");
        _log.AppendLine($"one tier per row, {FlowThroughs} flow-throughs each, averaged over the last {AverageOverLast}.");
        _log.AppendLine($"Published: Cd ~{RealCd:F2}, A ~{RealAreaM2:F1} m2, CdA ~{RealCd * RealAreaM2:F2} m2.");
        _log.AppendLine();

        var results = new List<Point>();
        foreach (var tier in Tiers)
            results.Add(Measure(prefab, tier));

        _log.AppendLine();
        _log.AppendLine("tier      grid            cell   cells/body  A_meas  blockage    Cd     std      CdA    Cd/real  CdA/real   Cl     CV");
        _log.AppendLine("--------------------------------------------------------------------------------------------------------------------------");
        foreach (var p in results)
            _log.AppendLine(string.Format(Ic,
                "{0,-9} {1,-15} {2,4:F0}mm {3,8:F0}   {4,6:F3}  {5,7:P1}  {6,6:F3}  {7,5:F3}  {8,6:F3}  {9,6:F2}x  {10,6:F2}x  {11,6:F3}  {12,5:P1}",
                p.tier, $"{p.dims.x}x{p.dims.y}x{p.dims.z}", p.cellMm, p.cellsOnBody, p.areaM2, p.blockage,
                p.cd, p.cdStd, p.cdA, p.cd / RealCd, p.cdA / (RealCd * RealAreaM2), p.cl, p.cv));

        _log.AppendLine();
        _log.AppendLine("Reading the table:");
        _log.AppendLine("  A_meas shrinks toward the real frontal area as cells shrink — voxel dilation.");
        _log.AppendLine("  CdA is the raw force over q, so it is free of that area artifact; judge convergence on it.");

        // Is the force trend monotonic, and is it heading toward or away from reality?
        bool monotoneDown = true, monotoneUp = true;
        for (int i = 1; i < results.Count; i++)
        {
            if (results[i].cdA > results[i - 1].cdA) monotoneDown = false;
            if (results[i].cdA < results[i - 1].cdA) monotoneUp = false;
        }
        float realCdA = RealCd * RealAreaM2;
        _log.AppendLine();
        _log.AppendLine(monotoneDown ? "  CdA falls monotonically with cell size."
                      : monotoneUp ? "  CdA rises monotonically with cell size."
                      : "  CdA is not monotonic in cell size.");
        _log.AppendLine(string.Format(Ic,
            "  Coarsest {0:F3} -> finest {1:F3} m2, against a real {2:F2} m2 " +
            "({3} the published value as the grid refines).",
            results[0].cdA, results[results.Count - 1].cdA, realCdA,
            Mathf.Abs(results[results.Count - 1].cdA - realCdA) < Mathf.Abs(results[0].cdA - realCdA)
                ? "converging toward" : "diverging from"));

        // Grid convergence in the Richardson sense needs the last three points on a
        // constant refinement ratio; ours is 256/384/512, close enough to report the
        // relative change between the two finest as the residual grid dependence.
        if (results.Count >= 2)
        {
            var fine = results[results.Count - 1];
            var prev = results[results.Count - 2];
            float rel = Mathf.Abs(fine.cdA - prev.cdA) / Mathf.Max(prev.cdA, 1e-6f);
            _log.AppendLine(string.Format(Ic,
                "  Residual grid dependence between the two finest tiers: {0:P1} of CdA " +
                "({1} the {2:P0} run-to-run scatter of these runs).",
                rel, rel < fine.cv ? "inside" : "outside", fine.cv));
        }
    }

    static Point Measure(GameObject prefab, TunnelResolution tier)
    {
        var tunnelGo = new GameObject("Tunnel");
        tunnelGo.transform.position = new Vector3(0f, 4f, 0f);
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.size = new Vector3(26f, 8f, 12f);
        domain.ground = GroundSimulation.FixedFloor;
        domain.inletSpeedMs = 30f;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = true;
        domain.autoFit.fitAutomatically = false;
        // The tier is the independent variable, so the fit must not choose one; every
        // row then solves the same box at a different cell size.
        domain.autoFit.autoResolution = false;
        domain.autoFit.floorPlaneY = 0f;
        domain.resolution = tier;

        var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var vehicle = car.GetComponentInChildren<AeroVehicle>(true);
        vehicle.RefreshWheels();
        domain.vehicle = vehicle;

        var plan = domain.FitToVehicle(logSummary: false);
        if (!plan.valid) throw new InvalidOperationException("auto-fit failed: " + plan.error);

        domain.rotatingWheels = false;   // held equal across tiers
        domain.StartSimulation();
        if (domain.Solver == null) throw new InvalidOperationException($"{tier}: solver never started");

        _log.AppendLine(string.Format(Ic, "{0}: {1}x{2}x{3}, {4:F0} mm cells, {5:F2}M cells, tunnel {6:F1}x{7:F1}x{8:F1} m",
            tier, domain.Dims.x, domain.Dims.y, domain.Dims.z, domain.CellSize * 1000f,
            (double)domain.Dims.x * domain.Dims.y * domain.Dims.z / 1e6,
            domain.EffectiveSize.x, domain.EffectiveSize.y, domain.EffectiveSize.z));

        int ticksPerFt = Mathf.CeilToInt(domain.FlowThroughSteps / domain.stepsPerTick);
        for (int ft = 1; ft <= FlowThroughs; ft++)
        {
            for (int i = 0; i < ticksPerFt; i++)
            {
                domain.Tick();
                if ((i & 15) == 15) AsyncGPUReadback.WaitAllRequests();
            }
            AsyncGPUReadback.WaitAllRequests();
            _log.AppendLine(string.Format(Ic, "  {0}: FT {1,2}  Cd {2:F3}  Cl {3:F3}  CV {4:P2}",
                tier, ft, domain.LatestSample.cd, domain.LatestSample.cl, domain.ConvergenceCV));
        }

        int perFt = Mathf.Max(1, Mathf.RoundToInt(domain.FlowThroughSteps / domain.sampleIntervalSteps));
        int window = Mathf.Min(AverageOverLast * perFt, domain.SampleHistory.Count);
        float meanCd = 0f, meanCl = 0f;
        for (int i = domain.SampleHistory.Count - window; i < domain.SampleHistory.Count; i++)
        {
            meanCd += domain.SampleHistory[i].cd;
            meanCl += domain.SampleHistory[i].cl;
        }
        meanCd /= Mathf.Max(window, 1);
        meanCl /= Mathf.Max(window, 1);

        float variance = 0f;
        for (int i = domain.SampleHistory.Count - window; i < domain.SampleHistory.Count; i++)
        {
            float d = domain.SampleHistory[i].cd - meanCd;
            variance += d * d;
        }
        float std = Mathf.Sqrt(variance / Mathf.Max(window, 1));

        var point = new Point
        {
            tier = tier,
            dims = domain.Dims,
            cells = (long)domain.Dims.x * domain.Dims.y * domain.Dims.z,
            cellMm = domain.CellSize * 1000f,
            areaM2 = domain.FrontalAreaM2,
            blockage = domain.BlockageRatio,
            cd = meanCd,
            cdStd = std,
            cdA = meanCd * domain.FrontalAreaM2,
            cl = meanCl,
            cv = std / Mathf.Max(Mathf.Abs(meanCd), 1e-6f),
            cellsOnBody = plan.bodyLengthM / Mathf.Max(domain.CellSize, 1e-6f)
        };
        _log.AppendLine(string.Format(Ic, "  {0}: averaged {1} samples — Cd {2:F3} +/- {3:F3}, CdA {4:F3} m2, A {5:F3} m2",
            tier, window, point.cd, point.cdStd, point.cdA, point.areaM2));

        domain.ShutdownSimulation();
        Object.DestroyImmediate(car);
        Object.DestroyImmediate(tunnelGo);
        return point;
    }
}
