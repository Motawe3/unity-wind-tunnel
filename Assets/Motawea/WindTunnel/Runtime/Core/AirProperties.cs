using System;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Working fluid of the tunnel. Air is the default; the water media exist for
    /// watercraft tests in <see cref="WatercraftMode.SubmergedHull"/>.
    /// </summary>
    public enum AeroFluidMedium
    {
        [Tooltip("Air: density from the ideal gas law, viscosity from Sutherland's law.")]
        Air,
        [Tooltip("Fresh water at the given temperature (density from the ITS-90 fit, viscosity from Vogel's equation).")]
        FreshWater,
        [Tooltip("Sea water, approximated at 35 PSU: fresh-water properties with a salinity correction.")]
        SeaWater
    }

    /// <summary>
    /// Ambient fluid state for a test. For air, density follows the ideal gas law and
    /// dynamic viscosity Sutherland's law, so engineers can enter the tunnel conditions
    /// they actually log (temperature + barometric pressure). For water, density and
    /// viscosity come from standard temperature fits and the barometric pressure is
    /// ignored (the solver's fluid is incompressible in the relevant sense).
    /// </summary>
    /// <remarks>
    /// Named <c>AirProperties</c> for backwards compatibility with scenes serialized
    /// before water media existed; it is the tunnel's fluid, whatever that fluid is.
    /// </remarks>
    [Serializable]
    public struct AirProperties
    {
        [Tooltip("Working fluid. Water media are used by submerged watercraft tests; every other class runs in air.")]
        public AeroFluidMedium medium;

        [Tooltip("Ambient temperature in degrees Celsius.")]
        public float temperatureC;

        [Tooltip("Barometric pressure in Pascals (standard sea level = 101325 Pa). Air only — ignored for water.")]
        public float pressurePa;

        public static AirProperties StandardSeaLevel => new AirProperties
        {
            medium = AeroFluidMedium.Air,
            temperatureC = 15f,
            pressurePa = 101325f
        };

        public static AirProperties StandardFreshWater => new AirProperties
        {
            medium = AeroFluidMedium.FreshWater,
            temperatureC = 15f,
            pressurePa = 101325f
        };

        public float TemperatureK => temperatureC + 273.15f;

        public bool IsLiquid => medium != AeroFluidMedium.Air;

        /// <summary>Density in kg/m³.</summary>
        public float Density => medium switch
        {
            // Salinity correction at 35 PSU: roughly +0.8 kg/m³ per PSU near 15 °C.
            AeroFluidMedium.SeaWater => FreshWaterDensity(temperatureC) + 28f,
            AeroFluidMedium.FreshWater => FreshWaterDensity(temperatureC),
            _ => pressurePa / (287.058f * TemperatureK)
        };

        /// <summary>Dynamic viscosity μ in Pa·s.</summary>
        public float DynamicViscosity => medium switch
        {
            // Sea water is ~7% more viscous than fresh water at the same temperature.
            AeroFluidMedium.SeaWater => WaterDynamicViscosity(temperatureC) * 1.07f,
            AeroFluidMedium.FreshWater => WaterDynamicViscosity(temperatureC),
            _ => AirDynamicViscosity(TemperatureK)
        };

        /// <summary>Kinematic viscosity ν in m²/s.</summary>
        public float KinematicViscosity => DynamicViscosity / Density;

        /// <summary>Sutherland's law for air.</summary>
        static float AirDynamicViscosity(float tK)
        {
            const float mu0 = 1.716e-5f;
            const float t0 = 273.15f;
            const float s = 110.4f;
            return mu0 * Mathf.Pow(tK / t0, 1.5f) * (t0 + s) / (tK + s);
        }

        /// <summary>
        /// Fresh-water density (kg/m³) from the standard ITS-90 polynomial fit; valid
        /// 0–40 °C, and the 3.98 °C density maximum falls out of it correctly.
        /// </summary>
        static float FreshWaterDensity(float tC)
        {
            float t = Mathf.Clamp(tC, 0f, 100f);
            return 1000f * (1f - (t + 288.9414f) / (508929.2f * (t + 68.12963f)) * (t - 3.9863f) * (t - 3.9863f));
        }

        /// <summary>Water dynamic viscosity (Pa·s) from Vogel's equation — 1.14e-3 at 15 °C.</summary>
        static float WaterDynamicViscosity(float tC)
        {
            float t = Mathf.Clamp(tC, 0f, 100f);
            return 2.414e-5f * Mathf.Pow(10f, 247.8f / (t + 133.15f));
        }

        public string MediumLabel => medium switch
        {
            AeroFluidMedium.FreshWater => "fresh water",
            AeroFluidMedium.SeaWater => "sea water",
            _ => "air"
        };
    }
}
