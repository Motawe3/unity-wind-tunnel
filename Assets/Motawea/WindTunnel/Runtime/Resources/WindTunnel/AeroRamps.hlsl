// Shared color ramps for every Wind Tunnel heatmap shader (FlowSlice, FlowSliceImage,
// SurfaceHeatmap). AeroRamps.cs mirrors these curves in C# for the UI legends —
// any change here must be applied there too, or the legend lies about the colors.
#ifndef WINDTUNNEL_RAMPS_INCLUDED
#define WINDTUNNEL_RAMPS_INCLUDED

// Sequential ramp (dark blue -> cyan -> green -> yellow -> red).
float3 SpeedRamp(float t)
{
    t = saturate(t);
    float3 c0 = float3(0.05, 0.05, 0.35);
    float3 c1 = float3(0.0, 0.55, 0.85);
    float3 c2 = float3(0.1, 0.8, 0.3);
    float3 c3 = float3(0.98, 0.85, 0.1);
    float3 c4 = float3(0.85, 0.1, 0.05);
    if (t < 0.25) return lerp(c0, c1, t / 0.25);
    if (t < 0.5)  return lerp(c1, c2, (t - 0.25) / 0.25);
    if (t < 0.75) return lerp(c2, c3, (t - 0.5) / 0.25);
    return lerp(c3, c4, (t - 0.75) / 0.25);
}

// Diverging ramp for Cp (blue = suction, white = 0, red = compression).
// Input is signed: -1 .. +1 maps across the ramp.
float3 CpRamp(float t)
{
    t = saturate(t * 0.5 + 0.5);
    float3 lo = float3(0.1, 0.25, 0.85);
    float3 mid = float3(0.96, 0.96, 0.96);
    float3 hi = float3(0.85, 0.15, 0.1);
    return t < 0.5 ? lerp(lo, mid, t * 2.0) : lerp(mid, hi, t * 2.0 - 1.0);
}

// Signed rainbow ("jet") pressure ramp, the industry surface-plot convention:
// GREEN sits at zero, so the mild suction that covers most of a car body reads
// green/cyan instead of drowning everything in blue. Input -1 .. +1.
float3 PressureRamp(float t)
{
    return SpeedRamp(t * 0.5 + 0.5);
}

#endif // WINDTUNNEL_RAMPS_INCLUDED
