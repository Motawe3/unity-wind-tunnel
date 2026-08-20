using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Motawea.WindTunnel.Editor
{
    /// <summary>
    /// Headless-ish test driver for CI / command line. Runs every enabled test in the
    /// scene's AeroTestRunner queue and exports the report, then exits with 0 on
    /// success. Requires a GPU (run WITHOUT -nographics).
    ///
    /// Unity.exe -batchmode -projectPath <path>
    ///           -executeMethod Motawea.WindTunnel.Editor.AeroBatchRunner.RunTests
    ///           [-aeroScene Assets/Scenes/SimulationScene.unity] [-aeroMinutes 30]
    ///           -logFile batch.log
    /// </summary>
    public static class AeroBatchRunner
    {
        public static void RunTests()
        {
            string scenePath = Arg("-aeroScene", "Assets/Scenes/SimulationScene.unity");
            float maxMinutes = float.Parse(Arg("-aeroMinutes", "30"));

            Debug.Log($"AeroBatch: opening {scenePath}");
            EditorSceneManager.OpenScene(scenePath);

            var tunnel = UnityEngine.Object.FindFirstObjectByType<WindTunnelDomain>();
            var runner = UnityEngine.Object.FindFirstObjectByType<AeroTestRunner>();
            if (tunnel == null || runner == null || tunnel.vehicle == null)
            {
                Debug.LogError("AeroBatch: scene needs a WindTunnelDomain (with vehicle) and an AeroTestRunner.");
                EditorApplication.Exit(1);
                return;
            }

            // No frame budget in batch mode: push the solver hard.
            tunnel.stepsPerTick = 64;

            // Diagnostic overrides.
            string tunnelArg = Arg("-aeroTunnel", "");
            if (!string.IsNullOrEmpty(tunnelArg))
            {
                var parts = tunnelArg.Split(',');
                if (parts.Length == 3 &&
                    float.TryParse(parts[0], out float tx) &&
                    float.TryParse(parts[1], out float ty) &&
                    float.TryParse(parts[2], out float tz))
                {
                    tunnel.size = new Vector3(tx, ty, tz);
                    Debug.Log($"AeroBatch: tunnel size override = {tunnel.size}");
                }
            }

            string tolArg = Arg("-aeroTolerance", "");
            if (float.TryParse(tolArg, out float tol))
            {
                tunnel.convergenceTolerance = tol;
                Debug.Log($"AeroBatch: convergence tolerance override = {tol:P1}");
            }

            string groundArg = Arg("-aeroGround", "");
            if (Enum.TryParse(groundArg, true, out GroundSimulation groundOverride))
            {
                tunnel.ground = groundOverride;
                foreach (var def in runner.testQueue)
                    def.ground = groundOverride;
                Debug.Log($"AeroBatch: ground override = {groundOverride}");
            }

            AeroTestSession session = null;
            runner.SessionCompleted += s => session = s;

            string singleArg = Arg("-aeroSingle", "");
            if (int.TryParse(singleArg, out int singleIndex) &&
                singleIndex >= 0 && singleIndex < runner.testQueue.Count)
            {
                Debug.Log($"AeroBatch: running single test [{singleIndex}] '{runner.testQueue[singleIndex].testName}', " +
                          $"vehicle '{tunnel.vehicle.Name}', cap {maxMinutes:0} min");
                runner.StartSingle(runner.testQueue[singleIndex]);
            }
            else
            {
                Debug.Log($"AeroBatch: running {runner.testQueue.Count} queued test(s), " +
                          $"vehicle '{tunnel.vehicle.Name}', cap {maxMinutes:0} min");
                runner.StartQueue();
            }
            if (!runner.IsRunning)
            {
                Debug.LogError("AeroBatch: the runner did not start (no enabled tests?).");
                EditorApplication.Exit(1);
                return;
            }

            var watch = Stopwatch.StartNew();
            var lastLog = TimeSpan.Zero;
            int tick = 0;
            while (session == null && watch.Elapsed < TimeSpan.FromMinutes(maxMinutes))
            {
                tunnel.Tick();
                runner.Tick();

                // No player loop in a blocking batch method: pump readback callbacks.
                if (++tick % 8 == 0)
                    AsyncGPUReadback.WaitAllRequests();

                if (watch.Elapsed - lastLog > TimeSpan.FromSeconds(10))
                {
                    lastLog = watch.Elapsed;
                    Debug.Log($"AeroBatch: [{watch.Elapsed:mm\\:ss}] {runner.StatusLine}");
                }
            }
            AsyncGPUReadback.WaitAllRequests();

            if (session == null)
            {
                Debug.LogError($"AeroBatch: timed out after {maxMinutes:0} min — {runner.StatusLine}");
                runner.AbortQueue();
                tunnel.ShutdownSimulation();
                EditorApplication.Exit(2);
                return;
            }

            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Reports");
            Directory.CreateDirectory(dir);
            string baseName = $"batch-{session.vehicleName}-{DateTime.Now:yyyyMMdd-HHmmss}";
            AeroReportExporter.ExportHtml(session, Path.Combine(dir, baseName + ".html"));
            AeroReportExporter.ExportCsv(session, Path.Combine(dir, baseName + ".csv"));
            // The JSON archive is what a later comparison can actually audit — a CI run
            // that only wrote HTML and CSV could never be differenced against another.
            AeroReportExporter.ExportJson(session, Path.Combine(dir, baseName + AeroReportExporter.JsonExtension));

            Debug.Log($"AeroBatch: DONE in {watch.Elapsed:mm\\:ss} — report: Reports/{baseName}.csv");
            foreach (var test in session.tests)
                foreach (var p in test.points)
                    Debug.Log($"AeroBatch: {test.testName} | {test.parameterName}={p.parameter:0.###} | " +
                              $"Cd={p.sample.cd:0.000} CdA={p.sample.cdA:0.000} Cl={p.sample.cl:0.000} " +
                              $"(F {p.sample.clFront:0.000} / R {p.sample.clRear:0.000}) Cy={p.sample.cy:0.000} " +
                              $"drag={p.sample.dragForceN:0}N converged={p.converged} steps={p.solverSteps}");

            tunnel.ShutdownSimulation();
            EditorApplication.Exit(0);
        }

        static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }
    }
}
