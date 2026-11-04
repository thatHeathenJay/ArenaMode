using ArenaMode.Kits;
using EFT;
using EFT.InventoryLogic;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Still portraits of the player wearing each preset, as live's preset cards show them: the character preview
/// renders the kit once into the card, the frame is copied to a texture, the preview is released. One at a time
/// (the preview has four world slots), cached per preset until the preset is edited.
/// </summary>
internal static class ArenaKitPortraits
{
    private static readonly Dictionary<string, Texture2D> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<(KitDefinition Kit, Profile Profile, InventoryEquipment Equipment, RectTransform Host, Action<Texture2D> Done)> Pending = new();
    private static bool _running;

    internal static Texture2D Get(string kitId)
        => kitId != null && Cache.TryGetValue(kitId, out var texture) && texture != null ? texture : null;

    internal static void Invalidate(string kitId)
    {
        if (kitId == null || !Cache.TryGetValue(kitId, out var texture)) return;
        Cache.Remove(kitId);
        if (texture != null) Object.Destroy(texture);
    }

    /// <summary>Delivers the cached portrait at once, or renders one into <paramref name="host"/> and delivers it.</summary>
    internal static void Request(KitDefinition kit, Profile profile, InventoryEquipment equipment, RectTransform host, Action<Texture2D> done)
    {
        if (kit == null || host == null) return;
        if (Get(kit.Id) is { } cached) { done(cached); return; }
        if (profile == null || equipment == null) return;
        Pending.Enqueue((kit, profile, equipment, host, done));
        if (!_running) Plugin.Instance.StartCoroutine(Pump());
    }

    private static IEnumerator Pump()
    {
        _running = true;
        while (Pending.Count > 0)
        {
            var job = Pending.Dequeue();
            if (job.Host == null) continue;                                 // the card is gone
            if (Get(job.Kit.Id) is { } cached) { job.Done(cached); continue; }
            yield return Capture(job);
        }
        _running = false;
    }

    private static IEnumerator Capture((KitDefinition Kit, Profile Profile, InventoryEquipment Equipment, RectTransform Host, Action<Texture2D> Done) job)
    {
        Session.ArenaCharacterPreview preview;
        try
        {
            preview = new Session.ArenaCharacterPreview(job.Profile, job.Host, () => { }, job.Equipment);
            preview.Frame(new Vector3(0f, 0.05f, 1.25f));   // head to knees in a 306x221 card
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogDebug($"Arena portraits: '{job.Kit.Id}' preview failed: {ex.Message}");
            yield break;
        }
        float until = Time.unscaledTime + 10f;
        while (Time.unscaledTime < until && job.Host != null && !(preview.IsReady && preview.CurrentTexture != null)) yield return null;
        for (int i = 0; i < 4 && job.Host != null; i++) yield return null;   // first frames rendered
        Texture2D texture = null;
        try
        {
            var source = preview.CurrentTexture;
            if (source != null && job.Host != null)
            {
                texture = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false);
                var previous = RenderTexture.active;
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                texture.Apply(false, true);
                RenderTexture.active = previous;
                texture.name = "Arena portrait " + job.Kit.Id;
            }
        }
        catch (Exception ex) { Plugin.ModLogger.LogDebug($"Arena portraits: '{job.Kit.Id}' capture failed: {ex.Message}"); }
        try { preview.Dispose(); } catch { /* already gone */ }
        if (texture != null)
        {
            Invalidate(job.Kit.Id);
            Cache[job.Kit.Id] = texture;
        }
        if (job.Host != null) job.Done(texture);
    }
}
