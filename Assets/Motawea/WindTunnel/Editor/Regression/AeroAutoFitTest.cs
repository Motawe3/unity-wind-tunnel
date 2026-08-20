// Regression test for the vehicle-class auto-fit (update 1):
//   Unity.exe -batchmode -projectPath . -executeMethod AeroAutoFitTest.Run
// Fits the tunnel to each of the project's prefabs as a different class of craft and
// checks the geometry, the class policy, the memory budget and the reference-area
// convention. Needs a GPU (voxelization is a compute shader) — do not pass -nographics.
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

public static class AeroAutoFitTest
{
    const string ReportPath = "aero_autofit_test.txt";

    class Case
    {
        public string prefab;
        public AeroVehicleClass vehicleClass;
        public WatercraftMode watercraft = WatercraftMode.AboveWaterlineAir;
        public bool solve;              // run flow after fitting
    }

    static readonly Case[] Cases =
    {
        new Case { prefab = "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab",
                   vehicleClass = AeroVehicleClass.RoadVehicle, solve = true },
        new Case { prefab = "Assets/Prefabs/OtherVehicles/MolniaAnimated.prefab",
                   vehicleClass = AeroVehicleClass.Motorsport },
        new Case { prefab = "Assets/Prefabs/OtherVehicles/spy dron.prefab",
                   vehicleClass = AeroVehicleClass.Aircraft, solve = true },
        new Case { prefab = "Assets/Prefabs/OtherVehicles/Boat.prefab",
                   vehicleClass = AeroVehicleClass.Watercraft, watercraft = WatercraftMode.AboveWaterlineAir },
        new Case { prefab = "Assets/Prefabs/OtherVehicles/Boat.prefab",
                   vehicleClass = AeroVehicleClass.Watercraft, watercraft = WatercraftMode.SubmergedHull },
    };

    static StringBuilder _log;
    static int _passed, _failed;
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSettings.asyncShaderCompilation = false;
            _log.AppendLine("AeroAutoFitTest — tunnel auto-fit across vehicle classes");

            foreach (var c in Cases) RunCase(c);
            RunCellSizeLockCase();
            RunAngleOfAttackCase();
        }
        catch (Exception e)
        {
            _log.AppendLine("EXCEPTION: " + e);
            _failed++;
        }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"AUTO-FIT TESTS: PASS ({_passed} checks)"
                                     : $"AUTO-FIT TESTS: FAIL ({_failed} of {_passed + _failed} checks)");
        File.WriteAllText(ReportPath, _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void RunCase(Case c)
    {
        _log.AppendLine();
        _log.AppendLine($"--- {Path.GetFileNameWithoutExtension(c.prefab)} as {c.vehicleClass}" +
                        (c.vehicleClass == AeroVehicleClass.Watercraft ? $" ({c.watercraft})" : "") + " ---");

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(c.prefab);
        if (prefab == null) { Check("prefab found", false, c.prefab); return; }

        // A deliberately wrong starting tunnel: sized for something else, parked away
        // from the vehicle. Everything below has to be produced by the fit.
        var tunnelGo = new GameObject("Tunnel");
        tunnelGo.transform.position = new Vector3(3f, 2.5f, -4f);
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.size = new Vector3(20f, 5f, 8f);
        domain.resolution = TunnelResolution.Coarse;
        domain.inletSpeedMs = 30f;
        domain.stepsPerTick = 32;
        domain.autoFit.memoryBudgetGB = 1.0f;
        domain.autoFit.floorPlaneY = 0f;

        var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        car.transform.position = new Vector3(1.5f, 0.75f, 2.5f);   // nowhere near the tunnel
        var vehicle = car.GetComponentInChildren<AeroVehicle>(true) ?? car.AddComponent<AeroVehicle>();
        vehicle.vehicleClass = c.vehicleClass;
        vehicle.watercraftMode = c.watercraft;
        vehicle.referenceAreaMode = AeroReferenceAreaMode.Automatic;
        vehicle.referenceAreaOverride = 0f;
        vehicle.RefreshWheels();

        domain.vehicle = vehicle;
        var profile = vehicle.Profile;
        var plan = domain.FitToVehicle(logSummary: false);

        Check("fit produced a plan", plan.valid, plan.error);
        if (!plan.valid) { Cleanup(car, tunnelGo, null); return; }
        _log.AppendLine("  " + plan.Summary());
        foreach (string note in plan.notes) _log.AppendLine("  note: " + note);

        // ---- class policy ----
        Check("ground boundary matches the class", domain.ground == profile.Ground,
              $"{domain.ground} vs {profile.Ground}");
        Check("wheel rotation matches the class",
              domain.rotatingWheels == (profile.RotatingWheels && vehicle.Wheels.Count > 0),
              $"{domain.rotatingWheels} (wheels {vehicle.Wheels.Count})");
        Check("working fluid matches the class", domain.air.medium == profile.Medium,
              $"{domain.air.medium} vs {profile.Medium}");

        // ---- budget ----
        float memGB = plan.cellCount * TunnelAutoFit.BytesPerCell / (1024f * 1024f * 1024f);
        Check("grid fits the memory budget", memGB <= domain.autoFit.memoryBudgetGB + 1e-3f,
              $"{memGB:F2} GB of {domain.autoFit.memoryBudgetGB:F2} GB");
        Check("grid is under the cell guard", plan.cellCount <= WindTunnelDomain.MaxCells,
              $"{plan.cellCount:N0} cells");
        Check("body is resolved by at least 16 cells", plan.cellsAcrossBody >= 16f,
              $"{plan.cellsAcrossBody:F0} cells across {plan.bodyLengthM:F2} m");

        // ---- geometry: the body must sit where the fit says it does ----
        Quaternion rot = domain.transform.rotation;
        Quaternion inv = Quaternion.Inverse(rot);
        vehicle.TryComputeAeroBounds(rot, out Bounds body);
        Vector3 center = inv * domain.transform.position;
        Vector3 halfSize = domain.EffectiveSize * 0.5f;
        Vector3 lo = center - halfSize, hi = center + halfSize;
        float dx = domain.CellSize;

        bool insideStream = body.min.x > lo.x + dx && body.max.x < hi.x - dx;
        bool insideLateral = body.min.z > lo.z + dx && body.max.z < hi.z - dx;
        bool insideVertical = body.max.y < hi.y - dx &&
                              (profile.Placement == AeroPlacement.CenterInDomain
                                  ? body.min.y > lo.y + dx
                                  : true); // grounded bodies touch the floor by design
        Check("body is inside the domain streamwise", insideStream,
              $"x {body.min.x:F2}..{body.max.x:F2} in {lo.x:F2}..{hi.x:F2}");
        Check("body is inside the domain laterally", insideLateral,
              $"z {body.min.z:F2}..{body.max.z:F2} in {lo.z:F2}..{hi.z:F2}");
        Check("body is inside the domain vertically", insideVertical,
              $"y {body.min.y:F2}..{body.max.y:F2} in {lo.y:F2}..{hi.y:F2}");

        Check("laterally centred", Mathf.Abs(body.center.z - center.z) < 0.02f,
              $"offset {body.center.z - center.z:F3} m");

        float upstream = body.min.x - lo.x;
        float expectedUpstream = profile.UpstreamLengths * plan.bodyLengthM;
        Check("upstream margin follows the class profile",
              Mathf.Abs(upstream - expectedUpstream) < Mathf.Max(0.05f * expectedUpstream, dx),
              $"{upstream:F2} m vs {expectedUpstream:F2} m");

        float downstream = hi.x - body.max.x;
        Check("wake has more room than the inlet", downstream > upstream,
              $"downstream {downstream:F2} m vs upstream {upstream:F2} m");

        switch (profile.Placement)
        {
            case AeroPlacement.SeatOnFloor:
            {
                float contact = vehicle.ContactHeight(rot, body);
                Check("contact patches sit on the tunnel floor", Mathf.Abs(contact - lo.y) < 0.005f,
                      $"contact {contact:F3} m, floor {lo.y:F3} m");
                Check("floor pinned to the scene ground plane", Mathf.Abs(lo.y - domain.autoFit.floorPlaneY) < 0.005f,
                      $"floor {lo.y:F3} m");
                break;
            }
            case AeroPlacement.WaterlineOnFloor:
            {
                float waterline = vehicle.WaterlineHeight(rot, body);
                Check("waterline sits on the tunnel floor", Mathf.Abs(waterline - lo.y) < 0.005f,
                      $"waterline {waterline:F3} m, floor {lo.y:F3} m");
                Check("submerged hull is below the domain", body.min.y < lo.y - 1e-3f,
                      $"keel {body.min.y:F3} m");
                break;
            }
            default:
                Check("body is centred in the domain cross-section",
                      Mathf.Abs(body.center.y - center.y) < 0.02f,
                      $"offset {body.center.y - center.y:F3} m");
                break;
        }

        // ---- what the solver then measures ----
        domain.StartSimulation();
        if (domain.Solver == null) { Check("simulation starts after the fit", false); Cleanup(car, tunnelGo, null); return; }
        Check("simulation starts after the fit", true);

        _log.AppendLine(string.Format(Ic,
            "  measured: frontal {0:F3} m², planform {1:F3} m², reference {2:F3} m² ({3}), blockage {4:P2}",
            domain.MeasuredFrontalAreaM2, domain.MeasuredPlanformAreaM2, domain.FrontalAreaM2,
            domain.ReferenceAreaMode, domain.BlockageRatio));

        Check("frontal area measured", domain.MeasuredFrontalAreaM2 > 0f);
        Check("planform area measured", domain.MeasuredPlanformAreaM2 > 0f);
        Check("blockage under the wind-tunnel guidance",
              domain.BlockageRatio <= WindTunnelDomain.BlockageWarningRatio,
              domain.BlockageRatio.ToString("P2"));
        Check("blockage close to the auto-fit target",
              domain.BlockageRatio <= domain.autoFit.targetBlockage * 1.5f,
              $"{domain.BlockageRatio:P2} vs target {domain.autoFit.targetBlockage:P1}");

        var expectedMode = c.vehicleClass == AeroVehicleClass.Aircraft
            ? AeroReferenceAreaMode.Planform
            : AeroReferenceAreaMode.FrontalSilhouette;
        Check("reference-area convention follows the class", domain.ReferenceAreaMode == expectedMode,
              $"{domain.ReferenceAreaMode} vs {expectedMode}");
        Check("reference area is the one the convention names",
              Mathf.Approximately(domain.FrontalAreaM2,
                  expectedMode == AeroReferenceAreaMode.Planform
                      ? domain.MeasuredPlanformAreaM2 : domain.MeasuredFrontalAreaM2));

        if (c.vehicleClass == AeroVehicleClass.Aircraft)
            Check("planform is the larger area for a flying wing/drone",
                  domain.MeasuredPlanformAreaM2 > domain.MeasuredFrontalAreaM2,
                  $"{domain.MeasuredPlanformAreaM2:F3} vs {domain.MeasuredFrontalAreaM2:F3} m²");

        if (c.solve) SolveAndReport(domain);

        Cleanup(car, tunnelGo, domain);
    }

    static void SolveAndReport(WindTunnelDomain domain)
    {
        int ticksPerFt = Mathf.CeilToInt(domain.FlowThroughSteps / domain.stepsPerTick);
        for (int ft = 1; ft <= 3; ft++)
        {
            for (int i = 0; i < ticksPerFt; i++)
            {
                domain.Tick();
                if ((i & 15) == 15) AsyncGPUReadback.WaitAllRequests();
            }
            AsyncGPUReadback.WaitAllRequests();
        }

        var s = domain.LatestSample;
        _log.AppendLine(string.Format(Ic, "  after 3 flow-throughs: Cd {0:F3} Cl {1:F3} CdA {2:F3} m² drag {3:F1} N",
            s.cd, s.cl, s.cdA, s.dragForceN));

        Check("a force sample was produced", domain.HasSample);
        Check("Cd is finite and positive", !float.IsNaN(s.cd) && !float.IsInfinity(s.cd) && s.cd > 0f,
              s.cd.ToString("F4"));
        Check("Cd is in a physical range for a bluff body", s.cd < 5f, s.cd.ToString("F4"));
    }

    /// <summary>
    /// Two vehicles of different size fitted with the same locked cell size must end up
    /// on the same resolution. Without this, the auto-fit gives every vehicle a domain
    /// scaled to itself, so a shared resolution TIER still means different cell sizes —
    /// and the comparison tool has to caveat every cross-vehicle A/B as a result.
    /// </summary>
    static void RunCellSizeLockCase()
    {
        _log.AppendLine();
        _log.AppendLine("--- locked cell size across two differently-sized vehicles ---");

        const float lockedMm = 90f;
        var sizes = new List<(string label, float cellM, float lengthM, int nx)>();

        foreach (string prefabPath in new[]
                 {
                     "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab",
                     "Assets/Prefabs/OtherVehicles/MolniaAnimated.prefab"
                 })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { Check("prefab found", false, prefabPath); return; }

            var tunnelGo = new GameObject("Tunnel");
            tunnelGo.transform.position = new Vector3(0f, 3f, 0f);
            var domain = tunnelGo.AddComponent<WindTunnelDomain>();
            domain.autoFit.fitAutomatically = false;
            domain.autoFit.matchCellSizeM = lockedMm / 1000f;
            domain.autoFit.memoryBudgetGB = 4f;

            var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var vehicle = car.GetComponentInChildren<AeroVehicle>(true);
            vehicle.RefreshWheels();
            domain.vehicle = vehicle;

            var plan = domain.FitToVehicle(logSummary: false);
            Check($"fit succeeded for {vehicle.Name}", plan.valid, plan.error);
            if (plan.valid)
            {
                sizes.Add((vehicle.Name, domain.CellSize, domain.EffectiveSize.x, domain.Dims.x));
                _log.AppendLine(string.Format(Ic,
                    "  {0}: tunnel {1:F1} m, {2} streamwise cells, {3:F1} mm cells",
                    vehicle.Name, domain.EffectiveSize.x, domain.Dims.x, domain.CellSize * 1000f));
            }

            domain.ShutdownSimulation();
            Object.DestroyImmediate(car);
            Object.DestroyImmediate(tunnelGo);
        }

        if (sizes.Count != 2) { Check("both vehicles fitted", false); return; }

        Check("tunnels really are different lengths",
              Mathf.Abs(sizes[0].lengthM - sizes[1].lengthM) > 1f,
              $"{sizes[0].lengthM:F1} m vs {sizes[1].lengthM:F1} m");
        Check("streamwise cell counts differ (the tier could not have done this)",
              sizes[0].nx != sizes[1].nx, $"{sizes[0].nx} vs {sizes[1].nx}");

        float relative = Mathf.Abs(sizes[0].cellM - sizes[1].cellM) / Mathf.Max(sizes[0].cellM, 1e-6f);
        Check("both land on the same cell size", relative < 0.01f,
              $"{sizes[0].cellM * 1000f:F2} mm vs {sizes[1].cellM * 1000f:F2} mm ({relative:P2} apart)");
        Check("and on the size that was asked for",
              Mathf.Abs(sizes[0].cellM * 1000f - lockedMm) / lockedMm < 0.01f,
              $"{sizes[0].cellM * 1000f:F2} mm vs {lockedMm:F0} mm requested");
    }

    /// <summary>
    /// The alpha sweep exists for aircraft, so its sign convention has to be right:
    /// a positive angle of attack must raise the nose. Driven through the real runner,
    /// with no solving — the first point is applied the moment the session starts.
    /// </summary>
    static void RunAngleOfAttackCase()
    {
        _log.AppendLine();
        _log.AppendLine("--- angle-of-attack sign convention ---");

        var tunnelGo = new GameObject("Tunnel");
        tunnelGo.transform.position = new Vector3(0f, 2f, 0f);
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.size = new Vector3(12f, 4f, 4f);
        domain.resolution = TunnelResolution.Coarse;
        domain.autoFit.fitAutomatically = false;

        // A slender body along the wind axis: nose at -X, tail at +X.
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Alpha probe";
        body.transform.position = new Vector3(0f, 2f, 0f);
        body.transform.localScale = new Vector3(4f, 0.3f, 1f);
        var vehicle = body.AddComponent<AeroVehicle>();
        vehicle.vehicleClass = AeroVehicleClass.Aircraft;
        vehicle.displayName = "Alpha Probe Mk II";
        domain.vehicle = vehicle;

        var runner = tunnelGo.AddComponent<AeroTestRunner>();
        runner.tunnel = domain;

        float noseBefore = NoseHeight(body);
        runner.StartSingle(new AeroTestDefinition
        {
            testName = "Alpha +10",
            kind = AeroTestKind.AngleOfAttackSweep,
            alphaFromDeg = 10f,
            alphaToDeg = 10f,
            alphaPoints = 2,
            ground = GroundSimulation.OpenFloor,
            rotatingWheels = false,
            maxStepsPerPoint = 1
        });
        float noseAfter = NoseHeight(body);

        // Checked while the session is live: the SAE area lock writes
        // referenceAreaOverride for the duration, which would make every locked run
        // report "manual override" and hide whether the area was a frontal silhouette
        // or a planform — the one thing a later comparison must not lose.
        var session = runner.CurrentSession;
        bool locked = vehicle.referenceAreaOverride > 0f;
        Check("session records the real area convention under the SAE area lock",
              session != null && session.referenceAreaMode == AeroReferenceAreaMode.Planform,
              $"area locked={locked}, recorded={session?.referenceAreaMode.ToString() ?? "no session"}");

        // Reports and exported file names must carry the display name, not the
        // GameObject name the asset happened to be imported with.
        Check("session uses the vehicle's display name",
              session != null && session.vehicleName == "Alpha Probe Mk II",
              $"GameObject '{body.name}' recorded as '{session?.vehicleName ?? "none"}'");
        var unnamed = new GameObject("Fallback Probe");
        Check("an unnamed vehicle still identifies itself",
              unnamed.AddComponent<AeroVehicle>().Name == "Fallback Probe");
        Object.DestroyImmediate(unnamed);

        runner.AbortQueue();

        _log.AppendLine(string.Format(Ic, "  nose height {0:F3} m → {1:F3} m at alpha +10°", noseBefore, noseAfter));
        Check("positive angle of attack raises the nose", noseAfter > noseBefore + 0.05f,
              $"{noseBefore:F3} → {noseAfter:F3}");

        domain.ShutdownSimulation();
        Object.DestroyImmediate(body);
        Object.DestroyImmediate(tunnelGo);
    }

    /// <summary>World height of the body's most upstream point (the nose faces −X).</summary>
    static float NoseHeight(GameObject go) =>
        go.transform.TransformPoint(new Vector3(-0.5f, 0f, 0f)).y;

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
