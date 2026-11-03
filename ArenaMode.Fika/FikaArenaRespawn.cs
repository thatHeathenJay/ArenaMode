using ArenaMode.Compat;
using ArenaMode.Events;
using ArenaMode.Modes;
using EFT;
using EFT.Game.Spawning;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ArenaMode.Fika;

/// <summary>
/// In-place respawn for the local Fika player: same body, same NetId, so Fika's position, health and inventory
/// sync keep working. Sequence after the lethal hit (player is downed, prone, input ignored):
/// death event for scoring -> wait for the mode (HoldRespawn) and the delay -> pick a player spawn away from the
/// corpse position -> un-down (Fika sends DownedSync false, restores IsAlive and damage) -> teleport -> full
/// health -> restore the kit in place (magazines, durability, meds) -> respawn event.
/// ponytail: v1 keeps the same kit; a preset change between rounds needs the inventory-sync packet (Phase 2 step 2).
/// </summary>
internal static class FikaArenaRespawn
{
    private static bool _running;

    internal static void Begin(FikaPlayer player)
    {
        if (_running) return;
        _running = true;
        ArenaEvents.RaisePlayerDied(player);
        player.StartCoroutine(Routine(player));
    }

    private static IEnumerator Routine(FikaPlayer player)
    {
        try
        {
            var corpsePosition = player.Position;
            float waited = 0f;
            while (ArenaCoop.RespawnsActive && ArenaMatchService.Current?.Mode?.HoldRespawn == true)
                yield return null;
            while (ArenaCoop.RespawnsActive && waited < Plugin.RespawnDelaySeconds.Value)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (!ArenaCoop.RespawnsActive || player == null || !player.Downed)
                yield break;        // match ended or the player left: Fika's own flow takes over

            var (position, rotation) = PickSpawn(corpsePosition);
            player.ToggleDowned(false);
            player.Teleport(position);
            player.Transform.rotation = rotation;
            player.ActiveHealthController.RestoreFullHealth();
            RestoreKitInPlace(player);
            ArenaEvents.RaisePlayerRespawned(player);
            ArenaMatchService.Current?.Mode?.OnLocalRespawn(player);
            Plugin.Log.LogDebug($"Co-op arena respawn at {position}.");
        }
        finally
        {
            _running = false;
        }
    }

    private static (Vector3, Quaternion) PickSpawn(Vector3 avoid)
    {
        var candidates = new List<ISpawnPoint>();
        foreach (var marker in LocationScene.GetAllObjects<SpawnPointMarker>(false))
        {
            ISpawnPoint p = marker.SpawnPoint;
            if (p == null || (p.Categories & ESpawnCategoryMask.Player) == 0) continue;
            candidates.Add(p);
        }
        if (candidates.Count == 0)
            return (avoid + Vector3.up, Quaternion.identity);
        var far = candidates.FindAll(p => Vector3.Distance(p.Position, avoid) >= 25f);
        var pool = far.Count > 0 ? far : candidates;
        var pick = pool[UnityEngine.Random.Range(0, pool.Count)];
        return (pick.Position, pick.Rotation);
    }

    /// <summary>Refill what a kit respawn would have handed out fresh, without replacing a single item.</summary>
    private static void RestoreKitInPlace(Player player)
    {
        try
        {
            foreach (var item in player.Profile.Inventory.Equipment.GetAllItems())
            {
                if (item is Magazine magazine)
                {
                    foreach (var round in magazine.Cartridges.Items)
                        round.StackObjectsCount = Math.Max(1, Math.Min(magazine.MaxCount, round.StackMaxSize));
                }
                var repairable = item.GetItemComponent<RepairableComponent>();
                if (repairable != null)
                    repairable.Durability = repairable.MaxDurability;
                var medkit = item.GetItemComponent<MedKitComponent>();
                if (medkit != null)
                    medkit.HpResource = medkit.MaxHpResource;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Co-op arena: kit restore skipped: {ex.Message}");
        }
    }
}
