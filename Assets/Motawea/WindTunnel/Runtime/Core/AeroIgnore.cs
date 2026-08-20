using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Excludes this object and all its children from voxelization. Put it on
    /// non-vehicle geometry that ships inside imported models — studio floors,
    /// backdrops, interior props — so only the actual body is tested.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Aero Ignore (exclude from voxelization)")]
    public class AeroIgnore : MonoBehaviour
    {
    }
}
