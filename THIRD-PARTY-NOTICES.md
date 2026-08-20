# Third-party notices

The **source code** in this repository is licensed under the MIT License (see
[`LICENSE.md`](LICENSE.md)).

The **3D models** under `Assets/Models/` are *not* covered by that licence. Each one is
third-party work redistributed here under **Creative Commons Attribution 4.0 International
(CC BY 4.0)**, and each remains under that licence. If you fork, redistribute or build on
this project, you must keep the attributions below intact.

Full licence text: <https://creativecommons.org/licenses/by/4.0/>

## Model attributions

| Model | Author | Source | Licence | Used in |
|---|---|---|---|---|
| "2019 Chevrolet Silverado Trail Boss Z71" | Ddiaz Design | [skfb.ly/p88ox](https://skfb.ly/p88ox) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `Chevy_NoCap`, `Chevy_Cap`, `Chevy_FlatTonneau` — the bed-cap drag comparison |
| "Land Rover Range Rover Sport SVR" | Mona x Supercars | [skfb.ly/pznrG](https://skfb.ly/pznrG) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `range-rover-sport-svr-2022` — large SUV, high-blockage case |
| "Futuristic racing car - \"Molnia\"" | TuppsM | [skfb.ly/6TCWL](https://skfb.ly/6TCWL) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `MolniaAnimated` — concept / ground-effect body |
| "Boat" | milamila | [skfb.ly/6VKs9](https://skfb.ly/6VKs9) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `Boat` — watercraft vehicle class |
| "autopilot aircraft / drone" | Helindu | [skfb.ly/6SwHV](https://skfb.ly/6SwHV) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `spy dron` — aircraft vehicle class |

## Changes made to the originals

CC BY 4.0 §3(a)(1)(B) requires that modifications be indicated. The models in this
repository have been changed from their published form as follows:

- Imported into Unity and re-authored as prefabs, with materials remapped to URP shaders.
- Re-scaled and re-oriented so that each vehicle sits on the ground plane with its nose
  along the wind-tunnel's local −X axis, as the solver expects.
- Fitted with `AeroVehicle` and `AeroWheel` components, and with collision and wheel metadata
  that does not exist in the originals.
- The Silverado is additionally shipped as **three geometry variants** — open bed, bed cap
  and flat tonneau — authored for this project by enabling and disabling parts of the
  original mesh. These variants are not part of the model as published.

No original mesh data is claimed as the work of this project's author.

## Trademarks

Several of these models depict real production and racing vehicles. All product names,
vehicle model names, logos, badges and body designs are trademarks or registered trademarks
of their respective owners — including but not limited to General Motors and
Jaguar Land Rover.

The CC BY 4.0 licence above is granted by the *3D artists* for their modelling work. It does
not, and cannot, grant any right in the manufacturers' trademarks or vehicle trade dress.

This project is an independent, non-commercial educational tool. It is **not affiliated with,
sponsored by, endorsed by, or connected to** any vehicle manufacturer. The models are used
solely as geometry for aerodynamic demonstration, and no aerodynamic figure produced by this
software should be read as a statement about any real vehicle's actual performance.
