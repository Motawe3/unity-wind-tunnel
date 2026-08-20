// Stage 2.5a diagnostics — measure the missing physics instead of asserting it.
//
//   Unity.exe -batchmode -projectPath . -executeMethod AeroAhmedDiag.Run
//
// The improvement plan's whole ordering rests on one claim: the Ahmed benchmark is
// flat across slant angles because the near-wall physics is missing — the effective
// Reynolds number is far below the physical one and the flow separates at the nose
// regardless of what the tail looks like. This harness measures that claim on the
// 25° body (the angle where the real flow is partially attached on the slant):
//
//   1. WALE eddy viscosity ν_T per cell (new LbmSolver.CaptureNuT tap): domain mean,
//      near-body mean, and the implied effective Reynolds number.
//   2. Streamwise velocity in the first cells above the slant surface at stations
//      down the slant: attached flow has u_x > 0 hugging the wall; separated flow
//      has u_x ≤ 0 (reversed) or the shear layer detached from the surface.
//   3. First-cell height against a flat-plate boundary-layer estimate (y/δ).
//
// Physics values are REPORTED, not asserted — only harness mechanics can fail.
// Takes -aeroCells <n> to run at another resolution (default 536 = 15 mm).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Motawea.WindTunnel;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class AeroAhmedDiag
{
    const string ReportPath = "aero_ahmed_diag.txt";
    const float SpeedMs = 40f;
    const float SlantAngle = 25f;
    static readonly Vector3 TunnelSize = new Vector3(8.04f, 1.4f, 1.87f);
    const float UpstreamM = 2.0f;
    const float NuAirPhys = 1.5e-5f;                 // m²/s, for the δ estimate only

    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
    static StringBuilder _log;
    static int _passed, _failed;

    public static void Run() => EditorApplication.Exit(Execute());

    public static int Execute()
    {
        _log = new StringBuilder();
        _passed = _failed = 0;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSettings.asyncShaderCompilation = false;
            Diagnose();
        }
        catch (Exception e) { _log.AppendLine("EXCEPTION: " + e); _failed++; }

        _log.AppendLine();
        _log.AppendLine(_failed == 0 ? $"AHMED DIAG: PASS ({_passed} checks)"
                                     : $"AHMED DIAG: FAIL ({_failed} of {_passed + _failed} checks)");
        var cmdArgs = Environment.GetCommandLineArgs();
        string path = ReportPath;
        if (Array.IndexOf(cmdArgs, "-aeroWallModel") >= 0) path = path.Replace(".txt", "_wallmodel.txt");
        if (Array.IndexOf(cmdArgs, "-aeroLesCw") >= 0) path = path.Replace(".txt", "_cw.txt");
        File.WriteAllText(path, _log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void Diagnose()
    {
        int nx = 536;
        var args = Environment.GetCommandLineArgs();
        int ai = Array.IndexOf(args, "-aeroCells");
        if (ai >= 0 && ai + 1 < args.Length &&
            int.TryParse(args[ai + 1], NumberStyles.Integer, Ic, out int nxArg) && nxArg > 0)
            nx = nxArg;

        float dx = TunnelSize.x / nx;
        _log.AppendLine("AeroAhmedDiag — Stage 2.5a: measure the missing near-wall physics");
        _log.AppendLine(string.Format(Ic,
            "Ahmed {0:0}°, {1:F1} mm cells ({2} streamwise), {3:F0} m/s, soft voxels on, half-way walls{4}",
            SlantAngle, dx * 1000f, nx, SpeedMs,
            Array.IndexOf(args, "-aeroWallModel") >= 0 ? ", WALL MODEL ON" : ""));
        int cwi = Array.IndexOf(args, "-aeroLesCw");
        if (cwi >= 0) _log.AppendLine($"WALE Cw OVERRIDDEN to {args[cwi + 1]} (default 0.5) — SGS-magnitude experiment");
        _log.AppendLine();

        // ---- build the same case the benchmark runs ----
        var tunnelGo = new GameObject("Tunnel");
        tunnelGo.transform.position = new Vector3(0f, TunnelSize.y * 0.5f, 0f);
        var domain = tunnelGo.AddComponent<WindTunnelDomain>();
        domain.size = TunnelSize;
        domain.streamwiseCellsOverride = nx;
        domain.ground = GroundSimulation.FixedFloor;
        domain.rotatingWheels = false;
        domain.inletSpeedMs = SpeedMs;
        domain.stepsPerTick = 32;
        domain.sampleIntervalSteps = 100;
        domain.sealOpenModels = true;
        domain.softVoxels = true;
        domain.wallModel =
            Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroWallModel") >= 0;
        // -aeroLesCw <v>: override the WALE constant for SGS-magnitude experiments.
        // 2.5a measured bulk nu_T at ~400-900x molecular; nu_T scales with Cw², so this
        // is the direct knob for "is the SGS magnitude what keeps the flow separated?".
        int ci = Array.IndexOf(Environment.GetCommandLineArgs(), "-aeroLesCw");
        if (ci >= 0 && ci + 1 < Environment.GetCommandLineArgs().Length &&
            float.TryParse(Environment.GetCommandLineArgs()[ci + 1],
                NumberStyles.Float, Ic, out float cw))
            domain.lesCw = cw;
        domain.autoFit.fitAutomatically = false;

        var body = AeroAhmedBody.Create(SlantAngle);
        body.transform.position = new Vector3(
            -TunnelSize.x * 0.5f + UpstreamM + AeroAhmedBody.LengthM * 0.5f, 0f, 0f);
        domain.vehicle = body.GetComponent<AeroVehicle>();

        domain.StartSimulation();
        Check("simulation started", domain.Solver != null);
        if (domain.Solver == null) { Cleanup(body, tunnelGo); return; }

        var dims = domain.Solver.Dims;
        float uLat = domain.Units.ULattice;
        float nuLat = domain.Units.NuLattice;
        _log.AppendLine(string.Format(Ic,
            "grid {0}×{1}×{2}, U_lattice {3:F4}, nu_lattice {4:E2} (tau+ = {5:F7})",
            dims.x, dims.y, dims.z, uLat, nuLat, 3f * nuLat + 0.5f));
        _log.AppendLine();

        // ---- settle, averaging the display field, capture nuT over the last FT ----
        float ftSteps = domain.FlowThroughSteps;
        long settleSteps = (long)((AeroTestRunner.SettleFlowThroughs + 3f) * ftSteps);
        domain.Solver.DisplaySmoothing = 0.05f;   // strong EMA -> the snapshot is a real average
        while (domain.Solver.StepCount < settleSteps)
        {
            domain.Solver.CaptureNuT = settleSteps - domain.Solver.StepCount <= ftSteps;
            domain.Tick();
        }
        AsyncGPUReadback.WaitAllRequests();

        int cells = dims.x * dims.y * dims.z;
        var nuT = new float[cells];
        domain.Solver.NuTField.GetData(nuT);

        var vox = (VehicleVoxelizer)typeof(WindTunnelDomain)
            .GetField("_voxelizer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(domain);
        var flags = new uint[cells];
        vox.FlagsBuffer.GetData(flags);

        var vel = ReadVelocity(domain.Solver.VelocityField, dims);

        // ---- 1. effective viscosity / effective Reynolds number ----
        const uint SOLID = 1, WHEEL0 = 6;   // AeroCellType values (§1.3 invariant)
        bool IsBody(uint t) => t == SOLID || (t >= WHEEL0 && t <= WHEEL0 + 3);
        int Idx(int x, int y, int z) => x + dims.x * (y + dims.y * z);

        double sumAll = 0; long nAll = 0;
        double sumNear = 0; long nNear = 0;
        var fluidNuT = new List<float>(cells / 8);
        for (int z = 1; z < dims.z - 1; z++)
        for (int y = 1; y < dims.y - 1; y++)
        for (int x = 1; x < dims.x - 1; x++)
        {
            int i = Idx(x, y, z);
            if (flags[i] != 0) continue;              // fluid only
            sumAll += nuT[i]; nAll++;
            fluidNuT.Add(nuT[i]);
            if (IsBody(flags[Idx(x - 1, y, z)]) || IsBody(flags[Idx(x + 1, y, z)]) ||
                IsBody(flags[Idx(x, y - 1, z)]) || IsBody(flags[Idx(x, y + 1, z)]) ||
                IsBody(flags[Idx(x, y, z - 1)]) || IsBody(flags[Idx(x, y, z + 1)]))
            { sumNear += nuT[i]; nNear++; }
        }
        Check("nuT capture produced data", nAll > 0 && sumAll > 0, $"{nAll} fluid cells");

        fluidNuT.Sort();
        float median = fluidNuT.Count > 0 ? fluidNuT[fluidNuT.Count / 2] : 0f;
        double meanAll = sumAll / Math.Max(nAll, 1);
        double meanNear = sumNear / Math.Max(nNear, 1);

        float hLat = AeroAhmedBody.HeightM / dx;      // body height in cells
        double reMol = uLat * hLat / nuLat;
        double reEffMean = uLat * hLat / (nuLat + meanAll);
        double reEffNear = uLat * hLat / (nuLat + meanNear);

        _log.AppendLine("---- 1. effective viscosity (WALE nu_T, lattice units) ----");
        _log.AppendLine(string.Format(Ic, "  fluid cells {0:N0}, near-body fluid cells {1:N0}", nAll, nNear));
        _log.AppendLine(string.Format(Ic, "  nu_T mean {0:E2}   median {1:E2}   near-body mean {2:E2}", meanAll, median, meanNear));
        _log.AppendLine(string.Format(Ic, "  nu_T/nu_mol: mean {0:F0}x   median {1:F0}x   near-body {2:F0}x",
            meanAll / nuLat, median / nuLat, meanNear / nuLat));
        _log.AppendLine(string.Format(Ic, "  Re over body height: molecular {0:E2}   eff (mean nu_T) {1:E2}   eff (near-body) {2:E2}",
            reMol, reEffMean, reEffNear));
        _log.AppendLine(string.Format(Ic, "  physical Re over body height at {0:F0} m/s: {1:E2}",
            SpeedMs, SpeedMs * AeroAhmedBody.HeightM / NuAirPhys));
        _log.AppendLine();

        // ---- 2. slant attachment profile ----
        // Surface-hugging streamwise velocity at stations down the 25° slant. The wall
        // itself is found from the flags (topmost body cell in the column), not assumed.
        float tan = Mathf.Tan(SlantAngle * Mathf.Deg2Rad);
        float slantHoriz = AeroAhmedBody.SlantLengthM * Mathf.Cos(SlantAngle * Mathf.Deg2Rad);
        float tailX = body.transform.position.x + AeroAhmedBody.LengthM * 0.5f;
        float noseX = tailX - AeroAhmedBody.LengthM;
        float slantX0 = tailX - slantHoriz;
        float roofY = AeroAhmedBody.GroundClearanceM + AeroAhmedBody.HeightM;
        float minX = -TunnelSize.x * 0.5f;
        int zc = dims.z / 2;

        _log.AppendLine("---- 2. flow over the 25-degree slant (u_x / U_inf, cells above the found surface) ----");
        _log.AppendLine("  station      x_frac   surf_y    +1 cell   +2 cells  +3 cells  +5 cells  read");
        int separated = 0, stations = 0;
        for (int s = 0; s <= 8; s++)
        {
            float frac = s / 8f;
            float xw = Mathf.Lerp(slantX0 + dx, tailX - dx, frac);
            int ix = Mathf.Clamp(Mathf.RoundToInt((xw - minX) / dx - 0.5f), 1, dims.x - 2);
            float ySurfExpect = roofY - tan * (xw - slantX0);
            int iyStart = Mathf.Clamp(Mathf.RoundToInt((ySurfExpect + 6f * dx) / dx), 1, dims.y - 2);

            int iySurf = -1;
            for (int y = iyStart; y >= 1; y--)
                if (IsBody(flags[Idx(ix, y, zc)])) { iySurf = y; break; }
            if (iySurf < 0) { _log.AppendLine($"  {s}: no body surface found at x={xw:F3} — skipped"); continue; }

            float u1 = vel[Idx(ix, iySurf + 1, zc)].x / uLat;
            float u2 = vel[Idx(ix, iySurf + 2, zc)].x / uLat;
            float u3 = vel[Idx(ix, iySurf + 3, zc)].x / uLat;
            float u5 = iySurf + 5 < dims.y ? vel[Idx(ix, iySurf + 5, zc)].x / uLat : float.NaN;
            bool sep = u1 <= 0.02f && u2 <= 0.05f;
            stations++; if (sep) separated++;
            _log.AppendLine(string.Format(Ic,
                "  {0}/8       {1,5:F2}   {2,6:F3}   {3,7:F3}   {4,7:F3}   {5,7:F3}   {6,7:F3}   {7}",
                s, frac, (iySurf + 0.5f) * dx, u1, u2, u3, u5, sep ? "SEPARATED" : "attached-ish"));
        }
        Check("slant stations probed", stations >= 7, $"{stations} of 9");
        _log.AppendLine(string.Format(Ic, "  => {0} of {1} stations read as separated at the wall", separated, stations));
        _log.AppendLine();

        // ---- 3. first-cell height vs boundary-layer estimate ----
        float xRun = slantX0 - noseX;                          // development length to slant start
        float reX = SpeedMs * xRun / NuAirPhys;
        float delta = 0.37f * xRun / Mathf.Pow(reX, 0.2f);     // turbulent flat plate
        float yFirst = 0.5f * dx;
        _log.AppendLine("---- 3. first-cell height vs boundary layer ----");
        _log.AppendLine(string.Format(Ic,
            "  flat-plate turbulent delta at slant start (x_run {0:F2} m, Re_x {1:E2}): {2:F1} mm",
            xRun, reX, delta * 1000f));
        _log.AppendLine(string.Format(Ic,
            "  first fluid cell centre ~{0:F1} mm above the wall -> y/delta ~ {1:F2}",
            yFirst * 1000f, yFirst / delta));
        _log.AppendLine("  (wall-model validity per the plan: y/delta <~ 0.2)");

        Cleanup(body, tunnelGo);
    }

    static Vector3[] ReadVelocity(RenderTexture tex, Vector3Int dims)
    {
        var result = new Vector3[dims.x * dims.y * dims.z];
        var req = AsyncGPUReadback.Request(tex);
        req.WaitForCompletion();
        if (req.hasError) throw new InvalidOperationException("velocity readback failed");
        for (int z = 0; z < dims.z; z++)
        {
            var slice = req.GetData<ushort>(z);
            for (int i = 0; i < dims.x * dims.y; i++)
                result[z * dims.x * dims.y + i] = new Vector3(
                    Mathf.HalfToFloat(slice[i * 4 + 0]),
                    Mathf.HalfToFloat(slice[i * 4 + 1]),
                    Mathf.HalfToFloat(slice[i * 4 + 2]));
        }
        return result;
    }

    static void Cleanup(GameObject body, GameObject tunnelGo)
    {
        var domain = tunnelGo.GetComponent<WindTunnelDomain>();
        domain.ShutdownSimulation();
        Object.DestroyImmediate(body);
        Object.DestroyImmediate(tunnelGo);
    }

    static void Check(string what, bool condition, string detail = null)
    {
        if (condition) _passed++;
        else _failed++;
        _log.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {what}" +
                        (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
    }
}
