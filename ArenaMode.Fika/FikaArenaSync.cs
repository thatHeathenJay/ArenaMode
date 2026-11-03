using ArenaMode.Compat;
using ArenaMode.Modes;
using Comfort.Common;
using EFT;
using System;
using UnityEngine;
using FreeCameraController = global::Fika.Core.Main.FreeCamera.FreeCameraController;
using FikaPlayer = global::Fika.Core.Main.Players.FikaPlayer;
using FikaBackendUtils = global::Fika.Core.Main.Utils.FikaBackendUtils;
using IFikaNetworkManager = global::Fika.Core.Networking.IFikaNetworkManager;
using DeliveryMethod = global::Fika.Core.Networking.LiteNetLib.DeliveryMethod;
using INetSerializable = global::Fika.Core.Networking.LiteNetLib.Utils.INetSerializable;
using NetDataReader = global::Fika.Core.Networking.LiteNetLib.Utils.NetDataReader;
using NetDataWriter = global::Fika.Core.Networking.LiteNetLib.Utils.NetDataWriter;

namespace ArenaMode.Fika;

/// <summary>The TeamFight round state on the wire: the host sends it after every change and once a second.</summary>
public struct ArenaRoundStatePacket : INetSerializable
{
    public bool Rounds, MatchLive, PointActive;
    public byte Phase, LastReason;
    public int Round, UsecWins, BearWins;
    public float PhaseRemaining, RoundElapsed, CaptureProgress;
    public Vector3 Point;
    public sbyte CaptureUsec, LastRoundUsecWon;

    public ArenaRoundStatePacket(ArenaCoopRoundState s)
    {
        Rounds = s.Rounds; MatchLive = s.MatchLive; PointActive = s.PointActive;
        Phase = s.Phase; LastReason = s.LastReason;
        Round = s.Round; UsecWins = s.UsecWins; BearWins = s.BearWins;
        PhaseRemaining = s.PhaseRemaining; RoundElapsed = s.RoundElapsed; CaptureProgress = s.CaptureProgress;
        Point = s.Point; CaptureUsec = s.CaptureUsec; LastRoundUsecWon = s.LastRoundUsecWon;
    }

    public ArenaCoopRoundState ToState() => new()
    {
        Rounds = Rounds, MatchLive = MatchLive, PointActive = PointActive, Phase = Phase, LastReason = LastReason,
        Round = Round, UsecWins = UsecWins, BearWins = BearWins, PhaseRemaining = PhaseRemaining, RoundElapsed = RoundElapsed,
        CaptureProgress = CaptureProgress, Point = Point, CaptureUsec = CaptureUsec, LastRoundUsecWon = LastRoundUsecWon,
    };

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Rounds); writer.Put(MatchLive); writer.Put(PointActive);
        writer.Put(Phase); writer.Put(LastReason);
        writer.Put(Round); writer.Put(UsecWins); writer.Put(BearWins);
        writer.Put(PhaseRemaining); writer.Put(RoundElapsed); writer.Put(CaptureProgress);
        writer.Put(Point.x); writer.Put(Point.y); writer.Put(Point.z);
        writer.Put(CaptureUsec); writer.Put(LastRoundUsecWon);
    }

    public void Deserialize(NetDataReader reader)
    {
        Rounds = reader.GetBool(); MatchLive = reader.GetBool(); PointActive = reader.GetBool();
        Phase = reader.GetByte(); LastReason = reader.GetByte();
        Round = reader.GetInt(); UsecWins = reader.GetInt(); BearWins = reader.GetInt();
        PhaseRemaining = reader.GetFloat(); RoundElapsed = reader.GetFloat(); CaptureProgress = reader.GetFloat();
        Point = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        CaptureUsec = reader.GetSByte(); LastRoundUsecWon = reader.GetSByte();
    }
}

/// <summary>
/// Wires the co-op hooks the arena match needs under Fika: the host is the authority, a downed body counts as out,
/// living bodies reset between rounds through the companion's respawn primitives, and the round state travels as
/// <see cref="ArenaRoundStatePacket"/> (registered whenever a new network manager appears).
/// </summary>
internal sealed class FikaArenaSync : MonoBehaviour
{
    private IFikaNetworkManager _registered;

    private void Awake()
    {
        ArenaCoop.AuthorityProbe = () => FikaBackendUtils.IsServer;
        ArenaCoop.DownedProbe = player => player is FikaPlayer fika && fika.Downed;
        ArenaCoop.ResetForRound = player =>
        {
            if (player is FikaPlayer fika && fika.IsYourPlayer) fika.StartCoroutine(FikaArenaRespawn.ResetAlive(fika));
        };
        ArenaCoop.BroadcastRoundState = Send;
    }

    private void Update()
    {
        var manager = Singleton<IFikaNetworkManager>.Instantiated ? Singleton<IFikaNetworkManager>.Instance : null;
        if (manager == null || ReferenceEquals(manager, _registered)) return;
        _registered = manager;
        try
        {
            manager.RegisterPacket<ArenaRoundStatePacket>(OnState);
            Plugin.Log.LogInfo("Co-op arena: round state packet registered (" + (FikaBackendUtils.IsServer ? "host" : "client") + ").");
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Co-op arena: packet registration failed: " + ex.GetBaseException().Message); }
    }

    private static void Send(ArenaCoopRoundState state)
    {
        if (!FikaBackendUtils.IsServer || !Singleton<IFikaNetworkManager>.Instantiated) return;
        var packet = new ArenaRoundStatePacket(state);
        Singleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void OnState(ArenaRoundStatePacket packet)
    {
        if (FikaBackendUtils.IsServer) return;
        try { (ArenaMatchService.Current?.Mode as TeamDeathmatchMode)?.ApplyRoundState(packet.ToState()); }
        catch (Exception ex) { Plugin.Log.LogWarning("Co-op arena: round state apply failed: " + ex.Message); }
    }
}

/// <summary>
/// Spectating while out for the round, on Fika's own free camera: follow a living player when there is one, free
/// flight otherwise; back to first person when the next round is about to start.
/// </summary>
internal static class FikaSpectate
{
    private static bool _active;

    internal static void Begin()
    {
        var controller = Controller();
        if (controller == null || _active) return;
        try
        {
            if (!controller.IsScriptActive) controller.ToggleSpectateCamera();
            _active = controller.IsScriptActive;
            if (_active) Plugin.Log.LogInfo("Co-op arena: spectating until the next round.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning("Co-op arena: spectate failed: " + ex.Message); }
    }

    internal static void End()
    {
        var controller = Controller();
        _active = false;
        if (controller == null) return;
        try { if (controller.IsScriptActive) controller.ToggleCamera(); }
        catch (Exception ex) { Plugin.Log.LogWarning("Co-op arena: leaving spectate failed: " + ex.Message); }
    }

    private static FreeCameraController Controller()
        => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.GetComponent<FreeCameraController>() : null;
}
