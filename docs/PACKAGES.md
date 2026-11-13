# Packages: multi-part mods as one recipe (design, stage 0 of the NCAA mod)

A **package** is a mod made of instructions instead of bytes: add these teams, rename these strings, put these logos
on these art ids, move these players, add these code patches. HMS runs the instructions against the game **as it is
now** (any roster the user has), and records everything they changed as **one mod-list entry**. Removing that entry
puts back every byte. Because HMS keeps the instructions, it can take the package down and run it again whenever
something underneath changes.

## Why not the existing pack format
A `.nhl27mod` pack (format 1) stores a database item as the **whole edited database**: importing it overwrites every
other mod's database changes. Baseline roster / NHL Sync also replace whole player and team tables. A package works on
top of those instead.

## Source folder
```
<Package>/
  package.json        the recipe (below)
  data/               inputs the recipe reads (teams.json, players.json)
  logos/league/logo.png, logos/teams/<CODE>/logo.png (+ logo_lightbg.png, mono.png, ice.png)
  build/              pack output and install reports
```
`package-build` zips the folder into a format-2 `.nhl27mod` with one item of kind `package`.

## package.json
```json
{
  "id": "ncaa", "title": "NCAA", "version": "0.1",
  "league": { "id": 32, "strings": { "TXT_USA_LEAGUE": "NCAA" }, "logo": "logos/league/logo.png" },
  "teamBlock": { "first": 721, "last": 782 },
  "teams": [ { "code": "MINN", "index": 721, "template": 309, "name": "Minnesota Golden Gophers",
               "school": "Minnesota", "nickname": "Golden Gophers", "city": "Minneapolis", "abbr": "MINN",
               "conference": "Big Ten", "primary": "#7A0019", "secondary": "#FFCC33" } ],
  "players": { "file": "data/ncaa-rosters.json", "season": "2026" },
  "rules": { "faceProtector": 7 },
  "codeMods": { "file": "codemods.json" }
}
```

Optional per team: `nicknameShort`, `nameShort`, used when a name-set string has no room for the full text.
Optional `nameSets.avoid`: sets the package must not take (the NCAA package reserves the 15 old KHL club sets for a KHL
package).

**Status 2026-11-10:** built: `package-check`, `package-install` (+ `--dry`, `--emulator`), `package-remove`,
`package-status`, `package-codemods`. Core/Packages: PackageSpec, PackageCheck, PackageInstaller, Png, NcaaCodeMods.
Players step built (PackagePlayers; `package-players FOLDER` runs it read-only). The players file is
{ team: { season: { "First Last": { birthdate, position, shoots, height, weight, hometown, jersey_number } } } }.
Current-season roster players are matched and moved (taking the file's number and size), or created from the file, then given the NCAA bio (team -1, juniorteam = school, juniorleague) and
rules.faceProtector; later seasons (commits) are only listed for now.
Not built yet: the DbEditor take-down/re-apply hook, pack build (format 2), App UI, standalone installer.
Shared placeholder textures get their own copy at the end of the CAS file (ChunkGrower + Framing.Reframe).

## What an install does (`package-install <folder|pack>`)
1. Refuse while the emulator runs. If the package is applied, restore its entry first.
2. Open the game's databases as they are now (nhl_game.db, loc/eng_us.db).
3. **Resolve** the choices that must stay the same on every rebuild, and record them in
   `mods/packages/<id>/resolved.json`, reused next time:
   - team index (from `teamBlock`);
   - name set (a dead team's language strings where every text fits, see franchise-patch-research/name_sets);
   - logo art id (a spare id whose logo textures exist and no team uses);
   - league logo art id;
   - arena row.
4. Run the recipe on the databases in memory:
   - league strings and stockleagues art id;
   - each team cloned from `template`, with names, codes, colours, league, a shared group id, its own arena row,
     strategies, jerseys and equipment;
   - the name-set strings;
   - players (stage 3);
   - face protector.
5. Plan the bytes:
   - each changed database is written back into its chunk and planned with ChunkPatcher;
   - each logo texture is planned with Replacements.PlanTexture from the folder's images, resized per texture; mono
     styles from `mono.png` or a white silhouette.
6. Store **one** entry: kind `package`, name `<id>`, patches across every file. Apply it.
7. Code mods: merge the package's Kyty patches (school block filled in) into the HMS-managed patch file
   `mods/codemods.json`. Make sure the emulator config's `--game-patch` points at it; the emulator folder is set once
   (`emulator` setting), and HMS asks before changing it.
8. Write `build/install-report.md`: what was resolved, matched, skipped, and why.

## Removing (`package-remove <id>`)
Restore the entry, take its code mods out of the managed patch file, keep `resolved.json` (so a reinstall gets the same
ids).

## Staying on top of other database work
`DbEditor.Save` is the one path every database edit takes (Baseline roster, NHL Sync, db-set, create-player…). It
gains two steps:
- **before:** restore every applied package entry that touches the same database file;
- **after:** re-run those packages from their cached copy (`mods/packages/<id>/package`).

Order is always: stock → roster and database edits → packages. Two packages stack: each takes its own rows, indexes and
art ids, and the resolver skips ids another package already holds.

## Commands
| Command | What |
|---|---|
| `package-check <folder>` | read-only: loads the recipe, checks images, says what it would resolve and change |
| `package-build <folder>` | makes `build/<id>.nhl27mod` |
| `package-install <folder\|pack>` | install or rebuild (teardown + build + apply) |
| `package-remove <id>` | restore and remove |
| `package-status` | installed packages, their state and resolved ids |

## Later
- App UI: the Mods page lists packages as one row; Install pack accepts format 2.
- Standalone installer: one exe on Core, with a game folder, an emulator folder, Install and Uninstall.
