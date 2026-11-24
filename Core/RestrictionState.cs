using CounterStrikeSharp.API.Modules.Utils;

namespace UmbrellaWeaponRestrict.Core;

public enum RestrictTeam
{
    Both,
    Terrorist,
    CounterTerrorist
}

public readonly record struct TeamLimit(int T, int CT)
{
    public static TeamLimit Unrestricted => new(RestrictionState.Unlimited, RestrictionState.Unlimited);
}

/// <summary>
/// Runtime limits. The base limits come from the config and from admin commands; an active per-player
/// rule replaces the base limit of both teams for its weapon.
/// </summary>
public sealed class RestrictionState
{
    public const int Unlimited = -1;
    public const int MaxLimit = 64;

    private readonly int[,] _baseLimits = new int[WeaponCatalog.All.Count, 2];
    private readonly int?[] _perPlayerLimits = new int?[WeaponCatalog.All.Count];

    /// <summary>Goes up whenever any limit changes, so views of the limits know when to redraw.</summary>
    public long Version { get; private set; }

    public static int Clamp(int amount) => Math.Clamp(amount, Unlimited, MaxLimit);

    public static int Clamp(int amount, string name, ICollection<string> warnings)
    {
        int clamped = Clamp(amount);
        if (clamped != amount)
        {
            warnings.Add($"{name}: {amount} is out of range ({Unlimited} to {MaxLimit}), using {clamped}.");
        }

        return clamped;
    }

    public void ResetBase(IReadOnlyList<TeamLimit> limits)
    {
        for (int index = 0; index < limits.Count; index++)
        {
            _baseLimits[index, 0] = limits[index].T;
            _baseLimits[index, 1] = limits[index].CT;
        }

        Version++;
    }

    public void SetBase(WeaponDefinition weapon, RestrictTeam team, int amount)
    {
        if (team != RestrictTeam.CounterTerrorist)
        {
            _baseLimits[weapon.Index, 0] = amount;
        }

        if (team != RestrictTeam.Terrorist)
        {
            _baseLimits[weapon.Index, 1] = amount;
        }

        Version++;
    }

    public void SetPerPlayer(WeaponDefinition weapon, int? limit)
    {
        if (_perPlayerLimits[weapon.Index] != limit)
        {
            _perPlayerLimits[weapon.Index] = limit;
            Version++;
        }
    }

    public void ClearPerPlayer()
    {
        if (_perPlayerLimits.Any(limit => limit.HasValue))
        {
            Array.Clear(_perPlayerLimits);
            Version++;
        }
    }

    public int Get(WeaponDefinition weapon, CsTeam team)
    {
        int teamIndex = team switch
        {
            CsTeam.Terrorist => 0,
            CsTeam.CounterTerrorist => 1,
            _ => -1
        };

        if (teamIndex < 0)
        {
            return Unlimited;
        }

        return _perPlayerLimits[weapon.Index] ?? _baseLimits[weapon.Index, teamIndex];
    }

    public bool IsRestrictedForAnyTeam(WeaponDefinition weapon)
    {
        return Get(weapon, CsTeam.Terrorist) >= 0 || Get(weapon, CsTeam.CounterTerrorist) >= 0;
    }

    public bool HasPerPlayerLimit(WeaponDefinition weapon) => _perPlayerLimits[weapon.Index].HasValue;
}
