// Evidence run for the tunnel auto-fit (update 1):
//   Unity.exe -batchmode -projectPath . -executeMethod AeroFitAccuracyTest.Run
// Measures the same vehicle in the hand-sized tunnel this project used before the
// auto-fit existed, and in the tunnel the auto-fit builds, with every other setting
// held equal. This is not a pass/fail test — it quantifies what the fit changes, which
// is the only honest claim available: the solver's absolute road-car Cd reads high
// either way (no wall model), so what is being measured here is the blockage and
// domain-proportion error the fit removes.
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

public static class AeroFitAccuracyTest
{
    const string ReportPath = "aero_fit_accuracy.txt";
    const string PrefabPath = "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab";

    // Long enough for the wake to establish; the average is taken over the tail.
    const int FlowThroughs = 10;
    const int AverageOverLast = 3;

    static StringBuilder _log;
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        int exit = 0;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSettings.asyncShaderCompilation = false;

            _log.AppendLine("AeroFitAccuracyTest — hand-sized tunnel vs auto-fitted tunnel, same vehicle,");
            _log.AppendLine($"same solver settings, {FlowThroughs} flow-throughs, Cd averaged over the last {AverageOverLast}.");
            _log.AppendLine("Range Rover Sport SVR — published Cd ~0.35 (this solver reads high on smooth bodies:");
            _log.AppendLine("no wall model, so the absolute value is not the point; the domain effect is).");
            _log.AppendLine();

            var legacy = Measure("hand-sized 26x8x12", autoFit: false);
            var fitted = Measure("auto-fitted", autoFit: true);

            _log.AppendLine();
            _log.AppendLine("case                 tunnel (m)            cell   blockage   Cd     std     Cl      CdA");
            _log.AppendLine("------------------------------------------------------------------------------------------");
            _log.AppendLine(legacy.Row());
            _log.AppendLine(fitted.Row());
            _log.AppendLine();

            float dCd = fitted.cd - legacy.cd;
            _log.AppendLine(string.Format(Ic,
                "Cd {0:F3} → {1:F3} ({2:+0.0;-0.0}%), blockage {3:P1} → {4:P1}, cells {5:F0} mm → {6:F0} mm.",
                legacy.cd, fitted.cd, 100f * dCd / Mathf.Max(legacy.cd, 1e-6f),
                legacy.blockage, fitted.blockage, legacy.cellMm, fitted.cellMm));
            _log.AppendLine(
                "Read this as the blockage/proportion correction, not as validation: the two cases " +
                "necessarily differ in cell size too (a tier fixes the streamwise cell count, so a " +
                "longer domain is a coarser one), and neither number is a trustworthy absolute Cd.");
        }
        catch (Exception e)
        {
            _log.AppendLine("EXCEPTION: " + e);
            exit = 1;
        }

        File.WriteAllText(ReportPath, _log.ToString());
        return exit;
    }

    struct Result
    {
        public string label;
        public Vector3 size;
        public float cellMm, blockage, cd, std, cl, cdA;

        public string Row() => string.Format(Ic,
            "{0,-20} {1,-21} {2,4:F0}mm  {3,7:P1}  {4,6:F3}  {5,5:F3}  {6,6:F3}  {7,6:F3}",
            label, $"{size.x:F1}x{size.y:F1}x{size.z:F1}", cellMm, blockage, cd, std, cl, cdA);
    }

    static Result Measure(string label, bool autoFit)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var tunnelGo = new GameObject("Tunnel");
        // Floor at y = 0 either way, so both cases sit the car on the same plane.
        tunnelGo.transform.position = new Vector3(0f, 4f, 0f);
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.size = new Vector3(26f, 8f, 12f);
        domain.resolution = TunnelResolution.Ultra;
        domain.ground = GroundSimulation.FixedFloor;
        domain.inletSpeedMs = 30f;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = true;
        domain.autoFit.fitAutomatically = false;
        domain.autoFit.autoResolution = false;   // hold the tier equal across cases
        domain.autoFit.floorPlaneY = 0f;

        var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var vehicle = car.GetComponentInChildren<AeroVehicle>(true);
        vehicle.RefreshWheels();

        domain.vehicle = vehicle;
        if (autoFit)
        {
            var plan = domain.FitToVehicle(logSummary: false);
            if (!plan.valid) throw new InvalidOperationException("auto-fit failed: " + plan.error);
            _log.AppendLine("  " + plan.Summary());
        }
        else
        {
            // How this project seated the car before the fit existed: centred in the
            // tunnel, 0.22 m of clearance so the tyre-contact fill engages.
            Bounds b = vehicle.ComputeBounds();
            float floorY = domain.transform.position.y - domain.size.y * 0.5f;
            car.transform.position += new Vector3(
                domain.transform.position.x - b.center.x,
                floorY + 0.22f - b.min.y,
                domain.transform.position.z - b.center.z);
        }

        // The class policy would switch the wheels on for the fitted case only; hold
        // every boundary condition equal so the domain is the one thing that differs.
        domain.rotatingWheels = false;
        domain.StartSimulation();
        if (domain.Solver == null)
            throw new InvalidOperationException($"'{label}': the solver never started — nothing to measure");

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
                label, ft, domain.LatestSample.cd, domain.LatestSample.cl, domain.ConvergenceCV));
        }

        // The wake is unsteady: a single reading per flow-through would be sampling
        // the oscillation, not measuring it. Average every force sample taken over the
        // last few flow-throughs, which is what the tunnel already recorded.
        int perFlowThrough = Mathf.Max(1, Mathf.RoundToInt(domain.FlowThroughSteps / domain.sampleIntervalSteps));
        int window = Mathf.Min(AverageOverLast * perFlowThrough, domain.SampleHistory.Count);
        var samples = new List<float>();
        var lifts = new List<float>();
        for (int i = domain.SampleHistory.Count - window; i < domain.SampleHistory.Count; i++)
        {
            samples.Add(domain.SampleHistory[i].cd);
            lifts.Add(domain.SampleHistory[i].cl);
        }

        float mean = 0f, meanCl = 0f;
        foreach (float v in samples) mean += v;
        foreach (float v in lifts) meanCl += v;
        mean /= Mathf.Max(samples.Count, 1);
        meanCl /= Mathf.Max(lifts.Count, 1);
        float variance = 0f;
        foreach (float v in samples) variance += (v - mean) * (v - mean);
        _log.AppendLine(string.Format(Ic, "  {0}: averaged {1} force samples over the last {2} flow-throughs",
            label, samples.Count, AverageOverLast));

        var result = new Result
        {
            label = label,
            size = domain.EffectiveSize,
            cellMm = domain.CellSize * 1000f,
            blockage = domain.BlockageRatio,
            cd = mean,
            std = Mathf.Sqrt(variance / Mathf.Max(samples.Count, 1)),
            cl = meanCl,
            cdA = mean * domain.FrontalAreaM2
        };

        domain.ShutdownSimulation();
        Object.DestroyImmediate(car);
        Object.DestroyImmediate(tunnelGo);
        return result;
    }
}
