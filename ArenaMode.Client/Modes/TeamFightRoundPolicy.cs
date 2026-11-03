using System;

namespace ArenaMode.Modes;

internal enum TeamFightPhase : byte { Assembling, Countdown, Live, RoundEnd, Finished }
internal enum TeamFightRoundReason : byte { None, Elimination, Capture, Timeout, Draw }

/// <summary>
/// Live TeamFight's round rules: one life per round, first team to <see cref="RoundsToWin"/> rounds. A round is
/// won by eliminating the other team, by holding the capture point (which appears after the opening phase) for
/// <see cref="CaptureSeconds"/>, or at the end of the capture window by the team with more players standing.
/// Pure state on the simulation clock; it owns no bodies, network or UI.
/// </summary>
internal sealed class TeamFightRoundPolicy
{
    internal const float CountdownSeconds = 5f;
    internal const float RoundEndSeconds = 6f;

    internal int RoundsToWin { get; }
    internal int OpeningSeconds { get; }
    internal int CaptureSeconds { get; }
    internal int CaptureWindowSeconds { get; }

    internal TeamFightPhase Phase { get; private set; } = TeamFightPhase.Assembling;
    internal int Round { get; private set; } = 1;
    internal int UsecWins { get; private set; }
    internal int BearWins { get; private set; }
    internal float PhaseEndsAt { get; private set; }
    internal float RoundStartedAt { get; private set; }
    internal bool PointActive { get; private set; }
    internal float CaptureProgress { get; private set; }
    /// <summary>Which side the capture progress belongs to; null until someone stands on the point.</summary>
    internal bool? CaptureUsec { get; private set; }
    internal bool? LastRoundUsecWon { get; private set; }
    internal TeamFightRoundReason LastReason { get; private set; }
    internal bool Finished => Phase == TeamFightPhase.Finished;

    internal TeamFightRoundPolicy(int roundsToWin, int openingSeconds, int captureSeconds, int captureWindowSeconds)
    {
        RoundsToWin = Math.Max(1, roundsToWin);
        OpeningSeconds = Math.Max(0, openingSeconds);
        CaptureSeconds = Math.Max(1, captureSeconds);
        CaptureWindowSeconds = Math.Max(5, captureWindowSeconds);
    }

    internal float Remaining(float now) => Phase is TeamFightPhase.Assembling or TeamFightPhase.Finished
        ? 0f : Math.Max(0f, PhaseEndsAt - now);

    /// <summary>Seconds until the point appears; zero once it is out.</summary>
    internal float UntilPoint(float now) => Phase != TeamFightPhase.Live || PointActive
        ? 0f : Math.Max(0f, RoundStartedAt + OpeningSeconds - now);

    /// <summary>
    /// Authority step. Alive counts include every human and bot of the side; on-point counts are the alive actors
    /// inside the capture radius.
    /// </summary>
    internal void Update(float now, float deltaTime, bool rosterReady, int usecAlive, int bearAlive, int usecOnPoint, int bearOnPoint)
    {
        switch (Phase)
        {
            case TeamFightPhase.Assembling:
                if (rosterReady) { Phase = TeamFightPhase.Countdown; PhaseEndsAt = now + CountdownSeconds; }
                return;
            case TeamFightPhase.Countdown:
                if (!rosterReady) { Phase = TeamFightPhase.Assembling; return; }
                if (now < PhaseEndsAt) return;
                Phase = TeamFightPhase.Live;
                RoundStartedAt = now;
                PhaseEndsAt = now + OpeningSeconds + CaptureWindowSeconds;
                PointActive = false; CaptureProgress = 0f; CaptureUsec = null;
                return;
            case TeamFightPhase.Live:
                if (usecAlive == 0 && bearAlive == 0) { EndRound(now, null, TeamFightRoundReason.Draw); return; }
                if (bearAlive == 0) { EndRound(now, true, TeamFightRoundReason.Elimination); return; }
                if (usecAlive == 0) { EndRound(now, false, TeamFightRoundReason.Elimination); return; }
                if (!PointActive && now - RoundStartedAt >= OpeningSeconds) PointActive = true;
                if (PointActive)
                {
                    bool? holder = usecOnPoint > 0 && bearOnPoint == 0 ? true : bearOnPoint > 0 && usecOnPoint == 0 ? false : null;
                    if (holder.HasValue)
                    {
                        if (CaptureUsec != holder) { CaptureUsec = holder; CaptureProgress = 0f; }
                        CaptureProgress = Math.Min(1f, CaptureProgress + Math.Max(0f, deltaTime) / CaptureSeconds);
                        if (CaptureProgress >= 1f) { EndRound(now, holder, TeamFightRoundReason.Capture); return; }
                    }
                }
                if (now >= PhaseEndsAt)
                {
                    if (usecAlive == bearAlive) EndRound(now, null, TeamFightRoundReason.Draw);
                    else EndRound(now, usecAlive > bearAlive, TeamFightRoundReason.Timeout);
                }
                return;
            case TeamFightPhase.RoundEnd:
                if (now < PhaseEndsAt) return;
                if (UsecWins >= RoundsToWin || BearWins >= RoundsToWin) { Phase = TeamFightPhase.Finished; return; }
                Round++;
                Phase = TeamFightPhase.Assembling;
                PointActive = false; CaptureProgress = 0f; CaptureUsec = null;
                return;
        }
    }

    private void EndRound(float now, bool? usecWon, TeamFightRoundReason reason)
    {
        if (usecWon == true) UsecWins++;
        else if (usecWon == false) BearWins++;
        LastRoundUsecWon = usecWon;
        LastReason = reason;
        Phase = TeamFightPhase.RoundEnd;
        PhaseEndsAt = now + RoundEndSeconds;
        PointActive = false;
    }

    /// <summary>Mirror step for co-op clients: adopt the host's state wholesale.</summary>
    internal void Apply(Compat.ArenaCoopRoundState state, float now)
    {
        Phase = (TeamFightPhase)state.Phase;
        Round = Math.Max(1, state.Round);
        UsecWins = state.UsecWins;
        BearWins = state.BearWins;
        PhaseEndsAt = now + state.PhaseRemaining;
        RoundStartedAt = now - Math.Max(0f, state.RoundElapsed);
        PointActive = state.PointActive;
        CaptureProgress = state.CaptureProgress;
        CaptureUsec = state.CaptureUsec < 0 ? null : state.CaptureUsec == 1;
        LastRoundUsecWon = state.LastRoundUsecWon < 0 ? null : state.LastRoundUsecWon == 1;
        LastReason = (TeamFightRoundReason)state.LastReason;
    }

    internal void Export(Compat.ArenaCoopRoundState state, float now)
    {
        state.Phase = (byte)Phase;
        state.Round = Round;
        state.UsecWins = UsecWins;
        state.BearWins = BearWins;
        state.PhaseRemaining = Remaining(now);
        state.RoundElapsed = Phase == TeamFightPhase.Live ? Math.Max(0f, now - RoundStartedAt) : 0f;
        state.PointActive = PointActive;
        state.CaptureProgress = CaptureProgress;
        state.CaptureUsec = (sbyte)(CaptureUsec == null ? -1 : CaptureUsec == true ? 1 : 0);
        state.LastRoundUsecWon = (sbyte)(LastRoundUsecWon == null ? -1 : LastRoundUsecWon == true ? 1 : 0);
        state.LastReason = (byte)LastReason;
    }
}
