# NHL Sync: what is pulled, from where, and what happens to it

How the Roster page's NHL Sync works today (2026-11-10, branch `feature/player-portraits`, uncommitted).
Nothing is written to the game until **Apply**; everything before it reads the web and works on copies.

---

## Step 1 · Base roster (optional, separate button)

| # | Reads | Does | Writes |
|---|---|---|---|
| 1 | A roster pack on disk (`manifest.json` + `tables\*.csv`, made once from a RosterCustom save) | Layered import on the game's own roster: ratings and bios by game id → players the game lacks added → Create-a-Player tables → roster spots by (player id, version) → Create-a-Player players converted (duplicates merged) → orphan stats removed → every reference checked | One roster mod entry (`nhl_game.db`), restorable on the Mods page. Refused if any reference would point at nothing. |

Step 2 always compares against whatever roster is in the game, so a base roster goes in first.

---

## Step 2 · Fetch & compare

### Phase A: four downloads at the same time

| # | Source | What is read | Kept as |
|---|---|---|---|
| A1 | **NHL.com** (`api-web.nhle.com`, `/roster/{team}/current`, 32 teams, 250 ms apart) | Every NHL roster player: NHL id, name, team, number, position, height, weight, birth date | `_api` |
| A2 | **TheAHL.com** (HockeyTech feed, 32 teams, 250 ms apart) | Every AHL roster player: AHL id, name, team, number, position, birth date, height, weight, birthplace | `_ahlFeed` (raw) |
| A3 | **CapWages team pages** (`capwages.com/teams/{slug}`, 32 teams, 500 ms apart; all or nothing) | Per NHL team: roster / inactive / non-roster players (name, birth date, status: NHL, Minor, IR, LTIR, SEIR, Junior, Loan…, positions played, contracts season by season, clauses, retention), the affiliate's AHL-contract players, the unsigned draft picks it holds the rights to (with their page addresses), dead cap (buyouts), draft picks | `_cap`; retained-salary notes saved for the player profile |
| A4 | **DailyFaceoff** (32 team pages) | Forward lines, D pairs, PP, PK, starting goalie | `_lines` |

### Phase B: AHL players get CapWages' details (needs A2 + A3)

| # | Source | What happens |
|---|---|---|
| B1 | (in memory) | Each TheAHL roster player is matched to CapWages by name (birth date breaks ties). A match takes **CapWages' birth date** and slug; TheAHL's height, weight and birthplace are dropped. **TheAHL.com keeps only: who is on which AHL team, and the AHL number.** |
| B2 | **CapWages player pages** (`capwages.com/players/{slug}`, ~770; cached 20 h on this PC under `%LOCALAPPDATA%\HockeyModdingSuite\cache\capwages-players`) | Height and weight for every matched AHL player. Players CapWages doesn't list (tryouts, ECHL deals) keep TheAHL's details. |

Result: `_ahl`, the AHL rosters as compared.

### Phase C: rosters compared (needs A1 + B)

One combined compare against the game's roster database, with one shared "matched" set so a player only ever gets one change:

1. **AHL first** (each TheAHL team resolved to its game team by name; an unresolvable feed team is skipped and listed). Each player is matched to a game player by name + birth date (then last name + birth date, then a single name-only match, then the only player on that team with his last name and a shortened or longer first name, "Ben" / "Benjamin"; the last two are flagged for review). Gives **AHL Join / Move / Update / Missing**.
2. **NHL second**, skipping anyone the AHL compare matched. Gives **Trade (Move) / Call-up (Join) / Info (Update) / Missing**, and **Left NHL** for players the game has on an NHL team that no roster list has.
3. **AHL stragglers**: players the game has on an AHL team whose roster was read, but whom neither compare matched. Gives **Left (from AHL)**.

**Info (Update) compares only:** jersey number, height, weight, birth date. Positions and contracts are compared later (Phase F, from CapWages).

### Phase D: bios of players the game doesn't have (needs C)

| # | Source | For |
|---|---|---|
| D1 | **NHL.com** player landing page | Each NHL **Missing** player: birth date, birthplace, country, size, hand, position, draft |
| D2 | **CapWages player page** (cached), else the TheAHL feed's player view | Each AHL **Missing** player: name, positions (forward/defence never mixed), shot, height, weight, birth date and place, draft; AHL number from TheAHL |

A Missing row is ticked when its bio is complete enough to create him.

### Phase E: who stays, who goes where (needs C + A3 + A4)

1. **Holds** (NHL Left rows, and AHL moves out of an NHL team): CapWages has him on that NHL team's roster or injured/inactive list, or DailyFaceoff has him in its lineup → held (hidden, never applied).
2. **Finding him on CapWages**: by full name on any team page (contracts, AHL deals, draft rights); else the same last name and birth date (Yegor / Egor Surin). A misspelled last name isn't matched: it's fixed in the base roster instead.
3. **CapWages player pages** (cached) for the remaining Left rows: the page of each non-roster player (minors included) and each unsigned pick, and for players no team page lists, the page their name most likely has (checked against the game's birth date).
4. **Placement** of every Left row (NHL and AHL):

| CapWages says | Result |
|---|---|
| In the minors, and his only club this season (or the latest) is an ECHL club the game has | Moved there, or held if he's already there |
| On that AHL team's NHL parent, in the minors or on an AHL deal, and he's already on that AHL team | **Held** there (TheAHL's list leaves out injured / just-assigned players) |
| In the minors / AHL deal with another team | Moved to that team's affiliate |
| On an NHL roster (AHL straggler) | Held; the CapWages call-up (Phase F) moves him |
| On another NHL team's roster (NHL Left) | Review: a trade NHL.com doesn't show yet |
| Junior / Loan / Europe, under contract | His latest club this season from his page's stats (NHL rows skipped); held if he's already there; parked with the affiliate (or held) when the game lacks the club |
| Unsigned draft pick (on a team's draft-rights list, or his page says Prospect) | Same as above: to his club if the game has it, else kept where he is (college, USHL, KHL) |
| No NHL team, status Inactive / Retired / UFA / Unsigned | **Released** |
| No NHL team, active, playing this season | To that club, or held if already there; released if his club isn't in the game |
| No page, but a namesake (same last name) on his team's CapWages page or AHL roster | Review |
| No page, and the game has a team holding his rights | Review |
| No page, no rights | **Released** |

### Phase F: everything else, against the rosters as Apply will leave them

On a **copy** of the game database: the ticked roster changes are applied and the ticked Missing players created (exactly as Apply will), then:

| # | Source | Check |
|---|---|---|
| F1 | DailyFaceoff + CapWages | **Call-ups**: lineup players the NHL list missed, and players CapWages has on an NHL roster / injured / suspended list whom no list places |
| F2 | CapWages | **Contracts**: each player's deal, clauses, two-way, extension; team retained salary; team buyouts |
| F3 | CapWages | **Positions**: primary/secondary from where he has played (forward ↔ defence switches go to Review) |
| F4 | CapWages | **Draft picks**: who owns each pick |
| F5 | Game files | **Name calls**: players with no play-by-play name who have a recording |
| F6 | DailyFaceoff | **Lines**: each NHL team's lines, PP, PK, goalie (a team whose lineup has a player not on it is left alone) |

Then the list is shown: **Review** (needs you) / everything else ticked automatically / held rows hidden.

Ticking a row re-runs only what it can affect (its teams' lines and call-ups; contracts and positions only if it creates a player or fixes a birth date).

---

## Apply (the only write)

One roster mod entry, in this order, each part re-compared fresh against the database being saved:

1. Roster changes: trades, call-ups, info (number, height, weight, birth date), Left moves and **releases** (off the team, no contract, on the free-agent list)
2. New players created (CapWages deal and positions written with them)
3. Call-ups from lineups / CapWages
4. Contracts (ticked, plus every new player's)
5. Positions (ticked, plus every new player's), before lines because lines are built by position
6. Name calls
7. Lines
8. Draft picks

Afterwards: retained-salary notes refreshed, then everything is compared again to show what's left.

---

## Gaps you raised (not built yet)

- **Existing players' bios aren't fully validated.** For players already in the game, only **number, height, weight and birth date** (plus positions and contracts via CapWages) are compared. Birthplace, country, shot/catches, draft year/round/pick/team and name spelling are only written for **new** players. Misspelled names are corrected in the base roster pack (Kurtz, Khazheev, William Zellers, 2026-11-10). Full validation of existing NHL players would need each player's NHL.com (or CapWages) page, ~800 more reads per sync (cached).
- **Existing NHL players' heights/weights** come from NHL.com's roster list; AHL players' from CapWages.
- The command-line `roster-sync` / `ahl-sync` still use TheAHL.com's details for AHL players.
