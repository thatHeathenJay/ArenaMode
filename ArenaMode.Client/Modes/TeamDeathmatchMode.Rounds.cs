using ArenaMode.Bots;
using ArenaMode.Compat;
using ArenaMode.Config;
using ArenaMode.Effects;
using ArenaMode.Session;
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
    private readonly List<DominationObjectivePoint> _roundObjectives = new();
    private bool _roundBridge;
    private float _roundLiveAt = -1f, _learnGatesAt = -1f;
    private readonly List<Vector3> _rushLanes = new();

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
        ArenaRoundReset.Reset();
        // Bots leave the spawn through the Domination objective layer: a point mid-way between the teams for the
        // opening seconds, then the capture point while it is up.
        if (ArenaCoop.IsAuthority && DominationObjectiveBridge.Available)
        {
            try { DominationObjectiveBridge.Begin(_match, _game, _world); _roundBridge = true; }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("TeamFight: objective layer unavailable, bots keep native pacing: " + ex.Message); }
        }
        else if (ArenaCoop.IsAuthority)
            Plugin.ModLogger.LogWarning("TeamFight: no Arena BigBrain objective layer; bots keep native pacing off spawn.");
        Plugin.ModLogger.LogInfo($"TeamFight: first to {_rounds.RoundsToWin} rounds; point after {_rounds.OpeningSeconds}s"
            + (_roundPoint.HasValue ? $" at {_roundPoint.Value}" : " (no capture point on this map)") + ".");
    }

    private void ReleaseRoundBridge()
    {
        if (!_roundBridge) return;
        _roundBridge = false;
        _roundObjectives.Clear();
        try { DominationObjectiveBridge.Stop(); } catch { /* already gone */ }
    }

    /// <summary>Where the bots are sent: mid-way between the teams while the round opens, the point while it is up.</summary>
    private void DriveRoundBots()
    {
        if (!_roundBridge) return;
        _roundObjectives.Clear();
        if (_rounds.Phase == TeamFightPhase.Live && !HasResult)
        {
            if (_rounds.PointActive && _roundPoint.HasValue)
                _roundObjectives.Add(new DominationObjectivePoint("point", _roundPoint.Value, _rounds.CaptureUsec, false));
            else if (_rushLanes.Count > 0 && Time.time - _roundLiveAt < Settings.TeamFightRushSeconds.Value)
                for (int i = 0; i < _rushLanes.Count; i++)
                    _roundObjectives.Add(new DominationObjectivePoint("lane-" + i, _rushLanes[i], null, false));
        }
        try { DominationObjectiveBridge.Update(_roundObjectives, _bots.Where(Alive).ToList(), _roundObjectives.Count > 0); }
        catch (Exception ex) { Plugin.ModLogger.LogDebug("TeamFight: objective update failed: " + ex.Message); }
    }

    /// <summary>
    /// Opening lanes: one point per bot spread across the middle of the arena, side by side at right angles to the
    /// line between the two teams. The objective layer penalises a point other teammates already hold, so each bot
    /// takes its own lane instead of the whole team filing down one path.
    /// </summary>
    private void ComputeRushLanes()
    {
        _rushLanes.Clear();
        Vector3 usecSum = Vector3.zero, bearSum = Vector3.zero;
        int usecCount = 0, bearCount = 0;
        foreach (var player in _world?.AllAlivePlayersList ?? new List<Player>())
        {
            if (player == null || player.HealthController?.IsAlive != true) continue;
            var side = ArenaSession.TeamDeathmatchBodySide(player, _game, _world);
            if (side == EPlayerSide.Usec) { usecSum += player.Position; usecCount++; }
            else if (side == EPlayerSide.Bear) { bearSum += player.Position; bearCount++; }
        }
        if (usecCount == 0 || bearCount == 0)
        {
            if (_roundPoint.HasValue) _rushLanes.Add(_roundPoint.Value);
            return;
        }
        Vector3 usecCentre = usecSum / usecCount, bearCentre = bearSum / bearCount;
        var middle = (usecCentre + bearCentre) * 0.5f;
        var axis = bearCentre - usecCentre;
        axis.y = 0f;
        float length = axis.magnitude;
        axis = length < 1f ? Vector3.forward : axis / length;
        var lateral = Vector3.Cross(Vector3.up, axis);
        float spacing = Mathf.Clamp(length * 0.15f, 6f, 18f);
        int lanes = Math.Max(1, TeamSize);
        for (int i = 0; i < lanes; i++)
        {
            var point = middle + lateral * ((i - (lanes - 1) * 0.5f) * spacing);
            if (UnityEngine.AI.NavMesh.SamplePosition(point, out var nav, spacing, UnityEngine.AI.NavMesh.AllAreas)) point = nav.position;
            else if (Physics.Raycast(point + Vector3.up * 3f, Vector3.down, out var hit, 12f, LayersMaskController.HighPolyWithTerrainMask)) point = hit.point;
            else continue;
            _rushLanes.Add(point);
        }
        if (_rushLanes.Count == 0 && _roundPoint.HasValue) _rushLanes.Add(_roundPoint.Value);
        Plugin.ModLogger.LogDebug($"TeamFight: {_rushLanes.Count} opening lanes {spacing:F0} m apart across {middle}.");
    }

    /// <summary>Per-client housekeeping that waits on the clock (gate learning after the first opening).</summary>
    private void TickRoundReset()
    {
        if (_learnGatesAt > 0f && Time.time >= _learnGatesAt)
        {
            _learnGatesAt = -1f;
            ArenaRoundReset.LearnGates();
        }
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
        UpdateRoundMarker();
        DriveRoundBots();
        TickRoundReset();
    }

    private GameObject _roundMarker;

    /// <summary>A visible capture zone at the point while it is up: a flat disc on the floor and a beacon above it.</summary>
    private void UpdateRoundMarker()
    {
        bool show = IsRounds && _roundPoint.HasValue && _rounds.Phase == TeamFightPhase.Live && _rounds.PointActive && !HasResult;
        if (!show) { ReleaseRoundMarker(); return; }
        if (_roundMarker == null)
        {
            _roundMarker = new GameObject("ArenaTeamFightPoint");
            _roundMarker.transform.position = _roundPoint.Value;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            foreach (var (name, scale, offset) in new[]
            {
                ("Disc", new Vector3(CaptureRadius * 2f, 0.03f, CaptureRadius * 2f), new Vector3(0f, 0.05f, 0f)),
                ("Beacon", new Vector3(0.35f, 40f, 0.35f), new Vector3(0f, 40f, 0f)),
            })
            {
                var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                part.name = name;
                UnityEngine.Object.Destroy(part.GetComponent<Collider>());
                part.transform.SetParent(_roundMarker.transform, false);
                part.transform.localPosition = offset;
                part.transform.localScale = scale;
                var renderer = part.GetComponent<Renderer>();
                if (shader != null) renderer.material = new Material(shader);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }
        bool? friendlyCapturing = _rounds.CaptureUsec == null ? null : _rounds.CaptureUsec == (_population?.LocalUsec == true);
        var colour = friendlyCapturing == null ? new Color(1f, 1f, 1f, 0.35f)
            : friendlyCapturing == true ? new Color(0.3f, 0.75f, 1f, 0.45f) : new Color(1f, 0.35f, 0.3f, 0.45f);
        foreach (var renderer in _roundMarker.GetComponentsInChildren<Renderer>()) renderer.material.color = colour;
    }

    private void ReleaseRoundMarker()
    {
        if (_roundMarker == null) return;
        UnityEngine.Object.Destroy(_roundMarker);
        _roundMarker = null;
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
        if (now == TeamFightPhase.Live)
        {
            _roundLiveAt = Time.time;
            ComputeRushLanes();
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
            if (_rounds.Round > 1)
            {
                // The arena as the round found it: doors shut, last round's bodies gone.
                ArenaRoundReset.CloseDoors();
                CorpseCleanupService.ClearAllSoon("round reset");
            }
            else ArenaRoundReset.Snapshot();
        }
        if (now == TeamFightPhase.Live && !_roundsAnnouncedLive)
        {
            _roundsAnnouncedLive = true;
            if (_rounds.Round > 1) { _match?.ReportAnnouncerCue(ArenaAnnouncerCue.RoundStart); ArenaRoundReset.OpenGates(); }
            else _learnGatesAt = Time.time + 8f;        // the first gate release has finished animating by then
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
        UpdateRoundMarker();
    }

    private bool _mirrorMatchLive;

    /// <summary>
    /// Co-op client tick: no bots to spawn or retire here (the host owns them); sides come from profiles, the
    /// scoreboard roster from the bodies in the world, and the match clock from the host.
    /// </summary>
    private readonly Dictionary<string, (ArenaScoreboardActor Actor, bool Friendly)> _mirrorSeats = new(StringComparer.Ordinal);

    private void MirrorTick()
    {
        TickRoundReset();
        var friendly = new List<ArenaScoreboardActor>();
        var opposing = new List<ArenaScoreboardActor>();
        var present = new HashSet<string>(StringComparer.Ordinal);
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
            if (actor == null) continue;
            (usec == _population.LocalUsec ? friendly : opposing).Add(actor);
            if (seat != null) { present.Add(seat); _mirrorSeats[seat] = (actor, usec == _population.LocalUsec); }
        }
        foreach (var pair in _mirrorSeats)
        {
            if (present.Contains(pair.Key)) continue;
            var a = pair.Value.Actor;
            (pair.Value.Friendly ? friendly : opposing).Add(new ArenaScoreboardActor(a.ProfileId, a.DisplayName, a.RoleLabel, false, pair.Key));
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
