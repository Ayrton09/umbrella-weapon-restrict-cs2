using CounterStrikeSharp.API.Core;

namespace UmbrellaWeaponRestrict.Menus;

internal sealed class WasdMenuItem
{
    /// <summary>Read each time the menu is drawn, so labels that show limits stay current.</summary>
    public required Func<string> Label { get; init; }
    public required Action<CCSPlayerController> OnSelect { get; init; }

    /// <summary>HTML color of the row, also while selected; white when null.</summary>
    public string? Color { get; init; }
}

internal sealed class WasdMenuScreen
{
    public required string Title { get; init; }
    public required IReadOnlyList<WasdMenuItem> Items { get; init; }
}

internal sealed class WasdMenuState
{
    public required WasdMenuScreen Screen { get; set; }
    public Stack<(WasdMenuScreen Screen, int SelectedIndex)> History { get; } = new();
    public int SelectedIndex { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; }
    public DateTimeOffset LastInputUtc { get; set; }

    /// <summary>Rendered overlay, rebuilt only after the menu changes.</summary>
    public string? Html { get; set; }

    /// <summary>Buttons held on the last tick, to act only on new presses.</summary>
    public ulong Buttons { get; set; }

    /// <summary>Raw handle of the pawn held in place while the menu is open; 0 when none.</summary>
    public uint FrozenPawn { get; set; }

    /// <summary>Move type the frozen pawn gets back when the menu closes.</summary>
    public MoveType_t FrozenMoveType { get; set; }
}
