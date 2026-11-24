using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using UmbrellaWeaponRestrict.Config;
using UmbrellaWeaponRestrict.Core;
using UmbrellaWeaponRestrict.Menus;

namespace UmbrellaWeaponRestrict;

[MinimumApiVersion(376)]
public sealed class UmbrellaWeaponRestrictPlugin : BasePlugin, IPluginConfig<UmbrellaWeaponRestrictConfig>
{
    private const string ConsolePrefix = "[UWR]";
    private const float NotifyDelaySeconds = 1.5f;
    private const float PickupRecheckSeconds = 0.25f;
    private const float JustCreatedSeconds = 0.05f;
    private const double ChatLimitTimeoutSeconds = 30;
    private const int MaxSlots = 64;
    private const int MaxListLineLength = 90;
    private const int MaxEntityWalk = 32768;

    public override string ModuleName => "Umbrella Weapon Restrict";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "Ayrton09";
    public override string ModuleDescription => "Weapon restrictions by team, weapon group and player count";

    public UmbrellaWeaponRestrictConfig Config { get; set; } = new();

    private readonly RestrictionState _limits = new();
    private readonly float[] _nextNotify = new float[MaxSlots];
    private readonly nint[] _lastPickupNotify = new nint[MaxSlots];
    private readonly Dictionary<nint, int> _slotByItemServices = new();
    private readonly Dictionary<nint, (nint Item, float Until)> _refusedPickups = new();
    private readonly Dictionary<int, PendingChatLimit> _pendingChatLimits = new();
    private readonly bool[] _immunityHeld = new bool[WeaponCatalog.All.Count];
    private TeamLimit[] _configLimits = CreateUnrestrictedLimits();
    private PerPlayerRules _perPlayerRules = PerPlayerRules.Empty;
    private WeaponImmunityRules _weaponImmunity = WeaponImmunityRules.Empty;
    private MapRules _mapRules = MapRules.Empty;
    private WasdMenuService _menus = null!;
    private bool _canAcquireHooked;
    private bool _enforceQueued;
    private bool _mapStarted;
    private int _lastPerPlayerCount = -1;
    private DateTime _lastHookErrorUtc = DateTime.MinValue;
    private int _itemOffsetInWeapon = -1;
    private int _identityNextOffset = -1;
    private long _menuLimitsVersion = -1;

    public void OnConfigParsed(UmbrellaWeaponRestrictConfig config)
    {
        List<string> warnings = [];

        config.Restrictions ??= new Dictionary<string, UmbrellaWeaponRestrictConfig.TeamLimitSettings>();
        config.WeaponImmunity ??= new Dictionary<string, string>();
        config.Maps ??= new Dictionary<string, Dictionary<string, UmbrellaWeaponRestrictConfig.TeamLimitSettings>>();
        config.PerPlayer ??= new UmbrellaWeaponRestrictConfig.PerPlayerSettings();
        config.PerPlayer.Rules ??= new Dictionary<string, Dictionary<string, int>>();
        config.Menu ??= new UmbrellaWeaponRestrictConfig.MenuSettings();

        config.AdminPermission = NormalizePermission(config.AdminPermission, "@css/cvar", "AdminPermission", warnings);
        config.ImmunityPermission = NormalizePermission(config.ImmunityPermission, "@css/root", "ImmunityPermission", warnings);
        config.RestrictedSound = NormalizeSoundPath(config.RestrictedSound, warnings);
        NormalizeMenu(config.Menu, warnings);

        TeamLimit[] limits = CreateUnrestrictedLimits();
        HashSet<int> seen = [];
        foreach ((string key, UmbrellaWeaponRestrictConfig.TeamLimitSettings? value) in config.Restrictions)
        {
            WeaponDefinition? weapon = WeaponCatalog.Find(key);
            if (weapon == null)
            {
                warnings.Add($"Restrictions: '{key}' is not a managed weapon, entry ignored.");
            }
            else if (value != null && !seen.Add(weapon.Index))
            {
                warnings.Add($"Restrictions: '{key}' repeats the entry for {weapon.Key}, keeping the first one.");
            }
            else if (value != null)
            {
                limits[weapon.Index] = new TeamLimit(
                    RestrictionState.Clamp(value.T ?? RestrictionState.Unlimited, $"Restrictions.{key}.T", warnings),
                    RestrictionState.Clamp(value.CT ?? RestrictionState.Unlimited, $"Restrictions.{key}.CT", warnings));
            }
        }

        MapRules mapRules = MapRules.Parse(ReadMapLimits(config.Maps), warnings);
        WeaponImmunityRules weaponImmunity = WeaponImmunityRules.Parse(config.WeaponImmunity, warnings);
        PerPlayerRules perPlayerRules = PerPlayerRules.Parse(config.PerPlayer.Rules, warnings);
        if (config.PerPlayer.Enabled && perPlayerRules.IsEmpty)
        {
            warnings.Add("PerPlayer.Enabled is true but PerPlayer.Rules has no valid rule.");
        }

        Config = config;
        _configLimits = limits;
        _mapRules = mapRules;
        _weaponImmunity = weaponImmunity;
        _perPlayerRules = perPlayerRules;

        foreach (string warning in warnings)
        {
            Logger.LogWarning("{Warning}", warning);
        }
    }

    public override void Load(bool hotReload)
    {
        _menus = new WasdMenuService(Config.Menu, Localizer);
        _limits.ResetBase(_configLimits);

        AddCommand("css_restrict", "Restrict a weapon, weapon group or all weapons.", OnRestrictCommand);
        AddCommand("css_unrestrict", "Remove the restriction of a weapon, weapon group or all weapons.", OnUnrestrictCommand);
        AddCommand("css_restrictmenu", "Open the weapon restriction menu.", OnMenuCommand);
        AddCommand("css_wrmenu", "Open the weapon restriction menu.", OnMenuCommand);
        AddCommand("css_remove_restricted", "Remove weapons that exceed the current limits.", OnRemoveRestrictedCommand);
        AddCommand("css_wr_list", "List the active weapon restrictions.", OnListCommand);
        AddCommand("css_restrictions", "Show the weapon limits of your team.", OnRestrictionsCommand);
        AddCommand("css_wr_reload", "Reload the weapon restriction config.", OnReloadCommand);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnClientDisconnectPost>(OnClientDisconnectPost);
        RegisterListener<Listeners.OnTick>(OnTick);

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventBotTakeover>(OnBotTakeover);

        // CounterStrikeSharp keys listeners by delegate: sharing one would leave "say" registered
        // after an unload.
        AddCommandListener("say", (player, info) => OnSay(player, info), HookMode.Pre);
        AddCommandListener("say_team", (player, info) => OnSay(player, info), HookMode.Pre);

        HookCanAcquire();

        // Loaded mid-map (css_plugins load or a hot reload): OnMapStart will not run for this map.
        Server.NextFrame(() =>
        {
            if (!_mapStarted && !string.IsNullOrEmpty(Server.MapName))
            {
                ResetToConfig(Server.MapName);
                RefreshPerPlayer(force: true);
                QueueEnforce();
            }
        });
    }

    public override void Unload(bool hotReload)
    {
        if (_canAcquireHooked)
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnCanAcquire, HookMode.Pre);
            _canAcquireHooked = false;
        }

        _menus.CloseAll();
    }

    private void HookCanAcquire()
    {
        try
        {
            VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnCanAcquire, HookMode.Pre);
            _canAcquireHooked = true;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not hook CCSPlayer_ItemServices::CanAcquire. Buys and pickups will not be blocked; limits are still enforced when they change and at round start.");
        }
    }

    private void OnMapStart(string mapName)
    {
        // A queued enforcement may not survive the map change; it must not keep blocking new ones.
        _enforceQueued = false;
        _mapStarted = true;
        _slotByItemServices.Clear();
        _refusedPickups.Clear();
        _pendingChatLimits.Clear();
        Array.Clear(_nextNotify);
        Array.Clear(_lastPickupNotify);

        // Every map starts from the config file; admin changes only last until the map changes.
        ResetToConfig(mapName);
        RefreshPerPlayer(force: true);
    }

    /// <summary>Restrictions from the config file, with the Maps entries for this map on top.</summary>
    private void ResetToConfig(string mapName)
    {
        _limits.ResetBase(_mapRules.Apply(_configLimits, mapName));

        // Names the map as the server reports it, which is what Maps entries are matched against.
        if (!_mapRules.IsEmpty)
        {
            string[] entries = _mapRules.Matching(mapName).ToArray();
            Logger.LogInformation("Maps entries for {Map}: {Entries}.", mapName, entries.Length > 0 ? string.Join(", ", entries) : "none");
        }
    }

    private void OnMapEnd()
    {
        _menus.CloseAll();
    }

    private void OnClientPutInServer(int slot)
    {
        ResetSlot(slot);
        Server.NextFrame(() => RefreshPerPlayer());
    }

    private void OnClientDisconnect(int slot)
    {
        ResetSlot(slot);
        _menus.Forget(slot);
    }

    private void OnClientDisconnectPost(int slot)
    {
        Server.NextFrame(() => RefreshPerPlayer());
    }

    private void OnTick()
    {
        // Limits changed by another admin, a reload or the player count show up in open menus too.
        if (_limits.Version != _menuLimitsVersion)
        {
            _menuLimitsVersion = _limits.Version;
            _menus.Invalidate();
        }

        _menus.OnTick();
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Array.Clear(_lastPickupNotify);
        RefreshPerPlayer();
        QueueEnforce();
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        // The event fires before the new team is applied, so count on the next frame.
        Server.NextFrame(() => RefreshPerPlayer());
        return HookResult.Continue;
    }

    private HookResult OnBotTakeover(EventBotTakeover @event, GameEventInfo info)
    {
        // Keys stop reaching a menu that stays open across the pawn swap; one opened on the bot works.
        if (@event.Userid is { IsValid: true } player)
        {
            _menus.Close(player);
        }

        return HookResult.Continue;
    }

    private void ResetSlot(int slot)
    {
        if (slot is < 0 or >= MaxSlots)
        {
            return;
        }

        _nextNotify[slot] = 0f;
        _lastPickupNotify[slot] = 0;
        _pendingChatLimits.Remove(slot);
        foreach (nint handle in _slotByItemServices.Where(entry => entry.Value == slot).Select(entry => entry.Key).ToList())
        {
            _slotByItemServices.Remove(handle);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Buy and pickup blocking
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// CanAcquire runs before a buy charges any money and before a ground weapon is picked up, so
    /// refusing here blocks both without refunds or drops.
    /// </summary>
    private HookResult OnCanAcquire(DynamicHook hook)
    {
        if (!Config.Enabled)
        {
            return HookResult.Continue;
        }

        try
        {
            // Raw pointers: the typed GetParam overload builds its wrappers through reflection.
            nint itemPointer = hook.GetParam<nint>(1);
            if (itemPointer == 0)
            {
                return HookResult.Continue;
            }

            WeaponDefinition? weapon = WeaponCatalog.FindByDefinitionIndex(new CEconItemView(itemPointer).ItemDefinitionIndex);
            if (weapon == null || !_limits.IsRestrictedForAnyTeam(weapon))
            {
                return HookResult.Continue;
            }

            AcquireMethod method = (AcquireMethod)hook.GetParam<int>(2);
            bool pickup = method == AcquireMethod.PickUp;
            if (pickup && Config.AllowRestrictedPickup)
            {
                return HookResult.Continue;
            }

            // The game asks again every tick while a player stands on a ground weapon; reuse the
            // last refusal for a moment instead of recounting the whole team each time.
            nint services = hook.GetParam<nint>(0);
            if (pickup &&
                _refusedPickups.TryGetValue(services, out (nint Item, float Until) refused) &&
                refused.Item == itemPointer &&
                Server.CurrentTime < refused.Until)
            {
                hook.SetReturn(AcquireResult.NotAllowedByProhibition);
                return HookResult.Handled;
            }

            (CCSPlayerController? player, CCSPlayerPawn? pawn) = FindItemServicesOwner(services);
            if (player == null || pawn == null || !CanEvaluate(player) || IsImmune(player, weapon))
            {
                return HookResult.Continue;
            }

            CsTeam team = player.Team;
            if (IsAllowed(pawn, team, weapon, method))
            {
                return HookResult.Continue;
            }

            if (pickup)
            {
                _refusedPickups[services] = (itemPointer, Server.CurrentTime + PickupRecheckSeconds);
                RemoveIfJustCreated(itemPointer);
            }

            OnRefused(player, weapon, _limits.Get(weapon, team), method, pickup ? itemPointer : 0);
            hook.SetReturn(AcquireResult.NotAllowedByProhibition);
            return HookResult.Handled;
        }
        catch (Exception exception)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastHookErrorUtc > TimeSpan.FromSeconds(30))
            {
                _lastHookErrorUtc = now;
                Logger.LogError(exception, "CanAcquire check failed; the item was allowed.");
            }

            return HookResult.Continue;
        }
    }

    /// <summary>
    /// Spawn loadouts and weapons handed out by other plugins are created right before this check, and
    /// the game drops a refused one at the player's feet. Those are deleted instead of piling up at the
    /// spawns; a weapon that was already on the ground stays where it is.
    /// </summary>
    private void RemoveIfJustCreated(nint itemPointer)
    {
        // A pickup's CEconItemView lives inside the weapon entity.
        if (_itemOffsetInWeapon < 0)
        {
            _itemOffsetInWeapon = Schema.GetSchemaOffset("CEconEntity", "m_AttributeManager") +
                                  Schema.GetSchemaOffset("CAttributeContainer", "m_Item");
        }

        // Nothing is read at the computed address until the entity list confirms a weapon lives there.
        nint address = itemPointer - _itemOffsetInWeapon;
        nint identity = FindEntityIdentity(address);
        if (identity == 0 || new CEntityIdentity(identity).DesignerName?.StartsWith("weapon_", StringComparison.Ordinal) != true)
        {
            return;
        }

        CBasePlayerWeapon weapon = new(address);
        if (Server.CurrentTime - weapon.CreateTime > JustCreatedSeconds)
        {
            return;
        }

        // The game is still inside the give; delete the weapon once it is done with it.
        CHandle<CBasePlayerWeapon> handle = new(weapon.EntityHandle.Raw);
        Server.NextFrame(() =>
        {
            CBasePlayerWeapon? dropped = handle.Value;
            if (dropped != null && dropped.IsValid && !dropped.OwnerEntity.IsValid)
            {
                dropped.Remove();
            }
        });
    }

    /// <summary>
    /// Walks the active entity list for the identity of the entity at <paramref name="entity"/>; 0 when
    /// no live entity is there. Only the list's own pointers are read.
    /// </summary>
    private nint FindEntityIdentity(nint entity)
    {
        if (_identityNextOffset < 0)
        {
            _identityNextOffset = Schema.GetSchemaOffset("CEntityIdentity", "m_pNext");
        }

        nint identity = EntitySystem.FirstActiveEntity;
        for (int step = 0; identity != 0 && step < MaxEntityWalk; step++)
        {
            // m_pInstance is the identity's first field.
            if (Marshal.ReadIntPtr(identity) == entity)
            {
                return identity;
            }

            identity = Marshal.ReadIntPtr(identity, _identityNextOffset);
        }

        return 0;
    }

    /// <summary>
    /// Finds the pawn that owns these item services and the player driving it. During a bot takeover
    /// the human drives the bot's pawn (m_hPawn) while the bot controller still owns it
    /// (m_hPlayerPawn); the driver is who gets the notice and whose immunity counts.
    /// </summary>
    private (CCSPlayerController? Player, CCSPlayerPawn? Pawn) FindItemServicesOwner(nint services)
    {
        if (services == 0)
        {
            return (null, null);
        }

        if (_slotByItemServices.TryGetValue(services, out int cachedSlot))
        {
            CCSPlayerController? cached = Utilities.GetPlayerFromSlot(cachedSlot);
            CCSPlayerPawn? cachedPawn = GetDrivenPawn(cached, services);
            if (cachedPawn != null)
            {
                return (cached, cachedPawn);
            }

            _slotByItemServices.Remove(services);
        }

        (CCSPlayerController? Player, CCSPlayerPawn? Pawn) owner = (null, null);
        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            CCSPlayerPawn? driven = GetDrivenPawn(player, services);
            if (driven != null)
            {
                _slotByItemServices[services] = player.Slot;
                return (player, driven);
            }

            // Nobody drives a pawn that is still spawning, so fall back to the controller that owns it.
            CCSPlayerPawn? own = player.PlayerPawn.Value;
            if (owner.Player == null && own != null && own.IsValid && own.ItemServices?.Handle == services)
            {
                owner = (player, own);
            }
        }

        return owner;
    }

    private static CCSPlayerPawn? GetDrivenPawn(CCSPlayerController? player, nint services)
    {
        CBasePlayerPawn? driven = player is { IsValid: true } ? player.Pawn.Value : null;
        return driven != null && driven.IsValid && driven.ItemServices?.Handle == services
            ? new CCSPlayerPawn(driven.Handle)
            : null;
    }

    private static bool CanEvaluate(CCSPlayerController player)
    {
        return player.IsValid &&
               player.Connected == PlayerConnectedState.Connected &&
               player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;
    }

    /// <summary>AdminImmunity, which covers every weapon.</summary>
    private bool IsImmune(CCSPlayerController player)
    {
        return Config.AdminImmunity &&
               !player.IsBot &&
               HasPermission(player, Config.ImmunityPermission);
    }

    /// <summary>
    /// AdminImmunity, or a WeaponImmunity entry that covers this weapon, unless a per-player rule holds
    /// immunity back for it.
    /// </summary>
    private bool IsImmune(CCSPlayerController player, WeaponDefinition weapon)
    {
        return !_immunityHeld[weapon.Index] && (IsImmune(player) || HasWeaponImmunity(player, weapon));
    }

    private bool HasWeaponImmunity(CCSPlayerController player, WeaponDefinition weapon)
    {
        if (player.IsBot)
        {
            return false;
        }

        foreach (string permission in _weaponImmunity.Get(weapon))
        {
            if (HasPermission(player, permission))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Accepts a flag ("@css/cvar") or an admin group ("#css/admin"). CounterStrikeSharp checks each
    /// kind with its own call; the flag check alone would let any admin through for a group.
    /// </summary>
    private static bool HasPermission(CCSPlayerController? player, string permission)
    {
        return permission.StartsWith('#')
            ? AdminManager.PlayerInGroup(player, permission)
            : AdminManager.PlayerHasPermissions(player, permission);
    }

    private bool IsAllowed(CCSPlayerPawn pawn, CsTeam team, WeaponDefinition weapon, AcquireMethod method)
    {
        int limit = _limits.Get(weapon, team);
        if (limit < 0)
        {
            return true;
        }

        if (limit == 0)
        {
            return false;
        }

        // Swapping a weapon the player already owns does not change the team total.
        if (!weapon.IsStackable && method is AcquireMethod.Buy or AcquireMethod.PickUp && WeaponInventory.Count(pawn, weapon) > 0)
        {
            return true;
        }

        return CountTeamUnits(team, weapon) < limit;
    }

    private int CountTeamUnits(CsTeam team, WeaponDefinition weapon)
    {
        int count = 0;
        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (player.Team != team)
            {
                continue;
            }

            CCSPlayerPawn? pawn = WeaponInventory.GetAlivePawn(player);
            if (pawn != null && !IsImmune(GetDriver(player, pawn), weapon))
            {
                count += WeaponInventory.Count(pawn, weapon);
            }
        }

        return count;
    }

    // ---------------------------------------------------------------------------------------------
    // Enforcement
    // ---------------------------------------------------------------------------------------------

    private void QueueEnforce()
    {
        if (_enforceQueued)
        {
            return;
        }

        _enforceQueued = true;
        Server.NextFrame(() =>
        {
            _enforceQueued = false;
            EnforceRestrictions();
        });
    }

    /// <summary>
    /// Removes owned items over the current limits. Players are walked in slot order and the first
    /// ones keep their items, same as the SourceMod version.
    /// </summary>
    private void EnforceRestrictions()
    {
        if (!Config.Enabled)
        {
            return;
        }

        int[] tBudget = BuildBudget(CsTeam.Terrorist);
        int[] ctBudget = BuildBudget(CsTeam.CounterTerrorist);
        if (tBudget.All(limit => limit < 0) && ctBudget.All(limit => limit < 0))
        {
            return;
        }

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (!CanEvaluate(player))
            {
                continue;
            }

            CCSPlayerPawn? pawn = WeaponInventory.GetAlivePawn(player);
            if (pawn == null)
            {
                continue;
            }

            CCSPlayerController driver = GetDriver(player, pawn);
            try
            {
                WeaponInventory.Enforce(pawn, player.Team == CsTeam.Terrorist ? tBudget : ctBudget,
                    weapon => IsImmune(driver, weapon));
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Could not enforce the limits on {Player}.", player.PlayerName);
            }
        }
    }

    /// <summary>
    /// During a bot takeover the human drives the bot's pawn, so the human's immunity applies to what it
    /// carries, the same as for buys and pickups.
    /// </summary>
    private static CCSPlayerController GetDriver(CCSPlayerController owner, CCSPlayerPawn pawn)
    {
        // A bot's m_hPawn only leaves its own pawn while a human drives it.
        if (!owner.IsBot || owner.Pawn.Value?.Handle == pawn.Handle)
        {
            return owner;
        }

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (!player.IsBot && player.Pawn.Value?.Handle == pawn.Handle)
            {
                return player;
            }
        }

        return owner;
    }

    private int[] BuildBudget(CsTeam team)
    {
        int[] budget = new int[WeaponCatalog.All.Count];
        foreach (WeaponDefinition weapon in WeaponCatalog.All)
        {
            budget[weapon.Index] = _limits.Get(weapon, team);
        }

        return budget;
    }

    // ---------------------------------------------------------------------------------------------
    // Per-player limits
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A stricter per-player limit blocks new buys and pickups at once but only takes weapons away at
    /// the next round start, so nobody loses a weapon mid-round because someone left.
    /// </summary>
    private void RefreshPerPlayer(bool force = false)
    {
        if (!Config.PerPlayer.Enabled || _perPlayerRules.IsEmpty)
        {
            _limits.ClearPerPlayer();
            Array.Clear(_immunityHeld);
            _lastPerPlayerCount = -1;
            return;
        }

        int count = CountPlayersForRules();
        if (!force && count == _lastPerPlayerCount)
        {
            return;
        }

        _lastPerPlayerCount = count;
        foreach (WeaponDefinition weapon in WeaponCatalog.All)
        {
            _limits.SetPerPlayer(weapon, _perPlayerRules.GetLimit(weapon, count));
            _immunityHeld[weapon.Index] = _perPlayerRules.HoldsImmunity(weapon, count);
        }

        if (Config.Debug)
        {
            Logger.LogInformation("Per-player limits refreshed for {Count} player(s).", count);
        }
    }

    private int CountPlayersForRules()
    {
        int count = 0;
        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            if (player.IsHLTV ||
                (!Config.PerPlayer.CountBots && player.IsBot) ||
                (!Config.PerPlayer.CountSpectators && player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    // ---------------------------------------------------------------------------------------------
    // Notifications
    // ---------------------------------------------------------------------------------------------

    private void OnRefused(CCSPlayerController player, WeaponDefinition weapon, int limit, AcquireMethod method, nint pickupKey)
    {
        int slot = player.Slot;
        if (slot is < 0 or >= MaxSlots)
        {
            return;
        }

        if (pickupKey != 0)
        {
            // Standing on a weapon asks again every tick; report it once per weapon.
            if (_lastPickupNotify[slot] == pickupKey)
            {
                return;
            }

            _lastPickupNotify[slot] = pickupKey;
        }
        else
        {
            float now = Server.CurrentTime;
            if (now < _nextNotify[slot])
            {
                return;
            }

            _nextNotify[slot] = now + NotifyDelaySeconds;
        }

        if (Config.Debug)
        {
            Logger.LogInformation("Refused {Method} of {Weapon} for {Player} ({Team}, limit {Limit}).", method, weapon.Key, player.PlayerName, player.Team, limit);
        }

        if (player.IsBot)
        {
            return;
        }

        if (Config.RestrictedSound.Length > 0)
        {
            player.ExecuteClientCommand($"play {Config.RestrictedSound}");
        }

        if (Config.Notify)
        {
            PrintToChat(player, limit == 0
                ? Localizer.ForPlayer(player, "notify.blocked", weapon.DisplayName)
                : Localizer.ForPlayer(player, "notify.limit", weapon.DisplayName, limit));
        }
    }

    private void PrintToChat(CCSPlayerController player, string message)
    {
        player.PrintToChat($"{Localizer.ForPlayer(player, "chat.prefix")} {message}");
    }

    private void Reply(CCSPlayerController? player, CommandInfo info, string message)
    {
        if (player != null && info.CallingContext == CommandCallingContext.Chat)
        {
            PrintToChat(player, message);
            return;
        }

        info.ReplyToCommand($"{ConsolePrefix} {StripColors(message)}");
    }

    private void ReplyKey(CCSPlayerController? player, CommandInfo info, string key, params object[] args)
    {
        Reply(player, info, Localizer.ForPlayer(player, key, args));
    }

    private static string StripColors(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (character >= ' ')
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    // ---------------------------------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------------------------------

    private bool HasAdminAccess(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || HasPermission(player, Config.AdminPermission))
        {
            return true;
        }

        ReplyKey(player, info, "command.no_permission");
        return false;
    }

    private void OnRestrictCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!HasAdminAccess(player, info))
        {
            return;
        }

        int args = info.ArgCount - 1;
        if (args is < 1 or > 3)
        {
            ReplyKey(player, info, "command.restrict_usage");
            return;
        }

        int amount = 0;
        if (args >= 2 && !TryParseLimit(info.GetArg(2), out amount))
        {
            ReplyKey(player, info, "command.invalid_amount");
            return;
        }

        RestrictTeam team = RestrictTeam.Both;
        if (args == 3 && !TryParseTeam(info.GetArg(3), out team))
        {
            ReplyKey(player, info, "command.invalid_team");
            return;
        }

        ApplyRestrictionCommand(player, info, info.GetArg(1), team, amount);
    }

    private void OnUnrestrictCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!HasAdminAccess(player, info))
        {
            return;
        }

        int args = info.ArgCount - 1;
        if (args is < 1 or > 2)
        {
            ReplyKey(player, info, "command.unrestrict_usage");
            return;
        }

        RestrictTeam team = RestrictTeam.Both;
        if (args == 2 && !TryParseTeam(info.GetArg(2), out team))
        {
            ReplyKey(player, info, "command.invalid_team");
            return;
        }

        ApplyRestrictionCommand(player, info, info.GetArg(1), team, RestrictionState.Unlimited);
    }

    private void ApplyRestrictionCommand(CCSPlayerController? player, CommandInfo info, string target, RestrictTeam team, int amount)
    {
        string teamName = GetTeamName(player, team);
        List<WeaponDefinition> weapons;
        string message;
        Func<CCSPlayerController, string> subjectName;

        if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || target.Equals("@all", StringComparison.OrdinalIgnoreCase))
        {
            weapons = WeaponCatalog.All.ToList();
            message = Localizer.ForPlayer(player, "restrict.all", teamName, amount);
            subjectName = reader => Localizer.ForPlayer(reader, "announce.all_weapons");
        }
        else if (WeaponCatalog.TryParseGroup(target, out WeaponGroup group))
        {
            weapons = WeaponCatalog.InGroup(group).ToList();
            message = Localizer.ForPlayer(player, "restrict.group", GetGroupName(player, group), teamName, amount);
            subjectName = reader => GetGroupName(reader, group);
        }
        else if (WeaponCatalog.Find(target) is { } weapon)
        {
            weapons = [weapon];
            message = Localizer.ForPlayer(player, "restrict.weapon", weapon.DisplayName, teamName, amount);
            subjectName = _ => weapon.DisplayName;
        }
        else
        {
            ReplyKey(player, info, "command.invalid_weapon", target);
            return;
        }

        SetBaseLimits(player, weapons, team, amount, target, subjectName);
        Reply(player, info, message);

        string? overridden = DescribePerPlayerOverride(player, weapons);
        if (overridden != null)
        {
            Reply(player, info, overridden);
        }
    }

    private void SetBaseLimits(CCSPlayerController? player, List<WeaponDefinition> weapons, RestrictTeam team, int amount, string target,
        Func<CCSPlayerController, string> subjectName)
    {
        foreach (WeaponDefinition weapon in weapons)
        {
            _limits.SetBase(weapon, team, amount);
        }

        string admin = player == null ? "Console" : $"{player.PlayerName} ({player.SteamID})";
        Logger.LogInformation("{Admin} set {Target} for {Team} to {Amount}.", admin, target, team, amount);
        AnnounceChange(player, weapons, subjectName, team, amount);
        QueueEnforce();
    }

    /// <summary>
    /// Tells the other players what an in-game admin changed, each in their own language. Changes from
    /// the server console, RCON or .cfg files stay quiet: they usually run on every map start. So does a
    /// change players would not notice: the plugin is off, or per-player rules decide every weapon.
    /// </summary>
    private void AnnounceChange(CCSPlayerController? admin, List<WeaponDefinition> weapons, Func<CCSPlayerController, string> subjectName,
        RestrictTeam team, int amount)
    {
        if (!Config.AnnounceChanges || !Config.Enabled || admin == null || weapons.All(_limits.HasPerPlayerLimit))
        {
            return;
        }

        string key = amount switch
        {
            RestrictionState.Unlimited => "announce.unrestricted",
            0 => "announce.blocked",
            _ => "announce.limited"
        };
        string teamKey = team switch
        {
            RestrictTeam.Terrorist => "team.t",
            RestrictTeam.CounterTerrorist => "team.ct",
            _ => "team.both"
        };

        foreach (CCSPlayerController reader in Utilities.GetPlayers())
        {
            if (!reader.IsBot && reader.Slot != admin.Slot)
            {
                PrintToChat(reader, Localizer.ForPlayer(reader, key, admin.PlayerName, subjectName(reader), Localizer.ForPlayer(reader, teamKey), amount));
            }
        }
    }

    /// <summary>
    /// Admin changes to a weapon with an active per-player rule are stored but the rule keeps deciding,
    /// so say which weapons are affected instead of failing silently.
    /// </summary>
    private string? DescribePerPlayerOverride(CCSPlayerController? player, IEnumerable<WeaponDefinition> weapons)
    {
        string[] names = weapons.Where(_limits.HasPerPlayerLimit).Select(weapon => weapon.DisplayName).ToArray();
        return names.Length == 0 ? null : Localizer.ForPlayer(player, "restrict.perplayer_active", string.Join(", ", names));
    }

    private void OnMenuCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null)
        {
            ReplyKey(player, info, "command.ingame_only");
            return;
        }

        if (HasAdminAccess(player, info))
        {
            _pendingChatLimits.Remove(player.Slot);
            _menus.Open(player, BuildMainMenu(player));
        }
    }

    private void OnRemoveRestrictedCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!HasAdminAccess(player, info))
        {
            return;
        }

        if (!Config.Enabled)
        {
            ReplyKey(player, info, "command.disabled");
            return;
        }

        EnforceRestrictions();
        ReplyKey(player, info, "command.enforced");
    }

    private void OnListCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!HasAdminAccess(player, info))
        {
            return;
        }

        List<WeaponDefinition> restricted = WeaponCatalog.All.Where(_limits.IsRestrictedForAnyTeam).ToList();
        if (restricted.Count == 0)
        {
            ReplyKey(player, info, "list.none");
            return;
        }

        ReplyKey(player, info, "list.title");
        string tName = Localizer.ForPlayer(player, "team.t_short");
        string ctName = Localizer.ForPlayer(player, "team.ct_short");
        string perPlayer = Localizer.ForPlayer(player, "list.perplayer");
        foreach (WeaponDefinition weapon in restricted)
        {
            string line = FormatWeaponLine(weapon, tName, ctName);
            Reply(player, info, _limits.HasPerPlayerLimit(weapon) ? $"{line} ({perPlayer})" : line);
        }
    }

    /// <summary>
    /// Open to everyone: players see their own team's limits, spectators and the console both teams.
    /// </summary>
    private void OnRestrictionsCommand(CCSPlayerController? player, CommandInfo info)
    {
        CsTeam[] teams = player?.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist
            ? [player.Team]
            : [CsTeam.Terrorist, CsTeam.CounterTerrorist];

        bool any = false;
        foreach (CsTeam team in teams)
        {
            (List<string> blocked, List<string> limited) = DescribeTeamLimits(player, team);
            if (blocked.Count == 0 && limited.Count == 0)
            {
                continue;
            }

            any = true;
            Reply(player, info, teams.Length == 1
                ? Localizer.ForPlayer(player, "list.your_team")
                : Localizer.ForPlayer(player, "list.team_header", Localizer.ForPlayer(player, team == CsTeam.Terrorist ? "team.t" : "team.ct")));
            ReplyEntries(player, info, "list.blocked", blocked);
            ReplyEntries(player, info, "list.limited", limited);
        }

        if (!any)
        {
            ReplyKey(player, info, "list.none");
        }
    }

    /// <summary>A group whose weapons are all blocked is named once instead of weapon by weapon.</summary>
    private (List<string> Blocked, List<string> Limited) DescribeTeamLimits(CCSPlayerController? player, CsTeam team)
    {
        List<string> blocked = [];
        List<string> limited = [];
        if (!Config.Enabled)
        {
            return (blocked, limited);
        }

        foreach (WeaponGroup group in MenuCategories)
        {
            WeaponDefinition[] weapons = WeaponCatalog.InGroup(group).ToArray();
            if (group != WeaponGroup.None && weapons.All(weapon => _limits.Get(weapon, team) == 0))
            {
                blocked.Add(GetGroupName(player, group));
                continue;
            }

            foreach (WeaponDefinition weapon in weapons)
            {
                int limit = _limits.Get(weapon, team);
                if (limit == 0)
                {
                    blocked.Add(weapon.DisplayName);
                }
                else if (limit > 0)
                {
                    limited.Add(Localizer.ForPlayer(player, "list.entry_limit", weapon.DisplayName, limit));
                }
            }
        }

        return (blocked, limited);
    }

    private void ReplyEntries(CCSPlayerController? player, CommandInfo info, string key, List<string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        List<string> lines = JoinInLines(entries, MaxListLineLength);
        Reply(player, info, Localizer.ForPlayer(player, key, lines[0]));
        foreach (string line in lines.Skip(1))
        {
            Reply(player, info, line);
        }
    }

    /// <summary>Chat cuts long messages, so the entries are spread over as many lines as needed.</summary>
    private static List<string> JoinInLines(IEnumerable<string> entries, int maxLength)
    {
        List<string> lines = [];
        StringBuilder line = new();
        foreach (string entry in entries)
        {
            if (line.Length > 0 && line.Length + 2 + entry.Length > maxLength)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            line.Append(line.Length > 0 ? ", " : string.Empty).Append(entry);
        }

        lines.Add(line.ToString());
        return lines;
    }

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!HasAdminAccess(player, info))
        {
            return;
        }

        try
        {
            OnConfigParsed(ConfigManager.Load<UmbrellaWeaponRestrictConfig>(Path.GetFileName(ModuleDirectory)));
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not reload the config.");
            ReplyKey(player, info, "command.reload_failed", exception.Message);
            return;
        }

        _menus.UpdateSettings(Config.Menu);
        ResetToConfig(Server.MapName);
        RefreshPerPlayer(force: true);
        QueueEnforce();
        ReplyKey(player, info, "command.reloaded");
    }

    private static bool TryParseLimit(string text, out int amount)
    {
        return int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount) &&
               amount is >= RestrictionState.Unlimited and <= RestrictionState.MaxLimit;
    }

    private static bool TryParseTeam(string text, out RestrictTeam team)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "t":
            case "terrorist":
            case "terrorists":
                team = RestrictTeam.Terrorist;
                return true;
            case "ct":
            case "counter":
            case "counter-terrorist":
            case "counter-terrorists":
                team = RestrictTeam.CounterTerrorist;
                return true;
            case "both":
            case "all":
            case "ambos":
                team = RestrictTeam.Both;
                return true;
            default:
                team = RestrictTeam.Both;
                return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Menu
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Menu categories: the weapon groups plus "Other" for the Zeus, which belongs to no group.
    /// </summary>
    private static readonly WeaponGroup[] MenuCategories = [.. WeaponCatalog.Groups, WeaponGroup.None];

    /// <summary>What a team/limit screen edits: one weapon, or every weapon of a group.</summary>
    private sealed record MenuSubject(WeaponDefinition? Weapon, WeaponGroup Group);

    private sealed record PendingChatLimit(MenuSubject Subject, RestrictTeam Team, DateTime ExpiresUtc);

    private WasdMenuScreen BuildMainMenu(CCSPlayerController player)
    {
        List<WasdMenuItem> items =
        [
            new WasdMenuItem
            {
                Label = () => Localizer.ForPlayer(player, "menu.groups_option"),
                OnSelect = selected => _menus.Push(selected, BuildGroupMenu(selected))
            }
        ];

        foreach (WeaponGroup category in MenuCategories)
        {
            items.Add(new WasdMenuItem
            {
                Label = () => FormatCategory(player, category),
                Color = CategoryColor(category),
                OnSelect = selected => _menus.Push(selected, BuildCategoryMenu(selected, category))
            });
        }

        return new WasdMenuScreen
        {
            Title = Localizer.ForPlayer(player, "menu.title"),
            Items = items
        };
    }

    private WasdMenuScreen BuildGroupMenu(CCSPlayerController player)
    {
        return new WasdMenuScreen
        {
            Title = Localizer.ForPlayer(player, "menu.groups_title"),
            Items = WeaponCatalog.Groups.Select(group => new WasdMenuItem
            {
                Label = () => FormatCategory(player, group),
                Color = CategoryColor(group),
                OnSelect = selected => _menus.Push(selected, BuildTeamMenu(selected, new MenuSubject(null, group)))
            }).ToList()
        };
    }

    private WasdMenuScreen BuildCategoryMenu(CCSPlayerController player, WeaponGroup category)
    {
        string tName = Localizer.ForPlayer(player, "team.t_short");
        string ctName = Localizer.ForPlayer(player, "team.ct_short");

        return new WasdMenuScreen
        {
            Title = GetGroupName(player, category),
            Items = WeaponCatalog.InGroup(category).Select(weapon => new WasdMenuItem
            {
                Label = () => FormatWeaponLine(weapon, tName, ctName),
                Color = CategoryColor(category),
                OnSelect = selected => _menus.Push(selected, BuildTeamMenu(selected, new MenuSubject(weapon, category)))
            }).ToList()
        };
    }

    private WasdMenuScreen BuildTeamMenu(CCSPlayerController player, MenuSubject subject)
    {
        (RestrictTeam Team, string Key, string? Color)[] teams =
        [
            (RestrictTeam.Terrorist, "team.t", "#eab54f"),
            (RestrictTeam.CounterTerrorist, "team.ct", "#8ab4ff"),
            (RestrictTeam.Both, "team.both", null)
        ];

        return new WasdMenuScreen
        {
            Title = Localizer.ForPlayer(player, "menu.team_title", GetSubjectName(player, subject)),
            Items = teams.Select(entry => new WasdMenuItem
            {
                Label = () => Localizer.ForPlayer(player, entry.Key),
                Color = entry.Color,
                OnSelect = selected => _menus.Push(selected, BuildLimitMenu(selected, subject, entry.Team))
            }).ToList()
        };
    }

    private WasdMenuScreen BuildLimitMenu(CCSPlayerController player, MenuSubject subject, RestrictTeam team)
    {
        List<WasdMenuItem> items =
        [
            new WasdMenuItem
            {
                Label = () => Localizer.ForPlayer(player, "menu.limit_unlimited"),
                OnSelect = selected => ApplyMenuSelection(selected, subject, team, RestrictionState.Unlimited)
            },
            new WasdMenuItem
            {
                Label = () => Localizer.ForPlayer(player, "menu.limit_blocked"),
                OnSelect = selected => ApplyMenuSelection(selected, subject, team, 0)
            },
            new WasdMenuItem
            {
                Label = () => Localizer.ForPlayer(player, "menu.limit_custom"),
                OnSelect = selected => RequestChatLimit(selected, subject, team)
            }
        ];

        return new WasdMenuScreen
        {
            Title = Localizer.ForPlayer(player, "menu.limit_title", GetSubjectName(player, subject), GetTeamName(player, team)),
            Items = items
        };
    }

    /// <summary>
    /// Closes the menu and takes the limit from the admin's next chat message instead of listing every
    /// number from 1 to 64.
    /// </summary>
    private void RequestChatLimit(CCSPlayerController player, MenuSubject subject, RestrictTeam team)
    {
        _menus.Close(player);
        _pendingChatLimits[player.Slot] = new PendingChatLimit(subject, team, DateTime.UtcNow.AddSeconds(ChatLimitTimeoutSeconds));
        PrintToChat(player, Localizer.ForPlayer(player, "menu.limit_prompt", GetSubjectName(player, subject), GetTeamName(player, team)));
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !_pendingChatLimits.Remove(player.Slot, out PendingChatLimit? pending) || DateTime.UtcNow > pending.ExpiresUtc)
        {
            return HookResult.Continue;
        }

        // Anything that is not a number is ordinary chat and cancels the request.
        if (!int.TryParse(info.GetArg(1).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int amount))
        {
            return HookResult.Continue;
        }

        if (amount is < RestrictionState.Unlimited or > RestrictionState.MaxLimit)
        {
            _pendingChatLimits[player.Slot] = pending;
            PrintToChat(player, Localizer.ForPlayer(player, "command.invalid_amount"));
        }
        else
        {
            ApplyMenuSelection(player, pending.Subject, pending.Team, amount);
        }

        return HookResult.Handled;
    }

    private void ApplyMenuSelection(CCSPlayerController player, MenuSubject subject, RestrictTeam team, int amount)
    {
        if (!HasPermission(player, Config.AdminPermission))
        {
            _menus.Close(player);
            PrintToChat(player, Localizer.ForPlayer(player, "command.no_permission"));
            return;
        }

        List<WeaponDefinition> weapons = subject.Weapon != null ? [subject.Weapon] : WeaponCatalog.InGroup(subject.Group).ToList();
        SetBaseLimits(player, weapons, team, amount, subject.Weapon?.Key ?? subject.Group.ToString().ToLowerInvariant(),
            reader => GetSubjectName(reader, subject));

        string teamName = GetTeamName(player, team);
        PrintToChat(player, subject.Weapon != null
            ? Localizer.ForPlayer(player, "restrict.weapon", subject.Weapon.DisplayName, teamName, amount)
            : Localizer.ForPlayer(player, "restrict.group", GetGroupName(player, subject.Group), teamName, amount));

        string? overridden = DescribePerPlayerOverride(player, weapons);
        if (overridden != null)
        {
            PrintToChat(player, overridden);
        }

        // Back to the list the admin came from, rebuilt so it shows the new limits.
        if (subject.Weapon != null)
        {
            _menus.Reset(player, BuildMainMenu(player), 1 + Array.IndexOf(MenuCategories, subject.Group));
            _menus.Push(player, BuildCategoryMenu(player, subject.Group), WeaponCatalog.InGroup(subject.Group).TakeWhile(weapon => weapon != subject.Weapon).Count());
        }
        else
        {
            _menus.Reset(player, BuildMainMenu(player), 0);
            _menus.Push(player, BuildGroupMenu(player), Array.IndexOf(MenuCategories, subject.Group));
        }
    }

    private string FormatCategory(CCSPlayerController player, WeaponGroup group)
    {
        int restricted = WeaponCatalog.InGroup(group).Count(_limits.IsRestrictedForAnyTeam);
        string name = GetGroupName(player, group);
        return restricted == 0 ? name : $"{name} ({restricted})";
    }

    private string FormatWeaponLine(WeaponDefinition weapon, string tName, string ctName)
    {
        int tLimit = _limits.Get(weapon, CsTeam.Terrorist);
        int ctLimit = _limits.Get(weapon, CsTeam.CounterTerrorist);

        if (tLimit < 0 && ctLimit < 0)
        {
            return weapon.DisplayName;
        }

        if (tLimit >= 0 && ctLimit >= 0)
        {
            return $"{weapon.DisplayName}  {tName}:{tLimit} {ctName}:{ctLimit}";
        }

        return tLimit >= 0
            ? $"{weapon.DisplayName}  {tName}:{tLimit}"
            : $"{weapon.DisplayName}  {ctName}:{ctLimit}";
    }

    private string GetSubjectName(CCSPlayerController? player, MenuSubject subject)
    {
        return subject.Weapon?.DisplayName ?? GetGroupName(player, subject.Group);
    }

    private static string CategoryColor(WeaponGroup group)
    {
        return group switch
        {
            WeaponGroup.Pistols => "#7fc8ff",
            WeaponGroup.Smgs => "#6ee7b7",
            WeaponGroup.Shotguns => "#ffab6e",
            WeaponGroup.Rifles => "#ff8484",
            WeaponGroup.Snipers => "#c99bff",
            WeaponGroup.Grenades => "#b8e05a",
            WeaponGroup.Armor => "#a9b7c6",
            _ => "#ff9ce0"
        };
    }

    private string GetGroupName(CCSPlayerController? player, WeaponGroup group)
    {
        return Localizer.ForPlayer(player, group switch
        {
            WeaponGroup.Pistols => "group.pistols",
            WeaponGroup.Smgs => "group.smgs",
            WeaponGroup.Shotguns => "group.shotguns",
            WeaponGroup.Rifles => "group.rifles",
            WeaponGroup.Snipers => "group.snipers",
            WeaponGroup.Grenades => "group.grenades",
            WeaponGroup.Armor => "group.armor",
            _ => "group.other"
        });
    }

    private string GetTeamName(CCSPlayerController? player, RestrictTeam team)
    {
        return Localizer.ForPlayer(player, team switch
        {
            RestrictTeam.Terrorist => "team.t_short",
            RestrictTeam.CounterTerrorist => "team.ct_short",
            _ => "team.both"
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Config helpers
    // ---------------------------------------------------------------------------------------------

    private static TeamLimit[] CreateUnrestrictedLimits()
    {
        return Enumerable.Repeat(TeamLimit.Unrestricted, WeaponCatalog.All.Count).ToArray();
    }

    private static Dictionary<string, Dictionary<string, MapRules.MapLimit>> ReadMapLimits(
        Dictionary<string, Dictionary<string, UmbrellaWeaponRestrictConfig.TeamLimitSettings>> maps)
    {
        return maps.ToDictionary(
            map => map.Key,
            map => (map.Value ?? []).Where(entry => entry.Value != null)
                .ToDictionary(entry => entry.Key, entry => new MapRules.MapLimit(entry.Value.T, entry.Value.CT)));
    }

    private static string NormalizePermission(string? value, string fallback, string name, ICollection<string> warnings)
    {
        string permission = value?.Trim() ?? string.Empty;
        if (permission.Length > 1 && permission[0] is '@' or '#')
        {
            return permission;
        }

        warnings.Add($"{name}: '{value}' is not a permission flag (@domain/flag) or group (#group), using '{fallback}'.");
        return fallback;
    }

    /// <summary>
    /// The sound is played with the client "play" command, so anything that could escape the argument
    /// or point outside the game content is refused.
    /// </summary>
    private static string NormalizeSoundPath(string? value, ICollection<string> warnings)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return string.Empty;
        }

        bool safe = path.Length <= 160 &&
                    !Path.IsPathRooted(path) &&
                    path[0] != '/' &&
                    !path.Contains("..", StringComparison.Ordinal) &&
                    !path.Contains("//", StringComparison.Ordinal) &&
                    path.IndexOfAny(['\\', ':', ';', '"', '\'', '\r', '\n', '\t', ' ']) < 0;

        if (safe)
        {
            return path;
        }

        warnings.Add($"RestrictedSound: '{value}' is not a safe sound path, the sound is disabled.");
        return string.Empty;
    }

    private static void NormalizeMenu(UmbrellaWeaponRestrictConfig.MenuSettings menu, ICollection<string> warnings)
    {
        menu.VisibleItems = ClampSetting(menu.VisibleItems, 3, 9, "Menu.VisibleItems", warnings);
        menu.InputDebounceMilliseconds = ClampSetting(menu.InputDebounceMilliseconds, 50, 1000, "Menu.InputDebounceMilliseconds", warnings);
        menu.InitialInputDelayMilliseconds = ClampSetting(menu.InitialInputDelayMilliseconds, 0, 5000, "Menu.InitialInputDelayMilliseconds", warnings);

        // Same order the menu checks them in, so a key claimed twice stays with the first action.
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        menu.ScrollUpKeys = NormalizeKeys(menu.ScrollUpKeys, ["W"], "Menu.ScrollUpKeys", taken, warnings);
        menu.ScrollDownKeys = NormalizeKeys(menu.ScrollDownKeys, ["S"], "Menu.ScrollDownKeys", taken, warnings);
        menu.SelectKeys = NormalizeKeys(menu.SelectKeys, ["E"], "Menu.SelectKeys", taken, warnings);
        menu.BackKeys = NormalizeKeys(menu.BackKeys, ["Shift", "A"], "Menu.BackKeys", taken, warnings);
        menu.CancelKeys = NormalizeKeys(menu.CancelKeys, ["R"], "Menu.CancelKeys", taken, warnings);
    }

    private static int ClampSetting(int value, int min, int max, string name, ICollection<string> warnings)
    {
        int clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            warnings.Add($"{name}: {value} is out of range ({min} to {max}), using {clamped}.");
        }

        return clamped;
    }

    private static List<string> NormalizeKeys(List<string>? keys, List<string> fallback, string name, HashSet<string> taken, ICollection<string> warnings)
    {
        List<string> result = [];
        foreach (string raw in keys ?? [])
        {
            string key = raw?.Trim() ?? string.Empty;
            if (!WasdMenuService.IsKnownKey(key))
            {
                warnings.Add($"{name}: '{raw}' is not a supported key (W, A, S, D, E, R, Shift), ignored.");
            }
            else if (!taken.Add(key))
            {
                warnings.Add($"{name}: '{key}' is already used, ignored.");
            }
            else
            {
                result.Add(key);
            }
        }

        if (result.Count > 0)
        {
            return result;
        }

        List<string> free = fallback.Where(key => !taken.Contains(key)).ToList();
        warnings.Add(free.Count > 0
            ? $"{name}: no usable key, using {string.Join(", ", free)}."
            : $"{name}: no usable key and its default is taken, the action has no key.");
        taken.UnionWith(free);
        return free;
    }
}
