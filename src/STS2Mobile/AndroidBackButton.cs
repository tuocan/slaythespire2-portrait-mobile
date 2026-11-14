using System;
using Godot;

namespace STS2Mobile;

// Android's back key used to quit the game outright: Godot's default
// quit_on_go_back ends the process without the game's own save-and-quit, so
// progress since the last autosave was lost. Instead, back now behaves like
// Escape on PC: the game's hotkey layer (NInputManager maps Escape to
// MegaInput.pauseAndBack) opens the pause menu, where "Save and Quit" lives, or
// backs out of the current submenu, exactly as the keyboard does.
internal static class AndroidBackButton
{
    private static bool _installed;

    internal static void Install()
    {
        if (_installed || !OperatingSystem.IsAndroid())
            return;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        _installed = true;
        tree.QuitOnGoBack = false;
        tree.Root.GoBackRequested += OnGoBackRequested;
        PatchHelper.Log("[Back] Android back now acts as Escape (pause / back); quit on back disabled");
    }

    private static void OnGoBackRequested()
    {
        try
        {
            Input.ParseInputEvent(EscapeKey(pressed: true));
            // Release on the next frame so the hotkey sees one clean press.
            Callable.From(() => Input.ParseInputEvent(EscapeKey(pressed: false))).CallDeferred();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Back] Could not forward back as Escape: {ex.GetBaseException().Message}");
        }
    }

    private static InputEventKey EscapeKey(bool pressed)
        => new()
        {
            Keycode = Key.Escape,
            PhysicalKeycode = Key.Escape,
            KeyLabel = Key.Escape,
            Pressed = pressed,
        };
}
