using UnityEngine;

namespace Motawea.WindTunnel
{
    public enum AeroRampKind
    {
        /// <summary>Sequential ramp, input 0..1 (speed, shear).</summary>
        Speed,
        /// <summary>Diverging blue/white/red, input −1..+1 (slice-plane Cp).</summary>
        CpDiverging,
        /// <summary>Signed rainbow with green at zero, input −1..+1 (surface pressure).</summary>
        Pressure
    }

    /// <summary>
    /// C# mirror of Resources/WindTunnel/AeroRamps.hlsl, used to draw UI legends that
    /// match the shader colors exactly. Any change to the HLSL curves must be
    /// applied here too, or the legend lies about the colors.
    /// </summary>
    public static class AeroRamps
    {
        /// <summary>Sequential ramp (dark blue → cyan → green → yellow → red), t in 0..1.</summary>
        public static Color Speed(float t)
        {
            t = Mathf.Clamp01(t);
            Color c0 = new Color(0.05f, 0.05f, 0.35f);
            Color c1 = new Color(0.0f, 0.55f, 0.85f);
            Color c2 = new Color(0.1f, 0.8f, 0.3f);
            Color c3 = new Color(0.98f, 0.85f, 0.1f);
            Color c4 = new Color(0.85f, 0.1f, 0.05f);
            if (t < 0.25f) return Color.Lerp(c0, c1, t / 0.25f);
            if (t < 0.5f) return Color.Lerp(c1, c2, (t - 0.25f) / 0.25f);
            if (t < 0.75f) return Color.Lerp(c2, c3, (t - 0.5f) / 0.25f);
            return Color.Lerp(c3, c4, (t - 0.75f) / 0.25f);
        }

        /// <summary>Diverging Cp ramp (blue = suction, white = 0, red = compression), t in −1..+1.</summary>
        public static Color Cp(float t)
        {
            t = Mathf.Clamp01(t * 0.5f + 0.5f);
            Color lo = new Color(0.1f, 0.25f, 0.85f);
            Color mid = new Color(0.96f, 0.96f, 0.96f);
            Color hi = new Color(0.85f, 0.15f, 0.1f);
            return t < 0.5f ? Color.Lerp(lo, mid, t * 2f) : Color.Lerp(mid, hi, t * 2f - 1f);
        }

        /// <summary>Signed rainbow pressure ramp (green at zero), t in −1..+1.</summary>
        public static Color Pressure(float t) => Speed(t * 0.5f + 0.5f);

        public static Color Evaluate(AeroRampKind kind, float t) => kind switch
        {
            AeroRampKind.CpDiverging => Cp(t),
            AeroRampKind.Pressure => Pressure(t),
            _ => Speed(t)
        };

        /// <summary>
        /// Builds a horizontal legend strip for the given ramp. Signed ramps span
        /// −1..+1 across the width; the sequential one spans 0..1.
        /// </summary>
        public static Texture2D BuildLegendTexture(AeroRampKind kind, int width = 256)
        {
            var tex = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                name = $"Wind Tunnel {kind} Legend"
            };
            bool signed = kind != AeroRampKind.Speed;
            for (int x = 0; x < width; x++)
            {
                float t = x / (width - 1f);
                tex.SetPixel(x, 0, Evaluate(kind, signed ? t * 2f - 1f : t));
            }
            tex.Apply(false, true);
            return tex;
        }
    }
}
