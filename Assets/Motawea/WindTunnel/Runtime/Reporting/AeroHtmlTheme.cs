using System.Globalization;
using System.Text;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// The exported reports' look, shared by the comparison page and the session
    /// report so both read as one product. Palette is the runtime console's
    /// (AeroComparison.uss): obsidian ground, silver text, petrol-teal accent;
    /// A keeps teal and B keeps violet as pure identity colours, and green/amber/red
    /// stay reserved for verdicts — a colour never means both "B" and "good".
    /// </summary>
    public static class AeroHtmlTheme
    {
        static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // The console palette, hex-ified from AeroComparison.uss.
        public const string Ground = "#0e1116";      // page
        public const string Panel = "#12161c";       // cards, charts
        public const string PanelHead = "#181d24";   // table headers
        public const string Line = "#2a2f38";        // borders
        public const string Text = "#d6dce4";
        public const string Bright = "#f0f4f8";
        public const string Muted = "#8a95a3";
        public const string Teal = "#33c2d1";        // accent + side A
        public const string TealBright = "#78dae5";
        public const string Violet = "#a78bfa";      // side B
        public const string VioletBright = "#c4b0ff";
        public const string Ok = "#6fcf7a";
        public const string Warn = "#e8c964";
        public const string Bad = "#ff7a6b";

        /// <summary>Everything inside &lt;style&gt;…&lt;/style&gt;.</summary>
        public static string Css =>
            $"body{{font-family:'Segoe UI',Arial,sans-serif;max-width:1040px;margin:2rem auto;padding:0 1.25rem;" +
            $"background:{Ground};color:{Text}}}" +
            $"h1{{font-size:1.25rem;letter-spacing:3px;text-transform:uppercase;color:{Bright};" +
            $"border-left:3px solid {Teal};padding-left:.65rem;margin-bottom:.25rem}}" +
            $"h2{{font-size:.92rem;letter-spacing:2px;text-transform:uppercase;color:{Bright};" +
            $"margin-top:2.2rem;border-bottom:1px solid {Line};padding-bottom:.35rem}}" +
            $".brand{{color:{Teal};font-size:.72rem;letter-spacing:4px;text-transform:uppercase;margin:0 0 .2rem .8rem}}" +
            $"table{{border-collapse:collapse;width:100%;font-size:.85rem;margin:.75rem 0;background:{Panel}}}" +
            $"th,td{{border:1px solid {Line};padding:.4rem .55rem;text-align:right}}" +
            $"th{{background:{PanelHead};color:{Bright};font-weight:600}}" +
            $"td:first-child,th:first-child{{text-align:left}}" +
            $".meta{{color:{Muted};font-size:.85rem}}" +
            $".chip{{display:inline-block;min-width:1.1rem;text-align:center;border-radius:3px;" +
            $"padding:0 .3rem;font-weight:700;border:1px solid}}" +
            $".a{{color:{TealBright};border-color:{Teal};background:rgba(51,194,209,.14)}}" +
            $".b{{color:{VioletBright};border-color:{Violet};background:rgba(167,139,250,.14)}}" +
            $".col-a{{border-left:2px solid rgba(51,194,209,.45)}}" +
            $".col-b{{border-left:2px solid rgba(167,139,250,.45)}}" +
            $".verdict{{border-radius:6px;padding:.9rem 1rem;margin:1rem 0;border:1px solid;background:{Panel}}}" +
            $".win{{border-color:rgba(111,207,122,.45);background:rgba(111,207,122,.07)}}" +
            $".tie{{border-color:rgba(232,201,100,.45);background:rgba(232,201,100,.07)}}" +
            $".stop{{border-color:rgba(255,122,107,.45);background:rgba(255,122,107,.07)}}" +
            $".ok{{color:{Ok};font-weight:600}}.note{{color:{Muted};font-weight:600}}" +
            $".warn{{color:{Warn};font-weight:600}}.block{{color:{Bad};font-weight:600}}" +
            $".why{{color:{Muted};font-size:.78rem;display:block;margin-top:.2rem}}" +
            $".primary{{background:rgba(51,194,209,.06)}}" +
            $".derived{{color:{Muted};font-style:italic}}" +
            $".purpose{{background:{Panel};border:1px solid {Line};border-left:3px solid {Teal};" +
            $"border-radius:6px;padding:1rem 1.15rem;font-size:.88rem;margin:1.5rem 0;line-height:1.5}}" +
            $".purpose ul{{margin:.6rem 0 .4rem 1.1rem;padding:0}}" +
            $".purpose li{{margin:.45rem 0}}" +
            "a{color:" + TealBright + "}";

        /// <summary>The page header brand line above the h1.</summary>
        public static string Brand => "<div class=\"brand\">Wind Tunnel · GPU lattice-Boltzmann CFD for Unity</div>";

        /// <summary>
        /// The honest-verdict block every exported page ends with: what the tool is,
        /// what the readings mean, and where the measured limits sit. Every claim in
        /// here is backed by a stored validation report — keep it that way when editing.
        /// <paramref name="noiseBandPct"/> &lt; 0 hides the comparison-specific line
        /// (the session report has no pair to difference).
        /// </summary>
        public static string PurposeBlock(float noiseBandPct)
        {
            var sb = new StringBuilder();
            sb.Append("<h2>What this page is — and is not</h2><div class=\"purpose\">");
            sb.Append("<p><strong>This is a GPU lattice-Boltzmann virtual wind tunnel that runs inside Unity.</strong> " +
                      "It is a design-exploration and comparison instrument: it measures how a change to a vehicle " +
                      "moves the air numbers, under controls that make two runs honestly differenceable. It is not " +
                      "certification CFD, and this page never pretends otherwise.</p><ul>");

            sb.Append("<li><strong>Trust the direction; bracket the magnitude.</strong> ");
            if (noiseBandPct >= 0f)
                sb.Append(string.Format(Ic, "Both runs used the audit above, and a delta must clear the ±{0:0.0} % " +
                          "uncertainty band before this page calls it real. ", noiseBandPct));
            sb.Append("Cross-vehicle validation showed the tool ranks designs correctly but can exaggerate the size " +
                      "of a difference by up to ~30 % (most on very bluff shapes) — read magnitudes as upper estimates.</li>");

            sb.Append("<li><strong>Absolute coefficients read roughly 2× road reality.</strong> The solver runs at a " +
                      "reduced effective Reynolds number (~10³ against several million on the road — measured with " +
                      "its own diagnostics), which thickens and detaches boundary layers early. Differences between " +
                      "paired runs are the product; absolute values are context.</li>");

            sb.Append("<li><strong>Measured blind spots.</strong> Separation on smooth, curved surfaces — roofline " +
                      "sculpting, subtle spoiler angles — barely registers (the Ahmed-body benchmark's published 51 % " +
                      "drag spread reads as ~6–8 % here). Geometry smaller than ~4 grid cells effectively does not " +
                      "exist for the solver. Road-car lift does not converge with grid and is reported for " +
                      "information only.</li>");

            sb.Append("<li><strong>Validated strengths.</strong> Sharp-edged and gross-shape changes — bed covers, " +
                      "roof boxes, racks, ride height, frontal-area and bluffness differences — where separation is " +
                      "pinned at edges and the reference bodies validate against published values. Plus flow " +
                      "visualization, for seeing <em>why</em> a change costs or saves.</li></ul>");

            sb.Append("<p class=\"meta\">Every claim above is backed by a stored measurement: the Ahmed-body benchmark, " +
                      "reference-body validation, cross-vehicle ranking and grid-convergence studies that ship with " +
                      "the project. Ask for them — a tool that quotes its own error bars is the point.</p></div>");
            return sb.ToString();
        }
    }
}
