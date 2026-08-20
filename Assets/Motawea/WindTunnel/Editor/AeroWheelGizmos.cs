using UnityEditor;
using UnityEngine;

namespace Motawea.WindTunnel.Editor
{
    /// <summary>
    /// Scene-view gizmos for <see cref="AeroWheel"/>: the tagging cylinder the
    /// voxelizer will carve (same EffectiveRadius/EffectiveWidth), the spin axis,
    /// and a rolling-direction arrow at the contact patch.
    /// </summary>
    static class AeroWheelGizmos
    {
        static readonly Color WheelColor = new Color(1f, 0.62f, 0.15f);

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable, typeof(AeroWheel))]
        static void DrawWheelGizmo(AeroWheel wheel, GizmoType type)
        {
            bool selected = (type & GizmoType.Selected) != 0;
            float alpha = selected ? 0.95f : 0.35f;
            Color color = new Color(WheelColor.r, WheelColor.g, WheelColor.b, alpha);

            Vector3 center = wheel.Center;
            Vector3 axis = wheel.Axis.normalized;
            float radius = wheel.EffectiveRadius;
            float halfWidth = 0.5f * wheel.EffectiveWidth;

            Vector3 sideA = center + axis * halfWidth;
            Vector3 sideB = center - axis * halfWidth;

            Handles.color = color;
            Handles.DrawWireDisc(sideA, axis, radius);
            Handles.DrawWireDisc(sideB, axis, radius);

            // Rim lines connecting the two discs.
            Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f
                ? Vector3.forward
                : Vector3.up;
            Vector3 spoke0 = Vector3.Cross(axis, reference).normalized;
            for (int i = 0; i < 4; i++)
            {
                Vector3 spoke = Quaternion.AngleAxis(90f * i, axis) * spoke0 * radius;
                Handles.DrawLine(sideA + spoke, sideB + spoke);
            }

            // Spin axis stub.
            Handles.DrawLine(center - axis * (halfWidth + 0.08f), center + axis * (halfWidth + 0.08f));

            if (!selected) return;

            // Rolling direction at the contact patch: the tire surface moves with the
            // belt (+X of the tunnel); the voxelizer normalizes the axis sign to match.
            Vector3 down = Vector3.down * radius;
            Vector3 contact = center + down;
            Vector3 roll = Vector3.Cross(axis, down).normalized;
            if (roll.sqrMagnitude < 0.5f) roll = Vector3.right;

            float arrow = Mathf.Max(radius * 0.6f, 0.12f);
            Vector3 tip = contact + roll * arrow;
            Handles.DrawLine(contact, tip);
            Handles.DrawLine(tip, tip - roll * (arrow * 0.3f) + Vector3.up * (arrow * 0.15f));
            Handles.DrawLine(tip, tip - roll * (arrow * 0.3f) - Vector3.up * (arrow * 0.15f));

            Handles.Label(sideA + Vector3.up * (radius + 0.05f),
                $"{wheel.name}  r={radius:0.00} m  w={wheel.EffectiveWidth:0.00} m");
        }
    }
}
