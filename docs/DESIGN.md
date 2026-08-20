# Wind Tunnel — Virtual Wind Tunnel for Unity

Open-source Unity project that turns a Unity scene into a virtual automotive wind
tunnel: drop in a vehicle with mesh colliders/renderers, fit a tunnel domain around it, and
run industry-style aerodynamic tests with GPU-computed flow and GPU-instanced smoke
particles.

> **Positioning (honest):** Wind Tunnel is a *design-exploration, comparison and education*
> tool. It is **not** certification-grade CFD (Star-CCM+, PowerFLOW, OpenFOAM). It is meant
> for early-stage comparative work — "is variant B better than variant A, and roughly by
> how much" — plus training and communication. The README must state this clearly.

> **This file is the design intent.** It is maintained against the code and is
> authoritative where the two disagree.

## 1. Physics core

**Method:** Lattice-Boltzmann (LBM), D3Q19 lattice, TRT collision with a WALE LES subgrid
model (needed because automotive Reynolds numbers are far above what any interactive grid
can resolve directly; TRT keeps the solver stable with the molecular viscosity low enough
to reach that regime, where BGK capped it near Re 10³). This is the same family of methods
used by GPU CFD tools such as FluidX3D and (commercially) PowerFLOW.

- **Grid:** uniform voxel grid over the tunnel domain. Streamwise axis = domain local +X,
  up = +Y, lateral = +Z (axis conventions documented against SAE J670 / ISO 8855).
- **Solver storage:** two ping-pong `StructuredBuffer<float>` (SoA, `f[dir * N + cell]`),
  D3Q19 ⇒ 19 floats/cell ⇒ **152 bytes/cell** for the distributions; with the three
  velocity textures, fluid mask, flags, coverage and sub-mask, budget ≈ **190 bytes/cell**.
  Resolution presets set the streamwise cell count `Nx`: Coarse 128 / Medium 192 /
  Fine 256 / Ultra 384 / Extreme 512; `Ny`, `Nz` follow from the tunnel aspect ratio at the
  same `dx`, so a wider tunnel costs memory as fast as a finer one. Example: a 26×8×12 m
  tunnel at Ultra ⇒ dx ≈ 68 mm, ≈ 8.0M cells, ≈ 1.5 GB. `MaxCells = 20M` refuses to start
  rather than hanging the driver. FP16 storage and in-place (esoteric pull) streaming are
  planned optimizations (roadmap).
- **Streaming:** pull scheme, fused collide+stream in one kernel.
- **Collision:** TRT. The symmetric rate `ω⁺ = 1/(3ν + 0.5)` carries the viscosity
  (molecular + WALE eddy). The antisymmetric rate is **pinned at ω⁻ = 1**, *not* slaved to
  a fixed magic parameter: at this solver's operating viscosity (ν ≈ 1.5e-6) the classical
  Λ = ¼ would give ω⁻ ≈ 1e-5, i.e. undamped ghost modes ringing against the bounce-back
  walls. Equivalent to a viscosity-dependent Λ = ½(τ⁺ − ½).
- **Boundary conditions:**
  - Inlet plane (x=0): equilibrium BC at freestream velocity.
  - Outlet plane (x=Nx−1): zero-gradient (copy from upstream neighbor).
  - Tunnel side/top walls: freestream equilibrium ("open jet" behavior). Same for the
    floor row under `GroundSimulation.OpenFloor`.
  - Vehicle: half-way bounce-back on voxelized solid cells.
  - Ground: bounce-back; **fixed floor** or **moving belt** (wall velocity = freestream),
    matching the real wind-tunnel fixed-floor vs rolling-road distinction.
  - Wheels: solid voxels tagged per wheel; moving-wall bounce-back with u = ω × r
    (rotating wheels toggle), ω = U∞/r so the tyre rolls without slip.
  - Soft voxels: cells with partial coverage stay fluid and apply **gray-LBM partial
    bounce-back** weighted by their solid fraction (see *Voxelization*).
- **Forces:** momentum-exchange algorithm summed over fluid→vehicle boundary links, in
  **gauge form** — the ambient equilibrium weight `2wᵢ` is subtracted so that uniform static
  pressure cancels exactly. Without it, a body touching a domain boundary (tyres on the
  floor shell) integrates raw ambient pressure over its planform and reports enormous fake
  vertical force. Accumulated via fixed-point atomics (scales 4096 force / 256 moment, must
  match `LbmSolver.cs`) → small GPU buffer → async readback. Also accumulates the moment
  about the turntable pivot for the front/rear lift split. Ground links are excluded.
- **Units:** physical ↔ lattice conversion keeps lattice inlet speed ≈ 0.08 to bound
  compressibility error (Ma ≈ 0.14, Ma² ≈ 2%). The molecular lattice viscosity is clamped
  only at ν ≥ 1.5e-6 (τ⁺ ≈ 0.5000045), which corresponds to effective Re ≈ 3.5e6 at
  road-car scale against a target of ≈ 9e6; the solver reports the **effective Reynolds
  number** and relies on LES for sub-grid scales. A high effective Re is *not* the same as
  resolving the boundary layer: boundary layers remain under-resolved on interactive grids
  (no wall model yet), and that is the dominant error source — documented limitation.
- **Turbulence:** WALE, constant **Cw = 0.5** (exposed as advanced setting `lesCw`). WALE
  rather than Smagorinsky because its eddy viscosity vanishes in pure shear: near-wall flow
  keeps molecular viscosity instead of being artificially thickened, which used to force
  laminar-early separation and several-times-real drag on smooth bodies. Two implementation
  constraints, both learned the hard way: the **strain** must come from the non-equilibrium
  momentum flux `Π_neq` (central-differenced gradients cancel exactly on period-2 grid
  modes — the one mode the LES viscosity exists to damp), while the **rotation** tensor is
  safely taken from central differences of the previous step; and the τ⁺ ↔ ν_t circularity
  is closed by a 3-step fixed-point iteration. Cw = 0.5 is **calibrated for this D3Q19
  lattice** (sharp-body Cd validates Re-independently there; the classical 0.325
  under-damps and grid noise reads as spurious drag). Re-calibrate if the lattice or
  collision operator changes.

### Voxelization

GPU pipeline, re-run whenever the vehicle/domain configuration changes (yaw step, ride
height step). Kernel order:

```
Clear → RasterizeTriangles → [ColumnSeal → SealVetoX → SealVetoZ] → SeedOutside
      → FloodStep ×(Nx+Ny+Nz) → ComputeCoverage → [PorousSeed → PorousSpread ×32]
      → Finalize → ComputeStats
```

1. Gather all `MeshFilter` triangles under the `AeroVehicle` root, transform to lattice
   space on CPU (one-time per run, a few MB). Subtrees under `AeroIgnore` are skipped;
   non-Read/Write meshes are skipped at runtime with a warning; geometry spanning most of
   the tunnel footprint raises an "environment geometry" warning (an imported studio floor
   invalidates every force number).
2. Compute kernel: conservative surface rasterization (thread per triangle, tri-box SAT
   test, `InterlockedOr` into a flags buffer). With soft voxels on, the same test also runs
   against each cell's 3×3×3 sub-boxes, packing a 27-bit occupancy mask.
3. **Column sealing** (`sealOpenModels`, on by default): game cars are hollow shells with
   no floor pan, so the flood fill would enter from below and leave the cabin as a fluid
   pocket whose drifting pressure pushes on interior panels. Each (x,z) column is closed
   between its lowest and highest surface hit; ground clearance below the lowest surface
   stays open. A **tyre contact fill** plants the body on the floor when its lowest surface
   sits within 2 cells (a pinched 1–2 cell sliver develops unphysical pressure).
   **Seal veto** then unseals any sealed cell with a straight, surface-free sight line out
   of the domain along ±X or ±Z — that reopens real air gaps (deck-to-wing, under-tail)
   without reopening enclosed interiors, which cannot see out sideways.
4. Iterative flood fill from the domain boundary marks *outside*; everything not outside
   and not surface = solid interior. Robust to imperfect (non-watertight) meshes.
5. **Coverage**: sub-cell mask → solid fraction (`popcount/27`, floored at 1.5/27). A
   **porous flood** (`PorousSeed`/`PorousSpread`) marks which partial surface cells are
   actually reachable from open air; only those stay `CELL_FLUID` and act as partially
   open. Interior detail geometry inside a sealed shell is never reached and stays solid —
   this is what preserves the cabin fix and must not regress.
6. Wheel cylinders tag wheel cells (always fully solid, so the rotating-wall BC covers the
   whole tyre); ground/inlet/outlet/far-field rows tagged from the domain shell.
7. Frontal area measured by projecting solid columns along X (silhouette count × dx²);
   partial cells with coverage ≥ 0.5 count. **Known gap:** this thresholds coverage rather
   than summing it, so a plate thinner than half a cell measures zero area —
   `AeroForces` floors the reference area at one cell so the failure is loud, but the real
   fix is to sum coverage.

### Vehicle classes and tunnel auto-fit

A vehicle declares **what kind of craft it is** (`AeroVehicle.vehicleClass`), and that
one declaration drives the whole test setup through `AeroVehicleProfile`: ground
boundary condition, wheel rotation, working fluid, reference-area convention, how the
body is seated, the domain clearances, and which direction of lift counts as an
improvement when two runs are compared. Testing an aircraft as "a car with the floor
left on" is the failure mode this exists to prevent.

| Class | Ground | Wheels | Reference area | Placement | Lift objective |
|---|---|---|---|---|---|
| RoadVehicle | FixedFloor | rotating | frontal | contact patches on the floor | lower is better |
| Motorsport | FixedFloor | rotating | frontal | contact patches on the floor | lower is better |
| Aircraft | OpenFloor | — | **planform** | centred in the domain | higher (scored on L/D) |
| Watercraft (`AboveWaterlineAir`) | FixedFloor | — | frontal | waterline on the floor | informational |
| Watercraft (`SubmergedHull`) | OpenFloor | — | frontal | centred, in water | informational |
| ReferenceBody | OpenFloor | — | frontal | centred in the domain | informational |

`MovingBelt` is deliberately *not* a class default: it is the road-realistic ground
simulation but it diverged under the previous solver and has not been re-verified.

**Watercraft honesty.** The solver is single-phase with no free surface. Above-waterline
mode is a genuine experiment (ship superstructure wind loading, exactly as a real wind
tunnel measures it) — the waterline becomes the domain floor and the submerged hull is
outside the grid, which the voxelizer's clamping gives for free. Submerged mode gives
pressure + friction drag in water but **no wave-making resistance**, the dominant term
for a planing hull; it is a hull-shape comparator, not a resistance prediction.

**Auto-fit** (`TunnelAutoFit`, `WindTunnelDomain.FitToVehicle`) resolves the trade-off
this solver cannot escape: a resolution tier fixes the *streamwise cell count*, so a
longer domain is a coarser body. The domain extents therefore come from aerodynamic
requirements and the tier is chosen afterwards, as fine as a memory budget allows:

1. Measure the body along the tunnel axes (`TryComputeAeroBounds` mirrors the
   voxelizer's traversal — `AeroIgnore` excluded — and uses mesh-local corners so a
   rotated tunnel does not over-measure).
2. Lay out clear air in body extents from the class profile: 1.5–2 lengths upstream,
   3.5–4 downstream, 2 widths per side, 2–2.5 heights above. For a grounded body only
   the part above its plane counts as the height.
3. Grow the cross-section until the frontal area is within the blockage target (default
   5%, deliberately tighter than the 7.5% warning because a yaw sweep grows the
   silhouette). The first fit predicts the frontal area from the bounding box × a
   per-class fill factor; a re-fit uses the area actually measured, and may then *trim*
   the cross-section back — never below 60% of the class clearances.
4. Choose the finest tier whose grid fits the memory budget (~190 B/cell) and the cell
   guard; report the cell size and cells-across-body.
5. Seat the vehicle: streamwise at the upstream margin, laterally centred, vertically by
   placement rule. The tunnel floor is pinned to the scene's ground plane (world Y,
   honoured only when the tunnel is level) so the simulated floor and the visible one
   coincide.
6. Re-aim the smoke rakes and slice plane at the fitted body.

The tunnel is **never re-fitted implicitly on a first start** — only when the vehicle
reference changes on a tunnel that had already been fitted (a car swap). Hand-built
domains, including every validation harness, keep the geometry they
were authored with; those harnesses also set `autoFit.fitAutomatically = false`
explicitly so the intent survives future edits.

## 2. What the engineer gets (industry outputs)

- **Coefficients:** Cd, CdA (drag area), Cl with **front/rear split (aero balance)**,
  side force Cy (under yaw). Reference area = measured frontal area (overridable).
- **Derived:** drag force (N) and aerodynamic power (kW) at test speed, plus a simple
  fuel/range impact estimate.
- **Environment:** air density/temperature/pressure (density via ideal gas, viscosity via
  Sutherland's law), test speed, effective Reynolds number.
- **Checks:** blockage ratio warning (frontal area / tunnel cross-section > ~7.5%; no
  correction is applied), convergence monitoring (Cd moving-window coefficient of
  variation **plus a half-window drift test**, so a slowly drifting run cannot pass), and a
  startup-transient discard of 0.3 flow-through times before any sample is taken.

### Test procedures (v1 = full suite)

`AeroTestKind` has **four** kinds. Ground simulation is *not* a fifth kind — it is a
per-test toggle (`ground`, `rotatingWheels`), so a fixed-floor vs rolling-road comparison
is run as two otherwise-identical queue entries.
`AeroTestDefinition.StandardQueue(class)` builds the procedures a given vehicle class
actually calls for (an aircraft gets an alpha sweep and a sideslip sweep; a car gets a
yaw sweep and a ride-height sweep).

| Test | Procedure | Output |
|---|---|---|
| Constant-speed drag | Run to convergence at set speed | Cd, CdA, Cl f/r, F_drag, power |
| Yaw sweep | Turntable rotates *vehicle* ±ψ in steps, re-voxelize, converge each | Cd(ψ), Cy(ψ) curves |
| Ride-height sweep | Translate vehicle vertically in steps | Cd(h), Cl(h) curves |
| Angle-of-attack sweep | Pitch the vehicle about the lateral axis (positive = nose up), re-voxelize, converge each | Cl(α), Cd(α), L/D — the aircraft counterpart of the yaw sweep |
| *(toggle)* Ground simulation | `OpenFloor` / `FixedFloor` / `MovingBelt` + rotating wheels, set per test | Δ on all metrics |

Each point runs to convergence or to `maxStepsPerPoint` (default 24 000), which is logged
and recorded as `converged = false`. The runner applies the **SAE reference-area lock**:
the frontal area is measured once at the authored zero-yaw pose and held for the whole
session, otherwise Cd(ψ) divides by a growing silhouette and reads artificially low. User
overrides are respected and the original value is restored on finish or abort.

### Comparing two sessions

Every export writes three files: HTML (people), CSV (spreadsheets) and
`<name>.windtunnel.json` (machines). Only the JSON archive carries the full test
configuration, and that is what makes a later comparison defensible.

`AeroComparison.Compare` is pure logic over two `AeroTestSession` objects, hosted by
`AeroComparisonView` (one element, mounted by the runtime HUD as a modal and by
`AeroComparisonWindow` in the editor). It runs a **like-for-like audit before it
differences anything**:

- **Blocking** — different test procedure, working fluid, or reference-area *convention*
  (frontal vs planform). These are not the same quantity, so no verdict is produced at all.
  A class difference alone does not block: a road car and a race car share the frontal-area
  convention and the drag objective, so they are compared with a caveat and their lift rows
  are left unscored. A hand-set reference area is likewise a caveat, not a block — it
  rescales Cd but leaves CdA = F/q untouched.
- **Caveat** — different grid/cell size, soft-voxel state, package version, test speed,
  ground simulation, blockage over the guidance, or a point that never converged. The
  numbers are still shown and the verdict repeats the caveat.

Metrics are scored by the polarity the vehicle class implies (Cd always lower-is-better;
lift depends on the class; aircraft are scored on L/D and everything else on CdA), and a
delta smaller than the runs' own recorded convergence scatter is reported as **"too close
to call"**. That last rule is the point of the feature: on this solver a 0.3% change is
not a result, and a comparison tool that names a winner anyway is lying.

All tests run from a queue (editor, play mode, or batch) and produce a session report
(**HTML with embedded SVG charts + CSV + JSON archive**). `AeroBatchRunner.RunTests` is the command-line /
CI driver (`-aeroScene`, `-aeroMinutes`, `-aeroSingle`, `-aeroGround`, `-aeroTunnel`,
`-aeroTolerance`); it needs a GPU, so run **without** `-nographics`.

## 3. Visualization

- **Flow particles ("smoke"):** GPU buffer advected through the solved velocity field
  (RK2 midpoint, trilinear sampling of the velocity 3D texture), rendered as GPU-instanced
  ribbon trails through a per-particle history ring, colored by local speed. These are
  **streaklines**, matching what a real smoke wand shows. Emitter "rake" is
  movable/resizable — mirrors real wind-tunnel smoke wands. Multiple rakes allowed.
  Implementation notes that matter: samples are divided by `_FluidMask` (the *open*
  fraction) to reconstruct fluid-only / pore velocity near walls and through soft-voxel
  gaps; dead particles go dormant for a random delay before re-emitting, otherwise equal
  transit times re-synchronize the population into visible waves; the trail ring advances
  by flow distance, not per tick, so trail length is independent of playback speed and
  steps-per-tick. **Depth contrast** fades tracers whose paths the body never deformed,
  keyed on each tracer's latched peak speed deficit and a stable random rank.
- **Slice plane:** movable/rotatable quad showing a velocity-magnitude or
  pressure-coefficient heatmap sampled from the field — the single most used CFD post-pro
  view. Cp is computed directly from the sampled density: `Cp = ((ρ−1)/3)/(½u∞²)`. The same
  view is also blitted camera-free into a RenderTexture by `SliceScannerRenderer` for the
  UI scanner, aspect-matched to the slice rectangle.
- **Visualization must sample `LbmSolver.VelocityField`**, a dedicated display snapshot
  copied once per `Step()` batch — never the internal ping-pong textures, which are rebound
  as UAVs dozens of times per frame (a missed UAV→SRV transition reads as flicker).
- **Surface heatmaps** (`SurfaceHeatmap`, shipped): while enabled, the vehicle's material set
  is cached and swapped for `WindTunnel/SurfaceHeatmap`, whose fragment shader steps ~2 cells out
  along the surface normal and samples the display field there — Cp (diverging ramp, same math
  as the slice plane), near-wall tangential speed (the wall-shear pattern; relative by design,
  since the display texture carries no eddy viscosity), or speed ratio. Disabling restores the
  cached materials. Solid cells write ρ = 1 and u = 0, so dividing a near-wall trilinear sample
  by the sampled `FluidMask` open fraction reconstructs the fluid-only value for both channels.
  Color ramps live in `AeroRamps.hlsl`, mirrored by `AeroRamps.cs` for the UI legends.

## 4. Package layout

`Assets/Motawea/WindTunnel` (MIT license, URP-first, Unity 6+).

```
Runtime/
  Core/            WindTunnelDomain, AeroVehicle, AeroWheel, AeroIgnore,
                   AirProperties, LatticeUnits
  Solver/          LbmSolver (dispatch + readback), AeroForces (coefficient math)
  Voxelization/    VehicleVoxelizer (+ WheelLatticeData)
  Particles/       FlowParticles (+ rake)
  Visualization/   FlowSlice
  Testing/         AeroTestDefinition(s), AeroTestRunner, results model
  Reporting/       AeroReportExporter (HTML+SVG / CSV), AeroScreenshot
  UI/              AeroDashboardView, WindTunnelHUD, AeroTestQueueView,
                   AeroVisualControlsView, ConvergenceChart, SliceScannerRenderer,
                   AeroRampPresets                (shared runtime/editor)
  Resources/WindTunnel/  Lbm.compute, Voxelize.compute, FlowParticles.compute,
                      FlowParticle/FlowSlice/FlowSliceImage shaders
Editor/            domain editor+gizmos, setup menu & wizard, dashboard EditorWindow,
                   AeroBatchRunner (CI / command line)
Samples~/          demo scene instructions + primitive test vehicle builder
```

Validation harnesses live in `Editor/Validation/` (benchmarks and studies: `AeroValidate`,
`AeroAhmedTest`, `AeroAhmedDiag`, `AeroGridConvergence`, `AeroFitAccuracyTest`,
`AeroGapMatrix`, `AeroInterpolatedWallsTest`) and `Editor/Regression/` (gates run before and
after a change: `AeroDemoTest`, `AeroCompareTest`, `AeroAutoFitTest`, `AeroCompareLive`,
`AeroUpdateTests`). Editor-only, so they are stripped from player builds.

Key components:

- **WindTunnelDomain** — the tunnel: bounds, resolution preset, inlet speed, air
  properties, ground mode; owns solver + voxelizer lifecycle; scene gizmo shows tunnel
  box, inlet arrow, turntable.
- **AeroVehicle** — marker on the vehicle root: wheel references, reference-area
  override, turntable pivot.
- **AeroTestRunner** — executes test queues in editor (EditorApplication.update ticker)
  or play mode.
- **Dashboard** — UI Toolkit: live readouts, convergence chart, test queue, report export.
  Same visual tree hosted by an EditorWindow and (optionally) a runtime UIDocument.

## 5. Milestones

- **M1 – Foundation:** package scaffold, domain/vehicle components, units, gizmos.
- **M2 – Flow:** voxelizer, LBM solver, force readback, single drag test converging on a
  primitive body (validation: sphere/cube Cd sanity vs published ranges).
- **M3 – Visualization:** particles + rake, slice plane.
- **M4 – Tests & reporting:** yaw/ride-height sweeps, ground modes, HTML/CSV reports.
- **M5 – Dashboard & polish:** UI Toolkit dashboard, setup wizard, samples, docs.
- **M6 – Realism (v0.2.0, shipped):** BGK+Smagorinsky → TRT+WALE, soft voxels, gauge-form
  momentum exchange, drift-aware convergence, SAE area lock, batch driver.
- **Roadmap after v1**, in descending value-per-effort:
  1. **Wall model / interpolated (Bouzidi) bounce-back** — attacks the root cause of the
     smooth-body error (see §6); everything else is polish by comparison.
  2. **Ahmed-body validation in CI** — turns the positioning claim into a published number.
  3. **Cp surface coloring** on the body (cheap, high demo value).
  4. **FP16 lattice + in-place (esoteric pull) streaming** — roughly halves VRAM, buys a
     resolution tier.
  5. **Local grid refinement / nested sub-domain tunnel** — the only route past the
     ≥ 3·dx gap rule for wings, splitters and intakes.
  6. **`AeroIntake` suction BC** — makes ducts and cooling flow meaningful.
  7. Wake iso-surfaces, drafting/platooning scenarios, motorsport presets.

## 6. Validation status

**Done (2026-07-21, `Editor/Validation/AeroValidate.cs` → `aero_validate.txt`; free air
12×5×5 m @ 256, 30 m/s, 20 samples after 20 flow-throughs, soft voxels off):**

| Case | Cd measured | Cd reference | ratio |
|---|---|---|---|
| Flat plate 1 m, normal | 1.128 | 1.17 | 0.96× |
| Cube 1 m, face-on | 0.900 | 1.05 | 0.86× |
| Sphere D = 1 m | 0.645 | ~0.45 | 1.43× |

**Interpretation — this is the argument to make when the tool is challenged.** The plate
and the cube are the two bodies whose Cd is Reynolds-*independent* (separation is pinned
geometrically at the edge), so they test the numerics without testing the turbulence
modelling. Getting them right rules out a force-integration, units or reference-area bug.
The sphere and a real car body are Reynolds-*dependent* — their separation line is found by
the boundary layer, which is not resolved (no wall model, dx ≈ 68 mm is thicker than the
boundary layer itself). Consequently the Range Rover Sport SVR converges rock-solid at
Cd ≈ 1.1 against a real ≈ 0.35, and finer cells previously made it *worse* — the signature
of a modelling limit, not a resolution artifact. **Deltas and trends are the product;
absolute road-car Cd is not.**

**Measured 2026-08-11 (`Editor/Validation/AeroFitAccuracyTest.cs` → `aero_fit_accuracy.txt`):** the
same Range Rover, same solver settings, 10 flow-throughs, Cd averaged over 144 force samples —
hand-sized 26 × 8 × 12 tunnel with the car raised 0.22 m gives **Cd 1.125 ± 0.050, Cl 0.222**;
the auto-fitted 29.2 × 6.2 × 11.1 tunnel with the tyres seated on the floor gives **Cd 0.836 ±
0.047, Cl 0.978**. The 26 % drop comes with *higher* blockage (3.2 % → 4.6 %) and *coarser* cells
(68 → 76 mm), so it is not a blockage or resolution effect: `bounds.min.y` on that model is the
bottom of the tyres, so the old setup had the whole car hovering 0.22 m (≈ 3 cells) above the
floor with air flowing under the wheels. Seating the contact patches on the ground seals that
gap. Absolute road-car Cd remains untrustworthy either way — see Known limitations.

**Grid convergence — done 2026-08-11** (`Editor/Validation/AeroGridConvergence.cs` →
`aero_grid_convergence.txt`). The auto-fit builds the domain once and it is held identical
across every tier (blockage 4.5–4.8 % throughout), so the trend is purely a grid effect.
Range Rover, 10 flow-throughs per tier, averaged over the last 3:

| tier | cell | cells on body | A measured | Cd | ± | CdA | Cd/real | Cl |
|---|---|---|---|---|---|---|---|---|
| Coarse | 228 mm | 21 | 3.286 | 1.318 | 0.077 | 4.331 | 3.66× | 0.516 |
| Medium | 152 mm | 32 | 3.268 | 1.017 | 0.059 | 3.323 | 2.82× | 0.815 |
| Fine | 114 mm | 43 | 3.221 | 0.979 | 0.047 | 3.154 | 2.72× | 2.276 |
| Ultra | 76 mm | 64 | 3.222 | 0.836 | 0.047 | 2.692 | 2.32× | 0.978 |
| Extreme | 57 mm | 85 | 3.103 | 0.820 | 0.043 | 2.546 | 2.28× | 0.654 |

Three results, all of them load-bearing for how this tool is described:

1. **Drag converges with grid, monotonically, toward the published value** — and the last
   refinement step moves Cd only 1.9 %, inside the ±5 % run-to-run scatter. This **retires
   the v0.2.0 claim that finer cells made the Range Rover worse** (68 → 34 mm: 1.53 → 2.05);
   that was measured under BGK + Smagorinsky and no longer describes this solver.
2. **It converges to ~2.3× the real Cd, not to it.** Once discretisation error is gone the
   remainder is the missing wall model. This is the strongest evidence yet for putting the
   wall model at the top of the roadmap, and the honest number to quote when challenged.
3. **Lift does not converge at all** (0.52, 0.82, 2.28, 0.98, 0.65 — non-monotonic, with an
   outlier at Fine). Road-car Cl is not a reportable quantity at any resolution here;
   front/rear balance trends at a fixed grid remain usable.

**Ahmed body — done 2026-08-11** (`Editor/Validation/AeroAhmedTest.cs` → `aero_ahmed_body.txt`;
geometry generated from the published dimensions by `AeroAhmedBody`). Slant sweep at
15 mm cells, 40 m/s, fixed floor, identical grid at every angle, 6 flow-throughs averaged,
coefficients on the published 0.11203 m² reference area:

| slant | Cd measured | ± | Cd published | ratio |
|---|---|---|---|---|
| 0° | 1.082 | 1.9 % | ~0.250 | 4.33× |
| 25° | 1.034 | 1.6 % | ~0.285 | 3.63× |
| 30° | 1.071 | 1.4 % | ~0.378 | 2.83× |
| 35° | 1.029 | 1.4 % | ~0.260 | 3.96× |

**The result is not the ratio — it is the flatness.** Published Cd varies by **51%** across
these angles; ours varies by **5%**, barely twice the measurement uncertainty. Every angle
lands near Cd ≈ 1.0, which is the drag of a plain square-backed box. The solver is
**blind to the rear slant**: the flow separates early and stays separated whatever shape
the tail is, so the geometry that defines this benchmark never gets to act.

This sharpens the positioning materially, and the README now says so:

- ✅ Deltas driven by **size, frontal area and gross bluffness** are real — the
  cross-vehicle run ranks SUV / sports car / race car correctly.
- ❌ Deltas driven by **where the flow separates on a smooth surface** — slant angle,
  roofline rake, tailgate treatment, spoiler angle — are currently **invisible** to it.

**Refining the grid does not rescue it** (`aero_ahmed_resolution.txt`, 0° case at 20 / 15 /
12 / 10.5 mm): Cd 1.177 → 1.082 → 1.019 → 1.022. It converges — the last step moves it 0.3 % —
and converges on the drag of a brick rather than on 0.25. The remaining error is therefore
**not discretisation**. Note also that the voxel staircase does not get relatively smaller
with refinement here (step height ≈ one cell ≈ 10–20 mm against a ~11 mm boundary layer), so
interpolated bounce-back — which removes the steps rather than shrinking them — attacks
something refinement provably does not, and the 0° number is where to measure it.

It also gives the wall model a precise target: the Ahmed sweep must show a spread of the
order of 50%, not 5%, and the 30°→35° drop must appear.

**Still owed:**
2. Cylinder cross-flow; sphere re-check with soft voxels tuned.

**Known limitations** (full list in the README): no
wall model; uniform grid; gap flow needs ≥ 3·dx (measured — `aero_gap_matrix.txt`); no
internal/cooling flow; `MovingBelt` + rotating wheels was observed to diverge to negative
Cd under the old BGK solver and has **not** been re-verified under TRT+WALE; far-field
walls are an open-jet idealisation; frontal area thresholds coverage instead of summing it.

## 7. Cross-file invariants

Breaking one of these produces wrong physics, not a compile error.

- `CELL_*` values must match across `Voxelize.compute`, `Lbm.compute` and `AeroCellType`.
- `FORCE_SCALE` / `MOMENT_SCALE` must match between `Lbm.compute` and `LbmSolver.cs`.
- `_Stats` slot count must match between `Voxelize.compute` and
  `VehicleVoxelizer.StatsSlots`: [0] frontal columns, [1] solid cells, [2] planform
  columns. A short buffer silently drops the planform area, and an aircraft would then
  be normalized by its frontal silhouette.
- `WindTunnelVersion.Value` must match the release tag — exported sessions are stamped with
  it, and the comparison tool uses it to warn that the physics may have moved.
- `TunnelAutoFit.BytesPerCell` is the memory model the auto-fit budgets against; keep it
  honest if the solver's per-cell storage changes (FP16 lattice would halve it).
- `_Coverage` contract: partial cells stay `CELL_FLUID`; only porous-flood-reached surface
  cells go partial (otherwise interior cabin geometry becomes porous and the fake
  interior-pressure forces come back).
- `_FluidMask` is the **open fraction**, not a binary mask — samplers divide by it.
- Any GPU buffer a kernel can skip writing must be explicitly initialised. Solid cells
  early-return in `LbmStep` and never write their `f` entries; leaving the second ping-pong
  half uninitialised made the first large-grid run of each session read recycled VRAM and
  settle on a spurious high-drag wake. Suspiciously bimodal "physics" across sessions is a
  state leak until proven otherwise.
- Consumers sample `LbmSolver.VelocityField` (the display snapshot), never the working
  textures.
- Every new scene component needs `[ExecuteAlways]` **and** lazy resource init — plain
  MonoBehaviours get no lifecycle callbacks in edit mode, and the dashboard ticks these
  outside play mode.
- Coefficients are formed in lattice units and the conversions cancel; do not convert to SI
  first.
