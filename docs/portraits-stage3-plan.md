# NHL Sync stage 3: player portraits (plan, not built)

Optional, after stage 2 is applied: for the chosen leagues (NHL and AHL first), find players with no portrait,
download a photo, fit it, and install it. The same slot rules must hold for every tool that writes portraits, the
NCAA package included, so two writers never land on the same slot.

Numbers below are from `nhl27kit portrait-slots` on 2026-11-11 (read only; it ran while the NCAA package was
installing, so league 27's counts may have been mid-write).

---

## How a portrait works (what limits us)

- A portrait is the texture `ui/textures/common/playerportraits/p{artid}` (512×340, BC3, transparent). The bio's
  `artid` names it and `hasportrait = 1` shows it.
- HMS can **replace** a texture but can't **add** one. A new photo goes into a texture no player uses, and the
  player's `artid` is pointed at it.
- A texture is "free" when no bio uses its id in `nhl_game.db` or in the game modes' rosters
  (`entry_draft`, `expansion_draft`, `initial_experience`, `threes_campaign`).
- A saved roster or a franchise keeps its own copy of the players, so a new portrait only shows on rosters and
  franchises started afterwards.

## What the game has

| | |
|---|---|
| Portrait textures | 16,963 (ids 1–31,999, plus 12 strays at 64,000+ and 100,000+) |
| Used by a bio | 7,448 (the game's roster 6,831; entry draft 616; initial experience 1) |
| **Free** | **9,515**, every one replaceable on its own (none share stored image data) |
| Longest unbroken run of free ids | 18: free ids are scattered between used ones |
| Portrait slots HMS has written | none today (the earlier Iowa Wild tests were restored) |

Free slots by id (2,000-id buckets):

| ids | textures | used | free |
|---|---|---|---|
| 0–1,999 | 868 | 361 | 507 |
| 2,000–3,999 | 477 | 61 | 416 |
| 4,000–5,999 | 513 | 94 | 419 |
| 6,000–7,999 | 715 | 247 | 468 |
| 8,000–9,999 | 814 | 292 | 522 |
| 10,000–11,999 | 1,140 | 456 | 684 |
| 12,000–13,999 | 1,424 | 434 | 990 |
| 14,000–15,999 | 1,601 | 449 | 1,152 |
| 16,000–17,999 | 1,313 | 514 | 799 |
| 18,000–19,999 | 1,186 | 444 | 742 |
| 20,000–21,999 | 1,359 | 522 | 837 |
| 22,000–23,999 | 1,582 | 735 | 847 |
| 24,000–25,999 | 1,375 | 856 | 519 |
| 26,000–27,999 | 1,320 | 899 | 421 |
| 28,000–29,999 | 1,067 | 901 | 166 |
| 30,000–31,999 | 197 | 183 | 14 |

Players with no portrait, by their club's league:

| League | Players | No portrait |
|---|---|---|
| NHL (0) | 825 | 14 |
| AHL (1) | 949 | 189 |
| ECHL (15) | 729 | 154 |
| OHL / QMJHL / WHL (11–13) | 1,654 | 701 |
| European leagues (2–9) | 3,083 | 657 |
| National teams (10, 24–26) | 673 | 373 |
| League 27 (NCAA mod?) | 213 | 61 |
| Others (alumni, Spengler, events) | 754 | 47 |

## Proposed id bands

Free ids are scattered, so a league can't own one neat block. Instead each owner gets an **id band** and takes free
slots only inside it:

| Band | ids | Free now | Owner | Need now |
|---|---|---|---|---|
| A | 1–9,999 | 2,332 | HMS NHL Sync: NHL, AHL, ECHL | ~360 |
| B | 10,000–17,999 | 3,625 | Packages (NCAA first) | ~1,800 (63 schools) |
| C | 18,000–23,999 | 2,426 | HMS: CHL (OHL, QMJHL, WHL) | ~700 |
| D | 24,000–29,999 | 1,106 | HMS: European leagues, later | ~650 |
| — | 30,000 and up | 26 | Nobody: next to the game's newest players, where an update may add its own | — |

- **Band A** is the oldest retired players' photos, which the game never shows any more. It has about 6× today's
  NHL/AHL/ECHL need, so seasons of call-ups and new signings fit before anything is recycled.
- **Band B** is the largest band. The NCAA mod's ~1,800 players take about half of it, which leaves room for roster
  turnover and a second package. Two packages share it by each holding the slots they resolved, the way packages
  already share team indexes and logo art ids.
- Bands C and D don't need building now. Reserving them means NHL/AHL and NCAA never grow into them.

## Who holds a slot: one registry

The rule "no bio uses it" isn't enough once there are several writers:
- a package taken down (restored) leaves its slots looking free until it's reinstalled;
- a roster restore can drop the bio that pointed at an HMS portrait;
- either way, another writer could take the slot, and the first one's reinstall would then show the wrong face.

So slots get recorded in one registry that every writer reads and writes. Where it lives isn't decided yet (see
"Still open"); the example below uses `mods/portraits.json`.

```json
{ "bands": { "nhl-sync": [1, 9999], "packages": [10000, 17999], "chl": [18000, 23999], "europe": [24000, 29999] },
  "slots": {
    "1523": { "owner": "nhl-sync", "player": "nhl:8484801", "gameId": 32418, "image": "sha1…", "written": "2026-11-12" },
    "10044": { "owner": "package:ncaa", "player": "ncaa:…", "gameId": 33012, "image": "sha1…", "written": "2026-11-12" } } }
```

Taking a slot: the lowest id in the owner's band that no roster bio uses and the registry doesn't give to anyone else.

- **The same player keeps his slot.** The registry is keyed by a source id (NHL id, CapWages slug, NCAA id) plus the
  game id. So a rerun, a reinstall or a stage-2 re-apply puts him back on the same slot instead of using up a new one.
- **Recycling:** a slot goes back into its band only when its owner releases it, for example when the player has no
  bio left and the user confirms the cleanup. Restores never free slots on their own.
- **Packages:** the package resolver (`resolved.json`) asks the registry for portrait slots in band B, the same way it
  resolves logo art ids now. A package removal keeps its slots, just as it keeps its team index, so a reinstall gets
  the same ones.

## Stage 3 in NHL Sync

1. **Choose leagues:** NHL and AHL ticked, ECHL listed but off until it has a photo source.
2. **Find:** players on those leagues' teams with no portrait to show (`hasportrait` 0, or `artid` with no texture).
   They're matched to the stage-2 sources by game id and birth date, so this runs after Apply, when new players exist.
3. **Look up photos** (cached like the CapWages pages):
   - NHL players: the `headshot` on NHL.com's roster list, which stage 2 already downloads.
   - AHL players: the NHL.com headshot from their CapWages page, which is a transparent mug and only exists for
     players NHL.com has photographed.
   - **AHL players without one are skipped and logged** (see below).
4. **Preview:** a grid of the fitted portraits with each player's name and team. Every one is ticked; untick any
   that look wrong.
5. **Install:** one mod entry per run, holding every texture plus the bios' `artid` and `hasportrait`. Restoring it
   takes the whole run back, unlike today's one entry per player. The registry is updated in the same step.
6. **Report:** written, skipped (no photo, no free slot), and a reminder about saved rosters and franchises.

**Only missing portraits** are filled. A player who already has one is left alone, even when his photo is old.

### The skipped-players log

Every player stage 3 couldn't give a photo is written to a list, so a supplemental source can be tracked down later:
`%LOCALAPPDATA%\NHL27Kit\export\portraits-skipped.csv`, rewritten on each run (the run's date in the file), and
openable from the stage's report.

| Column | Example |
|---|---|
| Name | Cooper Gay |
| Team (game) | Colorado Eagles |
| League | AHL |
| Game id | (his id in the game) |
| Born | 2002-03-15 |
| AHL id (TheAHL.com) | 10545 |
| CapWages page | capwages.com/players/cooper-gay |
| Why | no NHL.com photo (CapWages has only an EliteProspects photo) |

The ids are there so a later source (TheAHL.com's photos, EliteProspects with the background cut out) can be matched
to the player without guessing by name.

## Part 2 (later): updated photos for existing players

For the chosen leagues: compare each player's portrait with his current NHL.com photo and offer the newer one. It
writes into the player's **own** slot when HMS (or the game) already gave him one, so it uses no new slots. It needs
a way to tell when a photo is newer, such as NHL.com's mug season in the URL (`/mugs/nhl/20262027/…`) or a stored
image hash in the registry.

Progress shows in the stage list like stage 2's ("Portraits · NHL.com / CapWages · 37/189"), and Cancel stops it
before anything is written.

## Decided (2026-11-11)

- **AHL players with no NHL.com photo** are skipped for now and logged (the skipped-players log above), so a
  supplemental source can be found later. Photos with a background aren't used.
- **NCAA:** about 1,800 players across 63 schools, in band B (3,625 free).
- **Stage 3 fills missing portraits only.** Fresher photos for existing players are Part 2.

## Still open

- **Where the registry lives.** Some options:
  - **A file next to the mod list** (`%LOCALAPPDATA%\NHL27Kit\mods\portraits.json`): the app, the CLI and packages
    all see it, and it's per machine, like the mod list it has to agree with.
  - **Inside each owner's own records:** packages in their `resolved.json`, HMS in its own file. Nothing new is
    shared, but every writer would then have to read every other writer's records to know what's taken.
  - **In the game itself:** a spare database field or table holding each slot's owner. It travels with the game
    folder, but it's lost on a database restore, which is exactly when it's needed most.

## What exists today

- `PortraitSync` (Core): finds candidates from one CapWages team page, fits an NHL.com headshot (581 px at
  −47, −92, checked in game), lists free slots (lowest first, no bands), and points the bio at the slot.
- The Roster page's **Portraits** button: one team at a time, one mod entry per player, plus one database entry.
- `nhl27kit portrait-slots`: the survey above (read only).
