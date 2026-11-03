using EFT;
using Redeploy.Core.Compat;
using System;
using UnityEngine;

namespace ArenaMode.Compat;

/// <summary>
/// Bridge to the optional ArenaMode.Fika companion plugin. Stock ArenaMode refuses to run with Fika loaded
/// because its respawn replaces the local body, which remote clients never learn about. The companion plugin
/// provides an in-place respawn on Fika's own downed/revive primitive and announces itself here; every former
/// "unavailable with Fika" refusal now asks <see cref="BlocksArena"/> instead. It also wires the hooks a
/// host-authoritative match needs: who decides, who is downed, how a body resets for a new round, and how the
/// round state reaches the other clients.
/// </summary>
public static class ArenaCoop
{
    /// <summary>Set by ArenaMode.Fika once its patches are installed and its Fika version check passed.</summary>
    public static bool IsAvailable { get; set; }

    /// <summary>Fika is loaded and nothing provides co-op arena: the stock refusal applies.</summary>
    public static bool BlocksArena => FikaCompatCheck.IsFikaLoaded && !IsAvailable;

    /// <summary>True while a co-op arena match is running and a downed local player should respawn in place.</summary>
    public static bool RespawnsActive => IsAvailable && Modes.ArenaMatchService.Current != null;

    /// <summary>Whether this game decides match and round state (the Fika host; always true without Fika).</summary>
    public static Func<bool> AuthorityProbe { get; set; }
    public static bool IsAuthority => !IsAvailable || AuthorityProbe == null || AuthorityProbe();

    /// <summary>A body that is out for the round (Fika's downed state) while still technically alive.</summary>
    public static Func<Player, bool> DownedProbe { get; set; }
    public static bool IsDowned(Player player) => player != null && DownedProbe != null && DownedProbe(player);

    /// <summary>Puts a living local body back on a spawn with full health and kit for the next round.</summary>
    public static Action<Player> ResetForRound { get; set; }

    /// <summary>Host → clients: the round state after every change and once a second.</summary>
    public static Action<ArenaCoopRoundState> BroadcastRoundState { get; set; }
}

/// <summary>The TeamFight round state as it travels between clients; plain fields so the companion can serialize it.</summary>
public sealed class ArenaCoopRoundState
{
    public bool Rounds;
    public bool MatchLive;
    public byte Phase;
    public int Round;
    public int UsecWins;
    public int BearWins;
    public float PhaseRemaining;
    public float RoundElapsed;
    public bool PointActive;
    public Vector3 Point;
    public float CaptureProgress;
    public sbyte CaptureUsec = -1;
    public sbyte LastRoundUsecWon = -1;
    public byte LastReason;
}
