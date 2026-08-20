using UnityEditor;
using UnityEngine;

namespace Motawea.WindTunnel.Editor
{
    /// <summary>Scene setup helpers: one-click tunnel rig and a primitive test vehicle.</summary>
    public static class WindTunnelMenu
    {
        [MenuItem("GameObject/Wind Tunnel/Tunnel", false, 10)]
        public static void CreateWindTunnel()
        {
            var go = new GameObject("Wind Tunnel");
            Undo.RegisterCreatedObjectUndo(go, "Create Wind Tunnel");

            var domain = go.AddComponent<WindTunnelDomain>();
            var runner = go.AddComponent<AeroTestRunner>();
            runner.tunnel = domain;

            // Smoke rake just downstream of the inlet, vehicle-height.
            var rakeGo = new GameObject("Smoke Rake");
            rakeGo.transform.SetParent(go.transform, false);
            rakeGo.transform.localPosition = new Vector3(-domain.size.x * 0.45f, -domain.size.y * 0.5f + 1.2f, 0f);
            var rake = rakeGo.AddComponent<FlowParticles>();
            rake.tunnel = domain;

            // Vertical centerline slice.
            var sliceGo = new GameObject("Flow Slice");
            sliceGo.transform.SetParent(go.transform, false);
            sliceGo.transform.localRotation = Quaternion.Euler(0f, 90f, 0f); // plane normal = lateral
            sliceGo.transform.localScale = new Vector3(domain.size.x, domain.size.y, 1f);
            var slice = sliceGo.AddComponent<FlowSlice>();
            slice.tunnel = domain;

            // Vehicle-surface heatmap, off until picked from the dashboard.
            var heatmap = go.AddComponent<SurfaceHeatmap>();
            heatmap.tunnel = domain;
            heatmap.enabled = false;

            var vehicle = Object.FindFirstObjectByType<AeroVehicle>();
            if (vehicle != null)
            {
                domain.vehicle = vehicle;
                // Size and seat everything from the vehicle rather than from the
                // component defaults, and queue the procedures its class calls for.
                domain.FitToVehicle();
                runner.testQueue.AddRange(
                    AeroTestDefinition.StandardQueue(vehicle.vehicleClass, vehicle.watercraftMode));
            }
            else
            {
                runner.testQueue.AddRange(
                    AeroTestDefinition.StandardQueue(AeroVehicleClass.RoadVehicle, WatercraftMode.AboveWaterlineAir));
            }

            Selection.activeGameObject = go;
            AeroDashboardWindow.Open();
        }

        /// <summary>
        /// The standard automotive bluff-body benchmark, at the slant angle whose
        /// published drag is best known. Comes with the published reference area set,
        /// so its coefficients are on the same basis as the literature.
        /// </summary>
        [MenuItem("GameObject/Wind Tunnel/Ahmed Body (25° benchmark)", false, 12)]
        public static void CreateAhmedBody()
        {
            var go = AeroAhmedBody.Create(25f);
            Undo.RegisterCreatedObjectUndo(go, "Create Ahmed Body");

            var domain = Object.FindFirstObjectByType<WindTunnelDomain>();
            if (domain != null && domain.vehicle == null)
                domain.vehicle = go.GetComponent<AeroVehicle>();

            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/Wind Tunnel/Sample Test Vehicle", false, 11)]
        public static void CreateSampleVehicle()
        {
            var root = new GameObject("Sample Vehicle");
            Undo.RegisterCreatedObjectUndo(root, "Create Sample Vehicle");
            var vehicle = root.AddComponent<AeroVehicle>();

            // Simple hatchback out of primitives, nose facing -X (upstream).
            AddBox(root, "Body", new Vector3(0f, 0.55f, 0f), new Vector3(4.2f, 0.75f, 1.8f));
            AddBox(root, "Cabin", new Vector3(0.35f, 1.2f, 0f), new Vector3(2.2f, 0.62f, 1.7f));
            AddBox(root, "Hood Slope", new Vector3(-1.55f, 0.98f, 0f), new Vector3(1.1f, 0.28f, 1.72f));

            const float wheelR = 0.32f, wheelW = 0.24f;
            AddWheel(root, "Wheel FL", new Vector3(-1.35f, wheelR, 0.78f), wheelR, wheelW);
            AddWheel(root, "Wheel FR", new Vector3(-1.35f, wheelR, -0.78f), wheelR, wheelW);
            AddWheel(root, "Wheel RL", new Vector3(1.35f, wheelR, 0.78f), wheelR, wheelW);
            AddWheel(root, "Wheel RR", new Vector3(1.35f, wheelR, -0.78f), wheelR, wheelW);

            vehicle.RefreshWheels();

            var domain = Object.FindFirstObjectByType<WindTunnelDomain>();
            if (domain != null && domain.vehicle == null)
                domain.vehicle = vehicle;

            Selection.activeGameObject = root;
        }

        static void AddBox(GameObject parent, string name, Vector3 localPos, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = localPos;
            box.transform.localScale = size;
        }

        static void AddWheel(GameObject parent, string name, Vector3 localPos, float radius, float width)
        {
            var wheelRoot = new GameObject(name);
            wheelRoot.transform.SetParent(parent.transform, false);
            wheelRoot.transform.localPosition = localPos;
            // Spin axis (local right) must point laterally.
            wheelRoot.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

            var wheel = wheelRoot.AddComponent<AeroWheel>();
            wheel.radius = radius;
            wheel.width = width;

            var mesh = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            mesh.name = "Tire";
            mesh.transform.SetParent(wheelRoot.transform, false);
            // Cylinder height axis (local Y) aligned with the wheel's spin axis (local X).
            mesh.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            mesh.transform.localScale = new Vector3(radius * 2f, width * 0.5f, radius * 2f);
        }
    }
}
