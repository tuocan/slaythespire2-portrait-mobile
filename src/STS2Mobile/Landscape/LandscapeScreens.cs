using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace STS2Mobile.Landscape;

// Per-screen enlargements for landscape boots. The global content zoom
// (LandscapeLayout) shrinks everything so the hand and the board fit a phone;
// these screens then read too small, so each one is grown on its own:
//   - top bar: the HUD row (HP, gold, potions, map, deck, settings) and the
//     relic row under it, so the icons become thumb targets.
//   - main menu: logo and the text buttons, zoomed together so the authored
//     composition is kept, only larger.
//   - settings: the whole screen is laid out on a smaller logical canvas and
//     magnified back to full size, so rows, text and widgets grow while the
//     list still ends at the screen edge.
//   - upgrade / enchant preview: the before/after card pair.
// Screens are found as they enter the tree (SceneTree.NodeAdded), so no
// _Ready of the game needs a Harmony patch. Values can be tuned on a device:
//   /data/local/tmp/sts2_land_topbar    e.g. "1.45" (1 = off)
//   /data/local/tmp/sts2_land_menu      e.g. "1.45"
//   /data/local/tmp/sts2_land_settings  e.g. "1.4"
//   /data/local/tmp/sts2_land_preview   e.g. "1.65"
// They are read whenever a screen is (re)built.
internal static class LandscapeScreens
{
    private const float DefaultTopBarScale = 1.45f;
    private const float DefaultMenuScale = 1.45f;
    private const float DefaultSettingsScale = 1.4f;
    private const float DefaultPreviewScale = 1.65f;

    private const string TopBarFile = "/data/local/tmp/sts2_land_topbar";
    private const string MenuFile = "/data/local/tmp/sts2_land_menu";
    private const string SettingsFile = "/data/local/tmp/sts2_land_settings";
    private const string PreviewFile = "/data/local/tmp/sts2_land_preview";

    // Authored top bar art: the background strip is 100 tall at scale 1.01,
    // and NRelicInventory.AnimHide lifts the relic row by 68 per line + 90.
    private const float BarArtHeight = 101f;
    private const float RelicLineHeight = 68f;
    private const float RelicHideExtra = 90f;

    // Main menu geometry relative to the screen centre, measured on the
    // device at the game's 16:9 setting: the logo art starts 310 above the
    // centre, and the button column is centred 294 below it with 50 per row.
    private const float MenuLogoTop = -310f;
    private const float MenuColumnCentre = 294f;
    private const float MenuRowHeight = 50f;
    private const float MenuPivotX = -175f;
    private const float MenuTopMargin = 0.06f;
    private const float MenuBottomMargin = 0.05f;

    private const string OrigMeta = "sts2land_orig";

    private static bool _started;
    private static float _topBarScale = 1f;
    private static float _menuScale = 1f;
    private static readonly List<Node> Tracked = new();

    internal static void InstallPatches(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(NTopBar),
            "AnimHide",
            postfix: AccessTools.Method(typeof(LandscapeScreens), nameof(TopBarHidePostfix))
        );
        PatchHelper.Patch(
            harmony,
            typeof(NRelicInventory),
            "AnimHide",
            postfix: AccessTools.Method(typeof(LandscapeScreens), nameof(RelicHidePostfix))
        );
        PatchHelper.Patch(
            harmony,
            typeof(NMainMenu),
            "MainMenuButtonFocused",
            postfix: AccessTools.Method(typeof(LandscapeScreens), nameof(ReticlePostfix))
        );
    }

    internal static void Start(SceneTree tree)
    {
        if (_started)
            return;
        _started = true;
        tree.NodeAdded += OnNodeAdded;
        Scan(tree.Root);
        PatchHelper.Log("[Landscape] screen enlargements watching the scene tree");
    }

    // Screens that existed before Start (the main menu, usually).
    private static void Scan(Node node)
    {
        OnNodeAdded(node);
        foreach (var child in node.GetChildren())
            Scan(child);
    }

    private static void OnNodeAdded(Node node)
    {
        Action<Node> apply = node.GetType().Name switch
        {
            "NTopBar" => n => ApplyTopBar((Control)n),
            "NMainMenu" => n => ApplyMainMenu((Control)n),
            "NSettingsScreen" => n => ApplySettings((Control)n),
            "NUpgradePreview" or "NEnchantPreview" => n => ApplyPreview((Control)n),
            _ => null,
        };
        if (apply is null)
            return;

        // NodeAdded fires before the node's _Ready; run after it.
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
                return;
            try
            {
                apply(node);
                if (!Tracked.Contains(node))
                    Tracked.Add(node);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Landscape] {node.GetType().Name} enlarge failed: {ex.GetBaseException().Message}");
            }
        }).CallDeferred();
    }

    // Window changes move anchors; rebuild from the authored values.
    internal static void Reapply()
    {
        Tracked.RemoveAll(n => !GodotObject.IsInstanceValid(n) || !n.IsInsideTree());
        foreach (var node in Tracked.ToArray())
        {
            try
            {
                switch (node.GetType().Name)
                {
                    case "NTopBar":
                        ApplyTopBar((Control)node);
                        break;
                    case "NMainMenu":
                        ApplyMainMenu((Control)node);
                        break;
                    case "NSettingsScreen":
                        ApplySettings((Control)node);
                        break;
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Landscape] reapply failed: {ex.GetBaseException().Message}");
            }
        }
    }

    // Lowest screen point the run HUD (top bar row + relic row) covers, so
    // the shop zoom can keep its panel clear of it.
    internal static float HudBottom()
    {
        var bottom = 0f;
        foreach (var node in Tracked)
        {
            if (node.GetType().Name != "NTopBar"
                || !GodotObject.IsInstanceValid(node)
                || node is not Control { Visible: true } bar
                || !bar.IsVisibleInTree())
                continue;

            if (bar.GetNodeOrNull<Control>("LeftAlignedStuff") is { } left)
                bottom = Math.Max(bottom, left.GetGlobalRect().End.Y);
            if (bar.GetParent()?.GetNodeOrNull<Control>("RelicInventory") is { } relics
                && relics.IsVisibleInTree())
            {
                foreach (var child in relics.GetChildren())
                {
                    if (child is Control { Visible: true } holder)
                        bottom = Math.Max(bottom, holder.GetGlobalRect().End.Y);
                }
            }
        }
        return bottom;
    }

    // ---- top bar -------------------------------------------------------

    private static void ApplyTopBar(Control bar)
    {
        var s = ReadScale(TopBarFile, DefaultTopBarScale);
        _topBarScale = s;

        // Rows shrink to their content first: the authored boxes are far wider
        // than what they hold, and a grown empty box would swallow taps meant
        // for whatever sits under the bar.
        if (bar.GetNodeOrNull<Control>("LeftAlignedStuff") is { } left)
        {
            if (!left.HasMeta(OrigMeta))
            {
                left.SetMeta(OrigMeta, true);
                left.Size = new Vector2(0f, left.Size.Y);
            }
            left.PivotOffset = Vector2.Zero;
            left.Scale = Vector2.One * s;
        }

        if (bar.GetNodeOrNull<Control>("RightAlignedStuff") is { } right)
        {
            if (!right.HasMeta(OrigMeta))
            {
                right.SetMeta(OrigMeta, true);
                // Anchored to the right edge and growing leftwards: an empty
                // width collapses to the icons, still flush right.
                right.OffsetLeft = right.OffsetRight;
                right.Resized += () => right.PivotOffset = new Vector2(right.Size.X, 0f);
            }
            right.PivotOffset = new Vector2(right.Size.X, 0f);
            right.Scale = Vector2.One * s;
        }

        if (bar.GetNodeOrNull<Control>("BgImage") is { } bg)
        {
            var authored = Remember(bg, bg.Scale);
            bg.PivotOffset = new Vector2(bg.Size.X * 0.5f, 0f);
            bg.Scale = authored * s;
        }

        if (bar.GetParent() is Control ui)
        {
            if (ui.GetNodeOrNull<Control>("RelicInventory") is { } relics)
                ApplyRelics(relics, s);
            // The translucent build/seed lines sit just under the bar's art.
            if (ui.GetNodeOrNull<Control>("DebugInfo") is { } debug)
            {
                var o = Remember(debug, Offsets(debug));
                var dy = BarArtHeight * (s - 1f);
                debug.OffsetTop = o.Y + dy;
                debug.OffsetBottom = o.W + dy;
            }
        }

        PatchHelper.Log($"[Landscape] top bar x{s:F2}");
    }

    private static void ApplyRelics(Control relics, float s)
    {
        var o = Remember(relics, Offsets(relics));
        var parentWidth = relics.GetParentAreaSize().X;
        relics.PivotOffset = Vector2.Zero;
        relics.Scale = Vector2.One * s;
        // Same gap under the grown bar; the logical width shrinks so wrapped
        // rows still end where the authored row did.
        relics.OffsetTop = o.Y * s;
        relics.OffsetRight = o.X + (parentWidth + o.Z - o.X) / s - parentWidth;
        // AnimShow tweens back to the position captured in _Ready.
        Traverse.Create(relics).Field("_originalPos").SetValue(relics.Position);
    }

    // The game slides the bar up by its authored height; a grown bar needs
    // a longer slide or its lower part stays on screen over capstone menus.
    private static void TopBarHidePostfix(object __instance)
    {
        if (_topBarScale <= 1.01f || __instance is not Control bar)
            return;
        try
        {
            var field = Traverse.Create(bar).Field("_hideTween");
            (field.GetValue() as Tween)?.Kill();
            var tween = bar.CreateTween();
            tween.TweenProperty(bar, "position:y", -(BarArtHeight * _topBarScale + 4f), 0.25)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Cubic);
            field.SetValue(tween);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Landscape] top bar hide skipped: {ex.GetBaseException().Message}");
        }
    }

    private static void RelicHidePostfix(object __instance)
    {
        if (_topBarScale <= 1.01f || __instance is not FlowContainer relics)
            return;
        try
        {
            var t = Traverse.Create(relics);
            (t.Field("_curTween").GetValue() as Tween)?.Kill();
            var origin = t.Field("_originalPos").GetValue<Vector2>();
            var target = origin.Y - (RelicLineHeight * relics.GetLineCount() + RelicHideExtra) * _topBarScale;
            var tween = relics.CreateTween();
            tween.TweenProperty(relics, "global_position:y", target, 0.25)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Cubic);
            t.Field("_curTween").SetValue(tween);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Landscape] relic hide skipped: {ex.GetBaseException().Message}");
        }
    }

    // ---- main menu -----------------------------------------------------

    // Logo and buttons are zoomed by one factor about one pivot, so their
    // spacing scales with them. The pivot is chosen so the grown group sits
    // centred between a top and a bottom margin.
    private static void ApplyMainMenu(Control menu)
    {
        var g = ReadScale(MenuFile, DefaultMenuScale);
        var buttons = menu.GetNodeOrNull<Control>("MainMenuTextButtons");
        if (buttons is null)
            return;

        var rows = 0;
        foreach (var child in buttons.GetChildren())
        {
            if (child is Control { Visible: true })
                rows++;
        }

        var height = menu.Size.Y;
        var groupBottom = MenuColumnCentre + MenuRowHeight * 0.5f * Math.Max(rows, 7);
        var span = groupBottom - MenuLogoTop;
        var band = height * (1f - MenuTopMargin - MenuBottomMargin);
        g = Math.Min(g, band / span);
        if (g <= 1.01f)
            g = 1f;
        _menuScale = g;

        var bandCentre = (height * MenuTopMargin - height * MenuBottomMargin) * 0.5f;
        var groupCentre = (groupBottom + MenuLogoTop) * 0.5f;
        var pivot = new Vector2(
            MenuPivotX,
            g > 1f ? (bandCentre - g * groupCentre) / (1f - g) : 0f
        );
        Vector2 Zoom(Vector2 p) => pivot + (p - pivot) * g;

        // Button column: anchored at the screen centre, so its offsets are
        // already centre-relative points.
        var o = Remember(buttons, Offsets(buttons));
        var topLeft = Zoom(new Vector2(o.X, o.Y));
        var bottomRight = Zoom(new Vector2(o.Z, o.W));
        buttons.OffsetLeft = topLeft.X;
        buttons.OffsetTop = topLeft.Y;
        buttons.OffsetRight = bottomRight.X;
        buttons.OffsetBottom = bottomRight.Y;

        // Grow rows and fonts rather than scaling the column: the focus
        // reticles are placed from the labels' real sizes, and the hover
        // animation owns each label's Scale.
        foreach (var child in buttons.GetChildren())
        {
            if (child is not Control row)
                continue;
            var authored = Remember(row, row.CustomMinimumSize);
            row.CustomMinimumSize = authored * g;
            if (row.GetNodeOrNull<Label>("Label") is { } label)
            {
                var font = Remember(label, label.GetThemeFontSize("font_size"));
                if (font > 0)
                    label.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(font * g));
            }
        }

        // The run summary hangs off the Continue row's centre; keep it beside
        // the grown label and grow it the same.
        if (buttons.GetNodeOrNull<Control>("ContinueButton/ContinueRunInfo") is { } info)
        {
            var io = Remember(info, Offsets(info));
            info.OffsetLeft = io.X * g;
            info.OffsetTop = io.Y * g;
            info.OffsetRight = io.X * g + (io.Z - io.X);
            info.OffsetBottom = io.Y * g + (io.W - io.Y);
            info.PivotOffset = Vector2.Zero;
            info.Scale = Vector2.One * g;
            // AnimShow slides to the position read in _Ready; refresh it once
            // the column has re-laid its rows (that sort is deferred too).
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(info))
                    Traverse.Create(info).Field("_initPosition").SetValue(info.Position);
            }).CallDeferred();
        }

        // Focus reticles grow away from the label (see ReticlePostfix).
        if (menu.GetNodeOrNull<Control>("ButtonReticleLeft") is { } reticleLeft)
        {
            reticleLeft.PivotOffset = new Vector2(reticleLeft.Size.X, reticleLeft.Size.Y * 0.5f);
            reticleLeft.Scale = Vector2.One * g;
        }
        if (menu.GetNodeOrNull<Control>("ButtonReticleRight") is { } reticleRight)
        {
            reticleRight.PivotOffset = new Vector2(0f, reticleRight.Size.Y * 0.5f);
            reticleRight.Scale = Vector2.One * g;
        }

        if (menu.FindChild("Logo", recursive: true, owned: false) is Node2D logo)
        {
            var authored = Remember(logo, new Vector4(logo.Position.X, logo.Position.Y, logo.Scale.X, logo.Scale.Y));
            logo.Position = new Vector2(authored.X, authored.Y);
            logo.Scale = new Vector2(authored.Z, authored.W);
            var centre = menu.GlobalPosition + menu.Size * 0.5f;
            var globalPivot = centre + pivot;
            logo.GlobalPosition = globalPivot + (logo.GlobalPosition - globalPivot) * g;
            logo.GlobalScale *= g;
        }

        PatchHelper.Log($"[Landscape] main menu x{g:F2} (rows={rows}, pivot={pivot}, view={menu.Size})");
    }

    // The game parks the reticles 5 below the row top, centred for a 50-tall
    // row; centre them on the grown row instead (X is tweened by the game).
    private static void ReticlePostfix(object __instance, object[] __args)
    {
        if (_menuScale <= 1.01f
            || __instance is not Control menu
            || __args is not { Length: > 0 }
            || __args[0] is not Control button)
            return;

        foreach (var name in new[] { "ButtonReticleLeft", "ButtonReticleRight" })
        {
            if (menu.GetNodeOrNull<Control>(name) is not { } reticle)
                continue;
            var y = button.GlobalPosition.Y + (button.Size.Y - reticle.Size.Y) * 0.5f;
            reticle.GlobalPosition = new Vector2(reticle.GlobalPosition.X, y);
        }
    }

    // ---- settings ------------------------------------------------------

    // Lay the screen out on a canvas 1/s the size of its parent and magnify
    // it by s: the result covers the same area, with everything s larger.
    private static void ApplySettings(Control screen)
    {
        if (!Mathf.IsEqualApprox(screen.AnchorRight, 1f) || !Mathf.IsEqualApprox(screen.AnchorBottom, 1f))
        {
            PatchHelper.Log("[Landscape] settings screen is not full-rect; left alone");
            return;
        }

        var s = ReadScale(SettingsFile, DefaultSettingsScale);
        var parent = screen.GetParentAreaSize();
        screen.PivotOffset = Vector2.Zero;
        screen.OffsetLeft = 0f;
        screen.OffsetTop = 0f;
        screen.OffsetRight = parent.X / s - parent.X;
        screen.OffsetBottom = parent.Y / s - parent.Y;
        screen.Scale = Vector2.One * s;
        PatchHelper.Log($"[Landscape] settings x{s:F2} (logical {parent / s})");
    }

    // ---- card previews -------------------------------------------------

    // Upgrade and enchant previews are zero-size anchors at the screen
    // centre with the two cards at +-280, so scaling about the origin grows
    // the pair in place.
    private static void ApplyPreview(Control preview)
    {
        // A preview laid out by a container (a multi-card row) has no room
        // to grow; only the free-standing single preview is enlarged.
        if (preview.GetParent() is Container)
            return;
        var k = ReadScale(PreviewFile, DefaultPreviewScale, max: 2.5f);
        preview.PivotOffset = Vector2.Zero;
        preview.Scale = Vector2.One * k;
        PatchHelper.Log($"[Landscape] {preview.GetType().Name} x{k:F2}");
    }

    // ---- helpers -------------------------------------------------------

    private static float ReadScale(string file, float fallback, float max = 2f)
        => Math.Clamp(LandscapeLayout.ReadDevFloat(file, fallback), 1f, max);

    private static Vector4 Offsets(Control c)
        => new(c.OffsetLeft, c.OffsetTop, c.OffsetRight, c.OffsetBottom);

    // The first value seen is the authored one; later passes rebuild from it.
    private static Vector2 Remember(Node node, Vector2 value)
    {
        if (node.HasMeta(OrigMeta))
            return node.GetMeta(OrigMeta).AsVector2();
        node.SetMeta(OrigMeta, value);
        return value;
    }

    private static Vector4 Remember(Node node, Vector4 value)
    {
        if (node.HasMeta(OrigMeta))
            return node.GetMeta(OrigMeta).AsVector4();
        node.SetMeta(OrigMeta, value);
        return value;
    }

    private static int Remember(Node node, int value)
    {
        if (node.HasMeta(OrigMeta))
            return node.GetMeta(OrigMeta).AsInt32();
        node.SetMeta(OrigMeta, value);
        return value;
    }
}
