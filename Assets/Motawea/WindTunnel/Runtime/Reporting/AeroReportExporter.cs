using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Writes a completed test session as a self-contained HTML report (tables +
    /// inline SVG sweep charts) and as CSV for spreadsheet/scripting workflows.
    /// </summary>
    public static class AeroReportExporter
    {
        static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary>
        /// Extension of the machine-readable session archive. HTML is for people and
        /// CSV is for spreadsheets; neither carries the full test configuration, so
        /// the comparison tool reads this one.
        /// </summary>
        public const string JsonExtension = ".windtunnel.json";

        /// <summary>Prefix shared by every exported report file; the archive reader strips it
        /// back off, so the two must stay in step.</summary>
        public const string FilePrefix = "windtunnel-";

        /// <summary>Writes the session verbatim so it can be re-loaded and compared later.</summary>
        public static string ExportJson(AeroTestSession session, string path)
        {
            session.schemaVersion = AeroTestSession.CurrentSchema;
            if (string.IsNullOrEmpty(session.packageVersion)) session.packageVersion = WindTunnelVersion.Value;
            File.WriteAllText(path, JsonUtility.ToJson(session, true));
            return path;
        }

        /// <summary>
        /// Writes all three formats next to each other, named from the vehicle and the
        /// timestamp, and returns the base name (no extension).
        /// </summary>
        public static string ExportAll(AeroTestSession session, string directory)
        {
            Directory.CreateDirectory(directory);
            string safeVehicle = SanitizeFileName(session.vehicleName);
            string baseName = $"{FilePrefix}{safeVehicle}-{System.DateTime.Now:yyyyMMdd-HHmmss}";
            string stem = Path.Combine(directory, baseName);
            ExportHtml(session, stem + ".html");
            ExportCsv(session, stem + ".csv");
            ExportJson(session, stem + JsonExtension);
            return baseName;
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "vehicle";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');
            return name.Replace(' ', '-');
        }

        public static string ExportCsv(AeroTestSession session, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("test,kind,parameter_name,parameter,converged,speed_ms,cd,cdA_m2,cl,cl_front,cl_rear,cy,drag_N,lift_N,side_N,power_kW,frontal_area_m2,air_density,re_effective,solver_steps,convergence_cv,cd_uncertainty,samples_averaged,flow_throughs_averaged");
            foreach (var test in session.tests)
            {
                foreach (var p in test.points)
                {
                    var s = p.sample;
                    sb.AppendLine(string.Join(",",
                        Quote(test.testName), test.kind, Quote(test.parameterName),
                        p.parameter.ToString("0.####", Ic), p.converged,
                        s.airSpeedMs.ToString("0.###", Ic),
                        s.cd.ToString("0.####", Ic), s.cdA.ToString("0.####", Ic),
                        s.cl.ToString("0.####", Ic), s.clFront.ToString("0.####", Ic),
                        s.clRear.ToString("0.####", Ic), s.cy.ToString("0.####", Ic),
                        s.dragForceN.ToString("0.##", Ic), s.liftForceN.ToString("0.##", Ic),
                        s.sideForceN.ToString("0.##", Ic), (s.aeroPowerW / 1000f).ToString("0.###", Ic),
                        s.frontalAreaM2.ToString("0.####", Ic), s.airDensity.ToString("0.####", Ic),
                        s.reynoldsEffective.ToString("0.###e0", Ic), p.solverSteps,
                        p.convergenceCv.ToString("0.#####", Ic),
                        p.standardError.ToString("0.#####", Ic),
                        p.samplesAveraged,
                        p.flowThroughsAveraged.ToString("0.##", Ic)));
                }
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        public static string ExportHtml(AeroTestSession session, string path)
        {
            var sb = new StringBuilder();
            // Same theme as the comparison export and the runtime console (AeroHtmlTheme).
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Wind Tunnel report</title><style>");
            sb.Append(AeroHtmlTheme.Css);
            sb.Append("</style></head><body>");

            sb.Append(AeroHtmlTheme.Brand);
            sb.Append($"<h1>Wind-tunnel report — {Escape(session.vehicleName)}</h1>");
            sb.Append("<p class=\"meta\">");
            sb.Append($"{Escape(string.IsNullOrEmpty(session.vehicleClassLabel) ? session.vehicleClass.ToString() : session.vehicleClassLabel)} · ");
            sb.Append($"Session {Escape(session.startedAtIso)} → {Escape(session.finishedAtIso)} · ");
            sb.Append($"Grid {Escape(session.gridInfo)} · {Escape(MediumLabel(session.fluidMedium))} {session.airTemperatureC.ToString("0.#", Ic)} °C, ρ {session.airDensity.ToString("0.###", Ic)} kg/m³ · ");
            sb.Append($"Reference area {session.frontalAreaM2.ToString("0.###", Ic)} m² ({Escape(session.referenceAreaBasis)}) · Blockage {(session.blockageRatio * 100f).ToString("0.#", Ic)}%");
            if (session.blockageRatio > WindTunnelDomain.BlockageWarningRatio)
                sb.Append(" <span class=\"warn\">(high — coefficients read high)</span>");
            sb.Append($" · Effective Re {session.reynoldsEffective.ToString("0.##e0", Ic)}</p>");

            sb.Append("<p class=\"meta\">Values are comparative estimates at reduced Reynolds number — " +
                      "see “What this page is” at the bottom before quoting any number from it.</p>");

            foreach (var test in session.tests)
            {
                sb.Append($"<h2>{Escape(test.testName)} <span class=\"meta\">({test.kind}, {(test.speedMs * 3.6f).ToString("0.#", Ic)} km/h, {test.ground}{(test.rotatingWheels ? ", rotating wheels" : "")})</span></h2>");

                sb.Append("<table><tr><th>").Append(Escape(test.parameterName))
                  .Append("</th><th>Cd</th><th>± (Cd)</th><th>CdA (m²)</th><th>Cl</th><th>Cl front</th><th>Cl rear</th><th>Cy</th><th>Drag (N)</th><th>Power (kW)</th><th>Settled</th></tr>");
                foreach (var p in test.points)
                {
                    var s = p.sample;
                    sb.Append("<tr>")
                      .Append($"<td>{p.parameter.ToString("0.###", Ic)}</td>")
                      .Append($"<td>{s.cd.ToString("0.000", Ic)}</td>")
                      .Append($"<td>{(p.standardError >= 0f ? "±" + (p.standardError * 100f).ToString("0.0", Ic) + "%" : "—")}</td>")
                      .Append($"<td>{s.cdA.ToString("0.000", Ic)}</td>")
                      .Append($"<td>{s.cl.ToString("0.000", Ic)}</td><td>{s.clFront.ToString("0.000", Ic)}</td><td>{s.clRear.ToString("0.000", Ic)}</td>")
                      .Append($"<td>{s.cy.ToString("0.000", Ic)}</td><td>{s.dragForceN.ToString("0.#", Ic)}</td>")
                      .Append($"<td>{(s.aeroPowerW / 1000f).ToString("0.##", Ic)}</td>")
                      .Append($"<td>{(p.converged ? "yes" : "<span class=\"warn\">no</span>")}</td></tr>");
                }
                sb.Append("</table>");

                if (test.points.Count > 1)
                {
                    AppendSweepChart(sb, test, s => s.cd, "Cd");
                    if (test.kind == AeroTestKind.YawSweep)
                        AppendSweepChart(sb, test, s => s.cy, "Cy");
                    else
                        AppendSweepChart(sb, test, s => s.cl, "Cl");
                }
            }

            sb.Append(AeroHtmlTheme.PurposeBlock(-1f));
            sb.Append("</body></html>");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        static void AppendSweepChart(StringBuilder sb, AeroTestResult test,
                                     System.Func<AeroSample, float> metric, string label)
        {
            const int w = 460, h = 220, pad = 44;
            float xMin = float.MaxValue, xMax = float.MinValue;
            float yMin = float.MaxValue, yMax = float.MinValue;
            foreach (var p in test.points)
            {
                float v = metric(p.sample);
                xMin = Mathf.Min(xMin, p.parameter); xMax = Mathf.Max(xMax, p.parameter);
                yMin = Mathf.Min(yMin, v); yMax = Mathf.Max(yMax, v);
            }
            if (Mathf.Approximately(yMin, yMax)) { yMin -= 0.05f; yMax += 0.05f; }
            float yPadding = (yMax - yMin) * 0.12f;
            yMin -= yPadding; yMax += yPadding;

            float Px(float x) => pad + (x - xMin) / Mathf.Max(xMax - xMin, 1e-6f) * (w - 2 * pad);
            float Py(float y) => h - pad - (y - yMin) / Mathf.Max(yMax - yMin, 1e-6f) * (h - 2 * pad);

            sb.Append($"<svg width=\"{w}\" height=\"{h}\" style=\"margin:.5rem 1rem .5rem 0\" xmlns=\"http://www.w3.org/2000/svg\">");
            sb.Append($"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"{AeroHtmlTheme.Panel}\" stroke=\"{AeroHtmlTheme.Line}\"/>");
            sb.Append($"<line x1=\"{pad}\" y1=\"{h - pad}\" x2=\"{w - pad}\" y2=\"{h - pad}\" stroke=\"#4a5462\"/>");
            sb.Append($"<line x1=\"{pad}\" y1=\"{pad}\" x2=\"{pad}\" y2=\"{h - pad}\" stroke=\"#4a5462\"/>");

            var pts = new StringBuilder();
            foreach (var p in test.points)
                pts.Append($"{Px(p.parameter).ToString("0.#", Ic)},{Py(metric(p.sample)).ToString("0.#", Ic)} ");
            sb.Append($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{AeroHtmlTheme.Teal}\" stroke-width=\"2\"/>");
            foreach (var p in test.points)
                sb.Append($"<circle cx=\"{Px(p.parameter).ToString("0.#", Ic)}\" cy=\"{Py(metric(p.sample)).ToString("0.#", Ic)}\" r=\"3.5\" fill=\"{(p.converged ? AeroHtmlTheme.Teal : AeroHtmlTheme.Bad)}\"/>");

            sb.Append($"<text x=\"{w / 2}\" y=\"{h - 10}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{AeroHtmlTheme.Muted}\">{Escape(test.parameterName)}</text>");
            sb.Append($"<text x=\"14\" y=\"{h / 2}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{AeroHtmlTheme.Muted}\" transform=\"rotate(-90 14 {h / 2})\">{label}</text>");
            sb.Append($"<text x=\"{pad}\" y=\"{pad - 8}\" font-size=\"11\" fill=\"{AeroHtmlTheme.Muted}\">{yMax.ToString("0.###", Ic)}</text>");
            sb.Append($"<text x=\"{pad}\" y=\"{h - pad + 14}\" font-size=\"11\" fill=\"{AeroHtmlTheme.Muted}\">{xMin.ToString("0.###", Ic)}</text>");
            sb.Append($"<text x=\"{w - pad}\" y=\"{h - pad + 14}\" text-anchor=\"end\" font-size=\"11\" fill=\"{AeroHtmlTheme.Muted}\">{xMax.ToString("0.###", Ic)}</text>");
            sb.Append("</svg>");
        }

        static string MediumLabel(AeroFluidMedium medium) => medium switch
        {
            AeroFluidMedium.FreshWater => "Fresh water",
            AeroFluidMedium.SeaWater => "Sea water",
            _ => "Air"
        };

        static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        static string Escape(string s) => string.IsNullOrEmpty(s)
            ? ""
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
