using System;
using System.Globalization;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace STS2Mobile.Landscape;

// Landscape game boots keep the game's own PC layout, which is authored for a
// mouse on a big monitor. On a phone three things go wrong, fixed here only
// when OrientationPreference.LandscapeGameActive:
//   - zoom: everything reads large; the root content scale factor shrinks it
//     (<1 = smaller UI, more scene).
//   - side margins: corner icons (settings gear, deck, map) sit under the
//     panel's rounded corners; a slightly narrower "keep" canvas leaves a thin
//     bar on each side.
//   - hand lift: resting hand cards hang below the screen edge (on PC the text
//     shows on mouse hover); every resting slot is raised.
// Values can be tuned live on a device, polled every second:
//   /data/local/tmp/sts2_land_zoom      e.g. "0.80"
//   /data/local/tmp/sts2_land_aspect    canvas aspect, e.g. "2.05" (0 = no margins)
//   /data/local/tmp/sts2_land_handlift  canvas units, e.g. "170"
internal static class LandscapeLayout
{
    private const float DefaultZoom = 0.80f;
    // 0 = leave the canvas aspect to the game's own Aspect Ratio setting
    // (Settings: 4:3, 16:10, 16:9, 21:9, Auto), which already letterboxes.
    private const float DefaultAspect = 0f;
    private const float DefaultHandLift = 170f;
    private const int CanvasHeight = 1080;

    private const string ZoomFile = "/data/local/tmp/sts2_land_zoom";
    private const string AspectFile = "/data/local/tmp/sts2_land_aspect";
    private const string HandLiftFile = "/data/local/tmp/sts2_land_handlift";

    private static bool _applied;
    private static float _zoom = DefaultZoom;
    private static float _aspect = DefaultAspect;
    private static float _appliedLift;

    // The game rewrites the window's content scale from these handlers (menu
    // fit, display settings, global UI). A postfix re-applies ours after them.
    internal static void InstallPatches(Harmony harmony)
    {
        var postfix = AccessTools.Method(typeof(LandscapeLayout), nameof(WindowPostfix));
        PatchHelper.Patch(harmony, typeof(NGame), "ApplyDisplaySettings", postfix: postfix);
        PatchHelper.Patch(harmony, typeof(NGame), "OnWindowChange", postfix: postfix);
        PatchHelper.Patch(harmony, typeof(NGlobalUi), "OnWindowChange", postfix: postfix);
        PatchHelper.Patch(harmony, typeof(NMainMenu), "OnWindowChange", postfix: postfix);
    }

    private static void WindowPostfix()
    {
        if (_applied && Engine.GetMainLoop() is SceneTree tree && tree.Root is not null)
            ApplyWindow(tree.Root);
    }

    internal static void Apply()
    {
        if (_applied || !OrientationPreference.LandscapeGameActive)
            return;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        _applied = true;
        ReadSettings(out _zoom, out _aspect, out var lift);
        ApplyWindow(tree.Root);
        SetHandLift(lift);
        PatchHelper.Log(
            $"[Landscape] zoom={_zoom:F2} aspect={_aspect:F2} handLift={_appliedLift:F0} visible={tree.Root.GetVisibleRect().Size}"
        );
        Guard(tree);
    }

    private static void ApplyWindow(Window window)
    {
        if (_aspect > 0.5f)
        {
            var size = new Vector2I(Mathf.RoundToInt(CanvasHeight * _aspect), CanvasHeight);
            if (window.ContentScaleAspect != Window.ContentScaleAspectEnum.Keep)
                window.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
            if (window.ContentScaleSize != size)
                window.ContentScaleSize = size;
        }

        if (!Mathf.IsEqualApprox(window.ContentScaleFactor, _zoom))
            window.ContentScaleFactor = _zoom;
    }

    private static void Guard(SceneTree tree)
    {
        tree.CreateTimer(1.0).Timeout += () =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(tree.Root))
                    return;

                ReadSettings(out var zoom, out var aspect, out var lift);
                var changed = !Mathf.IsEqualApprox(zoom, _zoom)
                    || !Mathf.IsEqualApprox(aspect, _aspect)
                    || !Mathf.IsEqualApprox(lift, _appliedLift);
                _zoom = zoom;
                _aspect = aspect;
                ApplyWindow(tree.Root);
                if (!Mathf.IsEqualApprox(lift, _appliedLift))
                    SetHandLift(lift);
                if (changed)
                    PatchHelper.Log(
                        $"[Landscape] retuned zoom={_zoom:F2} aspect={_aspect:F2} handLift={_appliedLift:F0} visible={tree.Root.GetVisibleRect().Size}"
                    );
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Landscape] guard failed: {ex.GetBaseException().Message}");
                return;
            }

            Guard(tree);
        };
    }

    private static void ReadSettings(out float zoom, out float aspect, out float lift)
    {
        zoom = Math.Clamp(ReadDevFloat(ZoomFile, DefaultZoom), 0.5f, 1.5f);
        aspect = ReadDevFloat(AspectFile, DefaultAspect);
        if (aspect > 0f)
            aspect = Math.Clamp(aspect, 1.3f, 3.0f);
        lift = Math.Clamp(ReadDevFloat(HandLiftFile, DefaultHandLift), -400f, 600f);
    }

    // HandPosHelper.GetPosition returns rows of this static table directly, and
    // both the hand layout and each holder's default target read it, so moving
    // the table moves every resting card consistently (focus raise is relative).
    private static void SetHandLift(float lift)
    {
        var delta = lift - _appliedLift;
        if (Math.Abs(delta) < 0.5f)
            return;

        try
        {
            var field = typeof(HandPosHelper).GetField(
                "_cardPositionData",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            if (field?.GetValue(null) is not Vector2[][] rows)
            {
                PatchHelper.Log("[Landscape] HandPosHelper._cardPositionData not found; hand lift skipped");
                return;
            }

            foreach (var row in rows)
            {
                if (row is null)
                    continue;
                for (var i = 0; i < row.Length; i++)
                    row[i] = new Vector2(row[i].X, row[i].Y - delta);
            }

            _appliedLift = lift;
            RefreshHand();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Landscape] hand lift failed: {ex.GetBaseException().Message}");
        }
    }

    private static void RefreshHand()
    {
        try
        {
            var instance = AccessTools.Property(typeof(NPlayerHand), "Instance")?.GetValue(null);
            if (instance is GodotObject hand && GodotObject.IsInstanceValid(hand))
                AccessTools.Method(typeof(NPlayerHand), "RefreshLayout")?.Invoke(instance, null);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Landscape] hand refresh skipped: {ex.GetBaseException().Message}");
        }
    }

    private static float ReadDevFloat(string path, float fallback)
    {
        try
        {
            if (System.IO.File.Exists(path)
                && float.TryParse(
                    System.IO.File.ReadAllText(path).Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
                return value;
        }
        catch
        {
            // Unreadable override keeps the default.
        }

        return fallback;
    }
}
