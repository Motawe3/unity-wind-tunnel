using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// The Ahmed body (Ahmed, Ramm &amp; Faltin, SAE 840300, 1984) — the standard
    /// automotive bluff-body benchmark, generated from its published dimensions.
    ///
    /// It is a deliberately featureless car: rounded nose, plain rectangular middle,
    /// and a rear slant whose angle is the one variable. Its value as a test is what
    /// happens as that angle steepens — drag climbs to a peak near 30° and then
    /// **collapses**, because the flow that had been clinging to the slant gives up
    /// and separates at the top edge instead.
    ///
    /// That cliff is the thing this solver is suspected of getting wrong. A flat plate
    /// and a cube separate at a sharp edge, so geometry decides it and they validate
    /// easily; here separation happens on a smooth surface and is decided by the
    /// boundary layer, which an interactive grid cannot resolve. Reproducing the drop
    /// is therefore a direct pass/fail on separation prediction.
    ///
    /// Built nose-first along local −X so it faces into a tunnel whose wind blows
    /// along +X, sitting on its stilts with the underbody at the published ground
    /// clearance.
    /// </summary>
    public static class AeroAhmedBody
    {
        // ---- published dimensions, metres ----
        public const float LengthM = 1.044f;
        public const float WidthM = 0.389f;
        public const float HeightM = 0.288f;
        public const float GroundClearanceM = 0.050f;
        public const float NoseRadiusM = 0.100f;
        /// <summary>Slant length measured ALONG the slanted surface, not horizontally.</summary>
        public const float SlantLengthM = 0.222f;
        public const float StiltDiameterM = 0.030f;

        /// <summary>
        /// The reference area every published Ahmed-body coefficient uses: the plain
        /// width × height of the box, excluding the stilts. Set this as the vehicle's
        /// reference-area override, or the measured silhouette (which includes the
        /// stilts and voxel dilation) will put the coefficients on a different basis
        /// than the numbers being compared against.
        /// </summary>
        public const float ReferenceAreaM2 = WidthM * HeightM;   // 0.11203 m²

        /// <summary>
        /// Approximate published drag coefficients against slant angle, for
        /// orientation. The exact values vary between the original paper and later
        /// re-measurements; what is not in dispute is the SHAPE of this curve — a rise
        /// to a peak just below 30° and a sharp drop after it. Check the source before
        /// quoting a number from here.
        /// </summary>
        public static float PublishedCd(float slantAngleDeg)
        {
            // 0°, 25°, 30°, 35° from Ahmed et al. (1984), linearly interpolated between.
            var angles = new[] { 0f, 25f, 30f, 35f, 40f };
            var cds = new[] { 0.250f, 0.285f, 0.378f, 0.260f, 0.256f };
            if (slantAngleDeg <= angles[0]) return cds[0];
            for (int i = 1; i < angles.Length; i++)
            {
                if (slantAngleDeg > angles[i]) continue;
                float t = Mathf.InverseLerp(angles[i - 1], angles[i], slantAngleDeg);
                return Mathf.Lerp(cds[i - 1], cds[i], t);
            }
            return cds[cds.Length - 1];
        }

        /// <summary>Horizontal extent and vertical drop of the slant at this angle.</summary>
        public static void SlantExtents(float slantAngleDeg, out float horizontal, out float drop)
        {
            float rad = slantAngleDeg * Mathf.Deg2Rad;
            horizontal = SlantLengthM * Mathf.Cos(rad);
            drop = SlantLengthM * Mathf.Sin(rad);
        }

        /// <summary>
        /// Builds a complete test vehicle: body mesh, stilts, and an AeroVehicle set up
        /// with the published reference area. The origin sits on the ground plane
        /// between the stilts, so placing it at the tunnel floor seats it correctly.
        /// </summary>
        public static GameObject Create(float slantAngleDeg, bool includeStilts = true)
        {
            var root = new GameObject($"Ahmed body {slantAngleDeg:0.#}°");

            var vehicle = root.AddComponent<AeroVehicle>();
            vehicle.displayName = $"Ahmed body {slantAngleDeg:0.#}°";
            // A bare validation shape: free of wheels, and scored on drag alone.
            vehicle.vehicleClass = AeroVehicleClass.ReferenceBody;
            // Published coefficients use width × height, so the measured silhouette
            // (stilts included, voxel-dilated) must not be the divisor.
            vehicle.referenceAreaMode = AeroReferenceAreaMode.Manual;
            vehicle.referenceAreaOverride = ReferenceAreaM2;

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = new Vector3(0f, GroundClearanceM, 0f);
            bodyGo.AddComponent<MeshFilter>().sharedMesh = BuildBodyMesh(slantAngleDeg);
            bodyGo.AddComponent<MeshRenderer>();

            if (includeStilts) AddStilts(root.transform);
            return root;
        }

        /// <summary>
        /// The body as a lofted set of cross-sections along its length: the nose
        /// section shrinks along a quarter-circle of the published radius, the middle
        /// is constant, and the roof descends over the slant. Local origin is the
        /// underbody, nose at −X.
        /// </summary>
        public static Mesh BuildBodyMesh(float slantAngleDeg, int noseSegments = 16)
        {
            SlantExtents(slantAngleDeg, out float slantHorizontal, out float slantDrop);
            float halfWidth = WidthM * 0.5f;
            float slantStart = LengthM - slantHorizontal;

            // ---- stations (s measured from the nose) ----
            var stations = new List<Vector4>();   // (s, halfWidth, yBottom, yTop)

            // Nose: inset = R − sqrt(2Rs − s²), the profile of a quarter circle. At the
            // tip this leaves a flat face of (W−2R) × (H−2R); by s = R the section is
            // full size. Rounding here is what stops the flow separating at the front —
            // a square-nosed box is a different experiment entirely.
            for (int i = 0; i <= noseSegments; i++)
            {
                float s = NoseRadiusM * i / noseSegments;
                float inset = NoseRadiusM - Mathf.Sqrt(Mathf.Max(2f * NoseRadiusM * s - s * s, 0f));
                stations.Add(new Vector4(s, halfWidth - inset, inset, HeightM - inset));
            }

            stations.Add(new Vector4(slantStart, halfWidth, 0f, HeightM));
            stations.Add(new Vector4(LengthM, halfWidth, 0f, HeightM - slantDrop));

            // ---- loft ----
            var verts = new List<Vector3>();
            var tris = new List<int>();

            for (int i = 0; i < stations.Count - 1; i++)
                AddRing(verts, tris, stations[i], stations[i + 1]);

            AddCap(verts, tris, stations[0], front: true);
            AddCap(verts, tris, stations[stations.Count - 1], front: false);

            var mesh = new Mesh { name = $"AhmedBody{slantAngleDeg:0}" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Analytic volume, for checking the voxelization against the geometry.</summary>
        public static float BodyVolumeM3(float slantAngleDeg, int noseSegments = 512)
        {
            SlantExtents(slantAngleDeg, out float slantHorizontal, out float slantDrop);
            float slantStart = LengthM - slantHorizontal;

            // Nose, by the same quarter-circle inset, integrated numerically.
            float volume = 0f;
            float step = NoseRadiusM / noseSegments;
            for (int i = 0; i < noseSegments; i++)
            {
                float s = (i + 0.5f) * step;
                float inset = NoseRadiusM - Mathf.Sqrt(Mathf.Max(2f * NoseRadiusM * s - s * s, 0f));
                volume += (WidthM - 2f * inset) * (HeightM - 2f * inset) * step;
            }

            volume += WidthM * HeightM * (slantStart - NoseRadiusM);             // middle
            volume += WidthM * (HeightM - slantDrop * 0.5f) * slantHorizontal;   // slant wedge
            return volume;
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Local X runs nose (−) to tail (+): station s maps to x = s − L/2, so the
        /// body faces into a tunnel whose wind blows along +X.
        /// </summary>
        static float LocalX(float s) => s - LengthM * 0.5f;

        static void AddRing(List<Vector3> verts, List<int> tris, Vector4 a, Vector4 b)
        {
            float xa = LocalX(a.x), xb = LocalX(b.x);
            float wa = a.y, wb = b.y;
            float ba = a.z, bb = b.z;
            float ta = a.w, tb = b.w;

            // Corner order per station: bottom-left, bottom-right, top-right, top-left,
            // looking downstream (+X), with +Z to the left.
            Vector3 a0 = new Vector3(xa, ba, -wa), a1 = new Vector3(xa, ba, wa);
            Vector3 a2 = new Vector3(xa, ta, wa), a3 = new Vector3(xa, ta, -wa);
            Vector3 b0 = new Vector3(xb, bb, -wb), b1 = new Vector3(xb, bb, wb);
            Vector3 b2 = new Vector3(xb, tb, wb), b3 = new Vector3(xb, tb, -wb);

            Quad(verts, tris, a0, b0, b1, a1);   // underbody  (normal −Y)
            Quad(verts, tris, a3, a2, b2, b3);   // roof / slant (normal +Y)
            Quad(verts, tris, a1, b1, b2, a2);   // +Z flank
            Quad(verts, tris, a0, a3, b3, b0);   // −Z flank
        }

        static void AddCap(List<Vector3> verts, List<int> tris, Vector4 station, bool front)
        {
            float x = LocalX(station.x), w = station.y, yb = station.z, yt = station.w;
            Vector3 p0 = new Vector3(x, yb, -w), p1 = new Vector3(x, yb, w);
            Vector3 p2 = new Vector3(x, yt, w), p3 = new Vector3(x, yt, -w);
            if (front) Quad(verts, tris, p0, p1, p2, p3);
            else Quad(verts, tris, p0, p3, p2, p1);
        }

        static void Quad(List<Vector3> verts, List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        /// <summary>
        /// Four cylindrical supports from the underbody to the ground. Their exact
        /// stations differ slightly between published setups and their effect on Cd is
        /// small (order 0.01); these are placed symmetrically at representative
        /// positions.
        /// </summary>
        static void AddStilts(Transform parent)
        {
            float halfSpan = WidthM * 0.5f - 0.075f;
            foreach (float s in new[] { 0.202f, 0.734f })
            foreach (float z in new[] { -halfSpan, halfSpan })
            {
                var stilt = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                stilt.name = "Stilt";
                stilt.transform.SetParent(parent, false);
                // Unity's cylinder is 2 units tall and 1 across, centred on its origin,
                // so a half-clearance Y scale spans exactly the ground gap.
                stilt.transform.localPosition = new Vector3(LocalX(s), GroundClearanceM * 0.5f, z);
                stilt.transform.localScale = new Vector3(StiltDiameterM, GroundClearanceM * 0.5f, StiltDiameterM);
                var collider = stilt.GetComponent<Collider>();
                if (collider != null) Object.DestroyImmediate(collider);
            }
        }
    }
}
