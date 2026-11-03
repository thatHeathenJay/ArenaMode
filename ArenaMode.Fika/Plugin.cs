using ArenaMode.Compat;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fika.Core;
using HarmonyLib;

namespace ArenaMode.Fika;

/// <summary>
/// Co-op arena companion. Stock ArenaMode refuses Fika because its respawn replaces the local body (Redeploy).
/// This plugin provides an in-place respawn built on Fika's downed/revive transport, with every piece of the
/// revive gameplay suppressed (no teammate revive, no bleedout, no prone wait): the player sees Arena's death
/// flow and comes back in the same body, which Fika already syncs.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(ArenaMode.Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.maschine.ArenaMode.Fika";
    public const string PluginName = "maschine-ArenaMode.Fika";
    public const string PluginVersion = "0.1.0";

    /// <summary>Fika versions whose downed/revive code paths this plugin was written against.</summary>
    private static readonly string[] ReviewedFikaVersions = { "2.4.6" };

    internal static ManualLogSource Log;
    internal static ConfigEntry<float> RespawnDelaySeconds;

    private void Awake()
    {
        Log = Logger;
        RespawnDelaySeconds = Config.Bind("Respawn", "Delay seconds", 3f,
            "Seconds between the lethal hit and the respawn in modes that respawn (live Arena: a short death cam).");

        if (System.Array.IndexOf(ReviewedFikaVersions, FikaPlugin.FikaVersion) < 0)
        {
            Log.LogWarning($"Fika {FikaPlugin.FikaVersion} is not a reviewed version ({string.Join(", ", ReviewedFikaVersions)}); co-op arena stays off.");
            return;
        }

        var harmony = new Harmony(PluginGuid);
        FikaArenaPatches.Install(harmony);
        ArenaCoop.IsAvailable = true;
        Log.LogInfo($"Co-op arena enabled on Fika {FikaPlugin.FikaVersion}.");
    }
}
