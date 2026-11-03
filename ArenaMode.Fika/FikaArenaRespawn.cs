using ArenaMode.Compat;
using ArenaMode.Events;
using ArenaMode.Kits;
using ArenaMode.Modes;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.Game.Spawning;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Player;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ArenaMode.Fika;

/// <summary>
/// In-place respawn for the local Fika player: same body, same NetId, so Fika's position, health and inventory
/// sync keep working. Sequence after the lethal hit (player is downed, prone, input ignored):
/// death event for scoring -> wait for the mode (HoldRespawn) and the delay -> pick the player spawn farthest from
/// living enemies -> un-down (Fika sends DownedSync false, restores IsAlive and damage) -> teleport, stand up ->
/// full health with every negative effect removed -> refill the kit in place and restock what was used up
/// (spawned through Fika's own spawn-in-inventory packet so peers see it) -> respawn event.
/// </summary>
internal static class FikaArenaRespawn
{
    private static bool _running;

    internal static void Begin(FikaPlayer player)
    {
        if (_running) return;
        _running = true;
        ArenaEvents.RaisePlayerDied(player);
        bool replay = FikaKillcam.TryBegin(player);
        player.StartCoroutine(Routine(player, replay));
    }

    private static IEnumerator Routine(FikaPlayer player, bool replay)
    {
        try
        {
            var corpsePosition = player.Position;
            float waited = 0f;
            if (replay)
            {
                // Killcam starts its coroutine a frame or two after the request; wait for it, then for its end.
                float deadline = Time.unscaledTime + FikaKillcam.Bound;
                float startBy = Time.unscaledTime + 1.5f;
                while (!FikaKillcam.IsPlaying && Time.unscaledTime < startBy && ArenaCoop.RespawnsActive) yield return null;
                while (FikaKillcam.IsPlaying && Time.unscaledTime < deadline && ArenaCoop.RespawnsActive) yield return null;
            }
            while (ArenaCoop.RespawnsActive && ArenaMatchService.Current?.Mode?.HoldRespawn == true)
                yield return null;
            while (ArenaCoop.RespawnsActive && waited < Plugin.RespawnDelaySeconds.Value)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (!ArenaCoop.RespawnsActive || player == null || !player.Downed)
                yield break;        // match ended or the player left: Fika's own flow takes over

            var (position, rotation) = PickSpawn(player, corpsePosition);
            player.ToggleDowned(false);
            yield return null;                  // let the character controller leave the downed state first
            player.Teleport(position);
            player.Transform.rotation = rotation;
            StandUp(player);
            RestoreHealth(player);
            yield return Rekit(player, CoopEntryKit.LastIssued);
            RestoreKitInPlace(player, CoopEntryKit.LastIssued);
            Restock(player, CoopEntryKit.LastIssued);
            ArenaEvents.RaisePlayerRespawned(player);
            ArenaMatchService.Current?.Mode?.OnLocalRespawn(player);
            Plugin.Log.LogInfo($"Co-op arena respawn at {position} ({Vector3.Distance(position, corpsePosition):F0} m from the corpse).");
            player.StartCoroutine(Settle(player));
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// A native player spawn point for the local side through the raid's spawn system (Team DM team boxes and area
    /// filters apply), sampled several times; the sample farthest from living enemies wins, at least 25 m from the
    /// corpse when any sample allows it. Scene markers are not used: the ported Arena maps define their spawns in
    /// location data, not as scene objects.
    /// </summary>
    private static (Vector3, Quaternion) PickSpawn(Player player, Vector3 avoid)
    {
        var game = Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>;
        var spawnSystem = game != null ? Redeploy.Core.Spawning.SpawnSystemCache.GetOrCreate(game) : null;
        if (spawnSystem == null)
        {
            Plugin.Log.LogWarning("Co-op arena respawn: no spawn system; staying near the corpse.");
            return (avoid + Vector3.up, Quaternion.identity);
        }
        var enemies = new List<Vector3>();
        if (Singleton<GameWorld>.Instantiated)
            foreach (var other in Singleton<GameWorld>.Instance.AllAlivePlayersList)
                if (other != null && other != player && other.Side != player.Side && other.HealthController?.IsAlive == true)
                    enemies.Add(other.Position);

        ISpawnPoint best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < 12; i++)
        {
            var point = spawnSystem.SelectSpawnPoint(ESpawnCategory.Player, player.Profile.Info.Side, null, null, null, null, player.ProfileId);
            if (point == null) continue;
            float fromCorpse = Vector3.Distance(point.Position, avoid);
            float fromEnemy = enemies.Count == 0 ? 1000f : enemies.Min(e => Vector3.Distance(point.Position, e));
            float score = Mathf.Min(fromEnemy, 150f) + (fromCorpse >= 25f ? 1000f : 0f);
            if (score > bestScore) { bestScore = score; best = point; }
        }
        if (best == null)
        {
            Plugin.Log.LogWarning("Co-op arena respawn: the spawn system returned no player point; staying near the corpse.");
            return (avoid + Vector3.up, Quaternion.identity);
        }
        return (best.Position, best.Rotation);
    }

    /// <summary>
    /// Delayed damage, effect residues and hits in the frames around the teleport can land after the first restore,
    /// so it runs again once things settle, under a short spawn protection (live Arena shields fresh spawns too).
    /// </summary>
    private static IEnumerator Settle(FikaPlayer player)
    {
        const float protection = 2f;
        float started = Time.time;
        try { player.ActiveHealthController.SetDamageCoeff(0f); } catch { /* unavailable */ }
        yield return new WaitForSeconds(0.5f);
        if (player == null || !ArenaCoop.RespawnsActive) yield break;
        RestoreHealth(player);
        FikaArenaPatches.ClearDeathCameraEffects();
        while (Time.time - started < protection && ArenaCoop.RespawnsActive && player != null) yield return null;
        try { if (player != null) player.ActiveHealthController.SetDamageCoeff(1f); } catch { /* unavailable */ }
    }

    /// <summary>Fika's downed state puts the player prone and un-downing leaves them there.</summary>
    private static void StandUp(Player player)
    {
        try
        {
            player.MovementContext.IsInPronePose = false;
            player.MovementContext.SetPoseLevel(1f, false);
            player.MovementContext.SetTilt(0f, false);        // a lean held at death otherwise survives the respawn
            player.MovementContext.PlayerAnimatorSetTilt(0f);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Co-op arena: stand-up skipped: {ex.Message}"); }
    }

    /// <summary>Full health and no fractures, bleeds, pain or contusion carried over from the death.</summary>
    private static void RestoreHealth(Player player)
    {
        try
        {
            var health = player.ActiveHealthController;
            // Effects live per body part (contusion on the head, bleeds on limbs, the vignettes they drive): clear
            // every part, not just Common, or the screen keeps last life's effects.
            foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
            {
                try { health.RemoveNegativeEffects(part); } catch { /* part without effects */ }
                try { health.RemoveBleedingEffects(part); } catch { /* none */ }
                if (part != EBodyPart.Common) { try { health.FullRestoreBodyPart(part); } catch { /* none */ } }
            }
            try { health.RemoveIntoxication(); } catch { /* none */ }
            try { health.RemoveMedEffect(); } catch { /* none */ }
            try { health.RemoveMisfireEffect(); } catch { /* none */ }
            health.RestoreFullHealth();
            AccessTools.Method(typeof(Player), "UpdateSpeedLimitByHealth")?.Invoke(player, null);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Co-op arena: health restore incomplete: {ex.Message}"); }
    }

    private static readonly EquipmentSlot[] WeaponSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };

    /// <summary>
    /// Live hands the preset back each life. Weapons picked up last life are dropped (hands emptied first), the kit's
    /// primary is rebuilt with its magazines when it is missing or replaced, foreign grid items (the picked-up gun's
    /// magazines and ammo) are discarded, and the kit's primary is drawn. Armour slots are left as they are.
    /// ponytail: peers see the rebuilt gun as its bare template (Fika's spawn packet carries one item, no mods).
    /// </summary>
    private static IEnumerator Rekit(FikaPlayer player, KitDefinition kit)
    {
        if (kit?.Items == null) yield break;
        var equipment = player.Profile?.Inventory?.Equipment;
        var controller = player.InventoryController;
        if (equipment == null || controller == null) yield break;
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in kit.Items) if (node.Tpl != null) expected.Add(node.Tpl);
        foreach (var stack in kit.Fill?.RigContents ?? new List<KitStackedItem>()) if (stack.Tpl != null) expected.Add(stack.Tpl);
        string kitPrimary = kit.Items.FirstOrDefault(n => n.SlotId == "FirstPrimaryWeapon")?.Tpl;

        // Foreign weapons in the weapon slots.
        var foreign = new List<Item>();
        foreach (var slotName in WeaponSlots)
        {
            var slot = equipment.GetSlot(slotName);
            var item = slot?.ContainedItem;
            if (item == null) continue;
            string wanted = kit.Items.FirstOrDefault(n => n.SlotId == slotName.ToString())?.Tpl;
            if (!string.Equals(item.TemplateId, wanted, StringComparison.Ordinal)) foreign.Add(item);
        }
        bool handsHeldForeign = foreign.Any(f => ReferenceEquals(player.HandsController?.Item, f));
        if (handsHeldForeign)
        {
            bool done = false;
            try { player.SetEmptyHands(_ => done = true); } catch { done = true; }
            float until = Time.unscaledTime + 3f;
            while (!done && Time.unscaledTime < until) yield return null;
        }
        int dropped = 0;
        foreach (var item in foreign)
        {
            var result = ItemManipulator.RemoveWithoutRestrictions(item, controller);
            if (result.Succeeded) dropped++;
            else Plugin.Log.LogWarning($"Co-op arena rekit: could not drop {item.LocalizedShortName()}: {result.Error}");
        }

        // Foreign grid items (magazines and ammo for the dropped gun, loot).
        foreach (var container in KitBuilder.GridContainersOf(equipment))
        {
            if (ReferenceEquals(container, equipment.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem)) continue;
            foreach (var item in container.Grids.SelectMany(g => g.Items).ToList())
            {
                if (expected.Contains(item.TemplateId)) continue;
                var result = ItemManipulator.RemoveWithoutRestrictions(item, controller);
                if (result.Succeeded) dropped++;
            }
        }

        // The kit's primary, rebuilt when missing.
        var primarySlot = equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon);
        Weapon drawn = primarySlot?.ContainedItem as Weapon;
        if (kitPrimary != null && primarySlot != null && primarySlot.ContainedItem == null)
        {
            var weapon = KitBuilder.BuildPrimaryWeapon(kit, kit.Fill?.SpareMagazines ?? 0, out var spares);
            if (weapon != null)
            {
                var add = ItemManipulator.AddWithoutRestrictions(weapon, primarySlot.CreateItemAddress(), controller);
                if (add.Succeeded)
                {
                    drawn = weapon;
                    var containers = KitBuilder.GridContainersOf(equipment);
                    foreach (var spare in spares) KitBuilder.AddToAnyGridOf(containers, spare, out _);
                    KitBuilder.RegisterMagazinesAsChecked(player.Profile);
                    AnnounceSpawn(player, weapon);
                }
                else Plugin.Log.LogWarning($"Co-op arena rekit: could not place the kit weapon: {add.Error}");
            }
        }
        if (dropped > 0 || handsHeldForeign)
            Plugin.Log.LogInfo($"Co-op arena rekit: dropped {dropped} foreign item(s).");
        if (drawn != null && !ReferenceEquals(player.HandsController?.Item, drawn))
        {
            bool done = false;
            try { player.SetItemInHands(drawn, _ => done = true); } catch { done = true; }
            float until = Time.unscaledTime + 3f;
            while (!done && Time.unscaledTime < until) yield return null;
        }
    }

    /// <summary>Peers learn of a rebuilt gun as a bare template at the same address.</summary>
    private static void AnnounceSpawn(FikaPlayer player, Item item)
    {
        try
        {
            var packet = new SpawnItemInInventoryPacket
            {
                NetId = player.NetId, ItemId = item.Id, TemplateId = item.TemplateId, Amount = 1, ItemAddress = item.Parent,
            };
            Singleton<IFikaNetworkManager>.Instance.SendData(ref packet, global::Fika.Core.Networking.LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Co-op arena rekit: peer spawn packet failed: " + ex.Message); }
    }

    /// <summary>
    /// Refill what a kit respawn would have handed out fresh, without replacing a single item. A magazine shot dry
    /// has no ammo stack left at all (EFT deletes a stack at zero), so one is created from the kit's round, and an
    /// empty chamber gets a round too.
    /// </summary>
    private static void RestoreKitInPlace(Player player, KitDefinition kit)
    {
        try
        {
            var factory = Singleton<ItemFactory>.Instance;
            string ammoTpl = kit?.Items?.FirstOrDefault(n => n.SlotId == "cartridges")?.Tpl;
            int magazines = 0, refilled = 0;
            foreach (var item in player.Profile.Inventory.Equipment.GetAllItems().ToList())
            {
                if (item is Magazine magazine)
                {
                    magazines++;
                    if (magazine.Cartridges.Items.Any())
                    {
                        foreach (var round in magazine.Cartridges.Items)
                            round.StackObjectsCount = Math.Max(1, Math.Min(magazine.MaxCount, round.StackMaxSize));
                        ammoTpl ??= magazine.Cartridges.Items.First().TemplateId;
                    }
                    else if (ammoTpl != null)
                    {
                        var ammo = factory.CreateItem(MongoID.Generate(), ammoTpl, null);
                        ammo.StackObjectsCount = Math.Max(1, Math.Min(magazine.MaxCount, ammo.StackMaxSize));
                        var load = magazine.Cartridges.Add(ammo, false);
                        if (load.Failed) Plugin.Log.LogWarning($"Co-op arena: reloading an empty magazine failed: {load.Error}");
                        else refilled++;
                    }
                }
                else if (item is Weapon weapon && ammoTpl != null)
                {
                    foreach (var chamber in weapon.Chambers)
                        if (chamber.ContainedItem == null)
                        {
                            var round = factory.CreateItem(MongoID.Generate(), ammoTpl, null);
                            round.StackObjectsCount = 1;
                            var load = chamber.AddWithoutRestrictions(round);
                            if (load.Failed) Plugin.Log.LogWarning($"Co-op arena: chambering a round failed: {load.Error}");
                        }
                }
                var repairable = item.GetItemComponent<RepairableComponent>();
                if (repairable != null)
                    repairable.Durability = repairable.MaxDurability;
                var medkit = item.GetItemComponent<MedKitComponent>();
                if (medkit != null)
                    medkit.HpResource = medkit.MaxHpResource;
            }
            Plugin.Log.LogInfo($"Co-op arena: refilled {magazines} magazine(s), {refilled} of them from empty.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Co-op arena: kit restore skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts back the consumables the kit started with (pocket items, rig contents) that were used up or thrown,
    /// counted by template. Spawned the way Fika's own spawnItemInInventory does, so every peer learns of the item.
    /// </summary>
    private static void Restock(FikaPlayer player, KitDefinition kit)
    {
        if (kit == null) return;
        try
        {
            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in kit.Items)
                if (node.SlotId != null && node.SlotId.StartsWith("pocket", StringComparison.OrdinalIgnoreCase))
                    expected[node.Tpl] = expected.TryGetValue(node.Tpl, out int n) ? n + 1 : 1;
            foreach (var stack in kit.Fill?.RigContents ?? new List<KitStackedItem>())
                expected[stack.Tpl] = (expected.TryGetValue(stack.Tpl, out int n) ? n : 0) + Math.Max(1, stack.Count);

            var actual = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in player.Profile.Inventory.Equipment.GetAllItems())
                actual[item.TemplateId] = (actual.TryGetValue(item.TemplateId, out int n) ? n : 0) + 1;

            int spawned = 0;
            foreach (var pair in expected)
            {
                int missing = pair.Value - (actual.TryGetValue(pair.Key, out int have) ? have : 0);
                for (int i = 0; i < missing; i++)
                    if (SpawnInInventory(player, pair.Key)) spawned++;
            }
            if (spawned > 0) Plugin.Log.LogInfo($"Co-op arena: restocked {spawned} consumable(s).");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Co-op arena: restock skipped: {ex.Message}");
        }
    }

    private static bool SpawnInInventory(FikaPlayer player, string templateId)
    {
        var item = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), templateId, null);
        if (item == null) return false;
        item.StackObjectsCount = 1;
        var network = Singleton<IFikaNetworkManager>.Instance;
        var stash = network.TemporaryStash;
        var grid = stash.Grid;
        var tempMove = grid.AddItemWithoutRestrictions(item, grid.FindFreeSpace(item));
        if (tempMove.Failed)
        {
            Plugin.Log.LogWarning($"Co-op arena restock: temporary stash refused {templateId}: {tempMove.Error}");
            return false;
        }
        new ItemController(stash, player.InventoryController.ID, "temp item stash", false, EOwnerType.Profile);
        var root = player.InventoryController.RootItem as CompoundItem;
        var moveResult = ItemManipulator.QuickFindAppropriatePlace(item, player.InventoryController,
            new List<CompoundItem> { root }, ItemManipulator.EMoveItemOrder.IgnoreItemParent, true);
        if (moveResult.Failed)
        {
            Plugin.Log.LogWarning($"Co-op arena restock: no room for {item.LocalizedShortName()}.");
            return false;
        }
        player.InventoryController.ConvertOperationResultToOperation(moveResult.Value).Execute(result =>
        {
            if (result.Failed) Plugin.Log.LogWarning($"Co-op arena restock: move failed: {result.Error}");
        });
        _ = Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
            ObjectsFactory.AssemblyType.Online, item.Template.AllResources.ToHashSet(), JobYieldPriority.Immediate);
        var packet = new SpawnItemInInventoryPacket
        {
            NetId = player.NetId,
            ItemId = item.Id,
            TemplateId = templateId,
            Amount = 1,
            ItemAddress = item.Parent,
        };
        network.SendData(ref packet, global::Fika.Core.Networking.LiteNetLib.DeliveryMethod.ReliableOrdered);
        return true;
    }
}
