using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's death card (DeathInfoPanel): who killed you, their class, the weapon, where you were hit, what it
/// did to you, with the hit zone lit on the silhouette. Fed by the local player's incoming hits, shown when the
/// local player dies or goes down, hidden when the killcam takes the screen or the respawn lands.
/// </summary>
internal static class ArenaDeathCard
{
    private sealed class Hit
    {
        internal string AggressorId, AggressorName;
        internal bool AggressorIsAi;
        internal EPlayerSide AggressorSide;
        internal string WeaponName;
        internal EBodyPart BodyPart;
        internal EBodyPartColliderType Collider;
        internal float Body, Armor;
        internal int Count;
    }

    private static readonly Dictionary<string, Hit> ByAggressor = new(StringComparer.Ordinal);
    private static Hit _last;
    private static GameObject _canvasGo;
    private static ArenaUiPack _pack;
    private static float _shownAt;

    internal static void Enable() => new DamagePatch().Enable();

    internal static void Reset()
    {
        ByAggressor.Clear();
        _last = null;
        Hide();
    }

    private sealed class DamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.DeclaredMethod(typeof(Player), nameof(Player.ApplyDamageInfo),
                new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) });

        [PatchPostfix]
        private static void Postfix(Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType)
        {
            try
            {
                if (__instance == null || !__instance.IsYourPlayer) return;
                var aggressor = damageInfo.Player?.iPlayer;
                string id = aggressor?.ProfileId ?? ("env:" + damageInfo.DamageType);
                if (!ByAggressor.TryGetValue(id, out var hit))
                    ByAggressor[id] = hit = new Hit { AggressorId = id };
                hit.AggressorName = aggressor?.Profile?.Nickname ?? damageInfo.DamageType.ToString();
                hit.AggressorIsAi = aggressor?.IsAI ?? false;
                hit.AggressorSide = aggressor?.Side ?? EPlayerSide.Savage;
                string weapon = null;
                try { weapon = damageInfo.Weapon?.LocalizedShortName(); } catch { /* unnamed */ }
                hit.WeaponName = weapon ?? hit.WeaponName ?? damageInfo.DamageType.ToString();
                hit.BodyPart = bodyPartType;
                hit.Collider = colliderType;
                hit.Body += Mathf.Max(0f, damageInfo.DidBodyDamage > 0f ? damageInfo.DidBodyDamage : damageInfo.Damage);
                hit.Armor += Mathf.Max(0f, damageInfo.DidArmorDamage > 0f ? damageInfo.DidArmorDamage : damageInfo.ArmorDamage);
                hit.Count++;
                _last = hit;
            }
            catch { /* presentation only */ }
        }
    }

    internal static void Show()
    {
        if (_last == null || !ArenaUiPack.IsAvailable) return;
        Hide();
        try
        {
            _pack = new ArenaUiPack();
            var builder = new ArenaUiBuilder(_pack, _ => null);
            _canvasGo = new GameObject("ArenaDeathCard");
            _canvasGo.transform.SetParent(Plugin.Instance.transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 136;
            canvas.overrideSorting = true;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            var panel = builder.Build(_pack.Layout("DeathInfoPanel"), _canvasGo.transform);
            ArenaUiBuilder.Stretch(panel);
            Bind(panel, _last);
            _shownAt = Time.unscaledTime;
            ArenaDeathCardTicker.Ensure();
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena death card failed: " + ex.Message);
            Hide();
        }
    }

    internal static void Hide()
    {
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        _canvasGo = null;
        _pack?.Dispose();
        _pack = null;
    }

    internal static bool Visible => _canvasGo != null;

    internal static void Tick()
    {
        if (_canvasGo == null) return;
        // The killcam owns the screen once it plays; the card is for the dying view before it.
        var owner = Spectator.KillcamCompat.ReadState(out _, out _);
        bool replayRunning = owner is not (Spectator.OptionalCameraOwnerState.Absent or Spectator.OptionalCameraOwnerState.Idle);
        if (replayRunning || Time.unscaledTime - _shownAt > 8f) Hide();
    }

    private static void Bind(RectTransform panel, Hit hit)
    {
        var screen = ArenaUiBuilder.Find(panel, "DeathInfoScreen");
        var info = ArenaUiBuilder.Find(screen, "InfoPlayer");
        ArenaUiBuilder.SetActive(info, "RankBlock", false);
        var other = ArenaUiBuilder.Find(info, "OtherInfoBlock");
        ArenaUiBuilder.SetActive(other, "Bot", hit.AggressorIsAi);
        ArenaUiBuilder.SetActive(other, "ObservableAvatar48x48", !hit.AggressorIsAi);
        var letter = ArenaUiBuilder.TryFind(other, "ObservableAvatar48x48/FirstLetterNick");
        if (letter != null)
        {
            letter.gameObject.SetActive(true);
            letter.GetComponent<TextMeshProUGUI>().text = string.IsNullOrEmpty(hit.AggressorName) ? "?" : hit.AggressorName.Substring(0, 1).ToUpperInvariant();
        }
        ArenaUiBuilder.SetActive(other, "ObservableAvatar48x48/PlayerIconFace", false);
        ArenaUiBuilder.Text(other, "NameAndClassContainer/Name").text = hit.AggressorName ?? "";
        var cls = ArenaUiBuilder.TryFind(other, "NameAndClassContainer/Class");
        if (cls != null)
        {
            cls.gameObject.SetActive(true);
            ArenaUiBuilder.Text(cls, "ClassName").text = hit.AggressorIsAi ? "BOT" : hit.AggressorSide.ToString().ToUpperInvariant();
        }

        var death = ArenaUiBuilder.Find(screen, "DeathInfo");
        ArenaUiBuilder.Text(death, "WeaponInfo/WeaponName").text = hit.WeaponName ?? "";
        var icon = ArenaUiBuilder.Find(death, "WeaponInfo/IconWeapon").GetComponent<Image>();
        var silhouette = _pack.WholeTexture("Texture2D/" + (hit.WeaponName ?? "") + ".png");
        if (silhouette != null)
        {
            icon.sprite = silhouette;
            icon.rectTransform.sizeDelta = new Vector2(20f * silhouette.rect.width / Mathf.Max(1f, silhouette.rect.height), 20f);
        }
        else icon.gameObject.SetActive(false);
        ArenaUiBuilder.Text(death, "WeaponInfo/PointHit").text = Humanize(hit.BodyPart.ToString()).ToUpperInvariant() + ", " + Humanize(hit.Collider.ToString());

        var damage = ArenaUiBuilder.Find(death, "DamageInfo");
        for (int i = 1; i <= 3; i++) ArenaUiBuilder.SetActive(damage, "LastDamageIcon/AdditionalIconDamage " + i, false);
        var toYou = ArenaUiBuilder.Find(damage, "AggressorInfoBlock");
        ArenaUiBuilder.Text(toYou, "DamageAggressor").text = "To you: ";
        ArenaUiBuilder.Text(toYou, "HealthAndArmorGroup/HealthInfo").text = $"{Mathf.RoundToInt(hit.Body)} ({hit.Count} hit{(hit.Count == 1 ? "" : "s")})";
        ArenaUiBuilder.Text(toYou, "HealthAndArmorGroup/ArmorInfo").text = Mathf.RoundToInt(hit.Armor).ToString();
        ArenaUiBuilder.SetActive(damage, "PlayerInfoBlock", false);   // damage to the enemy: not tracked per aggressor yet

        var parts = ArenaUiBuilder.Find(screen, "CharacterHealthPanel/BodyParts");
        string hitNode = hit.BodyPart switch
        {
            EBodyPart.Head => "Head", EBodyPart.Chest => "Chest", EBodyPart.Stomach => "Stomach",
            EBodyPart.LeftArm => "LeftHand", EBodyPart.RightArm => "RightHand",
            EBodyPart.LeftLeg => "LeftLeg", EBodyPart.RightLeg => "RightLeg", _ => "",
        };
        foreach (Transform part in parts)
        {
            var image = part.GetComponent<Image>();
            if (image == null) continue;
            image.color = part.name == hitNode ? new Color(0.85f, 0.12f, 0.12f, 1f) : new Color(0.55f, 0.6f, 0.65f, 0.45f);
        }
    }

    private static string Humanize(string value)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1])) sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}

internal sealed class ArenaDeathCardTicker : MonoBehaviour
{
    private static ArenaDeathCardTicker _instance;
    internal static void Ensure()
    {
        if (_instance == null) _instance = Plugin.Instance.gameObject.AddComponent<ArenaDeathCardTicker>();
    }
    private void Update() => ArenaDeathCard.Tick();
}
