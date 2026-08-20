using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>One exported result file on disk, with enough header info to pick it.</summary>
    public class AeroReportFile
    {
        public string path;
        public string fileName;
        public AeroTestSession session;
        public string loadError;
        public DateTime modifiedUtc;

        public bool IsUsable => session != null && string.IsNullOrEmpty(loadError);

        /// <summary>Vehicle · class · when, plus the procedures it contains.</summary>
        public string DisplayName
        {
            get
            {
                if (session == null) return fileName;
                string kinds = "";
                for (int i = 0; i < session.tests.Count && i < 3; i++)
                    kinds += (i > 0 ? ", " : "") + session.tests[i].testName;
                if (session.tests.Count > 3) kinds += ", …";
                return string.IsNullOrEmpty(kinds) ? session.DisplayName : $"{session.DisplayName} — {kinds}";
            }
        }
    }

    /// <summary>
    /// Finds and loads exported test sessions. The JSON archive written next to every
    /// report is the authoritative format; CSV is accepted as a fallback so results
    /// exported before the archive existed can still be compared, at the cost of the
    /// solver settings the CSV never recorded.
    /// </summary>
    public static class AeroSessionArchive
    {
        /// <summary>
        /// Where the runtime HUD writes reports: the project root in the editor, the
        /// persistent data path in a build.
        /// </summary>
        public static string DefaultDirectory => Path.Combine(
            Application.isEditor ? Directory.GetCurrentDirectory() : Application.persistentDataPath,
            "Reports");

        /// <summary>
        /// Shows the report folder in the OS file browser. Creates it first: a folder
        /// that does not exist yet cannot be opened, and "nothing happened" is a worse
        /// answer than an empty folder that shows where exports will land.
        /// </summary>
        public static void OpenDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                Directory.CreateDirectory(directory);
                Application.OpenURL("file:///" + Path.GetFullPath(directory).Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Wind Tunnel: could not open '{directory}' — {e.Message}");
            }
        }

        /// <summary>Every readable session in the directory, newest first.</summary>
        public static List<AeroReportFile> List(string directory)
        {
            var files = new List<AeroReportFile>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return files;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(directory))
            {
                bool isJson = path.EndsWith(AeroReportExporter.JsonExtension, StringComparison.OrdinalIgnoreCase);
                bool isCsv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                if (!isJson && !isCsv) continue;

                // A CSV that has a JSON twin is the same session with less metadata.
                string stem = isJson
                    ? path.Substring(0, path.Length - AeroReportExporter.JsonExtension.Length)
                    : path.Substring(0, path.Length - 4);
                if (isCsv && File.Exists(stem + AeroReportExporter.JsonExtension)) continue;
                if (!seen.Add(stem)) continue;

                var entry = new AeroReportFile
                {
                    path = path,
                    fileName = Path.GetFileName(path),
                    modifiedUtc = File.GetLastWriteTimeUtc(path)
                };
                entry.session = Load(path, out entry.loadError);
                files.Add(entry);
            }

            files.Sort((a, b) => b.modifiedUtc.CompareTo(a.modifiedUtc));
            return files;
        }

        public static AeroTestSession Load(string path, out string error)
        {
            error = null;
            try
            {
                if (path.EndsWith(AeroReportExporter.JsonExtension, StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    return LoadJson(path, out error);
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    return LoadCsv(path, out error);
                error = "unsupported file type";
                return null;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        static AeroTestSession LoadJson(string path, out string error)
        {
            error = null;
            var session = JsonUtility.FromJson<AeroTestSession>(File.ReadAllText(path));
            if (session == null)
            {
                error = "not a valid Wind Tunnel session file";
                return null;
            }
            if (session.tests == null || session.tests.Count == 0)
            {
                error = "session contains no tests";
                return null;
            }
            if (string.IsNullOrEmpty(session.schemaVersion))
                session.metadataComplete = false;
            return session;
        }

        /// <summary>
        /// Rebuilds a session from the flat CSV. Everything the CSV never stored —
        /// grid, soft voxels, vehicle class, working fluid — is left at its default
        /// and flagged, so a comparison can say what it could not verify.
        /// </summary>
        static AeroTestSession LoadCsv(string path, out string error)
        {
            error = null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2)
            {
                error = "CSV has no data rows";
                return null;
            }

            var header = SplitCsvLine(lines[0]);
            var column = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++) column[header[i].Trim()] = i;

            if (!column.ContainsKey("cd") || !column.ContainsKey("test"))
            {
                error = "not an Wind Tunnel CSV report";
                return null;
            }

            var session = new AeroTestSession
            {
                schemaVersion = "",
                metadataComplete = false,
                vehicleName = VehicleNameFromFile(path),
                startedAtIso = File.GetLastWriteTime(path).ToString("s"),
                finishedAtIso = File.GetLastWriteTime(path).ToString("s")
            };

            AeroTestResult current = null;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cells = SplitCsvLine(lines[i]);
                if (cells.Count < header.Count) continue;

                string testName = Get(cells, column, "test");
                if (current == null || current.testName != testName)
                {
                    current = new AeroTestResult
                    {
                        testName = testName,
                        kind = ParseEnum(Get(cells, column, "kind"), AeroTestKind.ConstantSpeedDrag),
                        parameterName = Get(cells, column, "parameter_name"),
                        speedMs = Num(cells, column, "speed_ms"),
                        ground = GroundSimulation.FixedFloor,
                        rotatingWheels = false
                    };
                    session.tests.Add(current);
                }

                float area = Num(cells, column, "frontal_area_m2");
                var sample = new AeroSample
                {
                    cd = Num(cells, column, "cd"),
                    cdA = Num(cells, column, "cdA_m2"),
                    cl = Num(cells, column, "cl"),
                    clFront = Num(cells, column, "cl_front"),
                    clRear = Num(cells, column, "cl_rear"),
                    cy = Num(cells, column, "cy"),
                    dragForceN = Num(cells, column, "drag_N"),
                    liftForceN = Num(cells, column, "lift_N"),
                    sideForceN = Num(cells, column, "side_N"),
                    aeroPowerW = Num(cells, column, "power_kW") * 1000f,
                    frontalAreaM2 = area,
                    airSpeedMs = Num(cells, column, "speed_ms"),
                    airDensity = Num(cells, column, "air_density"),
                    reynoldsEffective = Num(cells, column, "re_effective"),
                    liftSplitValid = true
                };

                current.points.Add(new AeroTestPointResult
                {
                    parameter = Num(cells, column, "parameter"),
                    sample = sample,
                    converged = Get(cells, column, "converged").Trim().ToLowerInvariant() is "true" or "yes" or "1",
                    convergenceCv = column.ContainsKey("convergence_cv") ? Num(cells, column, "convergence_cv") : -1f,
                    solverSteps = (long)Num(cells, column, "solver_steps")
                });

                session.frontalAreaM2 = area;
                session.measuredFrontalAreaM2 = area;
                session.airDensity = sample.airDensity;
                session.reynoldsEffective = sample.reynoldsEffective;
            }

            if (session.tests.Count == 0)
            {
                error = "no test rows found";
                return null;
            }
            return session;
        }

        static string VehicleNameFromFile(string path)
        {
            // windtunnel-<vehicle>-<yyyyMMdd-HHmmss>.csv
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith(AeroReportExporter.FilePrefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(AeroReportExporter.FilePrefix.Length);
            int stamp = name.LastIndexOf('-');
            if (stamp > 0)
            {
                int prev = name.LastIndexOf('-', stamp - 1);
                if (prev > 0) name = name.Substring(0, prev);
            }
            return string.IsNullOrEmpty(name) ? "(unknown)" : name;
        }

        static T ParseEnum<T>(string text, T fallback) where T : struct =>
            Enum.TryParse(text, true, out T value) ? value : fallback;

        static string Get(List<string> cells, Dictionary<string, int> column, string key) =>
            column.TryGetValue(key, out int i) && i < cells.Count ? cells[i] : "";

        static float Num(List<string> cells, Dictionary<string, int> column, string key) =>
            float.TryParse(Get(cells, column, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v : 0f;

        /// <summary>Comma split that respects the exporter's doubled-quote escaping.</summary>
        static List<string> SplitCsvLine(string line)
        {
            var cells = new List<string>();
            var cell = new System.Text.StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(c);
            }
            cells.Add(cell.ToString());
            return cells;
        }
    }
}
