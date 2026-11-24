using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Localization;
using UmbrellaWeaponRestrict.Config;

namespace UmbrellaWeaponRestrict.Menus;

/// <summary>
/// Center-HTML menu driven with movement keys, same model as the other Umbrella CS2 plugins:
/// W/S move, E selects, Shift or A go back and R closes (all configurable).
/// </summary>
internal sealed class WasdMenuService
{
    private const int MaxTitleLength = 46;
    private const int MaxItemLabelLength = 34;
    private const string DefaultColor = "#ffffff";

    private static readonly Dictionary<string, PlayerButtons> ButtonMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W"] = PlayerButtons.Forward,
        ["A"] = PlayerButtons.Moveleft,
        ["S"] = PlayerButtons.Back,
        ["D"] = PlayerButtons.Moveright,
        ["E"] = PlayerButtons.Use,
        ["R"] = PlayerButtons.Reload,
        ["Shift"] = PlayerButtons.Speed
    };

    private readonly IStringLocalizer _localizer;
    private readonly Dictionary<int, WasdMenuState> _states = new();
    private readonly List<int> _slots = new();
    private UmbrellaWeaponRestrictConfig.MenuSettings _settings = null!;
    private PlayerButtons _scrollUp;
    private PlayerButtons _scrollDown;
    private PlayerButtons _select;
    private PlayerButtons _back;
    private PlayerButtons _cancel;

    public WasdMenuService(UmbrellaWeaponRestrictConfig.MenuSettings settings, IStringLocalizer localizer)
    {
        _localizer = localizer;
        UpdateSettings(settings);
    }

    public static bool IsKnownKey(string key) => ButtonMap.ContainsKey(key);

    public void UpdateSettings(UmbrellaWeaponRestrictConfig.MenuSettings settings)
    {
        _settings = settings;
        _scrollUp = ToButtons(settings.ScrollUpKeys);
        _scrollDown = ToButtons(settings.ScrollDownKeys);
        _select = ToButtons(settings.SelectKeys);
        _back = ToButtons(settings.BackKeys);
        _cancel = ToButtons(settings.CancelKeys);

        Invalidate();
    }

    /// <summary>Redraws every open menu on the next tick, for when what the labels show has changed.</summary>
    public void Invalidate()
    {
        foreach (WasdMenuState state in _states.Values)
        {
            state.Html = null;
        }
    }

    public void Open(CCSPlayerController player, WasdMenuScreen screen, int selectedIndex = 0)
    {
        if (_states.Remove(player.Slot, out WasdMenuState? previous))
        {
            Release(previous);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _states[player.Slot] = new WasdMenuState
        {
            Screen = screen,
            SelectedIndex = ClampSelection(screen, selectedIndex),
            OpenedAtUtc = now,
            LastInputUtc = now
        };
    }

    /// <summary>
    /// Replaces the whole menu with a new root screen. An open menu keeps accepting keys right away so
    /// an admin can keep editing; a closed one is opened with the usual initial delay.
    /// </summary>
    public void Reset(CCSPlayerController player, WasdMenuScreen screen, int selectedIndex)
    {
        if (!_states.TryGetValue(player.Slot, out WasdMenuState? state))
        {
            Open(player, screen, selectedIndex);
            return;
        }

        state.History.Clear();
        state.Screen = screen;
        state.SelectedIndex = ClampSelection(screen, selectedIndex);
        state.Html = null;
    }

    public void Push(CCSPlayerController player, WasdMenuScreen screen, int selectedIndex = 0)
    {
        if (!_states.TryGetValue(player.Slot, out WasdMenuState? state))
        {
            return;
        }

        state.History.Push((state.Screen, state.SelectedIndex));
        state.Screen = screen;
        state.SelectedIndex = ClampSelection(screen, selectedIndex);
        state.Html = null;
    }

    public void Close(CCSPlayerController player)
    {
        if (!_states.Remove(player.Slot, out WasdMenuState? state))
        {
            return;
        }

        Release(state);
        if (player.IsValid)
        {
            player.PrintToCenterHtml(" ");
        }
    }

    public void CloseAll()
    {
        try
        {
            foreach ((int slot, WasdMenuState state) in _states)
            {
                Release(state);
                CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
                if (player is { IsValid: true })
                {
                    player.PrintToCenterHtml(" ");
                }
            }
        }
        catch (NativeException)
        {
            // The entity system is already gone during shutdown.
        }

        _states.Clear();
    }

    public void Forget(int slot)
    {
        if (_states.Remove(slot, out WasdMenuState? state))
        {
            Release(state);
        }
    }

    /// <summary>
    /// Reads the keys of every open menu and re-sends it: center-HTML text fades on its own. Polling
    /// only the players with a menu open costs nothing the rest of the time, unlike a listener that
    /// fires for every button of every player.
    /// </summary>
    public void OnTick()
    {
        if (_states.Count == 0)
        {
            return;
        }

        _slots.Clear();
        _slots.AddRange(_states.Keys);
        foreach (int slot in _slots)
        {
            CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || !_states.TryGetValue(slot, out WasdMenuState? state))
            {
                Forget(slot);
                continue;
            }

            try
            {
                PollButtons(player, state);
                if (_states.TryGetValue(slot, out state))
                {
                    HoldInPlace(player, state);
                    state.Html ??= Render(player, state);
                    player.PrintToCenterHtml(state.Html);
                }
            }
            catch (Exception)
            {
                // A client that is going away must not stop the other menus from drawing.
                Forget(slot);
            }
        }
    }

    /// <summary>
    /// Keeps the pawn the player walks with still, so the movement keys only drive the menu. Checked
    /// every tick: a pawn that respawns or gets a new move type while the menu is open is frozen again.
    /// </summary>
    private void HoldInPlace(CCSPlayerController player, WasdMenuState state)
    {
        // Dead or spectating players drive the observer pawn, which keeps moving freely.
        CBasePlayerPawn? pawn = _settings.FreezePlayer ? player.Pawn.Value : null;
        if (pawn != null && (!pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE ||
                             pawn.Handle == player.ObserverPawn.Value?.Handle))
        {
            pawn = null;
        }

        uint handle = pawn?.EntityHandle.Raw ?? 0;
        if (handle != state.FrozenPawn)
        {
            Release(state);
        }

        if (pawn != null && pawn.MoveType != MoveType_t.MOVETYPE_OBSOLETE)
        {
            state.FrozenPawn = handle;
            state.FrozenMoveType = pawn.MoveType;
            SetMoveType(pawn, MoveType_t.MOVETYPE_OBSOLETE);
        }
    }

    private static void Release(WasdMenuState state)
    {
        if (state.FrozenPawn == 0)
        {
            return;
        }

        CBasePlayerPawn? pawn = new CHandle<CBasePlayerPawn>(state.FrozenPawn).Value;
        state.FrozenPawn = 0;

        // A respawn or another plugin already set a new move type; that one stays.
        if (pawn != null && pawn.IsValid && pawn.MoveType == MoveType_t.MOVETYPE_OBSOLETE)
        {
            SetMoveType(pawn, state.FrozenMoveType);
        }
    }

    private static void SetMoveType(CBasePlayerPawn pawn, MoveType_t moveType)
    {
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    private void PollButtons(CCSPlayerController player, WasdMenuState state)
    {
        // Keys land on the pawn the player drives: its own while alive, the observer pawn while dead or
        // spectating, a bot's pawn during a takeover.
        CBasePlayerPawn? source = player.Pawn.Value;
        CCSPlayerPawn? own = player.PlayerPawn.Value;
        bool drivesDeadOwnPawn = own != null && source?.Handle == own.Handle && own.LifeState != (byte)LifeState_t.LIFE_ALIVE;
        if (source == null || !source.IsValid || drivesDeadOwnPawn)
        {
            source = player.ObserverPawn.Value;
        }

        CPlayer_MovementServices? movement = source?.MovementServices;
        ulong buttons = movement == null ? 0 : movement.Buttons.ButtonStates[0];
        ulong pressed = buttons & ~state.Buttons;
        state.Buttons = buttons;

        if (pressed != 0)
        {
            HandlePressed(player, state, (PlayerButtons)pressed);
        }
    }

    private void HandlePressed(CCSPlayerController player, WasdMenuState state, PlayerButtons pressed)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if ((now - state.OpenedAtUtc).TotalMilliseconds < _settings.InitialInputDelayMilliseconds ||
            (now - state.LastInputUtc).TotalMilliseconds < _settings.InputDebounceMilliseconds)
        {
            return;
        }

        if ((pressed & _scrollUp) != 0)
        {
            MoveSelection(state, -1);
        }
        else if ((pressed & _scrollDown) != 0)
        {
            MoveSelection(state, 1);
        }
        else if ((pressed & _select) != 0)
        {
            state.Screen.Items[state.SelectedIndex].OnSelect(player);
        }
        else if ((pressed & _back) != 0)
        {
            NavigateBack(player, state);
        }
        else if ((pressed & _cancel) != 0)
        {
            Close(player);
        }
        else
        {
            return;
        }

        state.LastInputUtc = now;
    }

    private static void MoveSelection(WasdMenuState state, int delta)
    {
        int count = state.Screen.Items.Count;
        state.SelectedIndex = (state.SelectedIndex + delta + count) % count;
        state.Html = null;
    }

    private void NavigateBack(CCSPlayerController player, WasdMenuState state)
    {
        if (state.History.Count == 0)
        {
            Close(player);
            return;
        }

        (state.Screen, state.SelectedIndex) = state.History.Pop();
        state.Html = null;
    }

    private string Render(CCSPlayerController player, WasdMenuState state)
    {
        WasdMenuScreen screen = state.Screen;
        int selected = state.SelectedIndex;
        int start = Math.Clamp(selected - (_settings.VisibleItems / 2), 0, Math.Max(0, screen.Items.Count - _settings.VisibleItems));
        int end = Math.Min(screen.Items.Count, start + _settings.VisibleItems);

        StringBuilder html = new();
        html.Append($"<b><font color='#8bdcff'>{Escape(Trim(screen.Title, MaxTitleLength))}</font></b> ");
        html.Append($"<font color='#d7f08a'>{selected + 1}</font>/<font color='#e2e2e2'>{screen.Items.Count}</font><br>");

        for (int index = start; index < end; index++)
        {
            WasdMenuItem item = screen.Items[index];
            string marker = index == selected ? "▶ " : "• ";
            html.Append($"<font color='{item.Color ?? DefaultColor}'>{marker}{Escape(Trim(item.Label(), MaxItemLabelLength))}</font><br>");
        }

        string controls = _localizer.ForPlayer(player, "menu.controls",
            string.Join("/", _settings.ScrollUpKeys), string.Join("/", _settings.ScrollDownKeys),
            string.Join("/", _settings.SelectKeys), string.Join("/", _settings.BackKeys), string.Join("/", _settings.CancelKeys));
        string[] parts = controls.Split(" | ", StringSplitOptions.TrimEntries);
        if (parts.Length >= 4)
        {
            html.Append($"<small><small><font color='#f0b65a'>{Escape($"{parts[0]} | {parts[1]}")}</font></small></small><br>");
            html.Append($"<small><small><font color='#f0b65a'>{Escape(string.Join(" | ", parts[2..]))}</font></small></small>");
        }
        else
        {
            html.Append($"<small><small><font color='#f0b65a'>{Escape(controls)}</font></small></small>");
        }

        return html.ToString();
    }

    private static PlayerButtons ToButtons(IEnumerable<string> keys)
    {
        PlayerButtons buttons = 0;
        foreach (string key in keys)
        {
            buttons |= ButtonMap[key];
        }

        return buttons;
    }

    /// <summary>
    /// Only the markup characters: WebUtility.HtmlEncode also turns accented letters into numeric
    /// entities, which the game's HTML label is not guaranteed to decode.
    /// </summary>
    private static string Escape(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string Trim(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : $"{text[..(maxLength - 3)]}...";
    }

    private static int ClampSelection(WasdMenuScreen screen, int selectedIndex)
    {
        return Math.Clamp(selectedIndex, 0, screen.Items.Count - 1);
    }
}
