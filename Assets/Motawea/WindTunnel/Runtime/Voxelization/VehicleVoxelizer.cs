using System;
using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>Per-wheel data converted to lattice space, shared by voxelizer and solver.</summary>
    public struct WheelLatticeData
    {
        public Vector4[] Positions;   // xyz center, w radius (lattice)
        public Vector4[] Axes;        // xyz unit spin axis, w half-width (lattice)
        public int Count;

        public static WheelLatticeData Build(AeroVehicle vehicle, Matrix4x4 worldToLattice, float dx)
        {
            var data = new WheelLatticeData
            {
                Positions = new Vector4[4],
                Axes = new Vector4[4],
                Count = 0
            };
            if (vehicle == null) return data;

            vehicle.RefreshWheels();
            foreach (var wheel in vehicle.Wheels)
            {
                if (data.Count >= 4) break;
                if (wheel == null || !wheel.isActiveAndEnabled) continue;

                Vector3 p = worldToLattice.MultiplyPoint3x4(wheel.Center);
                Vector3 axis = worldToLattice.MultiplyVector(wheel.Axis).normalized;
                // Positive spin about the axis must move the contact patch downstream
                // (+X, with the belt): u_bottom.x = axis.z, so keep axis.z positive.
                if (axis.z < 0f) axis = -axis;
                data.Positions[data.Count] = new Vector4(p.x, p.y, p.z, wheel.EffectiveRadius / dx);
                data.Axes[data.Count] = new Vector4(axis.x, axis.y, axis.z, 0.5f * wheel.EffectiveWidth / dx);
                data.Count++;
            }
            return data;
        }
    }

    /// <summary>
    /// GPU voxelization of the vehicle into the tunnel grid: conservative surface
    /// rasterization followed by an outside flood fill, so imperfect meshes still
    /// produce a closed solid. Also measures the frontal (silhouette) area.
    /// </summary>
    public class VehicleVoxelizer : IDisposable
    {
        readonly ComputeShader _cs;
        readonly int _kClear, _kRaster, _kSeal, _kVetoX, _kVetoZ, _kSeed, _kFlood,
                     _kCoverage, _kPorousSeed, _kPorousSpread, _kFinalize, _kStats, _kStatsPlanform;

        public ComputeBuffer FlagsBuffer { get; private set; }
        /// <summary>Solid fraction per cell: 0 fluid, 1 solid, in-between = soft voxel
        /// (partially open cell the solver drags via gray-LBM bounce-back).</summary>
        public ComputeBuffer CoverageBuffer { get; private set; }
        /// <summary>
        /// Raw sub-cell solid fraction per cell, which survives Finalize. CoverageBuffer
        /// is overwritten with 1.0 on every solid cell, destroying the record of where
        /// inside that cell the surface actually lies; interpolated bounce-back needs
        /// exactly that, so it is kept here.
        /// </summary>
        public ComputeBuffer SurfaceFractionBuffer { get; private set; }
        public Vector3Int Dims { get; private set; }
        /// <summary>Silhouette projected along the wind axis, m² (automotive reference area).</summary>
        public float FrontalAreaM2 { get; private set; }
        /// <summary>Silhouette projected from above, m² (aeronautical wing-area reference).</summary>
        public float PlanformAreaM2 { get; private set; }
        public int SolidCellCount { get; private set; }

        /// <summary>_Stats slots: [0] frontal columns, [1] solid cells, [2] planform columns.</summary>
        const int StatsSlots = 3;

        ComputeBuffer _statsBuffer;
        ComputeBuffer _triangleBuffer;
        ComputeBuffer _subMaskBuffer;

        public VehicleVoxelizer()
        {
            _cs = Resources.Load<ComputeShader>("WindTunnel/Voxelize");
            if (_cs == null)
                throw new InvalidOperationException("Wind Tunnel: missing Resources/WindTunnel/Voxelize.compute");
            _kClear = _cs.FindKernel("Clear");
            _kRaster = _cs.FindKernel("RasterizeTriangles");
            _kSeal = _cs.FindKernel("ColumnSeal");
            _kVetoX = _cs.FindKernel("SealVetoX");
            _kVetoZ = _cs.FindKernel("SealVetoZ");
            _kSeed = _cs.FindKernel("SeedOutside");
            _kFlood = _cs.FindKernel("FloodStep");
            _kCoverage = _cs.FindKernel("ComputeCoverage");
            _kPorousSeed = _cs.FindKernel("PorousSeed");
            _kPorousSpread = _cs.FindKernel("PorousSpread");
            _kFinalize = _cs.FindKernel("Finalize");
            _kStats = _cs.FindKernel("ComputeStats");
            _kStatsPlanform = _cs.FindKernel("ComputeStatsPlanform");
        }

        /// <summary>Dispatches a 64-thread linear kernel, folding onto a 2D grid past the
        /// 65535-group D3D limit (hit above ~4.2M items, e.g. Ultra/Extreme tunnels).</summary>
        internal static void DispatchLinear(ComputeShader cs, int kernel, int count)
        {
            int groups = Mathf.CeilToInt(count / 64f);
            int gx = Mathf.Min(groups, 65535);
            int gy = Mathf.CeilToInt(groups / (float)gx);
            cs.SetInt("_DispatchWidth", gx * 64);
            cs.Dispatch(kernel, gx, gy, 1);
        }

        public void Voxelize(AeroVehicle vehicle, Vector3Int dims, Matrix4x4 worldToLattice,
                             float dx, bool groundIsWall, in WheelLatticeData wheels,
                             bool sealOpenModels = true, bool softVoxels = true,
                             bool subCellWalls = false)
        {
            Dims = dims;
            int cellCount = dims.x * dims.y * dims.z;

            if (FlagsBuffer == null || FlagsBuffer.count != cellCount)
            {
                FlagsBuffer?.Release();
                FlagsBuffer = new ComputeBuffer(cellCount, sizeof(uint));
                CoverageBuffer?.Release();
                CoverageBuffer = new ComputeBuffer(cellCount, sizeof(float));
                SurfaceFractionBuffer?.Release();
                SurfaceFractionBuffer = new ComputeBuffer(cellCount, sizeof(float));
                _subMaskBuffer?.Release();
                _subMaskBuffer = new ComputeBuffer(cellCount, sizeof(uint));
            }
            _statsBuffer ??= new ComputeBuffer(StatsSlots, sizeof(int));

            var triangles = CollectTriangles(vehicle, worldToLattice, dims);
            int triCount = triangles.Count / 3;
            if (triCount == 0)
                Debug.LogWarning("Wind Tunnel: no readable mesh triangles found under the AeroVehicle root.");

            if (_triangleBuffer == null || _triangleBuffer.count < Mathf.Max(triangles.Count, 3))
            {
                _triangleBuffer?.Release();
                _triangleBuffer = new ComputeBuffer(Mathf.Max(triangles.Count, 3), sizeof(float) * 3);
            }
            if (triangles.Count > 0)
                _triangleBuffer.SetData(triangles);

            _cs.SetInts("_Dims", dims.x, dims.y, dims.z);
            _cs.SetInt("_TriangleCount", triCount);
            _cs.SetInt("_GroundIsWall", groundIsWall ? 1 : 0);
            _cs.SetInt("_SealColumns", sealOpenModels ? 1 : 0);
            _cs.SetInt("_ContactFillCells", sealOpenModels && groundIsWall ? 2 : 0);
            _cs.SetInt("_SoftVoxels", softVoxels ? 1 : 0);
            // The 3x3x3 sub-raster is what produces sub-cell fractions. Soft voxels need
            // it; so does interpolated bounce-back, even with soft voxels off.
            _cs.SetInt("_SubCellRaster", softVoxels || subCellWalls ? 1 : 0);
            _cs.SetInt("_WheelCount", wheels.Count);
            _cs.SetVectorArray("_WheelPos", wheels.Positions ?? new Vector4[4]);
            _cs.SetVectorArray("_WheelAxis", wheels.Axes ?? new Vector4[4]);

            _cs.SetBuffer(_kClear, "_Flags", FlagsBuffer);
            _cs.SetBuffer(_kClear, "_SubMask", _subMaskBuffer);
            _cs.SetBuffer(_kClear, "_SurfaceFraction", SurfaceFractionBuffer);
            DispatchLinear(_cs, _kClear, cellCount);

            if (triCount > 0)
            {
                _cs.SetBuffer(_kRaster, "_Flags", FlagsBuffer);
                _cs.SetBuffer(_kRaster, "_SubMask", _subMaskBuffer);
                _cs.SetBuffer(_kRaster, "_Triangles", _triangleBuffer);
                DispatchLinear(_cs, _kRaster, triCount);
            }

            if (sealOpenModels)
            {
                _cs.SetBuffer(_kSeal, "_Flags", FlagsBuffer);
                _cs.Dispatch(_kSeal, Mathf.CeilToInt(dims.x / 8f), Mathf.CeilToInt(dims.z / 8f), 1);

                // Unseal real air gaps the column seal plugged (deck-to-wing, under-tail):
                // sealed cells with a straight surface-free sight line out of the domain.
                _cs.SetBuffer(_kVetoX, "_Flags", FlagsBuffer);
                _cs.Dispatch(_kVetoX, Mathf.CeilToInt(dims.y / 8f), Mathf.CeilToInt(dims.z / 8f), 1);
                _cs.SetBuffer(_kVetoZ, "_Flags", FlagsBuffer);
                _cs.Dispatch(_kVetoZ, Mathf.CeilToInt(dims.x / 8f), Mathf.CeilToInt(dims.y / 8f), 1);
            }

            _cs.SetBuffer(_kSeed, "_Flags", FlagsBuffer);
            DispatchLinear(_cs, _kSeed, cellCount);

            _cs.SetBuffer(_kFlood, "_Flags", FlagsBuffer);
            int floodIterations = dims.x + dims.y + dims.z;
            for (int i = 0; i < floodIterations; i++)
                DispatchLinear(_cs, _kFlood, cellCount);

            _cs.SetBuffer(_kCoverage, "_Flags", FlagsBuffer);
            _cs.SetBuffer(_kCoverage, "_SubMask", _subMaskBuffer);
            _cs.SetBuffer(_kCoverage, "_Coverage", CoverageBuffer);
            _cs.SetBuffer(_kCoverage, "_SurfaceFraction", SurfaceFractionBuffer);
            DispatchLinear(_cs, _kCoverage, cellCount);

            if (softVoxels)
            {
                // Mark partial surface cells reachable from open air; slot gaps are
                // short contiguous runs, so a few dozen spread steps saturate them.
                _cs.SetBuffer(_kPorousSeed, "_Flags", FlagsBuffer);
                _cs.SetBuffer(_kPorousSeed, "_Coverage", CoverageBuffer);
                DispatchLinear(_cs, _kPorousSeed, cellCount);

                _cs.SetBuffer(_kPorousSpread, "_Flags", FlagsBuffer);
                _cs.SetBuffer(_kPorousSpread, "_Coverage", CoverageBuffer);
                for (int i = 0; i < 32; i++)
                    DispatchLinear(_cs, _kPorousSpread, cellCount);
            }

            _cs.SetBuffer(_kFinalize, "_Flags", FlagsBuffer);
            _cs.SetBuffer(_kFinalize, "_Coverage", CoverageBuffer);
            DispatchLinear(_cs, _kFinalize, cellCount);

            _statsBuffer.SetData(new int[StatsSlots]);
            _cs.SetBuffer(_kStats, "_Flags", FlagsBuffer);
            _cs.SetBuffer(_kStats, "_Coverage", CoverageBuffer);
            _cs.SetBuffer(_kStats, "_Stats", _statsBuffer);
            _cs.Dispatch(_kStats, Mathf.CeilToInt(dims.y / 8f), Mathf.CeilToInt(dims.z / 8f), 1);

            _cs.SetBuffer(_kStatsPlanform, "_Flags", FlagsBuffer);
            _cs.SetBuffer(_kStatsPlanform, "_Coverage", CoverageBuffer);
            _cs.SetBuffer(_kStatsPlanform, "_Stats", _statsBuffer);
            _cs.Dispatch(_kStatsPlanform, Mathf.CeilToInt(dims.x / 8f), Mathf.CeilToInt(dims.z / 8f), 1);

            var stats = new int[StatsSlots];
            _statsBuffer.GetData(stats);
            FrontalAreaM2 = stats[0] * dx * dx;
            SolidCellCount = stats[1];
            PlanformAreaM2 = stats[2] * dx * dx;
        }

        static List<Vector3> CollectTriangles(AeroVehicle vehicle, Matrix4x4 worldToLattice, Vector3Int dims)
        {
            var result = new List<Vector3>();
            if (vehicle == null) return result;

            var filters = vehicle.GetComponentsInChildren<MeshFilter>();
            var verts = new List<Vector3>();
            var indices = new List<int>();

            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;

                if (mf.GetComponentInParent<AeroIgnore>() != null)
                    continue;

                if (!mesh.isReadable && Application.isPlaying)
                {
                    Debug.LogWarning($"Wind Tunnel: mesh '{mesh.name}' is not Read/Write enabled; skipped at runtime.", mf);
                    continue;
                }

                Matrix4x4 toLattice = worldToLattice * mf.transform.localToWorldMatrix;

                // Imported scenes often smuggle in studio floors/backdrops. Geometry
                // spanning most of the tunnel footprint corrupts every force number
                // (a floor plane reads as ~100x the car's lift area).
                Bounds wb = mf.TryGetComponent<Renderer>(out var rend) ? rend.bounds : default;
                if (rend != null)
                {
                    Vector3 lb = worldToLattice.MultiplyVector(wb.size);
                    if (Mathf.Abs(lb.x) > 0.6f * dims.x && Mathf.Abs(lb.z) > 0.6f * dims.z)
                        Debug.LogWarning(
                            $"Wind Tunnel: mesh '{mesh.name}' spans most of the tunnel footprint — it looks like " +
                            "environment geometry (floor/backdrop), which invalidates all force results. " +
                            "Add an AeroIgnore component to it (or move it out of the AeroVehicle hierarchy).", mf);
                }
                verts.Clear();
                mesh.GetVertices(verts);

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    indices.Clear();
                    mesh.GetTriangles(indices, sub);
                    for (int i = 0; i < indices.Count; i++)
                        result.Add(toLattice.MultiplyPoint3x4(verts[indices[i]]));
                }
            }
            return result;
        }

        public void Dispose()
        {
            FlagsBuffer?.Release(); FlagsBuffer = null;
            CoverageBuffer?.Release(); CoverageBuffer = null;
            SurfaceFractionBuffer?.Release(); SurfaceFractionBuffer = null;
            _subMaskBuffer?.Release(); _subMaskBuffer = null;
            _statsBuffer?.Release(); _statsBuffer = null;
            _triangleBuffer?.Release(); _triangleBuffer = null;
        }
    }
}
