using ArenaMode.Session;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace ArenaMode.Fika;

/// <summary>
/// Killcam (com.maschine.Killcam) under co-op. Its death replay starts from a postfix on the player's death, which a
/// downed Fika player never reaches, so the same postfix is invoked here when the local player goes down; the
/// respawn then waits for the replay (bounded) before un-downing. Absent Killcam, or a 0 s mode setting, no replay.
/// </summary>
internal static class FikaKillcam
{
    private static readonly Type PluginType = AccessTools.TypeByName("Killcam.Plugin");
    private static readonly Type DeathPatchType = AccessTools.TypeByName("Killcam.Patches.ReplayPatches+KillcamDeathReplayPatch");
    private static readonly MethodInfo DeathPostfix = DeathPatchType == null ? null
        : AccessTools.Method(DeathPatchType, "Postfix", new[] { typeof(Player), typeof(IPlayer) });
    private static readonly FieldInfo ReplayCtrlField = PluginType == null ? null : AccessTools.Field(PluginType, "ReplayCtrl");
    private static readonly PropertyInfo DeathReplayActive = PluginType == null ? null : AccessTools.Property(PluginType, "DeathReplayActive");
    private static PropertyInfo _isPlaying;

    internal static bool IsAvailable => PluginType != null && DeathPostfix != null && ReplayCtrlField != null;

    private static object Controller => ReplayCtrlField?.GetValue(null);

    internal static bool IsPlaying
    {
        get
        {
            var controller = Controller;
            if (controller == null) return false;
            _isPlaying ??= AccessTools.Property(controller.GetType(), "IsPlaying");
            return _isPlaying != null && (bool)_isPlaying.GetValue(controller);
        }
    }

    /// <summary>Starts the death replay for the downed local player; false when nothing will play.</summary>
    internal static bool TryBegin(FikaPlayer player)
    {
        if (!IsAvailable || player == null || ArenaSession.ReplaySeconds <= 0f) return false;
        try
        {
            if (DeathReplayActive != null && DeathReplayActive.GetGetMethod(true)?.IsStatic == true
                && !(bool)DeathReplayActive.GetValue(null)) return false;
            if (Controller == null) return false;
            // LastAggressor is set on real deaths only; a downed player keeps it null, so fall back to the last
            // damage record (both are protected on Player).
            var t = Traverse.Create(player);
            IPlayer aggressor = t.Field("LastAggressor").GetValue<IPlayer>();
            if (aggressor == null)
            {
                var info = t.Field("LastDamageInfo").GetValue() ?? t.Property("LastDamageInfo").GetValue();
                var owner = info == null ? null : Traverse.Create(info).Field("Player").GetValue();
                aggressor = owner == null ? null : Traverse.Create(owner).Property("iPlayer").GetValue<IPlayer>();
            }
            DeathPostfix.Invoke(null, new object[] { player, aggressor });
            Plugin.Log.LogInfo("Co-op arena: Killcam death replay requested.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Co-op arena: Killcam replay could not start: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>Seconds to allow the replay before the respawn goes ahead regardless.</summary>
    internal static float Bound => ArenaSession.ReplaySeconds + 8f;

    // Killcam arms its recorder from a postfix on EFT.LocalGame.vmethod_0 (raid start), which Fika's CoopGame never
    // runs, so no killer is ever tracked. The same postfix is invoked here once per co-op raid.
    private static readonly MethodInfo RaidStartPostfix = AccessTools.Method(
        AccessTools.TypeByName("Killcam.Patches.ReplayPatches+KillcamRaidStartPatch"), "Postfix");
    private static GameWorld _armedWorld;

    internal static void Tick()
    {
        if (RaidStartPostfix == null) return;
        var world = Comfort.Common.Singleton<GameWorld>.Instantiated ? Comfort.Common.Singleton<GameWorld>.Instance : null;
        if (world == null) { _armedWorld = null; return; }
        if (ReferenceEquals(_armedWorld, world) || !Compat.ArenaCoop.IsAvailable || world.MainPlayer == null) return;
        _armedWorld = world;
        try
        {
            RaidStartPostfix.Invoke(null, null);
            Plugin.Log.LogInfo("Co-op arena: Killcam recorder armed for this raid.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Co-op arena: Killcam arming failed: " + ex.GetBaseException().Message); }
    }
}

internal sealed class FikaKillcamArmer : UnityEngine.MonoBehaviour
{
    private void Update() => FikaKillcam.Tick();
}
