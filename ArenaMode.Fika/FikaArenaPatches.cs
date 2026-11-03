using ArenaMode.Compat;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Players;
using HarmonyLib;
using System;
using System.Reflection;

namespace ArenaMode.Fika;

/// <summary>
/// Turns Fika's downed state into a silent transport while an arena match runs:
///  - every lethal hit downs the local player, whatever the server's revive config, revive count or hit location;
///  - the bleedout clock is frozen (a round can hold the player for longer than the bleedout time);
///  - the bleedout UI is hidden on the downed player and observers create no revive prompt;
///  - the downed local player hands over to <see cref="FikaArenaRespawn"/>.
/// Outside an arena match nothing is touched.
/// </summary>
internal static class FikaArenaPatches
{
    // Fika keeps these internal; resolved by name (reviewed against 2.4.6).
    private static readonly Type BleedoutType = AccessTools.TypeByName("Fika.Core.Main.Components.Bleedout");
    private static readonly MethodInfo BleedoutHideUi = AccessTools.Method(BleedoutType, "HideUI");
    private static readonly MethodInfo DownedSetter = AccessTools.PropertySetter(typeof(FikaPlayer), nameof(FikaPlayer.Downed));

    internal static void Install(Harmony harmony)
    {
        var self = typeof(FikaArenaPatches);
        harmony.Patch(AccessTools.PropertyGetter(typeof(ClientHealthController), nameof(ClientHealthController.ReviveEnabled)),
            prefix: new HarmonyMethod(self, nameof(ForceTrue)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(ClientHealthController), nameof(ClientHealthController.CanBeDowned)),
            prefix: new HarmonyMethod(self, nameof(ForceTrue)));
        harmony.Patch(AccessTools.Method(typeof(ClientHealthController), nameof(ClientHealthController.CheckIfDamageShouldInstantKill)),
            prefix: new HarmonyMethod(self, nameof(ForceFalse)));
        harmony.Patch(AccessTools.Method(BleedoutType, "Update"),
            prefix: new HarmonyMethod(self, nameof(FreezeBleedout)));
        harmony.Patch(AccessTools.Method(typeof(FikaPlayer), nameof(FikaPlayer.ToggleDowned)),
            postfix: new HarmonyMethod(self, nameof(AfterLocalToggleDowned)));
        harmony.Patch(AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.ToggleDowned)),
            prefix: new HarmonyMethod(self, nameof(BeforeObservedToggleDowned)));
    }

    private static bool ForceTrue(ref bool __result)
    {
        if (!ArenaCoop.RespawnsActive) return true;
        __result = true;
        return false;
    }

    private static bool ForceFalse(ref bool __result)
    {
        if (!ArenaCoop.RespawnsActive) return true;
        __result = false;
        return false;
    }

    private static bool FreezeBleedout() => !ArenaCoop.RespawnsActive;

    private static void AfterLocalToggleDowned(FikaPlayer __instance, bool downed)
    {
        if (!downed || !__instance.IsYourPlayer || __instance is ObservedPlayer || !ArenaCoop.RespawnsActive)
            return;
        // Fika's Bleedout owns the bleed timer panel, the "hold <key> to give up" overlay text and a repeating agony
        // line; none of that is Arena. Destroying it runs its own teardown (text destroyed, panel closed, repeat
        // cancelled, events unhooked). Un-downing later sees a destroyed component as null and skips it.
        var bleedout = Traverse.Create(__instance).Field("_bleedout").GetValue() as UnityEngine.Component;
        if (bleedout != null)
        {
            BleedoutHideUi?.Invoke(bleedout, null);
            UnityEngine.Object.Destroy(bleedout);
        }
        FikaArenaRespawn.Begin(__instance);
    }

    /// <summary>Observers: keep Fika's state bookkeeping, drop the revive prompt, notification and agony line.</summary>
    private static bool BeforeObservedToggleDowned(ObservedPlayer __instance, bool downed)
    {
        if (!ArenaCoop.RespawnsActive) return true;
        DownedSetter.Invoke(__instance, new object[] { downed });
        __instance.NetworkHealthController.IsAlive = !downed;
        return false;
    }
}
