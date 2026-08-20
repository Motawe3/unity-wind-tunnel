using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel.UI
{
    public struct AeroRampPreset
    {
        public string key;          // stable id, e.g. "ice" (runtime UXML buttons are named "ramp-<key>")
        public string displayName;
        public Color slow, mid, fast;
    }

    /// <summary>Named tracer color-ramp presets shared by the runtime HUD and editor dashboard.</summary>
    public static class AeroRampPresets
    {
        public static readonly AeroRampPreset[] All =
        {
            new AeroRampPreset { key = "ice", displayName = "ICE / EMBER",
                slow = new Color(0.15f, 0.35f, 1f), mid = new Color(0.95f, 0.97f, 1f), fast = new Color(1f, 0.25f, 0.1f) },
            new AeroRampPreset { key = "thermal", displayName = "THERMAL",
                slow = new Color(0.06f, 0.02f, 0.16f), mid = new Color(0.90f, 0.22f, 0.05f), fast = new Color(1f, 0.95f, 0.55f) },
            new AeroRampPreset { key = "petrol", displayName = "PETROL",
                slow = new Color(0.02f, 0.10f, 0.14f), mid = new Color(0.12f, 0.75f, 0.80f), fast = new Color(0.95f, 1f, 1f) },
            new AeroRampPreset { key = "mono", displayName = "MONO",
                slow = new Color(0.22f, 0.24f, 0.27f), mid = new Color(0.70f, 0.73f, 0.78f), fast = new Color(1f, 1f, 1f) },
        };

        public static void Apply(IEnumerable<FlowParticles> rakes, in AeroRampPreset preset)
        {
            if (rakes == null) return;
            foreach (var r in rakes)
            {
                if (r == null) continue;
                r.slowColor = preset.slow;
                r.freestreamColor = preset.mid;
                r.fastColor = preset.fast;
            }
        }
    }
}
