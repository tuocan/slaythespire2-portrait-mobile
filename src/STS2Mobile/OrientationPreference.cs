using System;
using System.IO;
using Godot;

namespace STS2Mobile;

// Player choice between the portrait layer (default) and the game's own
// landscape layout. Stored as a plain file in the app's files directory so the
// Java host can read it before Godot starts: GodotApp.java reads the same
// "orientation_mode" file to decide which orientation the game leg requests.
// The launcher itself always stays portrait; only the game leg follows this.
internal static class OrientationPreference
{
    internal const string FileName = "orientation_mode";
    private const string LandscapeValue = "landscape";
    private const string PortraitValue = "portrait";

    // True only inside a game boot (not the bootstrap launcher) whose saved
    // preference is landscape. Portrait-only code checks this and stands down.
    internal static bool LandscapeGameActive { get; private set; }

    internal static bool IsLandscape()
    {
        try
        {
            var path = FilePath();
            return File.Exists(path)
                && string.Equals(File.ReadAllText(path).Trim(), LandscapeValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void SetLandscape(bool landscape)
    {
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, landscape ? LandscapeValue : PortraitValue);
            PatchHelper.Log($"[Orientation] Preference saved: {(landscape ? LandscapeValue : PortraitValue)}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Orientation] Could not save preference: {ex.Message}");
        }
    }

    internal static void ResolveForGameBoot()
    {
        LandscapeGameActive = IsLandscape();
        PatchHelper.Log(
            LandscapeGameActive
                ? "[Orientation] Landscape game boot: portrait layout layer disabled"
                : "[Orientation] Portrait game boot"
        );
    }

    private static string FilePath() => Path.Combine(OS.GetDataDir(), FileName);
}
