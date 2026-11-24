using System.Text.RegularExpressions;

namespace UmbrellaWeaponRestrict.Core;

/// <summary>
/// Limits for single maps or map name patterns, applied over Restrictions whenever a map starts:
/// <code>"awp_*": { "awp": { "T": -1, "CT": -1 } }</code>
/// frees the AWP on every map whose name starts with "awp_". Every matching entry applies, and for a
/// weapon that two entries set, the more specific name wins ("de_dust2" over "de_*").
/// </summary>
public sealed class MapRules
{
    /// <summary>A Maps value; a team the entry leaves out keeps the limit it already has.</summary>
    public readonly record struct MapLimit(int? T, int? CT);

    private readonly Entry[] _entries;

    private MapRules(Entry[] entries)
    {
        _entries = entries;
    }

    public static MapRules Empty { get; } = new([]);

    public bool IsEmpty => _entries.Length == 0;

    public static MapRules Parse(Dictionary<string, Dictionary<string, MapLimit>> maps, ICollection<string> warnings)
    {
        List<Entry> entries = [];
        foreach ((string key, Dictionary<string, MapLimit>? limits) in maps)
        {
            string name = key.Trim();
            if (name.Length == 0)
            {
                warnings.Add("Maps: an entry has no map name, ignored.");
                continue;
            }

            // Groups go first so that a weapon listed on its own wins over its group.
            List<(int Index, MapLimit Limit)> groupLimits = [];
            List<(int Index, MapLimit Limit)> weaponLimits = [];
            foreach ((string target, MapLimit limit) in limits ?? [])
            {
                WeaponDefinition? weapon = WeaponCatalog.Find(target);
                WeaponGroup group = WeaponGroup.None;
                if (weapon == null && !WeaponCatalog.TryParseGroup(target, out group))
                {
                    warnings.Add($"Maps.{name}: '{target}' is not a managed weapon or group, ignored.");
                    continue;
                }

                string prefix = $"Maps.{name}.{target}";
                if (limit.T == null && limit.CT == null)
                {
                    warnings.Add($"{prefix}: no T or CT limit, ignored.");
                    continue;
                }

                MapLimit clamped = new(Clamp(limit.T, $"{prefix}.T"), Clamp(limit.CT, $"{prefix}.CT"));
                if (weapon != null)
                {
                    weaponLimits.Add((weapon.Index, clamped));
                }
                else
                {
                    groupLimits.AddRange(WeaponCatalog.InGroup(group).Select(member => (member.Index, clamped)));
                }
            }

            if (groupLimits.Count == 0 && weaponLimits.Count == 0)
            {
                warnings.Add($"Maps.{name}: no limits, entry ignored.");
                continue;
            }

            entries.Add(new Entry(name, ToRegex(name), name.Count(character => character != '*'), [.. groupLimits, .. weaponLimits]));
        }

        // Least specific first so that more specific names overwrite them; OrderBy is stable, so
        // equally specific names keep the file order.
        return new MapRules(entries.OrderBy(entry => entry.Specificity).ToArray());

        int? Clamp(int? amount, string field)
        {
            return amount is int value ? RestrictionState.Clamp(value, field, warnings) : null;
        }
    }

    /// <summary>Names of the entries that match the map, least specific first.</summary>
    public IEnumerable<string> Matching(string? mapName)
    {
        return string.IsNullOrEmpty(mapName)
            ? []
            : _entries.Where(entry => entry.Pattern.IsMatch(mapName)).Select(entry => entry.Name);
    }

    /// <summary>A copy of <paramref name="limits"/> with the map's entries applied.</summary>
    public TeamLimit[] Apply(TeamLimit[] limits, string? mapName)
    {
        TeamLimit[] result = (TeamLimit[])limits.Clone();
        if (string.IsNullOrEmpty(mapName))
        {
            return result;
        }

        foreach (Entry entry in _entries)
        {
            if (!entry.Pattern.IsMatch(mapName))
            {
                continue;
            }

            foreach ((int index, MapLimit limit) in entry.Limits)
            {
                result[index] = new TeamLimit(limit.T ?? result[index].T, limit.CT ?? result[index].CT);
            }
        }

        return result;
    }

    /// <summary>
    /// The non-backtracking engine matches in linear time, so a pattern full of '*' cannot stall a map
    /// start.
    /// </summary>
    private static Regex ToRegex(string pattern)
    {
        return new Regex($"^{Regex.Escape(pattern).Replace("\\*", ".*")}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private sealed record Entry(string Name, Regex Pattern, int Specificity, (int Index, MapLimit Limit)[] Limits);
}
