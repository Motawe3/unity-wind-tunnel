using System.Collections.Generic;
using Motawea.WindTunnel;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Spawns one of several car prefabs into the wind tunnel and rebuilds the
/// simulation around it.
///
/// Swapping a car is more than an Instantiate: the tunnel holds a direct reference
/// to the AeroVehicle it voxelized, so that reference has to be re-pointed and the
/// solver restarted. Otherwise the tunnel keeps pushing air through the voxels of
/// the car that is no longer on screen.
///
/// Number keys 1..9 select a car.
/// </summary>
[AddComponentMenu("Wind Tunnel/Samples/Car Spawner")]
public class CarSpawner : MonoBehaviour
{
    [Tooltip("Car prefabs in hotkey order — key 1 spawns the first. Each root should carry an AeroVehicle; one is added automatically if missing.")]
    [SerializeField] private List<GameObject> cars = new List<GameObject>();

    [Tooltip("Tunnel the spawned car is tested in. Auto-found when empty.")]
    [SerializeField] private WindTunnelDomain tunnel;

    [Tooltip("Aborted before a swap so a running sweep never attributes its results to the wrong car. Auto-found when empty.")]
    [SerializeField] private AeroTestRunner runner;

    [Tooltip("Car spawned on Start. Negative leaves the tunnel empty.")]
    [SerializeField] private int spawnOnStart;

    [Tooltip("Restart the solver after each spawn so the new car is voxelized immediately. Off leaves the tunnel idle until you start it yourself.")]
    [SerializeField] private bool autoStartSimulation = true;

    [Tooltip("Legacy fallback: re-park the car on the tunnel floor, centred in the tunnel. Only used when the tunnel's auto-fit is switched off — the auto-fit does this and sizes the tunnel too.")]
    [SerializeField] private bool parkOnTunnelFloor;

    [Tooltip("Ride height above the tunnel floor in metres. Used only when parking.")]
    [Min(0f)][SerializeField] private float rideHeightM;

    GameObject _car;
    int _index = -1;

    public GameObject CurrentCar => _car;
    public int CurrentIndex => _index;
    public int CarCount => cars.Count;

    void Start()
    {
        if (tunnel == null) tunnel = FindFirstObjectByType<WindTunnelDomain>();
        if (runner == null) runner = FindFirstObjectByType<AeroTestRunner>();

        if (tunnel == null)
            Debug.LogError("CarSpawner: no WindTunnelDomain in the scene — spawned cars will not be simulated.", this);

        // Both the voxelizer and the LBM solver are compute-shader only. On a target
        // without compute support the spawn still works but nothing is ever measured,
        // and the failure surfaces much later as an unexplained empty tunnel.
        if (!SystemInfo.supportsComputeShaders)
            Debug.LogError("CarSpawner: this device reports no compute shader support; the wind tunnel cannot voxelize or solve.", this);

        if (spawnOnStart >= 0) Spawn(spawnOnStart);
    }

    void Update()
    {
        // Keyboard.current is null when the player has no keyboard attached.
        // (UnityEngine.Input is unusable here: the project is set to the new Input
        // System only, where the legacy Input class throws at runtime.)
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        int slots = Mathf.Min(cars.Count, 9);
        for (int i = 0; i < slots; i++)
        {
            if (!keyboard[Key.Digit1 + i].wasPressedThisFrame) continue;
            Spawn(i);
            break;
        }
    }

    /// <summary>Replaces the car under test and re-voxelizes the tunnel around it.</summary>
    public void Spawn(int index)
    {
        if (index < 0 || index >= cars.Count)
        {
            Debug.LogWarning($"CarSpawner: no car in slot {index + 1} ({cars.Count} assigned).", this);
            return;
        }

        GameObject prefab = cars[index];
        if (prefab == null)
        {
            Debug.LogWarning($"CarSpawner: slot {index + 1} is empty.", this);
            return;
        }

        // Stop everything that reads the vehicle before the geometry changes under it.
        // The runner caches the vehicle's transform and reference area for a whole
        // session and restores them on abort — let it do that against the old car.
        if (runner != null && runner.IsRunning) runner.AbortQueue();

        bool wasRunning = tunnel != null && tunnel.IsRunning;
        if (tunnel != null)
        {
            tunnel.StopSimulation();
            tunnel.vehicle = null;
        }

        DespawnCurrent();

        GameObject car = Instantiate(prefab);
        car.name = prefab.name;
        _car = car;
        _index = index;

        AeroVehicle vehicle = car.GetComponentInChildren<AeroVehicle>(true);
        if (vehicle == null)
        {
            vehicle = car.AddComponent<AeroVehicle>();
            Debug.LogWarning($"CarSpawner: '{prefab.name}' has no AeroVehicle; added one to its root. " +
                             "Add it to the prefab instead so the wheels and reference area are authored with the model.", car);
        }
        vehicle.RefreshWheels();

        ReportUnvoxelizableGeometry(vehicle);

        if (tunnel == null) return;

        tunnel.vehicle = vehicle;

        // Each of these prefabs is a different size and a different kind of craft, so
        // the tunnel that was right for the last one is wrong for this one: it fits
        // itself around the new body and applies that vehicle class's policy (ground
        // plane, wheel rotation, working fluid) before anything is measured.
        if (tunnel.autoFit != null && tunnel.autoFit.fitAutomatically)
            tunnel.FitToVehicle();
        else if (parkOnTunnelFloor)
            Park(vehicle);

        // The tunnel voxelizes whatever `vehicle` points at when the solver starts, so
        // a swap only takes effect on a (re)start. Revoxelize cannot bootstrap this —
        // it no-ops until a solver exists — and only StartSimulation recomputes the
        // reference length and frontal area for the new body.
        if (autoStartSimulation || wasRunning) tunnel.StartSimulation();
        else if (tunnel.Solver != null) tunnel.Revoxelize();
    }

    void DespawnCurrent()
    {
        if (_car == null)
        {
            _index = -1;
            return;
        }

        // Destroy is deferred to the end of the frame. Deactivate now so the outgoing
        // car is neither rendered nor picked up by anything scanning the scene during
        // the rest of this frame — including the voxelization about to run.
        _car.SetActive(false);
        Destroy(_car);
        _car = null;
        _index = -1;
    }

    /// <summary>
    /// The voxelizer reads vertices on the CPU and only ever looks at MeshFilters.
    /// In the editor Unity serves vertices for any mesh, but in a player a mesh
    /// without Read/Write enabled returns nothing and is skipped with a warning —
    /// the classic works-in-editor, empty-tunnel-in-a-build failure. Report it at
    /// spawn time, where the offending prefab is still obvious.
    /// </summary>
    static void ReportUnvoxelizableGeometry(AeroVehicle vehicle)
    {
        int readable = 0, unreadable = 0;

        foreach (MeshFilter filter in vehicle.GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || filter.GetComponentInParent<AeroIgnore>() != null) continue;
            if (mesh.isReadable) readable++;
            else unreadable++;
        }

        if (unreadable > 0)
            Debug.LogError($"CarSpawner: {unreadable} mesh(es) under '{vehicle.name}' are not Read/Write enabled. " +
                           "They voxelize in the editor but are skipped in a player build, so the car will be " +
                           "partly or wholly invisible to the flow there. Enable Read/Write on the model importer.", vehicle);

        if (readable == 0)
            Debug.LogError($"CarSpawner: '{vehicle.name}' has no readable MeshFilter geometry to voxelize" +
                           (vehicle.GetComponentInChildren<SkinnedMeshRenderer>(true) != null
                               ? " — its geometry is on SkinnedMeshRenderers, which the voxelizer ignores; bake it to plain MeshFilters."
                               : "."), vehicle);
    }

    /// <summary>
    /// Drops the car onto the tunnel floor, centred in the tunnel. Reads the authored
    /// tunnel size rather than EffectiveSize, because the latter is only computed once
    /// the solver has started. Assumes an axis-aligned tunnel, which is how the domain
    /// is set up in this scene.
    /// </summary>
    void Park(AeroVehicle vehicle)
    {
        if (vehicle.GetComponentInChildren<Renderer>() == null) return;

        Bounds bounds = vehicle.ComputeBounds();
        Vector3 centre = tunnel.transform.position;
        bool openFloor = tunnel.ground == GroundSimulation.OpenFloor;

        // With no floor the car belongs in mid-air at the tunnel's centre height;
        // with a floor it rests on it (the solver's ground wall is the lattice bottom).
        Vector3 anchor = new Vector3(bounds.center.x, openFloor ? bounds.center.y : bounds.min.y, bounds.center.z);
        Vector3 target = new Vector3(
            centre.x,
            openFloor ? centre.y : centre.y - tunnel.size.y * 0.5f + rideHeightM,
            centre.z);

        vehicle.transform.position += target - anchor;
    }

    void OnDestroy()
    {
        // Never leave the tunnel pointing at a car that is being torn down with us.
        if (tunnel == null || _car == null) return;
        tunnel.StopSimulation();
        tunnel.vehicle = null;
    }
}
