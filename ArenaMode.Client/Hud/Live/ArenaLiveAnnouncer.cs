using ArenaMode.Audio;
using ArenaMode.Combat;
using ArenaMode.Modes;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's English commentator (arena-ui/sounds/commentator, from the player's rip) bound to Team Deathmatch:
/// match start, first blood, kills by victim colour, streaks, lead changes, time warnings, final battle, match end.
/// One line at a time, random variant per family, never the same variant twice in a row.
/// </summary>
internal static class ArenaLiveAnnouncer
{
    private const float KillCooldown = 9f;
    private const float KillChance = 0.4f;

    private static readonly ArenaMasterAudioOutput Voice = new();
    private static readonly System.Random Pick = new();
    private static readonly Dictionary<string, List<string>> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> LastPlayed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Pending = new();

    private static ArenaMatch _match;
    private static TeamDeathmatchMode _tdm;
    private static float _busyUntil, _nextKillAt;
    private static bool _started, _firstBlood, _threeMin, _oneMin, _timeUp, _finalBattle, _ended;
    private static int _lead;
    private static int _timeLimit;

    private static string FriendlyColour => string.Equals(_tdm?.FriendlyFaction, "USEC", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
    private static string OpponentColour => FriendlyColour == "blue" ? "red" : "blue";

    internal static bool IsAvailable => Families.Count > 0;

    internal static void Attach(ArenaMatch match, TeamDeathmatchMode tdm)
    {
        Detach();
        Families.Clear();
        string folder = Path.Combine(ArenaUiPack.Directory, "sounds", "commentator");
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, "eng_commentator_*.wav"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int cut = name.LastIndexOf('_');
            if (cut <= 0) continue;
            string family = name.Substring("eng_commentator_".Length, cut - "eng_commentator_".Length);
            if (!Families.TryGetValue(family, out var list)) Families[family] = list = new List<string>();
            list.Add(Path.GetFileName(file));
        }
        if (Families.Count == 0) return;
        _match = match;
        _tdm = tdm;
        match.OnKill += OnKill;
        Plugin.ModLogger.LogInfo($"Arena live commentator: {Families.Count} phrase families.");
    }

    internal static void Detach()
    {
        if (_match != null) _match.OnKill -= OnKill;
        _match = null;
        _tdm = null;
        Pending.Clear();
        _busyUntil = _nextKillAt = 0f;
        _started = _firstBlood = _threeMin = _oneMin = _timeUp = _finalBattle = _ended = false;
        _lead = 0;
        _timeLimit = 0;
    }

    internal static void Tick(ArenaAnnouncementSnapshot snapshot)
    {
        if (_match == null || !IsAvailable) return;
        if (snapshot.Live && !_started)
        {
            _started = true;
            _timeLimit = snapshot.SecondsRemaining is double total ? (int)Math.Round(total) : 0;
            Say("start", true);
        }
        if (_started && !_ended && snapshot.SecondsRemaining is double left)
        {
            if (left <= 180 && !_threeMin && _timeLimit > 240) { _threeMin = true; Say("3_min_left", true); }
            if (left <= 60 && !_oneMin && _timeLimit > 90) { _oneMin = true; Say("1_min_left", true); }
        }
        if (_started && !_ended && snapshot.KillsRemaining is int remaining && remaining <= 2 && !_finalBattle)
        {
            _finalBattle = true;
            Say("eng_final_battle", true);
        }
        if (_started && !_ended && snapshot.Lead is int lead && lead != 0 && lead != _lead)
        {
            _lead = lead;
            string colour = lead > 0 ? FriendlyColour : OpponentColour;
            string phase = _timeLimit <= 0 || snapshot.SecondsRemaining is not double secs ? "middle"
                : secs > _timeLimit * 0.66 ? "start" : secs > _timeLimit * 0.33 ? "middle" : "end";
            Say($"{colour}_team_lead_match_{phase}", false);
        }
        if (snapshot.Terminal && !_ended)
        {
            _ended = true;
            Pending.Clear();
            _busyUntil = 0f;
            if (snapshot.Draw) Say("draw", true);
            else if (snapshot.SecondsRemaining is double s && s <= 0.5 && !_timeUp) { _timeUp = true; Say("end_of_time", true); Say(snapshot.Lead > 0 ? $"eng_match_end_{FriendlyColour}" : $"eng_match_end_{OpponentColour}", false); }
            else Say(snapshot.Lead > 0 ? $"eng_match_end_{FriendlyColour}" : $"eng_match_end_{OpponentColour}", true);
        }
        Pump();
    }

    private static void OnKill(ArenaKill kill)
    {
        if (_match == null || !_started || _ended || kill == null) return;
        if (!_firstBlood)
        {
            _firstBlood = true;
            Say("first_blood", true);
            return;
        }
        int streak = kill.KilledByLocal ? _match.Streak : 0;
        if (streak >= 3 && streak % 3 == 0) { Say("killing_streak", false); return; }
        if (Time.unscaledTime < _nextKillAt || Pick.NextDouble() > KillChance) return;
        _nextKillAt = Time.unscaledTime + KillCooldown;
        bool victimFriendly = kill.VictimIsFriendly ?? kill.LocalIsVictim;
        string family = "tm_player_death_" + (victimFriendly ? FriendlyColour : OpponentColour);
        Say(Families.ContainsKey(family) ? family : "player_death", false);
    }

    /// <summary>Important lines queue behind the current one; chatter is dropped when the commentator is busy.</summary>
    private static void Say(string family, bool important)
    {
        if (!Families.ContainsKey(family)) return;
        if (Time.unscaledTime < _busyUntil || Pending.Count > 0)
        {
            if (important && Pending.Count < 3) Pending.Enqueue(family);
            return;
        }
        Play(family);
    }

    private static void Pump()
    {
        if (Pending.Count == 0 || Time.unscaledTime < _busyUntil) return;
        Play(Pending.Dequeue());
    }

    private static void Play(string family)
    {
        var variants = Families[family];
        string file = variants[Pick.Next(variants.Count)];
        if (variants.Count > 1 && LastPlayed.TryGetValue(family, out var last) && last == file)
            file = variants[(variants.IndexOf(file) + 1) % variants.Count];
        LastPlayed[family] = file;
        _busyUntil = Time.unscaledTime + 5f;       // refined to the clip length once it is loaded
        ArenaLiveSounds.Play(Path.Combine("commentator", file), 1f, Voice, length => _busyUntil = Time.unscaledTime + length + 0.4f);
    }
}
