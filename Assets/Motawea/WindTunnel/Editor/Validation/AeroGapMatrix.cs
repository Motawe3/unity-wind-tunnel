// Temporary diagnostic (run via -executeMethod AeroGapMatrix.Run in batch mode):
// sweeps the gap between two slabs from 0.25 to 3 cells and measures how much
// flow the solver actually passes through, with soft voxels on and off.
using System;
using System.Text;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Motawea.WindTunnel;
using Object = UnityEngine.Object;

public static class AeroGapMatrix
{
    const string ReportPath = "aero_gap_matrix.txt";

    // Tunnel: 8x4x4 m at Coarse(128) -> dx = 62.5 mm, 128x64x64 = 524k cells.
    static readonly Vector3 TunnelSize = new Vector3(8, 4, 4);
    const float Dx = 8f / 128f;
    const float SlabThickness = 6 * Dx;   // hard-solid core guaranteed
    const float SlabChord = 8 * Dx;       // channel length in X
    const float SlabSpan = 2f;            // Z extent

    static readonly float[] GapCells = { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };

    static Vector3Int _dims;
    static float[] _mask;
    static float[] _vel;

    public static void Run()
    {
        var log = new StringBuilder();
        int exitCode = 0;
        try
        {
            Execute(log);
        }
        catch (Exception e)
        {
            log.AppendLine("EXCEPTION: " + e);
            exitCode = 1;
        }
        File.WriteAllText(ReportPath, log.ToString());
        EditorApplication.Exit(exitCode);
    }

    static void Execute(StringBuilder log)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var domainGo = new GameObject("Tunnel");
        var domain = domainGo.AddComponent<WindTunnelDomain>();
        // This harness owns its tunnel: the numbers below are only comparable
        // against past runs if the domain stays exactly as authored here.
        domain.autoFit.fitAutomatically = false;
        domain.size = TunnelSize;
        domain.resolution = TunnelResolution.Coarse;
        domain.ground = GroundSimulation.OpenFloor;
        domain.inletSpeedMs = 30f;
        domain.stepsPerTick = 32;
        domain.sealOpenModels = true;

        var rig = new GameObject("GapRig");
        rig.AddComponent<AeroVehicle>();
        var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var bottom = GameObject.CreatePrimitive(PrimitiveType.Cube);
        top.transform.SetParent(rig.transform);
        bottom.transform.SetParent(rig.transform);
        top.transform.localScale = new Vector3(SlabChord, SlabThickness, SlabSpan);
        bottom.transform.localScale = new Vector3(SlabChord, SlabThickness, SlabSpan);
        domain.vehicle = rig.GetComponent<AeroVehicle>();

        log.AppendLine($"Tunnel {TunnelSize} @128 -> dx={Dx * 1000f:F1}mm; slabs chord={SlabChord:F2}m thick={SlabThickness:F2}m span={SlabSpan}m; 1.5 flow-throughs per case");
        log.AppendLine("fluxRatio = (mask-weighted ux flux through gap band) / (freestream * ideal open gap area); poreU = mean in-gap velocity / freestream");
        log.AppendLine();
        log.AppendLine("soft  align     gap(dx)  gap(mm)  openCells partialCells  fluxRatio  poreU  maxUx");

        foreach (bool soft in new[] { true, false })
        foreach (float phase in new[] { 0.5f, 0f })   // gap center at cell center / cell boundary
        foreach (float f in GapCells)
        {
            float gap = f * Dx;
            // Lattice y origin is -TunnelSize.y/2; cell centers at origin+(i+0.5)*dx.
            float gapCenterY = -TunnelSize.y * 0.5f + (32 + phase) * Dx;
            top.transform.position = new Vector3(0, gapCenterY + 0.5f * (gap + SlabThickness), 0);
            bottom.transform.position = new Vector3(0, gapCenterY - 0.5f * (gap + SlabThickness), 0);

            domain.softVoxels = soft;
            domain.StartSimulation();
            if (domain.Solver == null) { log.AppendLine("StartSimulation failed"); return; }
            _dims = domain.Dims;

            int ticks = Mathf.CeilToInt(1.5f * domain.FlowThroughSteps / domain.stepsPerTick);
            for (int i = 0; i < ticks; i++) domain.Tick();

            ReadBack(domain.Solver);
            Measure(domain, gapCenterY, gap, out int open, out int partial,
                    out float fluxRatio, out float poreU, out float maxUx);
            log.AppendLine($"{(soft ? "ON " : "OFF")}   {(phase > 0 ? "center  " : "boundary")}  {f,5:F2}   {gap * 1000f,6:F0}   {open,6}    {partial,6}       {fluxRatio,6:F2}   {poreU,5:F2}  {maxUx,5:F2}");
        }
        log.AppendLine();
        log.AppendLine("Done.");
    }

    static void Measure(WindTunnelDomain domain, float gapCenterY, float gap,
                        out int open, out int partial, out float fluxRatio, out float poreU, out float maxUx)
    {
        float uInf = domain.Units.ULattice;
        // Sample column at mid-chord (world x=0 -> lattice x=64), central z band.
        int ix = 64;
        int z0 = 32 - 10, z1 = 32 + 10;
        // Gap band: cell centers within the open slot +- 0.6 cells (captures partials).
        float yLo = (gapCenterY - 0.5f * gap + TunnelSize.y * 0.5f) / Dx - 0.6f;
        float yHi = (gapCenterY + 0.5f * gap + TunnelSize.y * 0.5f) / Dx + 0.6f;

        open = 0; partial = 0;
        float flux = 0f; maxUx = 0f;
        float openSum = 0f;
        int cols = 0;
        for (int z = z0; z <= z1; z++)
        {
            cols++;
            for (int y = Mathf.Max(Mathf.FloorToInt(yLo), 0); y <= Mathf.Min(Mathf.CeilToInt(yHi), _dims.y - 1); y++)
            {
                float cy = y + 0.5f;
                if (cy < yLo || cy > yHi) continue;
                float m = MaskAt(ix, y, z);
                float ux = VelAt(ix, y, z).x;
                if (z == 32)   // count cell states once, on the center column
                {
                    if (m >= 0.999f) open++;
                    else if (m > 0.001f) partial++;
                }
                flux += ux * m;
                openSum += m;
                maxUx = Mathf.Max(maxUx, ux / uInf);
            }
        }
        float idealFlux = uInf * (gap / Dx) * cols;   // fully open slot, plug flow
        fluxRatio = idealFlux > 0f ? flux / idealFlux : 0f;
        poreU = openSum > 0.01f ? flux / (openSum * uInf) : 0f;
    }

    static void ReadBack(LbmSolver solver)
    {
        var mReq = AsyncGPUReadback.Request(solver.FluidMask, 0, GraphicsFormat.R32_SFloat);
        var vReq = AsyncGPUReadback.Request(solver.VelocityField, 0, GraphicsFormat.R32G32B32A32_SFloat);
        mReq.WaitForCompletion();
        vReq.WaitForCompletion();
        if (mReq.hasError || vReq.hasError) throw new Exception("GPU readback failed");

        int nx = _dims.x, ny = _dims.y, nz = _dims.z;
        _mask = new float[nx * ny * nz];
        _vel = new float[nx * ny * nz * 4];
        var mSlice = new float[nx * ny];
        var vSlice = new float[nx * ny * 4];
        for (int z = 0; z < nz; z++)
        {
            mReq.GetData<float>(z).CopyTo(mSlice);
            Array.Copy(mSlice, 0, _mask, z * nx * ny, nx * ny);
            vReq.GetData<float>(z).CopyTo(vSlice);
            Array.Copy(vSlice, 0, _vel, z * nx * ny * 4, nx * ny * 4);
        }
    }

    static float MaskAt(int x, int y, int z) => _mask[x + _dims.x * (y + _dims.y * z)];
    static Vector3 VelAt(int x, int y, int z)
    {
        int i = (x + _dims.x * (y + _dims.y * z)) * 4;
        return new Vector3(_vel[i], _vel[i + 1], _vel[i + 2]);
    }
}
