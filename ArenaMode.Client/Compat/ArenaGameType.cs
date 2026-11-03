using HarmonyLib;
using Redeploy.Core.Compat;
using System;

namespace ArenaMode.Compat;

/// <summary>
/// The concrete class of the running raid: <see cref="EFT.LocalGame"/> solo, Fika's CoopGame in co-op. Both derive
/// from <c>BaseLocalGame&lt;EftGamePlayerOwner&gt;</c>, which the project-wide <c>LocalGame</c> alias names; Harmony
/// targets for virtuals the concrete class overrides (Stop, vmethod_2, ...) must name the concrete class, or the
/// patch sits on the base and never fires.
/// </summary>
internal static class ArenaGameType
{
    private static Type _concrete;

    internal static Type Concrete
    {
        get
        {
            if (_concrete != null) return _concrete;
            Type coop = FikaCompatCheck.IsFikaLoaded ? AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame") : null;
            _concrete = coop ?? typeof(EFT.LocalGame);
            return _concrete;
        }
    }

    /// <summary>
    /// Whether a bot group's game is the running raid. Solo, BotsGroup.BotGame is the LocalGame itself; under Fika the
    /// bot game is Fika's host controller (CoopGame does not implement IBotGame), so the singleton is the reference.
    /// </summary>
    internal static bool IsBotGameOf(object botGame, object game)
        => ReferenceEquals(botGame, game)
           || (Comfort.Common.Singleton<IBotGame>.Instantiated && ReferenceEquals(botGame, Comfort.Common.Singleton<IBotGame>.Instance));

    /// <summary>A method declared on the concrete game class, falling back to the inherited one.</summary>
    internal static System.Reflection.MethodInfo Method(string name, Type[] parameters = null)
        => (parameters == null ? AccessTools.DeclaredMethod(Concrete, name) : AccessTools.DeclaredMethod(Concrete, name, parameters))
           ?? (parameters == null ? AccessTools.Method(Concrete, name) : AccessTools.Method(Concrete, name, parameters));
}
