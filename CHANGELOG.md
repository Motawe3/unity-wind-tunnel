# Changelog

## [1.0.0] — 2026-08-20

First open-source release.

- **Reorganised from an embedded UPM package into a plain Unity project.** The solver
  now lives at `Assets/Motawea/WindTunnel/{Runtime,Editor}` behind the
  `Motawea.WindTunnel` and `Motawea.WindTunnel.Editor` assembly definitions. Asset
  GUIDs were preserved through the move, so existing scenes and prefabs keep their
  references.
- **Renamed throughout: AeroSim → Wind Tunnel.** Namespaces are now
  `Motawea.WindTunnel` (`.UI`, `.Editor`, `.Samples`); menus live under
  `Window ▸ Wind Tunnel` and `GameObject ▸ Wind Tunnel`; shaders and compute
  resources moved to `WindTunnel/…`. Exported reports use the `windtunnel-` file
  prefix and the `.windtunnel.json` archive extension — **existing `.aerosim.json`
  archives will not load**.
- **Sample vehicles trimmed to five CC BY 4.0 models**, each attributed in
  `THIRD-PARTY-NOTICES.md`. Three were dropped before publication over asset
  provenance, and roughly 880 MB of unreferenced payload (duplicate archives,
  display-scene textures, duplicate sources) was stripped.
- Verified physics-neutral: the bit-exact determinism gate reproduces the
  pre-migration baseline exactly — identical voxel and coverage hashes, identical
  Cd/Cl per flow-through, identical probe readings.
- **Validation harnesses folded into the editor assembly.** The loose scripts in
  `Assets/Editor/` now live under `Assets/Motawea/WindTunnel/Editor/` split by role:
  `Validation/` for the benchmarks and studies the README's numbers are quoted from,
  `Regression/` for the gates run before and after a change. Three spent one-off
  diagnostics were dropped (`AeroDiag`, `AeroDiagRR`, `AeroDuctDiagnostic` — the last
  probed a scene marker that no longer exists).
- **Two harnesses repointed** after the vehicle removals: `AeroAutoFitTest` uses the
  Molnia body for its motorsport case, and `AeroCompareLive` now runs two subjects
  (SUV and pickup) instead of three. Both suites pass — 125 and 73 checks.


Also in this release:

- **Real-world impact section in comparison exports** (`AeroRealWorld`): the CdA
  delta is translated into what a buyer asks about — highway/mixed/urban fuel,
  litres and CO₂ per year, EV range, and the measured aero power at the test
  speed — with every assumption printed beside its number. Two honesty rules are
  enforced in code: a delta inside the measurement's noise band produces a
  literal "No claim" instead of consequences, and every derived figure spans
  0.7–1.0× of the measured delta because cross-vehicle validation measures
  pairwise deltas exaggerated by up to ~30 %. Aircraft and watercraft
  comparisons keep only the measured rows.

- **Honest purpose block + console theme on exported pages.** Both the
  comparison page and the session report now end with "What this page is — and
  is not": the validated strengths, the measured blind spots (smooth-surface
  separation, sub-4-cell features, road-car lift) and the ~2× absolute bias,
  each traceable to a stored validation report. Both pages now wear the runtime
  console's theme (obsidian/silver/petrol-teal, teal-A/violet-B identities,
  verdict colours reserved) via a shared `AeroHtmlTheme`, so the in-sim modal
  and the exported documents read as one product.

- **Wall-model prototype** (`WindTunnelDomain.wallModel`, default **off**, behind
  the `AERO_WALL_MODEL` keyword): Spalding's law solved per near-wall cell,
  applied as the eddy viscosity that reproduces law-of-the-wall shear at the
  first fluid cell off the body. The Stage 2.5 verdict, measured at 7.4 mm
  (y/δ ≈ 0.22): it restores near-wall effective Re to ~physical (ν_T 473× → 0.5×
  molecular) **and the Ahmed slant sweep does not move** (spread 8 % vs published
  51 %) — the blocker is the bulk SGS viscosity a coarse grid needs for
  stability, which caps effective Re at ~10³ and separates the flow at the nose.
  An SGS-magnitude experiment (Cw 0.5 → 0.15) destabilises into grid noise rather
  than attaching, pricing the real fix at ~1–2 mm surface cells (Re_eff ∝ Δ⁻²).
  Known prototype defect: ν_w is unbounded and blows up on soft-off staircase
  bodies — do not enable outside experiments. Positioning unchanged; the plan's
  Stage 3/4 are parked pending the §5.1 fork in docs/IMPROVEMENT-PLAN.md.

- **Eddy-viscosity diagnostics tap** (`LbmSolver.CaptureNuT`, behind the
  `AERO_NUT_TAP` shader keyword so the off state compiles bit-identical to the
  determinism baseline — a uniform branch was measured to move it via last-bit
  rounding). First measurements (`AeroAhmedDiag.Run`, Ahmed 25° at 15 mm): WALE
  ν_T runs at ~500–900× molecular viscosity, putting the effective Reynolds
  number at ~10³ against a physical 7.7×10⁵, and the slant flow is separated at
  all probed stations. The benchmark's slant-blindness is therefore a faithful
  simulation of the wrong Reynolds number — measured confirmation of the wall
  model's premise, not just an inference from Cd values.

- **FP16 lattice storage** — the distribution buffers now hold 16-bit deviations
  from the ambient equilibrium weight (`f[i] − W[i]`), packed two directions per
  uint: **190 → 118 bytes per cell**, with all arithmetic still in 32-bit float
  and stores rounded to nearest (D3D's own conversion truncates toward zero,
  a bias the lattice would integrate). Measured cost on the 20-flow-through
  reference bodies: plate +0.35 %, cube +0.44 %, sphere −1.9 %; no throughput
  change. The cell guard rises 20 M → 80 M to match, and the Ahmed body now runs
  at 7.4 mm / 51 M cells (Cd 1.057 ± 1.8 % — still bluff-box territory, so the
  benchmark's error is confirmed to not be resolution). The binding grid ceiling
  is now Unity/D3D11's 2 GiB per-ComputeBuffer cap (~53.7 M cells), not VRAM.
  The bit-exact demo baseline is re-recorded under FP16.

- **Interpolated (Bouzidi) bounce-back** (`WindTunnelDomain.interpolatedWalls`,
  default off). Half-way bounce-back always places the wall exactly midway
  between a fluid and a solid cell centre, turning every smooth surface into a
  staircase of cell-sized steps. This places it at its true sub-cell position
  instead. The sub-cell solid fractions are now preserved in their own
  `_SurfaceFraction` buffer — `Finalize` overwrites `_Coverage` with 1.0 on
  solid cells, which had been destroying exactly the information needed — and
  the 3×3×3 sub-raster runs whenever soft voxels *or* interpolated walls are on.
  Measured (`aero_interpolated_walls.txt`, A/B with only the toggle changed):
  **sphere 0.625 → 0.574, −8.1 %** (1.39× → 1.28× of published), while the cube
  stays inside its noise band and the toggle-off path is bit-identical to the
  stored determinism baseline. The **Ahmed body does not move at all**, which
  rules out geometry representation as the cause of its slant-blindness and
  leaves boundary-layer physics as the sole explanation. Recommended for smooth
  curved bodies with soft voxels off; not enabled globally, because gray
  bounce-back and Bouzidi are two models of the same sub-cell geometry and
  stacking them made the soft-voxel sphere case visibly noisier.

- **Ahmed body benchmark** (`AeroAhmedBody` + `Editor/Validation/AeroAhmedTest.cs`, and
  GameObject ▸ Wind Tunnel ▸ Ahmed Body): the standard automotive bluff-body reference,
  generated from its published dimensions and dimension-checked before any flow is
  solved. Closes the last outstanding validation item in `docs/DESIGN.md`.
  **Result: the solver is blind to the rear slant.** Published Cd varies 51 % across
  0/25/30/35°; ours varies 5 %, and every angle lands near the drag of a plain
  square-backed box. A resolution sweep (20 → 10.5 mm) shows the answer *converges*,
  on the wrong value, so the error is not discretisation. Positioning updated
  accordingly: deltas from size and gross bluffness are real, deltas from where the
  flow separates on a smooth surface are not.

- **Comparison export**: an EXPORT COMPARISON button at the bottom of the modal
  writes the audit, metric table, sweep and verdict as a self-contained HTML
  page (`AeroComparisonExporter`), carrying the same A/B identity colours as
  the modal. The audit comes first: a page of bare deltas invites trust in
  numbers whose basis the reader cannot see.
- Two bugs found by running the feature end-to-end on real solver output
  rather than synthetic sessions (`Editor/Regression/AeroCompareLive.cs`):
  the averaging window was sized off the *nominal* sample interval, but the
  tunnel only samples between tick batches (100 steps at 32 steps/tick is
  really 128), so the window never filled and the uncertainty was never
  computed; and a point that stopped at the step cap reported no uncertainty at
  all, when its mean's error is perfectly knowable. A wide band is a result, an
  absent one is a gap.
- Verdict caveats now name what was flagged ("grid, soft voxels were flagged
  above") instead of pointing vaguely at the audit — including when the flag is
  the runs' own uncertainty, which is not a settings difference at all.

- **A measurement point is now the mean of an averaging window, not one
  instantaneous reading.** A bluff-body wake never stops oscillating — the grid
  study measures 4.8–5.9% run-to-run scatter on an SUV at every resolution — so
  a single sample was a point on that oscillation, and the 1% convergence
  tolerance was unreachable by construction: every point hit the step cap and
  was recorded as unconverged. Each point now reports the mean over
  `averageOverFlowThroughs` (default 3, after a 1.5 flow-through settling
  allowance) plus the **standard error of that mean**, and "settled" means the
  mean is stable inside its own uncertainty. The error is divided by
  flow-throughs, not samples: samples inside one flow-through are the same
  eddies passing and are not independent.
- The averaging window **grows with the run** instead of trailing at a fixed
  span. A fixed span pins the uncertainty at whatever that span buys (~5% on an
  SUV at 3 flow-throughs) however long the point runs, so a longer cap bought
  nothing; averaging everything since the wake settled makes the uncertainty
  fall as 1/√(flow-throughs), which is what lets run length buy precision.
  `averageOverFlowThroughs` is now the *minimum* span. The runner reports up
  front how many flow-throughs a test's step cap can afford.
- Lift is scored only between two vehicles of the **same class**, and side
  force only in a **yaw sweep**. A race car's −3.8 downforce beats a road car's
  +3.3 lift under a shared "lower is better" rule while meaning nothing — they
  are not competing at the same task — and at zero yaw both bodies read a Cy of
  ~0.03, where a 27% difference is wake jitter, not a result.
- The comparison's noise band is now that standard error rather than the raw
  sample scatter, which is what makes a real 2% improvement resolvable instead
  of buried. Exports carry the uncertainty (`cd_uncertainty` in CSV, a ± column
  in HTML), because a number without one is a claim rather than a measurement.
- `AutoFitSettings.matchCellSizeM` locks the **cell size** instead of the tier,
  solving for the streamwise cell count. The auto-fit scales each domain to its
  own vehicle, so a shared tier gives every vehicle a *different* cell size —
  which forced the comparison tool to caveat every cross-vehicle A/B. Two
  vehicles fitted with the same locked value now solve at the same resolution.
- Comparability gained a `Note` level between Ok and Warning. A road car
  against a race car shares the reference-area convention and the drag
  objective, so it is stated, not caveated — and no longer drags the verdict's
  confidence down with it.

- Comparison modal restyled to the runtime console's theme, and it now carries
  that theme itself: `Resources/WindTunnel/AeroComparison.uss` ships with the
  package and is loaded by the view, so the modal looks the same hosted by the
  HUD or by the editor window instead of borrowing the project's stylesheet.
  Thin themed scrollbars with no arrow buttons, hover/active states on every
  button and list row, and compact dropdowns instead of Unity's default field
  chrome. **A and B now have identity colours** — teal and violet, kept clear
  of the green/amber/red the verdicts use — carried through the picker titles,
  the selected rows, a hairline down the A and B table columns, the status
  strip and the winner chip, so a side can be followed without reading labels.

- `AeroVehicle.displayName`: the name used in reports, exported file names,
  screenshot file names, the comparison picker, the runtime HUD and the editor
  dashboard. Falls back to the GameObject name when empty, so nothing has to be
  filled in for a vehicle to identify itself — but "range-rover-sport-svr-2022"
  stops being what a client sees on a report. Read it through
  `AeroVehicle.Name`, never `.name`.

## [0.3.0] - 2026-08-11

Vehicle classes, tunnel auto-fit, and result comparison.

- **Vehicle classification** (`AeroVehicle.vehicleClass`): RoadVehicle,
  Motorsport, Aircraft, Watercraft, ReferenceBody. The class is the single
  switch that sets the ground boundary condition, wheel rotation, the working
  fluid, the reference-area convention, how the body is seated in the tunnel,
  and which direction of lift counts as an improvement — so an aircraft is no
  longer tested as a car with the floor left on.
- **Tunnel auto-fit** (`WindTunnelDomain.FitToVehicle`, `AutoFitSettings`):
  sizes the domain from the measured body (clear air upstream/downstream/
  lateral/overhead in body extents, per class), grows the cross-section until
  blockage meets a target (default 5%), seats the vehicle at its station
  (contact patches or waterline on the floor, or centred in free air), pins the
  tunnel floor to the scene's ground plane, re-aims the smoke rakes and slice
  plane, and picks the finest resolution tier that fits a GPU memory budget.
  Runs on a vehicle swap (`CarSpawner`), from the tunnel inspector, from the
  dashboard, and from the setup wizard. A tunnel that has never been fitted is
  never touched implicitly, so hand-built validation rigs keep their domains.
- **Watercraft**: two modes. `AboveWaterlineAir` puts the waterline on the
  tunnel floor and tests the superstructure in air, as ship wind loads are
  really measured; `SubmergedHull` runs the whole hull in water (density and
  viscosity from standard temperature fits, fresh or sea). Neither models a
  free surface, so **wave-making resistance is absent** — documented, not
  hidden.
- **Working fluid**: `AirProperties.medium` adds fresh water and sea water
  alongside air. Air keeps the ideal-gas / Sutherland treatment.
- **Planform reference area**: new `ComputeStatsPlanform` voxelizer kernel
  measures the silhouette projected from above. Aircraft coefficients are
  divided by it (the aeronautical convention) instead of by the frontal area,
  which would flatter a wing by an order of magnitude. Blockage is always
  computed from the frontal silhouette regardless of the reference basis.
- **Angle-of-attack sweep** (`AeroTestKind.AngleOfAttackSweep`): the aircraft
  counterpart of the yaw sweep, pitching about the lateral axis (positive =
  nose up). `AeroTestDefinition.StandardQueue` builds the procedure list a
  given vehicle class actually calls for.
- **Result comparison** (`AeroComparison`, `AeroComparisonView`, "COMPARE
  RESULTS" in the runtime HUD and Window ▸ Wind Tunnel ▸ Compare Results): pick two
  exported sessions, and before any numbers are differenced a like-for-like
  audit checks procedure, vehicle class, reference-area basis, working fluid,
  speed, ground simulation, grid, soft voxels, package version, blockage and
  convergence. A mismatched procedure, working fluid or reference-area
  *convention* (frontal vs planform) **blocks** the comparison; a class
  difference that keeps the same convention (road car vs race car), a hand-set
  reference area, and grid or soft-voxel differences are caveats the verdict
  repeats. Lift is left unscored when the two classes disagree about which
  direction is better.
  The session records the reference-area convention *before* the SAE area lock
  turns it into an override, so a locked run can still be told apart from a
  planform one.
  The metric table shows A, B, Δ, Δ% and which side is better per metric
  polarity (lower Cd is better; lift depends on the class; aircraft are scored
  on L/D), and a delta smaller than the runs' own convergence scatter is
  reported as "too close to call" instead of a winner.
- **Machine-readable exports**: every export now also writes
  `<name>.windtunnel.json` carrying the full test configuration —
  `AeroReportExporter.ExportAll` writes HTML + CSV + JSON together. CSV reports
  can still be loaded as a fallback, flagged as incomplete metadata since a CSV
  never recorded the grid or the vehicle class. Sessions record per-point
  convergence CV, tunnel size, tier, cell size, soft-voxel state, LES constant
  and package version.

Also in this release — vehicle-surface heatmaps:

- Vehicle-surface heatmaps (`SurfaceHeatmap`): paints Cp, the wall-shear
  pattern (near-wall tangential speed, relative), or speed ratio directly onto
  the car's bodywork, ParaView-style. While enabled the component caches the
  vehicle's materials and swaps in one shared unlit material whose shader
  samples the display field ~2 cells off the surface along the normal
  (fluid-only reconstruction via the `FluidMask` open fraction); disabling
  restores the originals. Sealed pockets the flow never reached render as
  no-data gray. Toggle + color-scale legend (Pa for pressure) in the
  dashboard's new "Vehicle surface" controls; survives CarSpawner-style
  vehicle swaps and restores originals around scene saves.
- Color ramps factored into a shared `AeroRamps.hlsl` include (used by the
  slice plane, scanner and surface heatmap) with a C# mirror (`AeroRamps`)
  for UI legends.
- Surface heatmap sampling upgraded from raw trilinear to a 2-cell box ×
  trilinear kernel (8 taps at the ±0.5-cell corners): the box has an exact
  spectral zero at the lattice Nyquist frequency, so the LBM's period-2
  checkerboard mode is nulled — not merely attenuated — and the cell-blob
  pixelation smooths into a quadratic-B-spline reconstruction.
- Display snapshot is now a temporal exponential average
  (`LbmSolver.DisplaySmoothing`, default 0.15; 1 = old raw copy). Standing
  acoustic waves in the field change phase between Step() batches, which made
  surface-heatmap/slice colors shimmer; averaging across batches cancels them
  while the wake, evolving over many batches, stays sharp. Visual only —
  forces never read the display texture.
- Surface pressure now uses the signed rainbow ("jet") ramp with green at
  zero — the industry surface-plot convention — instead of the slice plane's
  white-centered diverging ramp, so mildly loaded panels read green rather
  than blue. The slice plane keeps its original Cp ramp.
- Runtime HUD: VEHICLE SURFACE section (OFF / PRESSURE / SHEAR / SPEED +
  range sliders) in the visuals panel, and a bottom-center color key
  (gradient, physical-unit endpoints, plain-language color meanings) shown
  only while a heatmap is active.

## [0.2.0] - 2026-07-21

Realism upgrade: TRT + WALE.

- Collision operator: BGK → two-relaxation-time (TRT). The symmetric rate
  ω⁺ = 1/(3ν + 0.5) carries the viscosity; the antisymmetric rate is pinned at
  ω⁻ = 1 rather than slaved to a fixed magic parameter — at this operating
  viscosity (ν ≈ 1.5e-6) the classical Λ = ¼ would give ω⁻ ≈ 1e-5, leaving
  undamped ghost modes ringing against the bounce-back walls. Stable with
  molecular viscosity ~10³× lower than the BGK clamp allowed; effective
  Reynolds number rises from ~3.5e3 into the automotive range (millions at
  road-car scale).
- Sub-grid model: Smagorinsky → WALE (Cw, default 0.5, `lesCw` setting;
  replaces `smagorinskyCs`). WALE's eddy viscosity vanishes in pure shear, so
  near-wall boundary layers are no longer artificially thickened — the main
  driver of the early-separation / several-times-real drag readings on smooth
  bluff bodies. The strain input comes from the non-equilibrium momentum flux
  (grid-scale modes a finite-difference gradient cannot see), the rotation from
  central differences of the previous step; Cw = 0.5 is calibrated so sharp-body
  Cd validates Re-independently on this lattice (the classical 0.325
  under-damps it).
- Soft voxels matter more now: at automotive effective Re the voxel staircase
  on a smooth body acts as roughness (each step sheds); sub-cell coverage
  smooths it (Range Rover 1.75 → 1.13 at Ultra).
- Velocity field is double-buffered internally (WALE reads gradients from the
  completed previous step). `LbmSolver.VelocityField` returns a dedicated
  display snapshot copied once per Step() batch — a stable texture identity
  that is never UAV-bound, so visualization samples cannot race the solver
  (sampling the working ping-pong buffers caused intermittent scanner flicker
  on DX12 once the wake developed).
- Determinism fix: Reset initializes BOTH distribution buffers and velocity
  textures. Solid cells never write their f entries, so unwritten memory held
  fresh VRAM on a session's first large-grid run and recycled prior state on
  later runs — the first run of a session could read garbage (editor async
  shader compilation skips dispatches of still-compiling kernels while the
  CPU ping-pong advances) and settle on a spurious high-drag wake state
  (Range Rover 1.8 instead of 1.1). Runs are now bit-reproducible regardless
  of session order.

## [0.1.0] - 2026-07-16

Initial release.

- D3Q19 GPU lattice-Boltzmann solver (BGK + Smagorinsky LES, pull streaming,
  half-way bounce-back with moving walls).
- GPU voxelization: conservative surface rasterization + outside flood fill,
  wheel tagging, silhouette frontal-area measurement.
- Momentum-exchange force/moment sampling → Cd, CdA, Cl front/rear, Cy, drag
  power, blockage check, convergence monitoring.
- Ground simulation: open floor / fixed floor / moving belt, rotating wheels.
- Test procedures: constant-speed drag, yaw sweep (turntable), ride-height
  sweep; HTML + CSV session reports.
- Smoke-rake GPU particles and velocity/Cp slice plane (URP).
- UI Toolkit dashboard (editor window; edit-mode simulation driver).
