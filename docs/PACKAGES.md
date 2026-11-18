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
  rinks/<CODE>.png    optional top-down rink layout (TheFaceoff.net 5000x2236): the team's centre ice is cut from it
                      (CenterIce.Logo) onto the team's own logo art id only; a team on the shared placeholder is skipped.
                      PNG only (a .jpg is reported); folder set by "rinks", default "rinks"
  <league>/uniforms/<CODE>/<home|away|third>/uniform.json + PNGs + stamp_*.ebx: team uniforms exported by the Jersey
                      Studio ("Export to mod"), recipe "uniforms" (per league). Each was rendered for one free kit
                      folder of the game, recorded in uniform.json (folder, artId, label, light); the installer writes
                      the textures and stamps into that folder and makes the team's jersey menu exactly its uniforms
                      (home, away, third first). A team with none keeps the template's jerseys.
  build/              pack output and install reports
```
`package-export` zips the folder to share (the planned format-2 `.nhl27mod` pack was not needed).

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
Optional group setting `"cityFromSchool": true`: the group's teams take the school name as their city (db cityname
and NHLCITYNAME), so pickers that show and sort by city (Edit Lines) list them by school. Not inherited by extra
leagues (the USHL keeps its cities); the arena keeps `arena_city`.

**Status 2026-11-10:** built: `package-check`, `package-install` (+ `--dry`, `--emulator`), `package-remove`,
`package-status`, `package-codemods`. Core/Packages: PackageSpec, PackageCheck, PackageInstaller, Png, NcaaCodeMods.
Players step built (PackagePlayers; `package-players FOLDER` runs it read-only). The players file is
{ team: { season: { "First Last": { birthdate, position, shoots, height, weight, hometown, jersey_number, class,
draft: { year, round, pick, team } } } } }. `"bubble": "true"` (or true/yes/1) gives a skater a clear full shield
(faceprotectortype `rules.bubbleFaceProtector`, default 9) instead of `rules.faceProtector` (the cage, 7). `draft` (NHL team abbreviation) is written to the bio of a placed player
(created or moved) and of an exactly matched commit: draftyear/round/position/team, and proteam = the drafting team
unless the game has his rights with another team. No `draft`: the game's draft stays (a created player is undrafted).
Current-season roster players are matched and moved (taking the file's number and size), or created from the file, then given the NCAA bio (team -1, juniorteam = school, juniorleague) and
rules.faceProtector; later seasons (commits) are only listed for now.
Goalies may give `"catches": "L|R"` in place of `shoots` (catches L = handedness 0, as most of the game's goalies).
`"captaincy": "C|A"` → the player's school roster row `captain` (1 C / 2 A), for a team whose letters are 1 C + 2 A or
3 A (other teams get none, with a warning). `"player_type"` → its `playerstyle` (DFD 1, OFD 2, TWD 4, GRN 5, PLY 6,
SNP 7, PWF 8, TWF 9, ENF 10; skaters). `"player_model": { "skin_tone": ... }` → bio skincolor (1-12, light to dark:
fair 1, light 1-2, medium 3-4, olive 4-5, medium_brown 6-7, brown 8-9, dark 10-12) and a generic head (100000+)
the game's own players mostly wear at that colour, picked by name (stable across installs). Only players on a
generic head change, and one already in the tone's range on a head of it is left. `"ethnicity"` asian / east_asian /
korean / japanese / chinese → one of the game's East Asian generic heads (the ones its own Korean, Japanese and
Chinese players wear). `"hair_color"` (black 0, dark_brown 1, medium_brown 2, blond 3, light_brown 4, platinum 5,
auburn 6, white 7, red/ginger 8, golden_blond 11, grey 12) → haircolorcode, and eyebrowcolorcode and the beard colour
the same. `"facial_hair"` → the beard worn now (playoffbeardstart, stage 1-4) and its style (playoffbeardtype):
none = clean-shaven (start 0), stubble = partial/full stage 1, mustache = moustache stage 2, goatee = goatee/goatee2
stage 2, beard = full/full2/groomed stage 2; the playoff beard grows at least one stage past it. Hair and beard on
generic heads only. hair_length / hair_type are read but not applied yet (hairid male_0-73 has no names in the game).
Optional `rules.gloves`, `rules.helmet`, `rules.neckGuard`: every package skater's exhibitionskaterequipment gloves
(player_glove_mapping id, e.g. 113 CCM JetSpeed FT4), helmetbrand (e.g. 34 CCM Tacks 910) and neckguardtype (1
turtleneck, 2 floating, 0 none); a neck guard also clears the bio's neckguardexempt. Unset leaves the created
players' defaults. A team's own `"equipment": { "gloves": 132, "helmet": 39, "neckGuard": 1 }` (in the package's
"teams" entry, like "arena") overrides the rules for its skaters, and a roster entry's `"neck_guard"` ("none",
"turtleneck", "floating" or 0-2) overrides both for that player, as "bubble" does the cage. Goalies are left alone.
Sticks follow the glove brand (Bauer, CCM, Warrior, TRUE, Sherwood): each skater gets one of that brand's sticks the
game's NHL skaters carry, picked by name (stable across installs); `"equipment": { "stick": 222 }`
(player_stick_mapping id) gives a team one stick. No glove setting: the stick is left (created players: 221 Bauer
Vapor FlyLite).
A team's `"team": { field: value }` sets its exhibitionteams fields last: `audioid` + `audioid2` (the play-by-play
call; every team has the same id in both, e.g. 433 = "the Huskies" from Rouyn-Noranda), `chantaudioid` (crowd chant;
0 = none; teams cloned from 309 carry the North Stars' 90), `coachheadid` (the bench coach's face), `goalsongid`
(the home goal song: an `audioid` from creation_zone_csv/songs.csv, e.g. 1994003 "NHL94Theme03"; -1 = default).
`"goalSongs": { "folder": "ncaa/audio/goal_songs", "loudness": -10, "donors": ["ArtfulWaltz"] }`: the teams' own goal
songs. A file `<folder>/<team code>.wav` (MINN.wav; .mp3/.flac/.ogg/.m4a need ffmpeg) takes over one Creation Zone
song and becomes the team's goalsongid (over a `"team"` goalsongid). The songs' audio is one 425-stream ATRAC9 chunk
(sound/music_fb/be/cz/nhl_music_be_cz_wave) whose table maps audioid → stream; a donor's stream is rewritten in place
with its own ATRAC9 settings, so nothing else moves. Donors: the `donors` list first (identifier or audioid), then
the Misc genre in list order, shortest one that holds the song; never the NHL 94 themes, the anthems or a song
another team uses. The audio is first brought to `loudness` LUFS (default -10; the game's songs are about -9, a
normal WAV is often -19 and plays far quieter) with a look-ahead limiter at -1 dBFS. The donor keeps its name in the
song picker. Removing the package puts the donors' audio back.
A team's `"palette": true` gives it a palette in stockteamcolorstable (color_1..3 of the teams file; or a list of
"#RRGGBB", primary first). The game's own teams have one (it tints the crowd's jerseys, among others); created teams
don't, and their crowd wears the kit owner's colours. The table's index field is 8 bits and 221 of its 253 rows are
used, so only ~32 colours fit; teams past that are skipped with a warning.
A team's `"abbr"` overrides the teams file's abbreviation (the team `code` stays the key for rosters, logos and
stats), and `"nameSet": "TING"` makes it take that name set before the other teams pick (only a few sets hold a
4-letter code; when none is free, package-check lists the ones that could and who has them).
`"careerStats"`: one file or a list (JSON, or CSV like NCAA_Mod/ncaa/player_stats/*.csv) of real past seasons for the
package's players, written to stockcareerseasonplayerstats (skaters) / stockcareerseasongoaliestats (goalies, by the
game's position). A row is one player, season ("2024-25" = year 2025) and team (default his school); a CSV row without
`gp` is skipped. The tables ship full, so they're grown (T3Db.Grow) and nhl_game.db is written bigger: its chunk is
rebuilt and appended to its CAS (ChunkGrower), the legacy index entries and the index's chunk-size list updated. The
league field is one bit: rows use `careerStatsLeague` (default 1); the NCAA code mod labels school lines "NCAA".
Created players copy a whole ratings row from a template player: by default a typical NHL/AHL player of the same
position, hand and age (near the median: about 72 overall). `"ratings": { "from": [11, 12, 13], "maxOverall": 67 }`
in a players block takes templates from those leagues' teams instead (exhibitionteams.league; 11-13 = OHL/QMJHL/WHL),
at most that overall, and any one of the matching candidates, so the new players spread over that league's range.
Add `"median": 65` and/or `"teams": { "MICH": 70, ... }` (a team's own median by code) and the players aim at that
overall +/- `"spread"` (default 6, most near the middle, never above `maxOverall`), copying the candidate nearest it.
Helmet logos: a kit folder's helmetlogo_0 (2:1, both sides of the helmet) still holds its old team's logo, so an exported
uniform without a `helmetlogo_0_color` texture of its own gets the team's main logo centred on a 1024x512 decal, and a
helmetlogo_2 (league badge or flag on some kits) is cleared (PackageUniforms.HelmetLogos; made in build/helmetlogos).
Only teams with an exported uniform: the others wear their template's folder, which other teams use.
Name strings are packed (PackageInstaller.PackNameStrings): a reused set's strings only have the room the old club's
texts took, so a team's strings with the same text share one stored copy (T3Db.PointString), and a text too long for
its own place goes to a place freed that way (any team's), counting a freed neighbour's room. Then shorter texts, a
blank, the old text, as before; a layout that would overlap anything falls back to writing in place. NCAA: 586
strings, 0 blanked (was 40 blanked).
`package-export` ships no rink layouts: each team's centre ice is cut at export exactly as the installer cuts it and
goes in as the team's ice logo (`<CODE>_ice.png`, or `ice.png` in its logo folder), which an install takes as it is.
`"game": { "titleId": "PPSA34063", "version": "01.003.001" }` (sce_sys/param.json titleId and contentVersion): an
install into another game or version is refused before anything changes (PackageInstaller.CheckGame).
`"createCommits": true` in a players block: commits (later seasons) not in the game are created as unsigned
prospects with no team (team -1, no roster row), rated by the block's `ratings` for their school and in its `ids`,
given the file's draft, and join their school by art id like any commit; ones without a usable position stay name
commits. They (and any matched commit with no roster row) go on exhibitionfreeagents: a new franchise drops a rosterless
player who isn't on that list. No name commit for them: it would also send the franchise's own generated namesake (it
generates ~80 real-named prospects at start, e.g. a second Joey Cullen) to the school.
`"ids": [40000, 41999]` in a players block puts that file's created players at those game ids (in order, after any
already there). Ids 40000-49999 are for packages' blocks (PlayerCreator.PackageIdStart); anything created without a block
(NHL Sync, a roster file) stays below 40000, and 50000+ is the game's (game modes 56500+, archetypes, Create-a-Player).
Art ids are stable across reinstalls (ArtIdMemory, kept in the package's resolved.json as OwnArtIds/PortraitArtIds by
"first last yyyy-mm-dd"): a created or committed player gets his own id back, a player with a photo his portrait slot,
and new players get ids nobody held. A franchise save keeps each player's art id while portraits and the NCAA code mods'
tables find players by it, so a reinstall (more photos, roster-file fixes) must not move them. One photo per game player:
a second photo that finds the same player is left out with a warning; and before anything is written, two ranges over
the same bytes must write the same bytes (else the install stops).
Built 2026-11-14: the take-down/re-apply hook (a database save blocked by a package offers to take it off, save,
and reinstall it: ModService.SaveDbAsync), the App UI (Mods page: Install package… / Reinstall / Remove package; a
package row is installed and removed as a whole, and left out of Apply all / Restore all / Export pack), and a plain
zip as the shared form (`package-export`, `package-unzip`; the App unzips into `<kit>/packages/<name>`). Not built: the
format-2 `.nhl27mod` pack (the zip does the job), a standalone installer (HMS's Mods page is it for now).
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
   - face protector;
   - the game's pool of real people (customplayerbiotable, state 2): a row with the same name and birth date as one of
     the package's players is set to state 127 (unused), so a new franchise doesn't generate a second copy of him.
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
| `package-export <folder> <zip>` | the package as one zip to share (no photos/, backups/, build/, cache/, *.bak) |
| `package-unzip <zip>` | unpacks a shared zip into `<kit>/packages/<name>` (what Install package… does first) |
| `package-install <folder\|pack>` | install or rebuild (teardown + build + apply) |
| `package-remove <id>` | restore and remove |
| `package-status` | installed packages, their state and resolved ids |

## Later
- Standalone installer: one exe on Core, with a game folder, an emulator folder, Install and Uninstall.
