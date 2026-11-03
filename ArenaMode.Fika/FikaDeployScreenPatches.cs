using ArenaMode.Hud.Live;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using HarmonyLib;

namespace ArenaMode.Fika;

/// <summary>
/// Live Arena's deploy screen over Fika's loading flow: shown when Fika creates its loading screen (raid load
/// begins), switched to the deploy countdown when Fika disables it (EFT's final countdown starts), removed when
/// Fika destroys it (abort) or when the battle UI appears (the screen watches that itself).
/// </summary>
internal static class FikaDeployScreenPatches
{
    internal static void Install(Harmony harmony)
    {
        var self = typeof(FikaDeployScreenPatches);
        harmony.Patch(AccessTools.Method(typeof(NetManagerUtils), nameof(NetManagerUtils.CreateLoadingScreenUI)),
            postfix: new HarmonyMethod(self, nameof(AfterCreate)));
        harmony.Patch(AccessTools.Method(typeof(NetManagerUtils), nameof(NetManagerUtils.DisableLoadingScreenUI)),
            prefix: new HarmonyMethod(self, nameof(BeforeDisable)));
        harmony.Patch(AccessTools.Method(typeof(NetManagerUtils), nameof(NetManagerUtils.DestroyLoadingScreenUI)),
            prefix: new HarmonyMethod(self, nameof(BeforeDestroy)));
    }

    private static void AfterCreate()
    {
        string nickname = null;
        try { nickname = FikaBackendUtils.PMCName; } catch { /* no profile yet */ }
        ArenaDeployScreen.Show(nickname);
    }

    private static void BeforeDisable()
    {
        float seconds = 0f;
        try { seconds = Singleton<GlobalConfiguration>.Instantiated ? Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal : 0f; }
        catch { /* keep 0 */ }
        ArenaDeployScreen.BeginCountdown(seconds);
    }

    private static void BeforeDestroy() => ArenaDeployScreen.Hide();
}
