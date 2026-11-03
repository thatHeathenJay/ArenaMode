using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;
using System;
using System.Collections;
using System.Linq;
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
            // The Arena battle flow already chose the location: select it here and go on, as live never shows this screen.
            string pending = ArenaBattleFlow.PendingLocationId;
            if (__instance == null) return;
            if (pending == null)
            {
                // Backing out of the Fika screen lands here; live goes back to SELECT ARENA, so do we.
                if (!ArenaBattleFlow.UsedThisSession || !ArenaUiPack.IsAvailable) return;
                Plugin.Instance.StartCoroutine(Press(__instance._backButton));
                if (ArenaBattleFlow.ReturnToMap) ArenaBattleFlow.ReopenAtMap();   // QUICK GAME goes back to the menu
                return;
            }
            ArenaBattleFlow.PendingLocationId = null;
            Plugin.Instance.StartCoroutine(SelectLocationAndAccept(__instance, pending));
        }
    }

    private static IEnumerator Press(EFT.UI.DefaultUIButton button)
    {
        yield return null;
        button?.OnClick?.Invoke();
    }

    private static IEnumerator SelectLocationAndAccept(MatchMakerSelectionLocationScreen screen, string locationId)
    {
        yield return null;                                  // the mod's own Show postfix has built its views
        if (screen == null || !screen.isActiveAndEnabled) yield break;
        LocationSettings.Location location = null;
        try
        {
            location = SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.LocationSettings?.locations?.Values
                .FirstOrDefault(l => string.Equals(l?.Id, locationId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning($"Arena battle flow: location lookup failed: {ex.Message}"); }
        if (location == null || screen._acceptButton == null)
        {
            Plugin.ModLogger.LogWarning($"Arena battle flow: location '{locationId}' or the Next button is unavailable; showing EFT's map screen.");
            ArenaCurtain.Hide();
            yield break;
        }
        if (!string.Equals(screen.SelectedLocation?.Id, locationId, StringComparison.OrdinalIgnoreCase))
            screen.OnLocationSelected(location);
        yield return null;
        if (screen == null || !string.Equals(screen.SelectedLocation?.Id, locationId, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.ModLogger.LogWarning($"Arena battle flow: '{locationId}' was not accepted for this mode; showing EFT's map screen.");
            ArenaCurtain.Hide();
            yield break;
        }
        _cameFromLocation = false;                          // going forward: the side screen must not bounce back
        screen._acceptButton.OnClick?.Invoke();
        // The curtain drops once EFT has moved on from its map screen (or after a moment, whatever happens).
        float until = Time.unscaledTime + 4f;
        while (screen != null && screen.isActiveAndEnabled && Time.unscaledTime < until) yield return null;
        yield return null;
        ArenaCurtain.Hide();
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
            ArenaCurtain.Hide();
            yield break;
        }
        screen._nextButton?.OnClick?.Invoke();
    }
}
