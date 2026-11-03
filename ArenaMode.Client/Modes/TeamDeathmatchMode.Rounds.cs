using ArenaMode.Compat;
using ArenaMode.Config;
using ArenaMode.Effects;
using EFT;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ArenaMode.Modes;

/// <summary>
/// TeamFight without respawns: live's round rules layered on the Team Deathmatch roster. The kill policy still
/// runs underneath with its limit set to the rounds needed, and the "team score" it reads is round wins, so the
/// match ends, publishes and exits through the ordinary Team Deathmatch path. Between rounds the dead are held
/// (the co-op companion keeps them downed and spectating), bots are retired and refilled, and every living body is
/// put back on a spawn with a fresh kit. The Fika host is the authority; clients mirror its state.
/// </summary>
public sealed partial class TeamDeathmatchMode
{
    private const float CaptureRadius = 4f, CaptureHeight = 1.6f;

    private bool _roundsMode;
    private TeamFightRoundPolicy _rounds;
    private Vector3? _roundPoint;
    private bool _rosterReady;
    private TeamFightPhase _lastRoundPhase = TeamFightPhase.Assembling;
    private float _roundStateSentAt;
    private bool _roundsAnnouncedLive;

    /// <summary>Live TeamFight rules (rounds, one life, capture point) instead of continuous respawns.</summary>
    public bool IsRounds => _roundsMode && _rounds != null;
    internal TeamFightRoundPolicy Rounds => _rounds;
    internal Vector3? RoundPoint => _roundPoint;
    private bool RoundsRefillAllowed => _rounds == null || _rounds.Phase is TeamFightPhase.Assembling or TeamFightPhase.Countdown;
    private int RoundWins(bool usec) => _rounds == null ? 0 : usec ? _rounds.UsecWins : _rounds.BearWins;

    /// <summary>The local body is out for this round: dead or downed while the round plays on.</summary>
    internal bool LocalAwaitingRound => IsRounds && _rounds.Phase is TeamFightPhase.Live or TeamFightPhase.RoundEnd
        && (_local == null || _local.HealthController?.IsAlive != true || ArenaCoop.IsDowned(_local));

    private void InitializeRounds()
    {
        if (IsDomination) return;
        _roundsMode = !Settings.TeamFightRespawns.Value;
        if (!_roundsMode) return;
        _rounds = new TeamFightRoundPolicy(Settings.TeamFightRoundsToWin.Value, Settings.TeamFightOpeningSeconds.Value,
            Settings.TeamFightCaptureSeconds.Value, Settings.TeamFightCaptureWindowSeconds.Value);
        _roundPoint = PickRoundPoint();
        Plugin.ModLogger.LogInfo($"TeamFight: first to {_rounds.RoundsToWin} rounds; point after {_rounds.OpeningSeconds}s"
            + (_roundPoint.HasValue ? $" at {_roundPoint.Value}" : " (no capture point on this map)") + ".");
    }

    private Vector3? PickRoundPoint()
    {
        try
        {
            var layout = DominationMapLayout.Resolve(_world.LocationId);
            var points = layout?.Points;
            if (points == null || points.Count == 0) return null;
            var anchor = points[new System.Random().Next(points.Count)].Position;
            // Settle the anchor on the current scene's floor.
            if (Physics.Raycast(anchor + Vector3.up * 3f, Vector3.down, out var hit, 12f, LayersMaskController.HighPolyWithTerrainMask))
                return hit.point;
            return anchor;
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("TeamFight: no capture point for this map: " + ex.Message);
            return null;
        }
    }

    /// <summary>Authority step, every frame while the kill policy is live.</summary>
    private void AdvanceRounds()
    {
        if (!IsRounds || !ArenaCoop.IsAuthority || _population == null || HasResult) return;
        var before = _rounds.Phase;
        CountAlive(out int usecAlive, out int bearAlive, out int usecOnPoint, out int bearOnPoint);
        bool ready = _rosterReady && _local != null && _local.HealthController?.IsAlive == true && !ArenaCoop.IsDowned(_local);
        _rounds.Update(Time.time, Time.deltaTime, ready, usecAlive, bearAlive, usecOnPoint, bearOnPoint);
        if (_rounds.Phase != before) OnRoundPhaseChanged(before, _rounds.Phase);
        if (_rounds.Phase != before || Time.time - _roundStateSentAt >= 1f) BroadcastRounds();
    }

    private void CountAlive(out int usecAlive, out int bearAlive, out int usecOnPoint, out int bearOnPoint)
    {
        usecAlive = bearAlive = usecOnPoint = bearOnPoint = 0;
        foreach (var bot in _bots)
        {
            if (!Alive(bot) || !_usecById.TryGetValue(bot.ProfileId, out bool usec)) continue;
            Tally(usec, bot.GetPlayer.Position, ref usecAlive, ref bearAlive, ref usecOnPoint, ref bearOnPoint);
        }
        foreach (var player in _world?.AllAlivePlayersList ?? new List<Player>())
        {
            if (player == null || player.IsAI || player.HealthController?.IsAlive != true || ArenaCoop.IsDowned(player)) continue;
            var side = player.Profile?.Info?.Side;
            if (side != EPlayerSide.Usec && side != EPlayerSide.Bear) continue;
            Tally(side == EPlayerSide.Usec, player.Position, ref usecAlive, ref bearAlive, ref usecOnPoint, ref bearOnPoint);
        }
    }

    private void Tally(bool usec, Vector3 position, ref int usecAlive, ref int bearAlive, ref int usecOnPoint, ref int bearOnPoint)
    {
        if (usec) usecAlive++; else bearAlive++;
        if (!_rounds.PointActive || !_roundPoint.HasValue) return;
        var delta = position - _roundPoint.Value;
        if (Mathf.Abs(delta.y) > CaptureHeight) return;
        delta.y = 0f;
        if (delta.sqrMagnitude > CaptureRadius * CaptureRadius) return;
        if (usec) usecOnPoint++; else bearOnPoint++;
    }

    private void OnRoundPhaseChanged(TeamFightPhase before, TeamFightPhase now)
    {
        Plugin.ModLogger.LogInfo($"TeamFight round {_rounds.Round}: {before} -> {now}; USEC {_rounds.UsecWins} : {_rounds.BearWins} BEAR"
            + (now == TeamFightPhase.RoundEnd ? $" ({_rounds.LastReason}, {(_rounds.LastRoundUsecWon == null ? "draw" : _rounds.LastRoundUsecWon == true ? "USEC" : "BEAR")})" : ""));
        if (now == TeamFightPhase.RoundEnd && ArenaCoop.IsAuthority)
        {
            // No reinforcements mid-round, so what is left is retired now and refilled during the next assembly.
            foreach (var bot in _bots.Where(Alive).ToList())
                if (!Retire(bot, "round over")) break;
        }
        OnRoundPhaseLocal(before, now);
    }

    /// <summary>Local consequences of a phase change, on every client.</summary>
    private void OnRoundPhaseLocal(TeamFightPhase before, TeamFightPhase now)
    {
        _lastRoundPhase = now;
        if (now == TeamFightPhase.Countdown && before != TeamFightPhase.Countdown)
        {
            // Living bodies go back to a spawn with a fresh kit; the downed are released by HoldRespawn turning off.
            if (_local != null && _local.HealthController?.IsAlive == true && !ArenaCoop.IsDowned(_local) && _rounds.Round > 1)
            {
                try { ArenaCoop.ResetForRound?.Invoke(_local); }
                catch (Exception ex) { Plugin.ModLogger.LogWarning("TeamFight: round reset failed: " + ex.Message); }
            }
            _roundsAnnouncedLive = false;
        }
        if (now == TeamFightPhase.Live && !_roundsAnnouncedLive)
        {
            _roundsAnnouncedLive = true;
            if (_rounds.Round > 1) _match?.ReportAnnouncerCue(ArenaAnnouncerCue.RoundStart);
        }
    }

    private void BroadcastRounds()
    {
        _roundStateSentAt = Time.time;
        if (ArenaCoop.BroadcastRoundState == null) return;
        var state = new ArenaCoopRoundState { Rounds = true, MatchLive = MatchPhase == TdmMatchPhase.Live, Point = _roundPoint ?? Vector3.zero };
        _rounds.Export(state, Time.time);
        try { ArenaCoop.BroadcastRoundState(state); }
        catch (Exception ex) { Plugin.ModLogger.LogDebug("TeamFight: broadcast failed: " + ex.Message); }
    }

    /// <summary>Co-op client: the host's round state replaces local judgement.</summary>
    internal void ApplyRoundState(ArenaCoopRoundState state)
    {
        if (state == null || IsDomination || _population == null || ArenaCoop.IsAuthority) return;
        if (!state.Rounds) { _roundsMode = false; return; }
        _roundsMode = true;
        _rounds ??= new TeamFightRoundPolicy(Settings.TeamFightRoundsToWin.Value, Settings.TeamFightOpeningSeconds.Value,
            Settings.TeamFightCaptureSeconds.Value, Settings.TeamFightCaptureWindowSeconds.Value);
        if (state.PointActive || state.Point != Vector3.zero) _roundPoint = state.Point;
        var before = _rounds.Phase;
        _rounds.Apply(state, Time.time);
        if (_rounds.Phase != before) OnRoundPhaseLocal(before, _rounds.Phase);
        _mirrorMatchLive = state.MatchLive;
    }

    private bool _mirrorMatchLive;

    /// <summary>
    /// Co-op client tick: no bots to spawn or retire here (the host owns them); sides come from profiles, the
    /// scoreboard roster from the bodies in the world, and the match clock from the host.
    /// </summary>
    private void MirrorTick()
    {
        var friendly = new List<ArenaScoreboardActor>();
        var opposing = new List<ArenaScoreboardActor>();
        foreach (var player in _world?.AllAlivePlayersList ?? new List<Player>())
        {
            if (player == null || ReferenceEquals(player, _local) || string.IsNullOrEmpty(player.ProfileId)) continue;
            var side = player.Profile?.Info?.Side;
            if (side != EPlayerSide.Usec && side != EPlayerSide.Bear) continue;
            bool usec = side == EPlayerSide.Usec;
            if (!_usecById.ContainsKey(player.ProfileId))
            {
                _usecById.Add(player.ProfileId, usec);
                ArenaTracerTeams.RegisterFactionActor(player.ProfileId, usec == _population.LocalUsec);
            }
            string seat = player.IsAI ? (usec ? "usec-" : "bear-") + player.ProfileId : null;
            var actor = ArenaMatchService.SnapshotActor(player, seat);
            if (actor != null) (usec == _population.LocalUsec ? friendly : opposing).Add(actor);
        }
        if (!_match.Scoreboard.TrySynchronizeRoster(1, ArenaMatchService.SnapshotActor(_local), friendly, opposing, out var problem))
            Plugin.ModLogger.LogDebug("TeamFight mirror roster: " + problem);
        if (_mirrorMatchLive && MatchPhase != TdmMatchPhase.Live && !_announcedReady)
        {
            TickMatchStart(true);
            if (MatchPhase == TdmMatchPhase.Live)
            {
                _announcedReady = true;
                Areas.ArenaAreaRuntime.FinishTeamAssembly();
                _match.ReportAnnouncerCue(ArenaAnnouncerCue.RoundStart);
            }
        }
    }
}
