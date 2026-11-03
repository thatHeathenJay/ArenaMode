using ArenaMode.Config;
using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Reflection;

namespace ArenaMode.Bots;

/// <summary>
/// Forces a SAIN preset by name on this install (Settings.SainPreset) ahead of the server's forced preset, so the
/// Arena client runs its own bot preset while the extraction client keeps the server's. SAIN applies its preset in
/// Awake, before this plugin loads, so after patching its config fetch the override re-runs SAIN's own remote
/// config-change path, which re-fetches (hitting the postfix) and switches presets outside a raid.
/// </summary>
internal static class ArenaSainPreset
{
    private static readonly Harmony Patches = new(Plugin.PluginGuid + ".SainPreset");
    private static PropertyInfo _forced;

    internal static void Enable()
    {
        string wanted = Settings.SainPreset?.Value?.Trim();
        if (string.IsNullOrEmpty(wanted)) return;
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue("me.sol.sain", out var sain) || sain.Instance == null) return;
            var assembly = sain.Instance.GetType().Assembly;
            var client = assembly.GetType("SAIN.Preset.Server.ServerConfigClient", true);
            _forced = client.GetProperty("ForcedPresetName", BindingFlags.Public | BindingFlags.Static);
            var fetch = client.GetMethod("Fetch", BindingFlags.Public | BindingFlags.Static);
            var reapply = assembly.GetType("SAIN.Preset.Server.PresetSync", true)
                .GetMethod("ApplyRemoteConfigChange", BindingFlags.Public | BindingFlags.Static);
            if (_forced?.GetSetMethod(true) == null || fetch == null || reapply == null)
                throw new MissingMemberException("SAIN preset contract changed");
            Patches.Patch(fetch, postfix: new HarmonyMethod(typeof(ArenaSainPreset), nameof(AfterFetch)));
            reapply.Invoke(null, null);
            Plugin.ModLogger.LogInfo($"Arena SAIN preset '{wanted}' forced on this install.");
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena SAIN preset override unavailable: {ex.GetBaseException().Message}");
        }
    }

    private static void AfterFetch()
    {
        try { _forced.SetValue(null, Settings.SainPreset.Value.Trim()); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning($"Arena SAIN preset override failed: {ex.Message}"); }
    }
}
