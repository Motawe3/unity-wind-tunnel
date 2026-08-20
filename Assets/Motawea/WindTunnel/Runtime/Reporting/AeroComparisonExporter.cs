using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Writes a finished comparison as a self-contained HTML page: the same audit,
    /// metric table and verdict the modal shows, in a form that can be attached to an
    /// email or handed to someone who does not have the project.
    ///
    /// The audit comes first, deliberately. A comparison that arrives as a bare table of
    /// deltas invites the reader to trust numbers whose basis they cannot see; leading
    /// with what the two runs did and did not share is the honest order.
    /// </summary>
    public static class AeroComparisonExporter
    {
        static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        public static string Export(AeroComparisonReport report, string path)
        {
            var sb = new StringBuilder();
            AppendHead(sb, report);
            AppendVerdict(sb, report);
            AppendAudit(sb, report);
            AppendMetrics(sb, report);
            AppendRealWorld(sb, report);
            AppendSweep(sb, report);

            sb.Append(AeroHtmlTheme.PurposeBlock(report.noiseBandPct));
            sb.Append("</body></html>");

            File.WriteAllText(path, sb.ToString());
            return path;
        }

        /// <summary>Writes into a directory under a name built from the two runs.</summary>
        public static string ExportTo(AeroComparisonReport report, string directory)
        {
            Directory.CreateDirectory(directory);
            string name = $"windtunnel-compare-{Slug(report.sessionA?.vehicleName)}-vs-" +
                          $"{Slug(report.sessionB?.vehicleName)}-{System.DateTime.Now:yyyyMMdd-HHmmss}.html";
            return Export(report, Path.Combine(directory, name));
        }

        static string Slug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "run";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name.Trim().Replace(' ', '-');
        }

        // ------------------------------------------------------------------ sections

        static void AppendHead(StringBuilder sb, AeroComparisonReport r)
        {
            // The page carries the runtime console's theme (AeroHtmlTheme), so the modal,
            // the session report and this export read as one product in different media.
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Wind Tunnel comparison</title><style>");
            sb.Append(AeroHtmlTheme.Css);
            sb.Append("</style></head><body>");

            sb.Append(AeroHtmlTheme.Brand);
            sb.Append("<h1>Comparison</h1>");
            sb.Append("<p class=\"meta\"><span class=\"chip a\">A</span> ")
              .Append(Escape(r.labelA)).Append(" &nbsp;vs&nbsp; <span class=\"chip b\">B</span> ")
              .Append(Escape(r.labelB)).Append("</p>");

            if (r.sessionA != null && r.sessionB != null)
                sb.Append("<p class=\"meta\">")
                  .Append($"A: {Escape(ClassOf(r.sessionA))}, {Escape(r.sessionA.gridInfo)}, run {Escape(r.sessionA.startedAtIso)}<br>")
                  .Append($"B: {Escape(ClassOf(r.sessionB))}, {Escape(r.sessionB.gridInfo)}, run {Escape(r.sessionB.startedAtIso)}")
                  .Append("</p>");
        }

        static string ClassOf(AeroTestSession s) =>
            string.IsNullOrEmpty(s.vehicleClassLabel) ? s.vehicleClass.ToString() : s.vehicleClassLabel;

        static void AppendVerdict(StringBuilder sb, AeroComparisonReport r)
        {
            string style = !r.comparable ? "stop" : r.winner == 0 ? "tie" : "win";
            string title = !r.comparable ? "Not comparable"
                : r.winner == 0 ? r.verdict
                : $"Winner: <span class=\"chip {(r.winner > 0 ? "b" : "a")}\">{(r.winner > 0 ? "B" : "A")}</span> " +
                  $"{Escape(string.IsNullOrEmpty(r.winnerName) ? r.verdict : r.winnerName)}";

            sb.Append($"<div class=\"verdict {style}\"><strong style=\"font-size:1.05rem\">{title}</strong>");
            sb.Append($"<div style=\"margin-top:.35rem\">{Escape(r.verdictDetail)}</div></div>");
        }

        static void AppendAudit(StringBuilder sb, AeroComparisonReport r)
        {
            sb.Append("<h2>Like-for-like audit</h2>");
            sb.Append("<table><tr><th>Check</th><th class=\"col-a\">A</th><th class=\"col-b\">B</th><th>Verdict</th></tr>");
            foreach (var c in r.checks)
            {
                (string cls, string label) = c.level switch
                {
                    ComparabilityLevel.Blocking => ("block", "BLOCKS COMPARISON"),
                    ComparabilityLevel.Warning => ("warn", "CAVEAT"),
                    ComparabilityLevel.Note => ("note", "NOTED"),
                    _ => ("ok", "MATCH")
                };
                sb.Append("<tr>")
                  .Append($"<td>{Escape(c.label)}</td>")
                  .Append($"<td class=\"col-a\">{Escape(c.a)}</td>")
                  .Append($"<td class=\"col-b\">{Escape(c.b)}</td>")
                  .Append($"<td><span class=\"{cls}\">{label}</span>")
                  .Append(string.IsNullOrEmpty(c.note) ? "" : $"<span class=\"why\">{Escape(c.note)}</span>")
                  .Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        static void AppendMetrics(StringBuilder sb, AeroComparisonReport r)
        {
            if (r.rows.Count == 0) return;
            sb.Append(r.testA != null && r.testA.kind == AeroTestKind.ConstantSpeedDrag
                ? "<h2>Measurements</h2>" : "<h2>Sweep averages</h2>");
            sb.Append("<table><tr><th>Metric</th><th class=\"col-a\">A</th><th class=\"col-b\">B</th>")
              .Append("<th>&Delta;</th><th>&Delta; %</th><th>Better</th></tr>");

            foreach (var row in r.rows)
            {
                string unit = string.IsNullOrEmpty(row.unit) ? "" : " " + row.unit;
                string better;
                if (row.polarity == MetricPolarity.Informational) better = "<span class=\"meta\">—</span>";
                else if (row.withinNoise) better = "<span class=\"meta\">within noise</span>";
                else better = $"<span class=\"chip {(row.better > 0 ? "b" : "a")}\">{(row.better > 0 ? "B" : "A")}</span>";

                sb.Append(row.primary ? "<tr class=\"primary\">" : "<tr>")
                  .Append($"<td>{Escape(row.label)}{(row.primary ? " <span class=\"meta\">(primary)</span>" : "")}</td>")
                  .Append($"<td class=\"col-a\">{row.a.ToString(row.format, Ic)}{Escape(unit)}</td>")
                  .Append($"<td class=\"col-b\">{row.b.ToString(row.format, Ic)}{Escape(unit)}</td>")
                  .Append($"<td>{(row.delta >= 0f ? "+" : "")}{row.delta.ToString(row.format, Ic)}</td>")
                  .Append($"<td>{(row.hasDeltaPct ? (row.deltaPct >= 0f ? "+" : "") + row.deltaPct.ToString("0.0", Ic) + "%" : "—")}</td>")
                  .Append($"<td>{better}</td></tr>");
            }
            sb.Append("</table>");
            sb.Append($"<p class=\"meta\">Deltas are B − A. ±{r.noiseBandPct.ToString("0.0", Ic)}% is the uncertainty on " +
                      "these two means (standard error over the averaged flow-throughs); anything inside it is not a result.</p>");
        }

        static void AppendRealWorld(StringBuilder sb, AeroComparisonReport r)
        {
            var impact = AeroRealWorld.Derive(r);
            if (!impact.applicable) return;

            sb.Append("<h2>Real-world impact — <span class=\"chip b\">B</span> relative to <span class=\"chip a\">A</span></h2>");

            if (impact.withinNoise)
            {
                sb.Append("<div class=\"verdict tie\"><strong>No claim.</strong> The drag-area difference " +
                          $"({(impact.cdaDeltaPct >= 0 ? "+" : "")}{impact.cdaDeltaPct.ToString("0.0", Ic)} %) is inside the " +
                          $"±{impact.noiseBandPct.ToString("0.0", Ic)} % measurement uncertainty, so no fuel, cost or range " +
                          "consequence can honestly be derived from it. Run longer averages or a bigger design change.</div>");
                return;
            }

            sb.Append("<table><tr><th>Consequence</th><th>Estimate</th><th>Basis</th></tr>");
            foreach (var reading in impact.readings)
            {
                // Verdict colours, not identity colours: green = B improves on A here,
                // red = B regresses, muted = informational.
                string cls = reading.better > 0 ? "ok" : reading.better < 0 ? "block" : "note";
                sb.Append("<tr>")
                  .Append($"<td>{Escape(reading.label)}</td>")
                  .Append($"<td class=\"{cls}\">{Escape(reading.value)}{(reading.measured ? "" : " <span class=\"derived\">est.</span>")}</td>")
                  .Append($"<td class=\"meta\" style=\"text-align:left\">{Escape(reading.basis)}</td>")
                  .Append("</tr>");
            }
            sb.Append("</table>");

            if (impact.assumptions.Count > 0)
            {
                sb.Append("<p class=\"meta\">Assumptions, stated so they can be challenged: ");
                for (int i = 0; i < impact.assumptions.Count; i++)
                    sb.Append(i > 0 ? " · " : "").Append(Escape(impact.assumptions[i]));
                sb.Append("</p>");
            }
        }

        static void AppendSweep(StringBuilder sb, AeroComparisonReport r)
        {
            if (r.sweep.Count == 0) return;
            sb.Append($"<h2>Point by point — {Escape(r.testA.parameterName)}</h2>");
            sb.Append("<table><tr><th>Point</th><th class=\"col-a\">Cd A</th><th class=\"col-b\">Cd B</th>")
              .Append("<th>&Delta; Cd</th><th class=\"col-a\">Cl A</th><th class=\"col-b\">Cl B</th>")
              .Append("<th class=\"col-a\">Cy A</th><th class=\"col-b\">Cy B</th></tr>");
            foreach (var p in r.sweep)
            {
                float d = p.cdB - p.cdA;
                bool suspect = !p.convergedA || !p.convergedB;
                sb.Append("<tr>")
                  .Append($"<td>{p.parameter.ToString("0.###", Ic)}{(suspect ? " *" : "")}</td>")
                  .Append($"<td class=\"col-a\">{p.cdA.ToString("0.000", Ic)}</td>")
                  .Append($"<td class=\"col-b\">{p.cdB.ToString("0.000", Ic)}</td>")
                  .Append($"<td>{(d >= 0f ? "+" : "")}{d.ToString("0.000", Ic)}</td>")
                  .Append($"<td class=\"col-a\">{p.clA.ToString("0.000", Ic)}</td>")
                  .Append($"<td class=\"col-b\">{p.clB.ToString("0.000", Ic)}</td>")
                  .Append($"<td class=\"col-a\">{p.cyA.ToString("0.000", Ic)}</td>")
                  .Append($"<td class=\"col-b\">{p.cyB.ToString("0.000", Ic)}</td></tr>");
            }
            sb.Append("</table><p class=\"meta\">* the point's mean had not settled inside its uncertainty at the step cap.</p>");
        }

        static string Escape(string s) => string.IsNullOrEmpty(s)
            ? ""
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
