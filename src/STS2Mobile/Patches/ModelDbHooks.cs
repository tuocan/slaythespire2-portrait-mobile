using System;

namespace STS2Mobile.Patches;

// Entry points that tools/SteamKitAndroidPatch wires into sts2.dll at build time.
// They are called across the assembly boundary from game code, so the type and
// its members must stay public and keep these exact signatures.
public static class ModelDbHooks
{
    // Prepended to ModelDb.Init(Type[] injectedModelTypes). Returning true means
    // the registry was populated here and the original body is skipped.
    public static bool Init(Type[] injectedModelTypes)
    {
        // Explicitly injected type lists (test resets) keep the game's own path.
        if (injectedModelTypes != null)
            return false;

        try
        {
            return ModelDbInitPatch.RunTwoPhaseInit();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"ModelDb.Init hook failed, running original: {ex}");
            return false;
        }
    }
}
