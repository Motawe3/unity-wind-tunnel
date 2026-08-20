// Validation harness (run via -executeMethod AeroValidate.Run in batch mode).
//
// Measures Cd for bodies whose drag coefficient is known from the literature, so a
// force-integration bug can be told apart from a resolution/modelling limit. Then
// runs the real vehicle under the same solver for comparison.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Motawea.WindTunnel;
using Object = UnityEngine.Object;

public static class AeroValidate
{
    const string ReportPath = "aero_validate.txt";

    // Free-air tunnel sized so every reference body stays under ~4% blockage.
    static readonly Vector3 TunnelSize = new Vector3(12f, 5f, 5f);
    const TunnelResolution Resolution = TunnelResolution.Fine;   // 256 -> dx = 46.9 mm

    const float FlowThroughs = 20f;     // run length before averaging (8 sampled the transient)
    const int AverageSamples = 20;      // trailing samples folded into the mean

    // -aeroQuick on the command line: one soft setting, one ride height — for
    // fast iteration on solver changes before paying for the full matrix.
    static bool Quick => Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroQuick") >= 0;

    static StringBuilder _log;

    public static void Run()
    {
        _log = new StringBuilder();
        int exit = 0;
        try
        {
            Execute();
        }
        catch (Exception e)
        {
            _log.AppendLine("EXCEPTION: " + e);
            exit = 1;
        }
        // A flagged run must not overwrite the canonical report.
        bool wallModelOn =
            Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroWallModel") >= 0;
        File.WriteAllText(wallModelOn
            ? ReportPath.Replace(".txt", "_wallmodel.txt") : ReportPath, _log.ToString());
        EditorApplication.Exit(exit);
    }

    static void Execute()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        _log.AppendLine($"Wind Tunnel validation — tunnel {TunnelSize} @ {(int)Resolution}, free air (OpenFloor), 30 m/s");
        _log.AppendLine($"averaging {AverageSamples} samples after {FlowThroughs} flow-throughs");
        _log.AppendLine();
        _log.AppendLine("case                       soft   A_meas   A_true   Cd_meas   Cd_ref   ratio");
        _log.AppendLine("-------------------------------------------------------------------------------");

        foreach (bool soft in Quick ? new[] { false } : new[] { true, false })
        {
            // Sphere: Cd ~ 0.47 subcritical; ~0.4 near Re 4e3.
            MeasurePrimitive("sphere D=1m", PrimitiveType.Sphere, Vector3.one,
                             Mathf.PI * 0.25f, 0.45f, soft);

            // Cube, face normal to the flow: Cd ~ 1.05.
            MeasurePrimitive("cube 1m face-on", PrimitiveType.Cube, Vector3.one,
                             1f, 1.05f, soft);

            // Thin square plate normal to the flow: Cd ~ 1.17.
            MeasurePrimitive("plate 1m normal", PrimitiveType.Cube, new Vector3(0.05f, 1f, 1f),
                             1f, 1.17f, soft);
        }

        _log.AppendLine();
        MeasureVehicle();
    }

    static void MeasurePrimitive(string label, PrimitiveType type, Vector3 scale,
                                 float trueAreaM2, float cdRef, bool soft)
    {
        var domain = BuildDomain(soft);

        var rig = new GameObject("Rig");
        var vehicle = rig.AddComponent<AeroVehicle>();
        var body = GameObject.CreatePrimitive(type);
        body.transform.SetParent(rig.transform);
        body.transform.localScale = scale;
        body.transform.localPosition = Vector3.zero;
        // Park the body a third of the way down the tunnel so the wake has room.
        rig.transform.position = domain.transform.position + new Vector3(-TunnelSize.x * 0.15f, 0f, 0f);

        domain.vehicle = vehicle;

        var result = RunToAverage(domain);
        _log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-25}  {1,-4}  {2,7:F3}  {3,7:F3}   {4,7:F3}  {5,7:F3}  {6,6:F2}x",
            label, soft ? "ON" : "OFF", domain.FrontalAreaM2, trueAreaM2,
            result.cd, cdRef, cdRef > 0f ? result.cd / cdRef : 0f));

        domain.ShutdownSimulation();
        Object.DestroyImmediate(rig);
        Object.DestroyImmediate(domain.gameObject);
    }

    static void MeasureVehicle()
    {
        const string prefabPath = "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            _log.AppendLine($"vehicle: prefab not found at {prefabPath}");
            return;
        }

        _log.AppendLine("vehicle (Range Rover Sport SVR) — published Cd ~0.35, A ~2.9 m²");
        _log.AppendLine("case                soft   res    A_meas   ride    Cd      Cl     ClF     ClR    blockage");
        _log.AppendLine("--------------------------------------------------------------------------------------------");

        // Ground-clearance sweep at Ultra, soft off. Contact-fill plants the tires on
        // the floor only when the lowest surface sits 1-2 cells above the ground shell
        // (dx≈68mm here); resting exactly on the shell (ride 0) skips the fill and
        // produces fake lift. This finds the clearance that gives clean ground effect.
        const TunnelResolution res = TunnelResolution.Ultra;
        foreach (bool soft in new[] { false })
        foreach (float ride in Quick ? new[] { 0.22f } : new[] { 0.07f, 0.14f, 0.22f, 0.35f, 1.5f })
        {
            var domain = BuildDomain(soft);
            domain.size = new Vector3(26f, 8f, 12f);
            domain.resolution = res;
            domain.ground = GroundSimulation.FixedFloor;

            var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var vehicle = car.GetComponentInChildren<AeroVehicle>(true);
            if (vehicle == null)
            {
                _log.AppendLine("vehicle: prefab has no AeroVehicle");
                Object.DestroyImmediate(car);
                Object.DestroyImmediate(domain.gameObject);
                return;
            }
            vehicle.RefreshWheels();

            // Seat the car on the tunnel floor, then lift it by `ride`.
            Bounds b = vehicle.ComputeBounds();
            float floorY = domain.transform.position.y - domain.size.y * 0.5f;
            car.transform.position += new Vector3(
                domain.transform.position.x - b.center.x,
                floorY + ride - b.min.y,
                domain.transform.position.z - b.center.z);

            domain.vehicle = vehicle;
            var result = RunToAverage(domain);

            _log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-18}  {1,-4}  {2,-5}  {3,6:F3}  {4,4:F2}m  {5,6:F3}  {6,6:F3}  {7,6:F3}  {8,6:F3}  {9:P2}",
                "range rover", soft ? "ON" : "OFF", (int)res, domain.FrontalAreaM2, ride,
                result.cd, result.cl, result.clFront, result.clRear, domain.BlockageRatio));

            domain.ShutdownSimulation();
            Object.DestroyImmediate(car);
            Object.DestroyImmediate(domain.gameObject);
        }
    }

    static WindTunnelDomain BuildDomain(bool softVoxels)
    {
        var go = new GameObject("Tunnel");
        go.transform.position = new Vector3(0f, 2.5f, 0f);
        var domain = go.AddComponent<WindTunnelDomain>();
        // This harness owns its tunnel: the numbers below are only comparable
        // against past runs if the domain stays exactly as authored here.
        domain.autoFit.fitAutomatically = false;
        domain.size = TunnelSize;
        domain.resolution = Resolution;
        domain.ground = GroundSimulation.OpenFloor;
        domain.inletSpeedMs = 30f;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = softVoxels;
        // -aeroWallModel: run the reference bodies with the Stage 2.5c wall model on.
        // Plate and cube are Reynolds-independent; if the model moves them, it is wrong.
        domain.wallModel =
            Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroWallModel") >= 0;
        domain.rotatingWheels = false;
        return domain;
    }

    /// <summary>Runs the tunnel and returns the mean of the trailing samples.</summary>
    static AeroSample RunToAverage(WindTunnelDomain domain)
    {
        domain.StartSimulation();
        if (domain.Solver == null)
        {
            _log.AppendLine("  StartSimulation failed");
            return default;
        }

        int ticks = Mathf.CeilToInt(FlowThroughs * domain.FlowThroughSteps / domain.stepsPerTick);
        for (int i = 0; i < ticks; i++)
        {
            domain.Tick();
            // Force samples arrive through AsyncGPUReadback callbacks, which only run
            // when the request queue is pumped — there is no frame loop in batch mode.
            if ((i & 15) == 15) AsyncGPUReadback.WaitAllRequests();
        }
        AsyncGPUReadback.WaitAllRequests();

        var history = domain.SampleHistory;
        if (history.Count == 0)
        {
            _log.AppendLine("  no samples collected");
            return default;
        }

        int take = Mathf.Min(AverageSamples, history.Count);
        var mean = new AeroSample();
        for (int i = history.Count - take; i < history.Count; i++)
        {
            mean.cd += history[i].cd;
            mean.cl += history[i].cl;
            mean.clFront += history[i].clFront;
            mean.clRear += history[i].clRear;
        }
        mean.cd /= take; mean.cl /= take;
        mean.clFront /= take; mean.clRear /= take;
        mean.frontalAreaM2 = domain.FrontalAreaM2;
        return mean;
    }

    static string SolidCells(WindTunnelDomain domain)
    {
        // Not exposed on the domain; report blockage instead as a sanity proxy.
        return $"blockage {domain.BlockageRatio:P2}";
    }
}
