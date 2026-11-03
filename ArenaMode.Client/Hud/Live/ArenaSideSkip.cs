using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ArenaMode.Hud.Live;

/// <summary>
/// The arena client is Arena only, so the PMC / Scav / ARENA screen is never asked: when it shows, the ARENA side is
/// selected and Next is pressed (straight to map selection); when the player backs out of map selection it is
/// skipped the other way (straight to the main menu). Only while the live UI pack is installed.
/// </summary>
internal static class ArenaSideSkip
{
    private static bool _cameFromLocation;

    internal static void Enable()
    {
        new SideShownPatch().Enable();
        new LocationShownPatch().Enable();
    }

    private sealed class SideShownPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(MatchMakerSideSelectionScreen), nameof(MatchMakerSideSelectionScreen.Show),
                new[] { typeof(MatchMakerSideSelectionScreen.RaidSideSelectionScreenController) });

        [PatchPostfix]
        private static void Postfix(MatchMakerSideSelectionScreen __instance)
        {
            if (!ArenaUiPack.IsAvailable || __instance == null) return;
            if (_cameFromLocation)
            {
                _cameFromLocation = false;
                Plugin.Instance.StartCoroutine(Press(__instance._backButton));
                return;
            }
            Plugin.Instance.StartCoroutine(SelectArenaAndAdvance(__instance));
        }
    }

    private sealed class LocationShownPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.ShowInternal));

        [PatchPostfix]
        private static void Postfix(MatchMakerSelectionLocationScreen __instance)
        {
            _cameFromLocation = ArenaUiPack.IsAvailable;
            // Arena has fixed daytime (the mod pins the phase); the time-phase / weather block is noise here.
            if (ArenaUiPack.IsAvailable && __instance?._conditions != null) __instance._conditions.gameObject.SetActive(false);
        }
    }

    private static IEnumerator Press(EFT.UI.DefaultUIButton button)
    {
        yield return null;
        button?.OnClick?.Invoke();
    }

    private static IEnumerator SelectArenaAndAdvance(MatchMakerSideSelectionScreen screen)
    {
        yield return null;                                  // the mod's own Show postfix has built the ARENA toggle
        if (screen == null || !screen.isActiveAndEnabled) yield break;
        screen.SetSelectedSide(ESideType.Random);           // the mod maps Random to ARENA
        yield return null;
        if (!Session.ArenaMenuSelection.IsSelected)
        {
            Plugin.ModLogger.LogWarning("Arena side skip: ARENA was not accepted; staying on the side screen so the notice shows.");
            yield break;
        }
        screen._nextButton?.OnClick?.Invoke();
    }
}
