// One entry point for both feature test suites:
//   Unity.exe -batchmode -projectPath . -executeMethod AeroUpdateTests.Run
// Needs a GPU (the auto-fit suite voxelizes) — do not pass -nographics.
using UnityEditor;
using UnityEngine;

public static class AeroUpdateTests
{
    public static void Run()
    {
        int compare = AeroCompareTest.Execute();
        int autoFit = AeroAutoFitTest.Execute();
        Debug.Log($"Wind Tunnel tests — compare: {(compare == 0 ? "PASS" : "FAIL")}, " +
                  $"auto-fit: {(autoFit == 0 ? "PASS" : "FAIL")}");
        EditorApplication.Exit(compare != 0 || autoFit != 0 ? 1 : 0);
    }
}
