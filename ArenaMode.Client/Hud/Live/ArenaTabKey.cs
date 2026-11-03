using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI.Screens;
using UnityEngine;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's Tab: a tap opens the character inventory, a hold shows the team table until release (moving and
/// shooting stay possible). Sits in EFT's input tree ahead of the player owner and swallows ToggleInventory, then
/// forwards it to the owner on a short release.
/// </summary>
internal sealed class ArenaTabKey : InputNode
{
    private const float HoldSeconds = 0.25f;
    private static InputTree _tree;

    private float _pressedAt = -1f;
    private bool _forwarding;

    internal static bool Held { get; private set; }

    internal static ArenaTabKey Attach(GameObject host)
    {
        _tree ??= GameObject.Find("___Input")?.GetComponent<InputTree>();
        if (_tree == null)
        {
            Plugin.ModLogger.LogWarning("Arena live HUD: EFT input tree not found; Tab keeps its stock behaviour.");
            return null;
        }
        var node = host.AddComponent<ArenaTabKey>();
        _tree.Add(node);
        return node;
    }

    private void OnDestroy()
    {
        Held = false;
        if (_tree != null) _tree.Remove(this);
    }

    public override ETranslateResult TranslateCommand(ECommand command)
    {
        if (_forwarding || !command.IsCommand(ECommand.ToggleInventory)) return ETranslateResult.Ignore;
        var screens = EftScreenManager.Instance;
        if (screens == null || !screens.CheckCurrentScreen(EEftScreenType.BattleUI)) return ETranslateResult.Ignore;
        _pressedAt = Time.unscaledTime;
        return ETranslateResult.Block;
    }

    public override void TranslateAxes(ref float[] axes) { }

    public override ECursorResult ShouldLockCursor() => ECursorResult.Ignore;

    private void Update()
    {
        if (_pressedAt < 0f) return;
        float held = Time.unscaledTime - _pressedAt;
        if (Input.GetKey(KeyCode.Tab))
        {
            Held = held >= HoldSeconds;
            return;
        }
        _pressedAt = -1f;
        bool wasHeld = Held;
        Held = false;
        if (!wasHeld && held < HoldSeconds) OpenInventory();
    }

    private void OpenInventory()
    {
        var owner = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance?.MainPlayer?.GetComponent<GamePlayerOwner>() : null;
        if (owner == null) return;
        _forwarding = true;
        try { owner.TranslateCommand(ECommand.ToggleInventory); }
        finally { _forwarding = false; }
    }
}
