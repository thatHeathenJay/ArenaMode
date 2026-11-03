using ArenaMode.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace ArenaMode.Hud.Live;

/// <summary>Live Arena UI sounds from the player's rip (arena-ui/sounds), played on the mod's master output.</summary>
internal static class ArenaLiveSounds
{
    private static readonly Dictionary<string, AudioClip> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ArenaMasterAudioOutput Output = new();

    internal static void Play(string file, float volume = 1f)
    {
        try
        {
            if (Cache.TryGetValue(file, out var clip))
            {
                if (clip != null) Output.TryPlay(clip, volume, out _);
                return;
            }
            string path = Path.Combine(ArenaUiPack.Directory, "sounds", file);
            if (!File.Exists(path)) { Cache[file] = null; return; }
            var type = file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : AudioType.WAV;
            var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
            var operation = request.SendWebRequest();
            operation.completed += op =>
            {
                try
                {
                    if (request.result != UnityWebRequest.Result.Success) { Cache[file] = null; return; }
                    var loaded = DownloadHandlerAudioClip.GetContent(request);
                    loaded.name = "Arena UI " + file;
                    Cache[file] = loaded;
                    Output.TryPlay(loaded, volume, out _);
                }
                finally { request.Dispose(); }
            };
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena live sound {file} failed: {ex.Message}");
        }
    }

    internal static void Clear()
    {
        foreach (var clip in Cache.Values) if (clip != null) UnityEngine.Object.Destroy(clip);
        Cache.Clear();
    }
}
