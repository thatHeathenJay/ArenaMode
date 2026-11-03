using EFT.Interactive;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArenaMode.Modes;

/// <summary>
/// Puts the arena back the way a round found it: every door that was opened during play shuts again, and the
/// spawn gate doors (the ones the first countdown's gate release opened) open again when the next round goes live.
/// Each client does this for itself when the round phase changes, so nothing has to travel over the network.
/// </summary>
internal static class ArenaRoundReset
{
    private static readonly Dictionary<string, EDoorState> AtCountdown = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Gates = new(StringComparer.Ordinal);

    internal static void Reset()
    {
        AtCountdown.Clear();
        Gates.Clear();
    }

    private static IEnumerable<Door> Doors()
    {
        try { return LocationScene.GetAllObjects<Door>(false).Where(d => d != null && !string.IsNullOrEmpty(d.Id)).ToList(); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogDebug("TeamFight: doors unavailable: " + ex.Message);
            return Array.Empty<Door>();
        }
    }

    /// <summary>Door states as the first countdown finds them (gates still shut).</summary>
    internal static void Snapshot()
    {
        AtCountdown.Clear();
        foreach (var door in Doors()) AtCountdown[door.Id] = door.DoorState;
    }

    /// <summary>Doors that were shut at the first countdown and are open once the match is live are the gates.</summary>
    internal static void LearnGates()
    {
        Gates.Clear();
        foreach (var door in Doors())
            if (AtCountdown.TryGetValue(door.Id, out var was) && was == EDoorState.Shut && door.DoorState == EDoorState.Open)
                Gates.Add(door.Id);
        Plugin.ModLogger.LogInfo($"TeamFight: {Gates.Count} spawn gate door(s) learned from {AtCountdown.Count} doors.");
    }

    internal static void CloseDoors()
    {
        int closed = 0;
        foreach (var door in Doors())
        {
            if (door.DoorState != EDoorState.Open) continue;
            try { door.Interact(new InteractionResult(EFT.EInteractionType.Close)); closed++; }
            catch (Exception ex) { Plugin.ModLogger.LogDebug($"TeamFight: door {door.Id} would not close: {ex.Message}"); }
        }
        Plugin.ModLogger.LogInfo($"TeamFight: round reset closed {closed} door(s).");
    }

    internal static void OpenGates()
    {
        int opened = 0;
        foreach (var door in Doors())
        {
            if (!Gates.Contains(door.Id) || door.DoorState != EDoorState.Shut) continue;
            try { door.Interact(new InteractionResult(EFT.EInteractionType.Open)); opened++; }
            catch (Exception ex) { Plugin.ModLogger.LogDebug($"TeamFight: gate {door.Id} would not open: {ex.Message}"); }
        }
        if (Gates.Count > 0) Plugin.ModLogger.LogInfo($"TeamFight: round start opened {opened}/{Gates.Count} gate door(s).");
    }
}
