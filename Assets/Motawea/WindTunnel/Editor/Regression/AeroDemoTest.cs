// Demo regression test (run via -executeMethod AeroDemoTest.Run in batch mode):
// builds a tunnel around the Range Rover, runs six flow-throughs and logs voxel
// hashes + Cd per flow-through. The solver is deterministic (see AeroDiagRR), so
// two runs of this file on unchanged physics code must produce identical numbers —
// used as a before/after gate when touching non-solver package code.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Motawea.WindTunnel;
using Object = UnityEngine.Object;

public static class AeroDemoTest
{
    const string ReportPath = "aero_demo_test.txt";
    const string PrefabPath = "Assets/Prefabs/Cars/range-rover-sport-svr-2022.prefab";
    const float Ride = 0.22f;

    static StringBuilder _log;

    public static void Run()
    {
        _log = new StringBuilder();
        int exit = 0;
        try { Execute(); }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); exit = 1; }
        File.WriteAllText(ReportPath, _log.ToString());
        EditorApplication.Exit(exit);
    }

    static void Execute()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { _log.AppendLine("prefab not found"); return; }

        // Compile compute kernels synchronously: the editor otherwise skips
        // dispatches of still-compiling kernels on the session's first case.
        EditorSettings.asyncShaderCompilation = false;

        _log.AppendLine("AeroDemoTest — deterministic demo run, Range Rover, Medium grid");

        var go = new GameObject("Tunnel");
        go.transform.position = new Vector3(0f, 2.5f, 0f);
        var domain = go.AddComponent<WindTunnelDomain>();
        // This harness owns its tunnel: the numbers below are only comparable
        // against past runs if the domain stays exactly as authored here.
        domain.autoFit.fitAutomatically = false;
        domain.size = new Vector3(26f, 8f, 12f);
        domain.resolution = TunnelResolution.Medium;
        domain.ground = GroundSimulation.FixedFloor;
        domain.inletSpeedMs = 30f;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = true;
        domain.rotatingWheels = false;

        var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var vehicle = car.GetComponentInChildren<AeroVehicle>(true);
        vehicle.RefreshWheels();

        Bounds b = vehicle.ComputeBounds();
        float floorY = domain.transform.position.y - domain.size.y * 0.5f;
        car.transform.position += new Vector3(
            domain.transform.position.x - b.center.x,
            floorY + Ride - b.min.y,
            domain.transform.position.z - b.center.z);

        domain.vehicle = vehicle;
        domain.StartSimulation();
        if (domain.Solver == null) { _log.AppendLine("StartSimulation failed"); return; }

        var vox = (VehicleVoxelizer)typeof(WindTunnelDomain)
            .GetField("_voxelizer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(domain);
        _log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "dims {0}  frontal {1:F4} m²  solid cells {2}", domain.Dims, domain.FrontalAreaM2, vox.SolidCellCount));
        _log.AppendLine($"flags hash    {HashUints(vox.FlagsBuffer)}");
        _log.AppendLine($"coverage hash {HashFloats(vox.CoverageBuffer)}");

        int ticksPerFt = Mathf.CeilToInt(domain.FlowThroughSteps / (float)domain.stepsPerTick);
        for (int ft = 1; ft <= 6; ft++)
        {
            for (int i = 0; i < ticksPerFt; i++)
            {
                domain.Tick();
                if ((i & 15) == 15) AsyncGPUReadback.WaitAllRequests();
            }
            AsyncGPUReadback.WaitAllRequests();
            _log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "FT {0}: Cd {1:F3} Cl {2:F3}", ft, domain.LatestSample.cd, domain.LatestSample.cl));
        }

        RunFeatureChecks(domain, vehicle);

        domain.ShutdownSimulation();
        Object.DestroyImmediate(car);
        Object.DestroyImmediate(go);
    }

    // Surface-heatmap smoke test: shader import health, material swap/restore
    // round trip, and a rendered screenshot per mode.
    static void RunFeatureChecks(WindTunnelDomain domain, AeroVehicle vehicle)
    {
        _log.AppendLine();
        _log.AppendLine("--- surface heatmap smoke test ---");

        foreach (string name in new[] { "WindTunnel/FlowSlice", "WindTunnel/FlowSliceImage", "WindTunnel/SurfaceHeatmap", "WindTunnel/FlowParticle" })
        {
            var sh = Shader.Find(name);
            _log.AppendLine(sh == null
                ? $"shader {name}: NOT FOUND"
                : $"shader {name}: found, hasErrors={ShaderUtil.ShaderHasError(sh)}");
        }

        LogRhoProbes(domain);

        var heatmap = domain.gameObject.AddComponent<SurfaceHeatmap>();
        heatmap.tunnel = domain;
        heatmap.mode = SurfaceHeatmapMode.PressureCoefficient;

        // The renderer set the heatmap should paint (mirror of its traversal).
        var targets = new List<MeshRenderer>();
        var cachedByTest = new List<Material[]>();
        foreach (var mf in vehicle.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null || mf.GetComponentInParent<AeroIgnore>() != null) continue;
            if (!mf.TryGetComponent<MeshRenderer>(out var r)) continue;
            targets.Add(r);
            cachedByTest.Add(r.sharedMaterials);
        }

        TickHeatmap(heatmap);
        int painted = 0;
        foreach (var r in targets)
        {
            bool ours = r.sharedMaterials.Length > 0;
            foreach (var m in r.sharedMaterials)
                ours &= m != null && m.shader != null && m.shader.name == "WindTunnel/SurfaceHeatmap";
            if (ours) painted++;
        }
        _log.AppendLine($"painted {painted}/{targets.Count} renderers, IsPainting={heatmap.IsPainting}");

        Screenshot(vehicle, "aero_demo_heatmap_pressure.png");
        heatmap.mode = SurfaceHeatmapMode.WallShear;
        TickHeatmap(heatmap);
        Screenshot(vehicle, "aero_demo_heatmap_shear.png");

        heatmap.enabled = false; // OnDisable must hand the originals back
        int restored = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var cur = targets[i].sharedMaterials;
            bool same = cur.Length == cachedByTest[i].Length;
            for (int j = 0; same && j < cur.Length; j++) same = cur[j] == cachedByTest[i][j];
            if (same) restored++;
        }
        _log.AppendLine($"restored {restored}/{targets.Count} renderers after disable");
        _log.AppendLine(painted > 0 && painted == targets.Count && restored == targets.Count
            ? "HEATMAP: PASS" : "HEATMAP: FAIL");
        Object.DestroyImmediate(heatmap);
    }

    // Density at reference points: the inlet probe the surface shader uses as
    // p_inf, and a point just above the roof (should read suction relative to it).
    static void LogRhoProbes(WindTunnelDomain domain)
    {
        var dims = domain.Dims;
        var tex = domain.Solver.VelocityField;
        Probe(tex, new Vector3Int(2, Mathf.RoundToInt(dims.y * 0.7f), dims.z / 2), "inlet ref");
        Probe(tex, new Vector3Int(dims.x / 2, Mathf.RoundToInt(dims.y * 0.35f), dims.z / 2), "above roof");
    }

    static void Probe(RenderTexture tex, Vector3Int p, string label)
    {
        AsyncGPUReadback.Request(tex, 0, p.x, 1, p.y, 1, p.z, 1, request =>
        {
            if (request.hasError) { _log.AppendLine($"rho probe {label}: readback error"); return; }
            var v = request.GetData<ushort>(); // ARGBHalf: 4 half floats
            float rho = Mathf.HalfToFloat(v[3]);
            _log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "rho probe {0} at {1}: rho {2:F5} (gauge Cp {3:F3})", label, p, rho,
                (rho - 1f) / 3f / (0.5f * 0.08f * 0.08f)));
        });
        AsyncGPUReadback.WaitAllRequests();
    }

    // No player loop in a blocking batch method, so drive the component directly.
    static void TickHeatmap(SurfaceHeatmap heatmap) =>
        typeof(SurfaceHeatmap).GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(heatmap, null);

    static void Screenshot(AeroVehicle vehicle, string path)
    {
        Bounds b = vehicle.ComputeBounds();
        var camGo = new GameObject("DemoCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.93f, 0.94f, 0.96f);
        float d = b.size.magnitude;
        // Nose faces -X: front three-quarter view from above.
        cam.transform.position = b.center + new Vector3(-0.9f, 0.55f, -0.9f).normalized * d * 1.1f;
        cam.transform.LookAt(b.center);
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = d * 5f;

        var rt = new RenderTexture(1600, 900, 24);
        var request = new RenderPipeline.StandardRequest();
        if (RenderPipeline.SupportsRenderRequest(cam, request))
        {
            request.destination = rt;
            RenderPipeline.SubmitRenderRequest(cam, request);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            _log.AppendLine($"screenshot {path} ({new FileInfo(path).Length / 1024} KB)");
            Object.DestroyImmediate(tex);
        }
        else
        {
            _log.AppendLine("screenshot: render request unsupported on this pipeline");
        }
        rt.Release();
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
    }

    static string HashUints(ComputeBuffer buf)
    {
        var data = new uint[buf.count];
        buf.GetData(data);
        uint xor = 0; ulong sum = 0;
        foreach (uint v in data) { xor ^= v; sum += v; }
        return $"xor {xor:X8} sum {sum}";
    }

    static string HashFloats(ComputeBuffer buf, int max = int.MaxValue)
    {
        int n = Math.Min(buf.count, max);
        var data = new float[n];
        buf.GetData(data, 0, 0, n);
        uint xor = 0; double sum = 0;
        foreach (float v in data)
        {
            xor ^= BitConverter.ToUInt32(BitConverter.GetBytes(v), 0);
            sum += v;
        }
        return $"xor {xor:X8} sum {sum:G17} (n={n})";
    }
}
