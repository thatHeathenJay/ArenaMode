using EFT;
using EFT.InventoryLogic;
using System;
using System.Threading.Tasks;

namespace ArenaMode.Kits;

/// <summary>
/// Co-op (Fika) entry kit: the selected kit is written into the PMC profile after the server confirmed the
/// protected start and before the raid is created, so the local player spawns wearing it and Fika syncs it like
/// any loadout. Reuses the protected-team entry machinery (prepare, bundle preload, commit). The solo path keeps
/// its body-swap at raid start, which Redeploy refuses under Fika.
/// </summary>
internal static class CoopEntryKit
{
    internal static async Task IssueAsync(Profile profile, Func<bool> stillCurrent)
    {
        if (profile?.Inventory?.Equipment == null)
            throw new InvalidOperationException("Co-op arena entry kit: the PMC profile has no equipment.");
        KitDefinition kit = KitCatalog.GetSelectedKit()
            ?? throw new InvalidOperationException("Co-op arena entry kit: no kit is available.");
        InventoryEquipment original = profile.Inventory.Equipment;
        InventoryEquipment equipment = TeamArenaEntryKit.Prepare(profile, kit);
        await KitBundlePreloader.PreloadEntryEquipment(equipment, stillCurrent);
        if (!stillCurrent())
            throw new InvalidOperationException("Co-op arena entry kit: the raid start was superseded during preload.");
        TeamArenaEntryKit.Commit(profile, original, equipment);
        if (!ReferenceEquals(profile.Inventory.Equipment, equipment))
            throw new InvalidOperationException("Co-op arena entry kit did not retain its prepared equipment.");
        Plugin.ModLogger.LogInfo($"Co-op arena: issued kit '{kit.Name ?? kit.Id}' to the PMC profile before raid creation.");
    }
}
