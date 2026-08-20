using System;
using System.IO;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>Captures the game view to a timestamped PNG (Screenshots/ next to the
    /// project in the editor, persistentDataPath in builds).</summary>
    public static class AeroScreenshot
    {
        /// <param name="label">
        /// Optional subject name folded into the file name — pass the vehicle's display
        /// name so a folder of screenshots says what each one is of.
        /// </param>
        public static string Capture(string label = null)
        {
            string dir = Path.Combine(
                Application.isEditor ? Directory.GetCurrentDirectory() : Application.persistentDataPath,
                "Screenshots");
            Directory.CreateDirectory(dir);
            string slug = Sanitize(label);
            string path = Path.Combine(dir,
                $"windtunnel-{(string.IsNullOrEmpty(slug) ? "" : slug + "-")}{DateTime.Now:yyyyMMdd-HHmmss}.png");
            // Written asynchronously at the end of the current frame.
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }

        /// <summary>Turns a display name into something safe for a file name.</summary>
        static string Sanitize(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            foreach (char c in Path.GetInvalidFileNameChars())
                label = label.Replace(c, '-');
            return label.Trim().Replace(' ', '-');
        }

        /// <summary>Opens the folder containing the given file in the OS file browser.</summary>
        public static void RevealFolder(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return;
            Application.OpenURL("file:///" + dir.Replace('\\', '/'));
        }
    }
}
