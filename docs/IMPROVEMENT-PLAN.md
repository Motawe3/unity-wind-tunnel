
> before open-sourcing over asset provenance. The measurements above stand as recorded.
# Wind Tunnel improvement plan

A staged, resumable plan for closing the accuracy gap in the Wind Tunnel wind tunnel. Each
stage is self-contained: it states what to build, where, how to prove it worked, and what
number it has to beat. Stages are designed to be picked up **in a fresh session with no
prior context** — everything needed is either in this file or pointed at from it.

> **Status legend:** `[ ]` not started · `[~]` in progress · `[x]` done and validated
> Mark a stage done **only** after its validation section passes and its Result box is
> filled in with the measured numbers.

---

## 0. How to work this plan

1. **Read §1–§5 first** (project context, environment, harnesses, baselines). They are the
   minimum needed to touch this codebase safely.
2. Pick the lowest-numbered unfinished stage unless the user says otherwise. Stages are
   ordered by dependency, not by appeal.
3. Work the substages in order. Compile after each (§2.3).
4. Run the stage's **Validation** section. Do not mark a stage done on a compile alone.
5. **Fill in the Result box** with real measured numbers, then tick the box.
6. Update `CHANGELOG.md`, and `docs/DESIGN.md` if the stage changes
   what the tool can claim.
7. Commit. Do **not** add Claude/AI attribution to commit messages — the repo owner has
   asked for this explicitly.

**If a stage fails to deliver its expected improvement, that is a result, not a failure.**
Record it in the Result box and say so plainly. This project's value rests on its numbers
being honest; a stage that says "tried it, moved nothing, here's the evidence" is worth
more than a silent revert.

---

## 1. Project context

**What it is.** `Assets/Motawea/WindTunnel` — a Unity 6 project (
URP, MIT) implementing a GPU lattice-Boltzmann virtual wind tunnel for vehicles. Around it
sits a demo project (`Assets/`) with sample vehicles, a runtime HUD and batch test
harnesses.

**Positioning (do not drift from this).** A design-exploration, comparison and education
tool. Not certification CFD. Deltas and trends are the product; absolute road-car drag is
not trustworthy. Every claim in the docs is backed by a measurement in `docs/DESIGN.md` — keep it that way.

**Physics core.** D3Q19 lattice, TRT collision (symmetric rate carries viscosity,
antisymmetric pinned at ω⁻ = 1), WALE LES subgrid model at Cw = 0.5, fused collide+stream
pull scheme, half-way bounce-back on voxelized solids, momentum-exchange forces in gauge
form.

### 1.1 Files that matter

| Path | What it does |
|---|---|
| `Runtime/Resources/WindTunnel/Lbm.compute` | **The physics.** Collide+stream, bounce-back, WALE, force accumulation |
| `Runtime/Resources/WindTunnel/Voxelize.compute` | Geometry → cell flags + sub-cell coverage |
| `Runtime/Solver/LbmSolver.cs` | GPU buffers, ping-pong, dispatch, force readback |
| `Runtime/Solver/AeroForces.cs` | Lattice force → engineering coefficients; sample averaging |
| `Runtime/Core/WindTunnelDomain.cs` | Tunnel: grid, units, lifecycle, sampling, auto-fit entry |
| `Runtime/Core/TunnelAutoFit.cs` | Domain sizing/placement from the vehicle class |
| `Runtime/Voxelization/VehicleVoxelizer.cs` | Voxelizer driver (12-kernel pipeline) |
| `Runtime/Testing/AeroAhmedBody.cs` | Generates the Ahmed benchmark geometry |
| `docs/DESIGN.md` | Design intent + validation status |

### 1.2 Key code landmarks

- `Lbm.compute` ~**line 245–280**: the pull loop. The `if (IsBounceBack(srcType))` branch is
  half-way bounce-back plus the momentum-exchange force. **Stage 1 edits here.**
- `Lbm.compute` ~**line 357**: `float nu = _Nu + nuT;` then `omegaP = 1/(3ν+0.5)`.
  **Stages 2.5c / 4 (wall model) insert here.**
- `Lbm.compute` ~**line 361–380**: soft-voxel gray bounce-back (`lerp(fPost, f[o], ns)`).
- `LbmSolver.cs` ~**line 85–86**: `_fA` / `_fB` = `ComputeBuffer(cellCount * 19, sizeof(float))`.
  **Stage 2 (FP16) edits here.**
- `Voxelize.compute`: `_Coverage` (solid fraction 0..1 per cell) — the input Stages 1 and 4
  both need for sub-cell wall geometry.

### 1.3 Cross-file invariants (breaking these gives wrong physics, not compile errors)

- `CELL_*` values must match across `Voxelize.compute`, `Lbm.compute`, `AeroCellType`.
- `FORCE_SCALE` / `MOMENT_SCALE` must match between `Lbm.compute` and `LbmSolver.cs`.
- `_Stats` slot count must match between `Voxelize.compute` and `VehicleVoxelizer.StatsSlots`.
- `_FluidMask` is the **open fraction**, not a binary mask — samplers divide by it.
- `_Coverage` contract: partial cells stay `CELL_FLUID`; only porous-flood-reached surface
  cells go partial, or interior cabin geometry becomes porous and fake interior-pressure
  forces return.
- **Diagnostic taps in `Lbm.compute` must live behind a shader keyword** (`#pragma
  multi_compile _ AERO_NUT_TAP` pattern), not a uniform branch. Even a never-taken
  branch changes compiled code → changes last-bit rounding → the chaotic wake amplifies
  it → the bit-exact demo gate moves. Measured the hard way in Stage 2.5a.
- **Any GPU buffer a kernel can skip writing must be explicitly initialised.** Solid cells
  early-return in `LbmStep` and never write their `f`; leaving the second ping-pong half
  uninitialised made the first large-grid run of a session read recycled VRAM.
- Consumers sample `LbmSolver.VelocityField` (the display snapshot), never the working
  ping-pong textures.
- Every new scene component needs `[ExecuteAlways]` **and** lazy resource init — plain
  MonoBehaviours get no lifecycle callbacks in edit mode.
- `WindTunnelVersion.Value` must match the release tag.

---

## 2. Working environment

### 2.1 Unity

`C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Unity.exe` — project at
`F:\Personal\Unity\VehicleAerodynamicsSimulation`.

### 2.2 Batch runs (needs the editor CLOSED)

```powershell
$unity = "C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Unity.exe"
$proj  = 'F:\Personal\Unity\VehicleAerodynamicsSimulation'
$p = Start-Process -FilePath $unity -ArgumentList `
     '-batchmode','-projectPath',$proj,'-executeMethod','AeroAhmedTest.Run', `
     '-logFile',"$proj\run.log" -Wait -PassThru
"exit=$($p.ExitCode)"
```

- **Never pass `-nographics`** — every harness needs a GPU.
- `& Unity.exe …` does **not** wait (GUI-subsystem exe). Use `Start-Process -Wait -PassThru`.
- If the editor is open, batch dies immediately with
  `Exiting without the bug reporter … return code 1` right after "changed project path".
  Check `Temp/UnityLockfile`.
- **Only one Unity at a time.** Launching a second while the first runs produces exactly the
  same lock failure, and stale `.txt` reports from the previous run will look like results.
  Delete report files before a run.

### 2.3 Compiling while the editor is OPEN (Roslyn)

Batch is locked, so verify compilation directly. Build response files from the generated
csproj reference lists, then invoke Unity's bundled Roslyn:

```powershell
$csc = "C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Data\DotNetSdkRoslyn\csc.dll"
& dotnet $csc "@<scratch>\runtime.rsp"     # Packages/…/Runtime/**/*.cs
& dotnet $csc "@<scratch>\pkgeditor.rsp"   # Packages/…/Editor/*.cs
& dotnet $csc "@<scratch>\editor.rsp"      # Editor/Validation + Editor/Regression
```

Each `.rsp` = `-target:library -nostdlib+ -langversion:9.0`, `-r:` every `<HintPath>` from
the matching csproj (`Motawea.WindTunnel.csproj`, `Motawea.WindTunnel.Editor.csproj`,
`Assembly-CSharp-Editor.csproj`), then the source paths. For the two editor assemblies,
drop the stale `Motawea.WindTunnel.dll` HintPath and reference the freshly-built one instead.
**Regenerate the `.rsp` after adding a file** — they are built from globs and will silently
miss new sources.

### 2.4 Notes

- Compute-shader changes only compile inside Unity; Roslyn will not catch HLSL errors. A
  batch run is the only real check.
- Git pushes sometimes hang on the credential manager. If `git push` stalls, the commit is
  safe locally — ask the user to run `git push origin main` themselves.

---

## 3. Test harnesses

All write a `.txt` report to the repo root. Benchmarks live in
`Assets/Motawea/WindTunnel/Editor/Validation/`, gates in `.../Editor/Regression/`.

| Harness | Entry point | Output | Purpose |
|---|---|---|---|
| Update suites | `AeroUpdateTests.Run` | `aero_compare_test.txt`, `aero_autofit_test.txt` | Comparison engine + auto-fit unit checks (fast, ~2 min) |
| Ahmed benchmark | `AeroAhmedTest.Run` | `aero_ahmed_body.txt` | **The accuracy benchmark.** Slant sweep vs published |
| Ahmed resolution | `AeroAhmedTest.RunResolutionCheck` | `aero_ahmed_resolution.txt` | Is the error discretisation or modelling? Takes `-aeroInterpolatedWalls` |
| Bouzidi A/B | `AeroInterpolatedWallsTest.Run` | `aero_interpolated_walls.txt` | Paired A/B of interpolated vs half-way walls |
| Reference bodies | `AeroValidate.Run` | `aero_validate.txt` | Plate / cube / sphere vs published |
| Grid convergence | `AeroGridConvergence.Run` | `aero_grid_convergence.txt` | Cd vs cell size on a fixed domain |
| Live comparison | `AeroCompareLive.Run` | `aero_compare_live.txt` | End-to-end: fit → run → export → compare → HTML |
| Determinism gate | `AeroDemoTest.Run` | `aero_demo_test.txt` | **Bit-exact regression gate.** Voxel hashes + Cd per flow-through |
| Fit accuracy | `AeroFitAccuracyTest.Run` | `aero_fit_accuracy.txt` | Hand-sized vs auto-fitted tunnel |
| Gap flow | `AeroGapMatrix.Run` | `aero_gap_matrix.txt` | Minimum cells for a slot to flow |
| Ahmed diagnostics | `AeroAhmedDiag.Run` | `aero_ahmed_diag.txt` | Effective Re (ν_T tap), slant attachment probe, y/δ. Takes `-aeroCells` |

Each harness writes its report to the **project root** under the bare name shown above.
Archived copies of the runs these docs cite are tracked in [`docs/validation/`](validation/);
fresh runs at the root are gitignored so a re-run never dirties the tree.

**Before and after any solver change, run `AeroDemoTest.Run`.** It is bit-reproducible on
unchanged physics. If its hashes or Cd values move, you changed the physics — verify that
was intended.

---

## 4. Validated baselines (the numbers to beat)

Measured 2026-08-11. Any stage claiming an improvement must move one of these.

### 4.1 Ahmed body — the primary benchmark
15 mm cells, 40 m/s, fixed floor, identical grid per angle, reference area 0.11203 m².

| slant | Cd measured | ± | Cd published | ratio |
|---|---|---|---|---|
| 0° | **1.082** | 1.9 % | ~0.250 | 4.33× |
| 25° | 1.034 | 1.6 % | ~0.285 | 3.63× |
| 30° | 1.071 | 1.4 % | ~0.378 | 2.83× |
| 35° | 1.029 | 1.4 % | ~0.260 | 3.96× |

**Spread across angles: 5 % measured vs 51 % published.** The solver is blind to the rear
slant; every angle reads like a plain square-backed box.

Resolution sweep on 0°: 20 mm → 1.177, 15 mm → 1.082, 12 mm → 1.019, 10.5 mm → **1.022**.
It converges — on the wrong value. The residual error is **not** discretisation.

### 4.2 Reference bodies (free air, 12×5×5 m @ 256, soft voxels off)

| Case | measured | published | ratio |
|---|---|---|---|
| Flat plate, normal | 1.128 | 1.17 | 0.96× |
| Cube, face-on | 0.900 | 1.05 | 0.86× |
| Sphere | 0.645 | ~0.45 | **1.43×** |

Plate and cube are Reynolds-independent (separation pinned at a sharp edge) — they test
numerics, not turbulence modelling. **A change that moves these has broken something.**
The sphere's excess is largely voxel staircasing on a curved surface.

### 4.3 Range Rover grid convergence (fixed auto-fitted domain)

| cell | 228 mm | 152 mm | 114 mm | 76 mm | 57 mm |
|---|---|---|---|---|---|
| Cd | 1.318 | 1.017 | 0.979 | 0.836 | 0.820 |
| vs real 0.36 | 3.66× | 2.82× | 2.72× | 2.32× | 2.28× |

Converges monotonically, plateaus at ~2.3×. Lift does **not** converge (0.52, 0.82, 2.28,
0.98, 0.65) — road-car Cl is not a reportable quantity.

### 4.4 Cross-vehicle (cell size locked at 90 mm)

| vehicle | CdA sim | CdA real | ratio |
|---|---|---|---|
| Range Rover Sport SVR | 2.099 | ~1.04 | 2.01× |
| Porsche 911 GT3 | 1.309 | ~0.78 | 1.68× |
| McLaren F1 GTR Longtail | 1.161 | ~0.72 | 1.61× |

> The Porsche and McLaren models are no longer bundled with the project — they were
> dropped before open-sourcing over asset provenance. The measurements above stand as
> recorded.

Ranking correct. Pairwise ratios exaggerated 4–25 % — the bias is largest on the bluffest
body, so it does not cancel between different shapes.

### 4.5 What the tool may and may not claim today

- ✅ Deltas from **size, frontal area, gross bluffness** — ranking validated.
- ✅ Sharp-edged reference bodies — validated.
- ✅ Flow visualization as a diagnostic and teaching instrument.
- ❌ Deltas from **where flow separates on a smooth surface** (slant, roofline, spoiler) —
  the Ahmed benchmark shows 51 % of real variation registering as 5 %.
- ❌ Absolute road-car Cd (~2.3× high) and road-car lift (does not converge).

---

## 5. Why the stages are in this order

The Ahmed resolution sweep proved the residual error is **not** discretisation, so:

- Finer cells alone will never fix it → refinement is not an end in itself.
- A wall model is the physics that is missing, **but** it needs the first fluid cell inside
  the log layer (y/δ ≲ 0.2). At every affordable uniform resolution the first cell sits at
  y/δ ≈ 0.5–0.8 — outside the boundary layer. Feeding a law-of-the-wall a velocity from
  there makes it a tuning knob, not physics.
- Getting into validity needs ~15 mm cells at the surface. Uniformly on a car domain that
  is ~6×10⁸ cells (≈114 GB). With refinement it is ~6–15×10⁶ — already affordable.

**Stage 1 (done)** was cheap and attacked something refinement provably cannot (staircase
steps do not shrink relative to δ). Its Ahmed null result eliminated geometry
representation as a suspect — near-wall physics is now the only remaining explanation, and
the wall model is the load-bearing bet of this plan.

That changes the ordering logic downstream of Stage 2. Stage 3 (refinement) is the most
expensive item in the plan, and its payoff is entirely contingent on the wall model
working. But the wall model's hypothesis can be tested **without** refinement: the Ahmed
domain is small enough that Stage 2's memory savings put uniform ~7 mm cells (first fluid
cell at y/δ ≈ 0.3 against the slant boundary layer) within reach, and ~5 mm (y/δ ≈ 0.23,
inside the validity window) if VRAM allows. So:

- **Stage 2** cuts memory ~4× (FP16, then single-buffer streaming). It buys resolution
  everywhere, and specifically it makes the Stage 2.5 gate affordable.
- **Stage 2.5** is the decision gate: diagnostics plus a wall-model prototype on
  uniform-fine Ahmed. If the slant sweep spreads, Stage 3 is justified by evidence; if it
  does not, the most expensive stage gets rethought **before** it is built, not after.
- **Stage 3** then exists to carry a *proven* wall model to car-sized domains, where
  uniform fine cells are impossible (~15 mm at the surface = ~6×10⁸ uniform cells
  ≈ 114 GB; with refinement ~6–15×10⁶).
- **Stage 4** productionises the wall model on top of Stage 3.

### 5.1 — The fork after the gate (added 2026-08-12; owner's decision pending)

Stage 2.5 answered NO: a working wall model at y/δ ≈ 0.22 does not move the slant sweep,
because the SGS viscosity a coarse grid needs for stability caps the effective Reynolds
number at ~10³ and the flow leaves the nose before the tail matters. The measured scaling
(ν_T ∝ Δ² → Re_eff ≈ 2×10³·(7.4 mm/Δ)²) prices the real fix:

- **Option A — re-scope Stage 3 as a 1–2 mm WMLES shell.** 3–4 nesting levels, ~30 M fine
  cells around an Ahmed-sized body (feasible memory-wise), but fine-level sub-cycling puts
  a flow-through at hours-to-days on this GPU, and the refinement subsystem triples in
  complexity (multi-level, not one level). High cost, real (no longer hopeful) physics
  basis, still no guarantee of the published 51 % spread.
- **Option B — reposition.** Keep the validated claims (§4.5: size/bluffness deltas,
  sharp-edged bodies, flow visualisation, education), fold the 2.5 instruments into the
  product (effective-Re readout tells users what regime they are in), and spend the
  effort on Stage 5's usability items instead. Smooth-surface separation is declared out
  of scope with a measured justification rather than a shrug.

Neither is picked here. Stages 3 and 4 stay parked until the owner chooses.

---

## Stage 1 — Interpolated (Bouzidi) bounce-back

- [x] **Stage 1 complete** — implemented, fully validated, mixed result (see Result box)

**Goal.** Replace half-way bounce-back with sub-cell-accurate wall placement, so a curved
surface stops being a staircase of forward-facing steps.

**Why now.** Half-way bounce-back always places the wall exactly halfway between a fluid
and a solid cell, so any smooth surface becomes a stair. Step height is ~1 cell
(10–20 mm) against a boundary layer of ~11 mm, and **refining does not reduce the ratio** —
both shrink together, which is exactly what §4.1's flat resolution sweep shows. Bouzidi
*removes* the steps rather than shrinking them, so it attacks something grid refinement
provably cannot. Cheap enough to be worth testing even though the outcome is uncertain.

**Expected outcome.** Honestly uncertain. The sphere (1.43×, error dominated by
staircasing) is the most likely to move. The Ahmed 0° case moves **if** early separation is
being tripped by nose staircase steps — that is the hypothesis under test.

### 1a — Sub-cell wall geometry
> Rewritten to match what was built. The original text specified a reconstructed wall
> plane (4 floats/cell); the real obstacle turned out to be upstream — see the deviation
> note in the Result box.

- [x] Preserve the raw sub-cell solid fraction in a new `_SurfaceFraction` buffer
      (**1 float/cell**), written by `ComputeCoverage`. `Finalize` overwrites `_Coverage`
      with 1.0 on every solid cell, which was destroying the only record of where inside
      that cell the surface lies.
- [x] Run the 3×3×3 sub-raster whenever soft voxels **or** interpolated walls are on
      (`_SubCellRaster`), so sub-cell fractions exist even with soft voxels off.
- [x] Derive q per direction at use time as the 0.5 iso-level of the fraction field
      between the two cells along the link: `q = (0.5 − s_here) / (s_wall − s_here)`,
      clamped; falls back to 0.5 when the two cells report no gradient.
- [x] Initialise the buffer explicitly in `Clear` (see §1.3 — a kernel may skip writing it).

### 1b — Bouzidi interpolation in the pull loop
- [x] `Lbm.compute` ~line 251, in the `IsBounceBack(srcType)` branch. Replace the plain
      `f[i] = fOut + 6·W[i]·(C[i]·uw)` with the standard two-branch Bouzidi rule:
      - q < ½: interpolate using the population from the *next* cell along the link
      - q ≥ ½: interpolate between the reflected and local populations
- [x] Fall back to plain half-way bounce-back where the plane is degenerate (no valid
      normal, q outside [0,1]) so the change can never make a cell worse than today.
- [x] Gate the whole thing behind a `WindTunnelDomain` toggle (`interpolatedWalls`,
      default **off** until validated) so every existing baseline stays reproducible.

### 1c — Force integration consistency
- [x] The momentum-exchange sum in the same branch assumes the wall is at the halfway
      point (`wallPos = c + 0.5 + 0.5·C[o]`). With Bouzidi the wall is at q — update
      `wallPos` accordingly, or drag and the moment arm are computed at the wrong place.
- [x] Keep gauge form (`− 2·W[i]`) intact. Verify the plate/cube still validate (§4.2):
      those are the check that force integration was not broken.

### 1d — Validation
- [x] `AeroDemoTest.Run` with the toggle **off** → must be bit-identical to the stored
      baseline. Proves the feature is truly opt-in.
- [x] Plate and cube must not move materially. **Run via
      `AeroInterpolatedWallsTest.Run`**, not `AeroValidate` — a paired A/B (each case run
      twice, toggle flipped, everything else identical) isolates the feature better than
      comparing against a table taken under different averaging. Cube held inside its
      band; plate moved 3.2 %, explained in the Result box.
- [x] **Sphere** is the primary target — same A/B harness. 0.625 → 0.574.
- [x] Ahmed 0° at 15 mm, both soft-voxel states — same A/B harness. Unmoved.
- [x] `AeroAhmedTest.RunResolutionCheck -aeroInterpolatedWalls` → does the converged value
      move? **Run 2026-08-12** → `aero_ahmed_resolution_bouzidi.txt`. Small consistent
      shift, no change to the conclusion — see the addendum in the Result box.

**Done when:** the A/B harness runs clean, the toggle-off path is bit-identical, and the
Result box below records what moved and what did not.

> All validation items have run.

> **Result — implemented 2026-08-11, `aero_interpolated_walls.txt`.**
> A/B harness `Editor/Validation/AeroInterpolatedWallsTest.cs` runs each case twice, identical
> in every respect but the toggle, so the difference *is* the feature. 5 flow-throughs
> averaged (shorter than §4.2, so absolute values sit slightly below the canonical table).
>
> | case | half-way | Bouzidi | change | band | verdict |
> |---|---|---|---|---|---|
> | plate, soft off | 1.037 | 1.071 | +3.2 % | ±2.1 % | moved slightly — see note |
> | cube, soft off | 0.944 | 0.899 | −4.7 % | ±5.7 % | inside noise ✓ |
> | **sphere, soft off** | **0.625** | **0.574** | **−8.1 %** | ±2.8 % | **real, toward published** |
> | sphere, soft on | 0.657 | 0.726 | +10.5 % | ±12.2 % | inside noise, and noisy |
> | Ahmed 0°, soft on | 1.073 | 1.070 | −0.4 % | ±3.9 % | inside noise |
> | Ahmed 0°, soft off | 1.085 | 1.097 | +1.1 % | ±4.1 % | inside noise |
>
> **The hypothesis held for the sphere and failed for the Ahmed body.**
>
> - ✅ **Sphere improves 8 %** (ratio 1.39× → 1.28× of published). Its error really was
>   dominated by voxel staircasing on a curved surface, and removing the steps recovers
>   part of it. This is the feature working as intended.
> - ❌ **Ahmed body does not move at all**, in either soft-voxel state. Early separation is
>   therefore **not** being tripped by staircase steps on the rounded nose. That was the
>   stated hypothesis and it is refuted.
> - ✅ Toggle **off** is bit-identical to the stored `AeroDemoTest` baseline — the feature
>   is genuinely opt-in and no existing result moved.
> - ⚠️ The **plate moved 3.2 %**, just outside its ±2.1 % band. Not a force-integration bug
>   (the cube would have moved too, and it did not): that "plate" is 20 mm thick in 47 mm
>   cells, i.e. sub-cell thin, so sub-cell wall placement legitimately changes its
>   effective thickness. Bouzidi is not perfectly neutral on sub-cell-thin geometry.
> - ⚠️ **Sphere with soft voxels ON went noisy** (±6 % against ±1 % elsewhere) and gave no
>   conclusion. Gray bounce-back and Bouzidi are two different sub-cell wall models, and
>   stacking them is questionable — see 1e.
>
> **Value to the project.** The Ahmed null result is the more useful half: it removes
> geometry representation as a suspect and leaves boundary-layer physics as the sole
> explanation for the benchmark's flatness. That *strengthens* the ordering in §5 and the
> case for Stage 4.
>
> **Recommended default: off.** Turn it on for smooth curved bodies with soft voxels off.
> Do not enable it globally until 1e is resolved.
>
> **Addendum — resolution sweep with the toggle on (2026-08-12).** The 0° case re-run at
> every cell size, half-way vs Bouzidi:
>
> | cell | half-way | Bouzidi | change | ±2σ band | outside noise? |
> |---|---|---|---|---|---|
> | 20.0 mm | 1.177 | 1.172 | −0.4 % | ±3.8 % | no |
> | 15.0 mm | 1.082 | 1.069 | −1.2 % | ±2.8 % | no |
> | 12.0 mm | 1.019 | 1.011 | −0.8 % | ±2.6 % | no |
> | 10.5 mm | 1.022 | 0.992 | −2.9 % | ±3.0 % | no |
>
> Every individual shift sits inside its own noise band, but **all four point the same
> way** (sign test p ≈ 0.06) — weak evidence of a real ~1 % improvement, and no more than
> that. At the finest cell the answer is still **3.97× the published value** and 0.94× a
> plain bluff box. **The Stage 1 conclusion is unchanged**: interpolated walls do not
> recover the Ahmed body's slant sensitivity at any resolution tested.
>
> One nuance worth carrying forward: with half-way walls the sweep flattens
> (1.019 → 1.022, +0.3 %), whereas with Bouzidi it is still falling at the finest cell
> (1.011 → 0.992, −1.9 %). That may be a real difference in convergence behaviour or may
> be scatter. Distinguishing them does **not** need a refinement subsystem — it needs one
> or two finer uniform points (~8 and ~7 mm), which Stage 2's memory reduction makes
> affordable. That is Stage 2.5b.
>
> *Harness note:* running this exposed a flaw in the sweep's own auto-verdict — it judged
> convergence on the coarse-to-fine endpoint change against a fixed −15 % threshold, which
> flipped to an optimistic "refinement is working" reading on a 2-point difference while
> the answer was still 4× high. It now judges the **last refinement step** and the
> **residual gap to published**, which is the question actually being asked. Both stored
> reports were regenerated with the corrected logic.
>
> **Deviation from the plan as written.** 1a specified a reconstructed wall plane stored as
> 4 floats/cell (normal + offset). The implementation instead keeps the **raw sub-cell solid
> fraction** (1 float/cell) in a new `_SurfaceFraction` buffer and reads the 0.5 iso-level
> between the two cells along each link. Reason: `Finalize` overwrites `_Coverage` with 1.0
> on every solid cell, destroying the sub-cell information entirely — so the fix had to be
> *preserving* that data, not reconstructing a plane from data that was no longer there.
> Cheaper (1 float vs 4) and closer to the source of truth. The sub-cell raster now runs
> whenever soft voxels **or** interpolated walls are on (`_SubCellRaster`).

### 1e — Follow-up: do not stack two sub-cell wall models
- [ ] With soft voxels ON, an outer skin cell that the porous flood reached stays FLUID and
      gets gray bounce-back; Bouzidi then also repositions the wall at the next link in.
      Both model the same sub-cell geometry, and the sphere-soft-on case went visibly
      noisier with both active.
- [ ] Investigate making them exclusive: Bouzidi as the wall treatment where a clean
      surface normal exists, gray reserved for genuinely porous/thin geometry.
- [ ] Re-run the A/B afterwards; the sphere-soft-on case is the one to watch.

---

## Stage 2 — Halve the lattice memory

- [ ] **Stage 2 complete**

**Goal.** Cut bytes/cell so every existing scene gets a finer grid for free, and the
Stage 2.5 decision gate becomes affordable.

**Why.** Budget today is ~190 B/cell, dominated by two `float` buffers of 19 values
(`LbmSolver.cs:85–86`). Halving storage is ~1.26× finer in each axis at the same memory,
and refinement (Stage 3) multiplies whatever this saves.

### 2a — FP16 distribution storage
- [x] Switch `_fA`/`_fB` to 16-bit, storing the deviation `f[i] − W[i]`. Packed two
      directions per uint (dir 2k low half, 2k+1 high; 10 uints/cell, 40 B vs 76 B per
      buffer). Safe against write races because every kernel path writes all 19
      directions of its own cell. One trap beyond the plan: D3D's `f32tof16` rounds
      **toward zero** — a systematic shrink on every store that the lattice would
      integrate — so stores go through a round-to-nearest-even helper (`PackHalfRN`).
- [x] All arithmetic in `float`; conversion only in `ReadF`/`PackHalfRN`/`StoreFreestream`.
      The outlet copy moves packed words untouched (exact, no re-rounding).
- [x] `TunnelAutoFit.BytesPerCell` 190 → 118. `WindTunnelDomain.MaxCells` 20 M → 80 M
      (the old guard was sized to FP32-era memory).
- [x] Force accumulator untouched.

### 2b — In-place (esoteric pull) streaming

> **DEFERRED 2026-08-12** (user-approved) — do Stage 2.5 first. The 2 GiB per-buffer
> discovery changed 2b's payoff: it halves *total* memory but does not raise the
> per-buffer cell ceiling, and the 7.4 mm grid that Stage 2.5 needs already runs at
> 6.3 GB after 2a alone. 2b's real customer is Stage 3 (many refined blocks, total
> memory bound), so it moves to just-before-Stage-3. Nothing below has been started.

- [ ] Replace the A/B ping-pong with a single buffer using the esoteric-pull scheme, which
      halves storage again. This changes the memory layout the whole kernel indexes, so it
      is riskier than 2a — do it as a separate commit, and only after 2a validates.
- [ ] Note: the existing determinism fix (initialise **both** buffers) becomes moot, but
      the general invariant in §1.3 still applies to any new buffer.
- [ ] **Known ceiling (found in 2a validation):** Unity/D3D11 caps a single ComputeBuffer
      at 2 GiB. At FP16-packed 40 B/cell that is ~53.7 M cells per f buffer (~7.3 mm on
      the Ahmed tunnel); at FP32 it was ~28 M (~9.1 mm) — the cap was always nearer than
      VRAM. 2b does **not** move it: one buffer of the same size replaces two. Going
      finer needs the f storage split across two buffers (easy: direction pairs 0–4 in
      one, 5–9 in the other) or a D3D12 switch. Stage 2.5's 5 mm ambition needs this;
      7.4 mm does not.

### 2c — Validation
- [x] `AeroDemoTest.Run` → ran clean; voxel hashes identical. Per-FT Cd values resampled
      (mean 1.093 → 1.175 over 6 FT). **The ~0.5 % expectation was the wrong instrument**:
      6 correlated flow-throughs of a chaotic wake resample the attractor under ANY
      bit-level change; precision is judged on the long-averaged bodies below, which held.
- [x] **Demo-gate baseline re-recorded under FP16** (this commit). 2b will re-record it
      again — every physics-representation change does.
- [x] `AeroValidate.Run` (20-FT averages) → plate 1.128 → 1.132 (+0.35 %), cube
      0.900 → 0.904 (+0.44 %) — the gates hold. Sphere 0.645 → 0.633 (−1.9 %), same
      direction in both soft states; a real small quantization effect on a body whose
      separation is free to wander. Recorded as a watch item, not a failure.
- [x] `AeroGridConvergence.Run` → endpoints on top of §4.3: Coarse +0.2 %, Ultra +2.0 %,
      Extreme +0.9 %. Medium 1.017 → 1.132 (+11 %) is the outlier — the harness's own
      run-to-run scatter is ~5 % and the mid-grid RR wake is bistable-ish (the validate
      suite's ride-height rows swung both directions too). Watch item.
- [x] Previously-impossible resolution: **Ahmed 0° at 7.4 mm — 51 M cells, ~6.3 GB of
      per-cell resources, Cd 1.057 ± 1.8 %, 27 min** (`aero_ahmed_resolution_custom.txt`,
      via the new `-aeroCells` override). Impossible at FP32 twice over: the 20 M guard
      and the 2 GiB per-buffer cap (28 M cells max). Throughput matched the (dx)⁴
      extrapolation from 10.5 mm — the packing costs no speed.

> **Result — 2a measured 2026-08-12.** Lattice 152 → 80 B/cell (190 → 118 total).
> Largest grid: 18 M cells (10.5 mm Ahmed) → 51 M (7.4 mm), now bounded by the D3D11
> 2 GiB per-buffer cap, not VRAM. Precision cost: plate/cube +0.4 %, sphere −1.9 %,
> convergence endpoints ≤2 %; demo gate re-recorded. The 7.4 mm Ahmed point reads
> **1.057 — still a bluff box (1.01×)**, sharpening Stage 2.5's premise: resolution
> alone is not closing this. _(2b: fill in bytes/cell and re-validation when it lands.)_

---

## Stage 2.5 — Decision gate: wall-model prototype on uniform-fine Ahmed

- [x] **Stage 2.5 complete** — gate answered: **NO** as premised, with a quantified
      alternative premise recorded (see 2.5d and §5.1)

**Goal.** Answer the two questions that decide whether Stage 3 — a multi-block refinement
subsystem, the most expensive item in this plan — is worth building. Both are answerable
with nothing but uniform grids that Stage 2 makes affordable:

1. Is the Bouzidi sweep still falling below 10.5 mm, or was that scatter? (Stage 1's
   open nuance.)
2. Does supplying wall shear stress from a law of the wall make the Ahmed body respond
   to its slant? (Stage 4's entire hypothesis.)

**Affordability** (updated with 2a's measured reality). The Ahmed tunnel is 8.04 × 1.4 ×
1.87 m, ~18 M cells at 10.5 mm. The binding constraint is **not VRAM but the 2 GiB
D3D11 per-buffer cap** (see 2b): at FP16 it allows ~53.7 M cells ≈ **7.4 mm** (51 M
cells ran in 2a validation; first fluid cell y/δ ≈ 0.33 against the δ ≈ 11 mm slant
boundary layer). **5 mm ≈ 167 M cells** needs the f storage split across buffers (or
D3D12) *and* ~10 GB total at 2b's ~78 B/cell — right at this machine's 12 GB card, so
plan 5 mm only if the 7.4 mm prototype result justifies it. Runtime scales as
(dx ratio)⁴; ~7 min at 10.5 mm → ~30 min at 7.4 mm per angle.

### 2.5a — Diagnostics: measure the missing physics instead of asserting it
> Done 2026-08-12 — `AeroAhmedDiag.Run` → `aero_ahmed_diag.txt`. The ν_T tap
> (`LbmSolver.CaptureNuT`) lives behind the `AERO_NUT_TAP` keyword so the tap-off
> variant is bit-identical to the demo gate (verified; see the §1.3 invariant it added).

- [x] Effective viscosity, measured on Ahmed 25° at 15 mm (τ⁺ = 0.5000058, ν_lat
      1.95×10⁻⁶): **ν_T/ν mean 871×, median 670×, near-body 473×.** Implied effective
      Re over body height: **~900 (mean) / ~1,700 (near-body) vs physical 7.7×10⁵** —
      the solver runs this case ~500× below the real Reynolds number. §5's argument is
      now a measurement.
- [x] First-cell y/δ: flat-plate δ at the slant start ≈ 16.7 mm → **y/δ ≈ 0.45 at
      15 mm; ≈ 0.22 at 7.4 mm** — the prototype resolution sits at the validity edge,
      so 2.5c is a legitimate test, not a tuning-knob exercise.
- [x] Slant attachment, probed numerically (stronger than the planned eyeball image):
      u_x/U∞ at the first cells above the found surface, 9 stations down the 25° slant —
      **9/9 separated** (u₁ ≤ 0 everywhere, free stream detached ~5 cells up). Published
      25° flow is attached on the slant; the solver never attaches.
- [x] Needed no Stage 2 dependency, as predicted.

### 2.5b — Extend the Ahmed resolution sweep to ~8 mm and ~7 mm
- [x] Run 2026-08-12, same build, both arms (`aero_ahmed_resolution_custom*.txt`):
      **half-way 8.0 mm 1.066 ± 1.8 % → 7.4 mm 1.057 ± 1.8 %; Bouzidi 8.0 mm
      1.088 ± 1.9 % → 7.4 mm 1.086 ± 1.6 %.** Every step inside its own noise band.
      **The Stage 1 "Bouzidi still falling" nuance is refuted** — it came back up and
      flattened; the 10.5 mm dip was scatter. Both wall treatments plateau at bluff-box
      drag; uniform refinement is formally exhausted on this hardware.
- [x] Verdict line added: the report now prints the last step against its own combined
      ±2σ band (`that step's own ±2σ band is X — the step is inside/OUTSIDE its noise`).

### 2.5c — Wall-model prototype
- [x] Implemented per Stage 4a–4c: Spalding solved for u_τ (4 Newton iterations,
      viscous-sublayer seed), applied as ν_w = u_τ²·y/u_t − ν replacing WALE at first
      fluid cells off the **body** (ground excluded); wall distance and direction from
      the nearest bounce-back link, reusing Stage 1's q. Behind the `AERO_WALL_MODEL`
      keyword + `WindTunnelDomain.wallModel` toggle, default off; all-off variant
      verified bit-identical to the demo gate.
- [x] 15 mm smoke test (y/δ ≈ 0.45, outside validity — stability check only): stable at
      all four angles, drag −1 to −3 %, spread 6 % — the expected null.
- [x] **The verdict run: full sweep at 7.4 mm (y/δ ≈ 0.22), wall model ON**
      (`aero_ahmed_body_custom_wallmodel.txt`): 0° 1.084 ± 1.9 %, 25° 1.007 ± 2.2 %,
      30° 1.026 ± 1.7 %, 35° 1.046 ± 1.5 %. **Spread 8 % vs published 51 %. No rise to
      30°, no drop after it.** Stable and well-behaved — and ineffective on the
      benchmark's headline failure.

### 2.5d — The gate
- [x] **The sweep did not spread → Stage 3 is NOT green-lit as premised.** Diagnosed
      with 2.5a's instruments (`aero_ahmed_diag_wallmodel.txt`): with the model on at
      7.4 mm, near-body ν_T collapsed 473× → **0.5× molecular** (near-wall effective Re
      restored to 5.3×10⁵ ≈ physical) — the formulation did its job — **and the slant
      stayed separated 9/9.** The blocker is the OUTER flow: bulk ν_T still ~380×,
      effective Re ~2×10³, so the flow leaves the nose long before the tail matters.
- [x] SGS-magnitude experiment (`aero_ahmed_diag_wallmodel_cw.txt`, Cw 0.5 → 0.15,
      ν_T ÷11): the field **destabilises into grid-scale noise** (adjacent-cell u
      swings of ±1 U∞; WALE mean ν_T rose to 5,900× responding to the noise) without
      producing coherent attachment. The SGS magnitude is not a bug to remove — it is
      what keeps a 7.4 mm grid alive at Re 7.7×10⁵. **Quantified consequence:** wake-region
      ν_T ∝ Δ², so effective Re ≈ 2×10³·(7.4 mm/Δ)²; coherent smooth-surface separation
      needs Re_eff ≳ 10⁵ → **Δ ≈ 1–2 mm at the surface** — three to four 2× nesting
      levels below today's cap, not the one level Stage 3 was scoped around.
- [x] 4c fallback (τ_w via partial-slip BC) **not run, deliberately**: it delivers the
      same wall stress by another route, and the measured blocker is not wall-stress
      delivery — the near-wall values were already correct and the flow still separated
      upstream. Recorded as a justified skip, not an oversight.
- [x] Plate/cube with the wall model on (`aero_validate_wallmodel.txt`): **soft-on
      plate/cube moved a few %; soft-off blew up catastrophically** (cube 14.7, sphere
      10.8 — staircase-corner cells feed the Spalding solve degenerate inputs and ν_w
      is unbounded above). The prototype fails the 4d criterion and stays default-off,
      prototype-labelled. Bound ν_w before any reuse.

> **Result (Stage 2.5 complete, 2026-08-12).**
> - 2.5a: effective Re measured at ~900–1,700 vs physical 7.7×10⁵ (ν_T/ν ~500–900×);
>   slant separated 9/9; y/δ 0.45 at 15 mm / 0.22 at 7.4 mm.
> - 2.5b: 8.0 and 7.4 mm points flat in both wall modes; Stage 1's "Bouzidi still
>   falling" refuted; uniform refinement exhausted at bluff-box drag.
> - 2.5c: Spalding wall-model prototype stable, restores near-wall effective Re to
>   ~physical, **does not move the slant sweep** (spread 8 % vs published 51 %).
> - 2.5d: gate says NO — and the diagnosis is quantified: the SGS viscosity a coarse
>   grid *needs* for stability caps effective Re at ~10³; recovering smooth-surface
>   separation needs ~1–2 mm surface cells (Re_eff ∝ Δ⁻²), i.e. 3–4 refinement levels.
> **The stage paid for itself: Stages 3+4 as premised would not have delivered the
> slant claim.** The strategic fork (re-scope Stage 3 to a 1–2 mm WMLES shell — cell
> counts feasible at ~30 M but wall-clock is days-scale; or reposition the tool on its
> validated claims) is the owner's call, recorded in §5.1 below.

---

## Stage 3 — Local grid refinement

- [ ] **Stage 3 complete**

**Goal.** Small cells near the body, large cells far away. This is the unlock: ~15 mm at the
surface for ~6–15 M cells instead of ~600 M.

**Prerequisite: the Stage 2.5 gate — which answered NO (2026-08-12).** The wall model
worked as formulated and did not move the benchmark; the measured blocker is bulk SGS
viscosity, and fixing it needs ~1–2 mm surface cells (3–4 nesting levels), not the one
level this stage was scoped around. **PARKED pending the §5.1 decision.** Everything below
is the original one-level design and would need re-scoping under Option A.

**Why it is hard (read before starting).** In LBM the cell size and time step are locked —
information moves exactly one cell per step. A region with half the cell size needs twice
the steps for the same physical time, so refined and coarse regions run on **different
clocks**. Refinement is therefore a whole subsystem, not a parameter. LBM also cannot use
the conventional trick of thin stretched cells near a wall: the lattice requires **cubes**.

**Scope discipline.** Aim for uniform 2× nesting with rectangular blocks. Do not attempt
octree/arbitrary AMR.

### 3a — Multi-block data structure
- [ ] Represent the domain as a coarse base grid plus one or more nested refined blocks
      (2× per level). Each block owns its own `f`, flags, coverage and velocity field.
- [ ] Dispatch each block independently; **no coupling yet.**
- [ ] Validation: a single block reproduces today's uniform result exactly.

### 3b — Time sub-cycling
- [ ] Fine blocks step twice per coarse step (2ⁿ for level n). Keep a single logical
      simulation time; sampling and force accumulation must agree on it.
- [ ] Validation: with the interface not yet coupled, both blocks stay stable and each
      independently matches a uniform run of its own resolution.

### 3c — Coarse → fine interface
- [ ] Fine-block ghost cells take their populations from the coarse block: spatial
      interpolation, **plus temporal interpolation** for the fine sub-step that has no
      coarse counterpart.

### 3d — Fine → coarse restriction
- [ ] Coarse cells overlapping the fine block take averaged values back from it.

### 3e — Non-equilibrium rescaling (the correctness crux)
- [ ] Populations do **not** transfer 1:1 between resolutions. Split into equilibrium and
      non-equilibrium parts; the non-equilibrium part scales with the ratio of relaxation
      times, which depends on cell size. Get this wrong and the interface acts as a mirror,
      reflecting pressure waves back into the domain.
- [ ] Note this solver is **TRT**, not BGK — the rescaling must respect both relaxation
      rates, not just ω⁺.

### 3f — Validation (this is the acceptance test for the whole stage)
- [ ] Choose a case affordable at **uniform** fine resolution — the Ahmed body at 12 mm
      (§4.1) is ideal, already measured at Cd 1.019.
- [ ] Run it refined (coarse far field + fine shell) and confirm it reproduces the uniform
      answer within the measurement uncertainty (~1.5 %).
- [ ] Interface-reflection check: place the refinement boundary at two different distances
      from the body. If the answer moves, the coupling is wrong.
- [ ] `AeroValidate.Run` and `AeroDemoTest.Run` unrefined → unchanged.

### 3g — Auto-fit integration
- [ ] Extend `TunnelAutoFit` to place a refinement shell around the body automatically
      (target cell size at the surface; levels derived from the coarse cell size).
- [ ] Report the achieved near-wall cell size and y/δ estimate in the fit summary, so the
      wall model's validity is visible before it is switched on.

> **Result:** _(fill in: refined vs uniform agreement on the Ahmed 12 mm case, near-wall
> cell size achieved on a car domain, total cells, memory, and wall-clock per flow-through)_

---

## Stage 4 — Wall model

- [ ] **Stage 4 complete**

**Goal.** Supply the near-wall physics the grid cannot resolve, so separation stops being
decided by the first cell's ignorance.

**Prerequisites: Stages 2.5 and 3 — PARKED with Stage 3 (see §5.1).** 2.5c prototyped this
stage's physics and 2.5d measured the verdict: correct wall stress at y/δ ≈ 0.22 does not
recover smooth-surface separation, because the outer flow's effective Re is the blocker.
Known defects to fix before any revival: ν_w is unbounded above and blows up on soft-off
staircase bodies (`aero_validate_wallmodel.txt`); the moving-wall term interaction is
unverified (Stage 5's rolling-road note).

### 4a — Wall distance and tangential velocity
- [ ] Reuse Stage 1a's sub-cell data — as built, that is the `_SurfaceFraction` field, not
      a stored wall plane: distance y from the 0.5 iso-level along the wall link (the same
      q the Bouzidi branch derives), normal n from the fraction gradient.
- [ ] Tangential velocity u_t = |u − (u·n)n| at the first fluid cell.

### 4b — Friction velocity
- [ ] Solve Spalding's law implicitly for u_τ (Newton, 3–4 iterations — it is smooth and
      valid across viscous sublayer, buffer and log layer, unlike the bare log law).

### 4c — Apply as an effective viscosity
- [ ] At `Lbm.compute` ~line 357, for near-wall cells replace the WALE eddy viscosity with
      ν_w = u_τ²·y/u_t − ν, floored at 0, so the momentum flux at the wall matches τ_w.
- [ ] Behind a toggle, default off, like Stage 1.
- [ ] **Fallback if the viscosity route misbehaves** (2.5 will have shown it): impose τ_w
      through the boundary condition instead — a partial-slip bounce-back on the wall
      links. Viscosity coupling can fight WALE's near-wall behaviour; the BC route
      sidesteps that interaction.

### 4d — Validation — the pass/fail is fixed in advance
- [ ] **Primary: `AeroAhmedTest.Run`.** The sweep must spread by **tens of percent**
      (published 51 %) rather than today's 5 %, and the 30° → 35° drop must appear. This is
      the claim the wall model either earns or does not.
- [ ] `AeroValidate.Run` → plate and cube must **not** move. They are Reynolds-independent;
      if a wall model changes them, it is wrong.
- [ ] Sphere and Range Rover expected to fall. Record by how much.
- [ ] Report the achieved y/δ range so the reader can judge whether the model was inside
      its validity.

**Realistic expectation:** equilibrium wall models assume attached, equilibrium boundary
layers, and a car's drag is dominated by separated flow where that assumption fails. A move
from ~2.3× to ~1.3–1.5× would be a success; ~1.0× would not be credible.

> **Result:** _(fill in: Ahmed spread before/after, 30→35 drop, plate/cube unchanged?,
> sphere and RR before/after, y/δ achieved)_

---

## Stage 5 — Loose ends

Independent, small, each worth doing on its own. Tick individually.

- [ ] **5a — Rolling road.** `MovingBelt` + rotating wheels diverged to negative Cd under
      the old BGK solver and has never been re-verified under TRT+WALE. Run
      `AeroValidate.Run -aeroGround MovingBelt`; either fix it or document it as broken.
      It is currently the only ground mode a road-realistic study would want.
      While here: Stage 1's Bouzidi branch scales the moving-wall velocity term by 2q
      (q<½) or 1/(2q) (q≥½) as a side effect of interpolating the already-corrected
      population. Harmless for a stationary body, unverified against Lallemand–Luo for
      moving walls — check it if interpolated walls are ever combined with belt/wheels.
- [ ] **5b — Frontal area summing.** `Voxelize.compute` `ComputeStats` *thresholds* coverage
      at 0.5 instead of summing it, so a body thinner than half a cell measures zero area.
      `AeroForces` floors the area at one cell so the failure is loud. Sum max-coverage per
      column instead. **Warning:** this changes measured areas slightly on soft-voxel runs
      and will move existing baselines — re-run §4 afterwards.
- [ ] **5c — `AeroIntake` suction BC.** Dead-end ducts stagnate; intakes need a suction
      boundary. Without it there is no cooling flow at all (5–10 % of real Cd).
- [ ] **5d — Zoom sub-domain.** A one-way-coupled fine box driven by a coarse run's
      boundary values. Not valid for global drag (the wake feeds back), but the right tool
      for local questions — brake ducts, wing gaps, mirrors — where `aero_gap_matrix.txt`
      shows ~7 mm cells are needed and a whole-car uniform grid cannot reach them.
- [ ] **5e — Cylinder cross-flow** validation case, and a sphere re-check with soft voxels
      tuned. Cheap additions to `AeroValidate`.
- [ ] **5f — Ahmed body at finer resolution once Stage 2/3 land.** Largely absorbed into
      Stage 2.5b, which puts the sweep extension on the critical path; this item remains
      for re-runs after Stage 3 lands.

---

## 6. Plan history

| Date | Change |
|---|---|
| 2026-08-11 | Plan created after the Ahmed body benchmark established the baselines in §4 |
| 2026-08-12 | **Stage 1 complete** (implemented 08-11, final sweep 08-12). Bouzidi walls: sphere −8.1 % (1.39× → 1.28× of published), Ahmed body unmoved at every resolution tested. Geometry representation eliminated as the cause of the benchmark's flatness |
| 2026-08-12 | **Resequenced.** Inserted Stage 2.5 — a decision gate that prototypes the wall model on uniform-fine Ahmed (affordable after Stage 2) before Stage 3 is built. Review found the plan routing a cheap question (is the wall model worth anything?) through its most expensive stage (refinement). Stage 3 now requires the gate |
| 2026-08-12 | **Stage 2a done.** FP16 packed lattice: 190 → 118 B/cell, no throughput cost, gates hold (plate/cube +0.4 %). Ahmed 0° at 7.4 mm / 51 M cells now runs: Cd 1.057 — still a bluff box. Found the real ceiling: D3D11's 2 GiB per-buffer cap (~53.7 M cells at FP16), not VRAM |
| 2026-08-12 | **2b deferred** (user-approved): no longer on 2.5's critical path; its customer is Stage 3. **Stage 2.5a done:** measured effective Re ~900–1,700 vs physical 7.7×10⁵ (ν_T/ν ~500–900×); slant separated 9/9 stations at 25°. The missing-physics diagnosis is now a measurement |
| 2026-08-12 | **Stage 2.5 complete — the gate says NO.** 2.5b: 8.0/7.4 mm flat both wall modes, "Bouzidi still falling" refuted. 2.5c: Spalding prototype restores near-wall effective Re to ~physical and the slant sweep stays flat (8 % vs 51 %). 2.5d: blocker quantified — bulk SGS viscosity caps effective Re at ~10³; the fix needs 1–2 mm surface cells (Re_eff ∝ Δ⁻²). Stages 3+4 as premised would not have delivered; both PARKED pending the §5.1 fork (WMLES shell vs reposition) |

_When you finish a stage, add a row here with the date, the stage, and the headline number
it moved._
