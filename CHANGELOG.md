# Changelog

## [1.0.3] - 2026-11-23

### Fixed
- Open menus show the current limits when another admin, a reload or the player count changes them.
- A refused spawn or plugin weapon is only deleted once the entity list confirms it is a live weapon.
- `Maps` patterns with many `*` can no longer slow down a map start.

### Changed
- Built against CounterStrikeSharp 1.0.380; 1.0.376 or newer still works.

## [1.0.2] - 2026-09-30

### Added
- `Maps`: different limits for single maps or map name patterns such as `awp_*`.
- `css_restrictions` (`!restrictions`): anyone can see the current limits of their team.
- `AnnounceChanges`: everyone is told in chat when an in-game admin changes a limit.
- Per-player rules take an `immunity` key: VIP and admin immunity for that weapon only applies from that many players.

### Fixed
- An immune player controlling a bot no longer uses the team's slots with what they carry, the same as on their own character.

## [1.0.1] - 2026-09-29

### Added
- `WeaponImmunity`: a flag or group can ignore the limits of single weapons or groups, for example VIPs and the AWP.

### Changed
- Requires CounterStrikeSharp 1.0.376.

## [1.0.0] - 2026-09-27

Initial CS2 release, based on the [SourceMod plugin](https://github.com/Ayrton09/umbrella-weapon-restrict) for CS:S/CS:GO.

### Added
- Per-team weapon limits for every buyable weapon, grenade and armor, managed by item definition index.
- Weapon groups (pistols, smgs, shotguns, rifles, snipers, grenades, armor) in commands and the menu.
- Buys and pickups refused through CS2's own acquire check, before any money is taken.
- Weapons over a lowered limit removed right away and at every round start.
- Optional limits that follow the number of players on the server.
- WASD center menu organized by weapon group and color coded, with custom limits typed in chat and the player held in place while it is open.
- `css_restrict`, `css_unrestrict`, `css_restrictmenu`, `css_wrmenu`, `css_remove_restricted`, `css_wr_list` and `css_wr_reload` commands.
- Admin immunity and configurable admin permission (flag or group).
- Chat notices with anti-spam and the game's "can't buy" sound.
- Translations in 9 languages: English, Spanish, Portuguese, Chinese, Russian, French, German, Turkish, Polish.
