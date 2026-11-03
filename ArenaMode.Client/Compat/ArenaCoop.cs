using Redeploy.Core.Compat;

namespace ArenaMode.Compat;

/// <summary>
/// Bridge to the optional ArenaMode.Fika companion plugin. Stock ArenaMode refuses to run with Fika loaded
/// because its respawn replaces the local body, which remote clients never learn about. The companion plugin
/// provides an in-place respawn on Fika's own downed/revive primitive and announces itself here; every former
/// "unavailable with Fika" refusal now asks <see cref="BlocksArena"/> instead.
/// </summary>
public static class ArenaCoop
{
    /// <summary>Set by ArenaMode.Fika once its patches are installed and its Fika version check passed.</summary>
    public static bool IsAvailable { get; set; }

    /// <summary>Fika is loaded and nothing provides co-op arena: the stock refusal applies.</summary>
    public static bool BlocksArena => FikaCompatCheck.IsFikaLoaded && !IsAvailable;

    /// <summary>True while a co-op arena match is running and a downed local player should respawn in place.</summary>
    public static bool RespawnsActive => IsAvailable && Modes.ArenaMatchService.Current != null;
}
