# HexManiacAdvance MCP

HexManiacAdvance MCP is a Model Context Protocol server that exposes the HexManiacAdvance GBA ROM editor as a set of AI-callable tools. It lets an LLM (or any MCP client) read and edit Pokémon GBA ROM data — tables, values, scripts, and more — via structured JSON-RPC calls. It operates in two modes: **live** (targeting an open HexManiacAdvance GUI) and **headless** (loading a ROM directly from disk). Every tool response includes a `mode` field so you always know which mode was active.

## Quick start

Add the server to your `.mcp.json`:

```json
{
  "mcpServers": {
    "hexmaniac": {
      "command": "artifacts/HexManiac.Mcp/bin/Release/net6.0/HexManiac.Mcp.exe",
      "args": []
    }
  }
}
```

After a rebuild, reconnect with `/mcp` in Claude Code. Then open a ROM and start calling tools:

```json
{"name": "open_rom", "arguments": {"path": "C:/roms/firered.gba"}}
{"name": "read_table", "arguments": {"name": "data.pokemon.stats", "start": 1, "count": 5}}
```

## Live vs headless

When the HexManiacAdvance GUI (built from this fork) is running, MCP tools automatically target its open tabs — this is **live mode** (`"mode": "live"`). Edits appear immediately in the GUI and are fully undoable there with Ctrl+Z.

When no GUI is running, tools fall back to **headless mode** (`"mode": "headless"`): `open_rom` loads a ROM from disk into an in-process session, and all reads/writes go through that session. Some tools are live-only (e.g. `goto`, `select`, `close_tab`) and return an error in headless mode.

Every tool response includes `"mode": "live"` or `"mode": "headless"` so you always know which mode was active.

Use `tab` (index) or `tabFile` (filename substring) to target a specific GUI tab when running live.

## Open a ROM first

Call `open_rom` with the absolute path to a `.gba` file before using any table tools in headless mode.

```json
{"name": "open_rom", "arguments": {"path": "C:/roms/firered.gba"}}
```

- **Live mode**: opens the ROM as a new tab in the running GUI.
- **Headless mode**: loads the ROM into the in-process session (replaces any previously loaded ROM).

**Recognized games** (FireRed BPRE, LeafGreen BPGE, Emerald BPEE, Ruby AXVE, Sapphire AXPE) load with full built-in metadata — tables, anchors, and type information are all known. The response includes `"recognized": true` and `"metadataSource": "builtin"`.

**Unrecognized ROMs** (no matching game code, no sidecar `.toml`) trigger a `needsMetadataChoice` warning under the default `metadata="auto"`. Re-call with one of:
- `metadata="find_toml"` — use a `.toml` file placed next to the ROM.
- `metadata="guess"` — open anyway with guessed offsets (reads/writes may be inaccurate).
- `metadata="guess_offsets"` — auto-detect offsets (not yet implemented).

## Reading data

Three tools expose table data:

**`list_tables`** — list all named anchors in the open ROM. Optionally filter by substring:

```json
{"name": "list_tables", "arguments": {"filter": "pokemon"}}
```

**`read_table`** — read rows as JSON. Use `start`/`count` for paging (default: first 25 rows):

```json
{"name": "read_table", "arguments": {"name": "data.pokemon.stats", "start": 0, "count": 10}}
```

Each row is a JSON object with one key per field (e.g. `hp`, `attack`, `type1`). Field values use their display form — enum fields show the name (e.g. `"FIRE"`), text fields show the string.

**`export_table`** — dump an entire table (all rows, no paging) to a JSON file on disk:

```json
{"name": "export_table", "arguments": {"name": "data.trainers.stats", "outPath": "C:/out/trainers.json"}}
```

### Placeholder / "limbo" species slots

Gen-3 species tables carry ~25 unused "limbo" slots — internal indices reused for
Unown-variant graphics (plus index 0, the `?????` slot), not real species. Their
name in the species table is blank or only `?`. `read_table` and `export_table`
**exclude these by default** from any species-indexed table (the species name table
itself and anything whose length is tied to it — stats, level-up moves, TM/tutor
compatibility, etc.). The response reports how many were dropped via
`excludedPlaceholders` and a `placeholderNote`. FireRed/LeafGreen/Emerald drop 26
slots, leaving the 386 real species.

To keep the placeholder rows, pass `includePlaceholders: true`:

```json
{"name": "export_table", "arguments": {"name": "data.pokemon.stats", "outPath": "C:/out/stats.json", "includePlaceholders": true}}
```

### Canonical species `slug` (names, genders, forms)

Rows of species-indexed tables also gain a canonical **`slug`** so ROM names line
up with external sources (e.g. PokeAPI) instead of looking like unique mons:

- punctuation collapses — `MR. MIME` → `mr-mime`, `FARFETCH'D` → `farfetchd`, `HO-OH` → `ho-oh`
- gender symbols stay distinct — `NIDORAN♀` → `nidoran-f`, `NIDORAN♂` → `nidoran-m`
- multi-form species also get a **`forms`** array — Deoxys → the four formes,
  Castform → the weather formes — and Deoxys additionally gets a **`defaultForm`**
  matching the open game (FireRed = `deoxys-attack`, LeafGreen = `deoxys-defense`,
  Emerald = `deoxys-speed`, Ruby/Sapphire = `deoxys-normal`).

```json
{"index": 122, "name": "MR. MIME", "slug": "mr-mime"}
{"index": 29,  "name": "NIDORAN♀", "slug": "nidoran-f"}
{"index": 386, "name": "DEOXYS", "slug": "deoxys", "forms": ["deoxys-normal","deoxys-attack","deoxys-defense","deoxys-speed"], "defaultForm": "deoxys-attack"}
```

### Dumping trainers (`export_trainers`)

`export_trainers` writes every trainer and their team to a JSON file. Each party
member carries a **`hardcodedMoves`** flag: when the trainer stores explicit moves
for it (`structType` bit 0) the moves are those exact stored entries (which may be
fewer than four). When `hardcodedMoves` is false, `moves` is the game's **default
level-up moveset** — the last ≤4 moves the species learns at or below the mon's
level — filled in for you (pass `includeDefaultMoves: false` to leave it off).

Each trainer also gets a **`uses`** array (on by default; pass `includeUses: false`
to omit it and skip the script walk). Every entry has a **`source`**:
- `"source": "script"` — a `trainerbattle` (opcode `0x5C`) reference in the game's
  map scripts (object events + map-header scripts): the command's `scriptOffset`
  (bare 6-digit hex address), the `subtype` (raw byte + name), the
  `mapBank`/`mapNumber`/`mapName` it belongs to, and the `introText`/`winText`/`loseText`
  decoded from the command's text-pointer args (`null` when that subtype carries none).
- `"source": "rematch"` — an entry in the rematch / VS-Seeker table
  (`data.trainers.vsseeker`): the `rematchIndex`, the `rematchSlots` the trainer fills
  (`match1`..`match6`), and the rematch `mapBank`/`mapNumber`/`mapName`. This is why
  rematch-only opponents no longer read as unused.

A trainer with an **empty `uses` array** is referenced by neither — the signal for an
unused/placeholder/RSE-leftover trainer. (Hand-written ASM references aren't covered.)

```json
{"name": "export_trainers", "arguments": {"outPath": "C:/out/trainers.json"}}
```

```json
{"index": 89, "name": "BEN", "uses": [
  {"source": "script", "scriptOffset": "1A93C9", "subtype": 0, "subtypeName": "single.battle",
   "mapBank": 3, "mapNumber": 21, "mapName": "ROUTE 3",
   "introText": "Hi!\nI like shorts!", "winText": "I don't believe it!", "loseText": null}
]}
```

```json
{"index": 101, "name": "BEN", "uses": [
  {"source": "rematch", "rematchIndex": 0, "rematchSlots": ["match2"],
   "mapBank": 3, "mapNumber": 21, "mapName": "ROUTE 3"}
]}
```

### Script-granted Pokemon (`export_script_encounters`)

`export_script_encounters` fills the gap the wild/trainer/evolution tables leave: the
species handed out or fought via **map scripts**. It walks every top-level map script
(object events + map-header scripts, same coverage as the trainer walk) for two commands:

- **`givePokemon` (0x79)** → `"kind": "gift"` — the fossils revived at Cinnabar, Eevee
  in Celadon, Lapras in Silph Co., the Magikarp salesman on Route 4, …
- **`setwildbattle` (0xB6)** → `"kind": "static"` — the legendary birds, Mewtwo, the
  sleeping Snorlax, the Power-Plant Electrodes, …

Each site has `species`/`speciesId`, `level`, `heldItem`, `mapBank`/`mapNumber`/`mapName`,
and the `scriptOffset`; the output also groups everything `bySpecies`. Mons given via a
`special` (the three starters, the Fighting-Dojo Hitmons) or `giveEgg` (Togepi) use other
opcodes and aren't captured.

```json
{"name": "export_script_encounters", "arguments": {"outPath": "C:/out/script-encounters.json"}}
```

```json
{"kind": "static", "speciesId": 150, "species": "MEWTWO", "level": 70, "heldItem": null,
 "mapBank": 1, "mapNumber": 74, "mapName": "CERULEAN CAVE", "scriptOffset": "16251D"}
```

### Cross-referencing a species (`export_species_sources`)

`export_species_sources` is HMA's **Show Uses** for species, run headlessly for every species at
once. Some Pokemon aren't given by a script command at all — the **starters** live in a *table*
(`scripts.newgame.starters.{left,middle,right}`), so a give-command walk can't see them. This tool
reports both reference kinds per species:

- **`tableRefs`** — every array field typed `data.pokemon.names` equal to the species
  (`anchor` + `field` + element `index`). Carries the starters, in-game trades, evolutions,
  battle-tower prizes, … (plus non-obtain refs like the Pokédex search index).
- **`scriptRefs`** — every map-script command with a species arg (the generalization of
  `export_script_encounters` from two opcodes to *all* species-typed commands — `givePokemon`,
  `setwildbattle`, `giveEgg`, …), with `command` + map + `scriptOffset`.

Literal args only: a species loaded into a variable before being given (e.g. some Game Corner
prizes) isn't resolved — that needs `setvar`→give data-flow tracing. The species' own
movesets/trainer teams are omitted (not obtain methods).

```json
{"name": "export_species_sources", "arguments": {"outPath": "C:/out/species-sources.json"}}
```

```json
{"species": "BULBASAUR",
 "tableRefs": [{"anchor": "scripts.newgame.starters.left", "field": "species1", "index": 0}],
 "scriptRefs": []}
```

### Obtain-method coverage (what these tools can and can't derive)

Together `data.pokemon.wild`, `export_script_encounters`, and `export_species_sources` **auto-derive**
most ways a species is obtained: **wild** (grass/surf/rock-smash/fishing), **gift** (literal
`givePokemon`), **static** (`setwildbattle`), **egg** (`giveEgg`), **starter**
(`scripts.newgame.starters.*`), **trade** (`data.pokemon.trades`), **evolution**
(`data.pokemon.evolutions`), and **breeding** (a base-form baby — itself in the *Undiscovered* egg
group — is bred from its obtainable adult + Ditto; so Pichu/Cleffa/Tyrogue/… are obtainable).

Two FRLG mechanisms are **beyond static cross-reference** — verified, not merely unimplemented:

- **Fighting-Dojo Hitmonlee/Hitmonchan** — there is no literal `givePokemon` and no literal
  `setvar` of their species anywhere in the ROM; the species are baked into an ASM `special`, so
  there is nothing in the script *data* to read.
- **Game-Corner prizes (Porygon, …)** — these *do* use a literal `givePokemon`, but it sits in a
  menu jump-table script the top-level walk can't reach. HMA's own "Show Uses > scripts" can't
  reach it either: both only follow `call`/`goto` pointer args, not menu jump tables.

The `setvar 0x8004, <species>` + `special` give pattern can't be mined generically — that same
`setvar` is used 340+ times, overwhelmingly to *display* a mon (cry / picture / message), with no
single "give-mon" special to key on.

A few of these *can* be read from the ROM after all:
- **Game-Corner costs** live in the prize menu's option *text* (`scripts.text.multichoice`, e.g.
  `"PORYGON 9,999 COINS"`) — **`export_coin_prizes`** parses species + cost out of it.
- **Ticket legendaries** use the named `StartLegendaryBattle` special with the species/level in
  `VAR_0x8004`/`VAR_0x8005` — `export_script_encounters` reads all three as `kind: "legendary"`
  (**Lugia** & **Ho-Oh** @ Navel Rock, **Deoxys** @ Birth Island). Ho-Oh's summit is a step-on
  coord-trigger event; the walk covers object events, **coord triggers, signposts**, and map-header
  scripts (matching HMA's own `GetAllTopLevelScripts`), so it's reached.
- **Gifted eggs** are the `giveEgg` command (Togepi) — already caught by the script walk.
- **Not catchable**: a `setwildbattle` handed to `StartMarowakBattle` is the Pokémon-Tower **ghost
  Marowak** (a forced plot battle), so it's excluded from statics; catchable statics use
  `dowildbattle` / `StartLegendaryBattle`.
- **Dojo Hitmons** and the **starters** use `setvar VAR_TEMP_1 <species>` then a shared
  `givePokemon VAR_TEMP_1` — `export_script_encounters` tracks `setvar` and resolves a variable-species
  `givePokemon` when that var was set exactly once in the walk (so **Hitmonlee/Hitmonchan @ Lv25** and
  the starters are detected; the Game-Corner prize menu, which sets the var many times, is left to
  `export_coin_prizes`).

What genuinely *can't* be read **on vanilla FireRed**: the **roaming beasts** — `InitRoamer` picks the
species in ASM by your starter, with no literal anywhere in the script (confirmed with `read_script`:
it's the last command of the post-game "Network Machine" script). The tool *does* detect a roamer when
the species is loaded into a var before `InitRoamer` (CFRU-style romhacks), but vanilla isn't, so it
stays in the small **curated override** —

> **Romhack-safety:** `export_script_encounters` resolves the command opcodes (`givePokemon`,
> `setwildbattle`, `setvar`, `special`) and the special indices (`StartLegendaryBattle`, `InitRoamer`)
> **by name** from the script engine + `GetOptions("specials")`, not as hardcoded bytes — so a hack
> that uses a different opcode map or specials table still works. (Variable space `0x4000`+ and the
> `VAR_0x8004/5/6` carry vars are stable Gen-3 conventions and stay constant.)
[`docs/firered-obtain-overrides.json`](firered-obtain-overrides.json) — which the dump merges as
`obtainCurated` kinds (`dojo` / `gameCorner` / `roaming` / `event`). After
that, the only species with no obtain route are genuinely FireRed-unobtainable: LeafGreen exclusives,
Johto/Hoenn species (trade-only post-National-Dex), and distribution-only events (Mew/Celebi/Jirachi).

**Mutually-exclusive ("pick one") groups:** the **starters** (1 of 3) are derivable from the three
`scripts.newgame.starters.*` tables; the **Mt. Moon fossils** (Kabuto / Omanyte — the choice is on
the fossil *items*; Aerodactyl/Old-Amber is separate), the **Dojo Hitmons**, and the **roaming beast**
(1 of Raikou/Entei/Suicune by starter) are real pick-one choices recorded in the override file.

## Editing values (write_value)

`write_value` sets a single field on a table row. The `value` parameter is interpreted by the field's type:

| Field type | Pass as `value` | Example |
|---|---|---|
| Text / name | string | `"Bulbasaur"` |
| Integer (power, hp, pp) | number | `120` |
| Enum (type, category) | enum name (string) or index (number) | `"FIRE"` or `10` |
| Bit-array checkbox | `true` / `false` + `flag="<name>"` | `true`, `flag="Makes Contact"` |

Examples:

```json
{"name": "write_value", "arguments": {"table": "data.pokemon.moves.names", "index": 19, "field": "name", "value": "SMOKETEST"}}
{"name": "write_value", "arguments": {"table": "data.pokemon.stats", "index": 1, "field": "hp", "value": 99}}
{"name": "write_value", "arguments": {"table": "data.pokemon.stats", "index": 6, "field": "type1", "value": "FIRE"}}
{"name": "write_value", "arguments": {"table": "data.pokemon.moves.stats.battle", "index": 1, "field": "info", "flag": "Makes Contact", "value": false}}
```

Bad enum values return a clear error listing the valid options. Flag names are matched case-insensitively, ignoring surrounding quotes that HexManiac uses for names with spaces.

Live mode: edits are visible in the GUI immediately and are undoable with Ctrl+Z or the `undo` tool.

## Navigation

**`list_shortcuts`** — list the GUI's "Goto" shortcut buttons as `{display, anchor}` pairs (e.g. Pokemon, Trainers, Moves, Items, Maps). Works in both live and headless.

```json
{"name": "list_shortcuts", "arguments": {}}
```

**`goto`** — navigate the active GUI tab to a target: a shortcut label (e.g. `Pokemon`), an anchor name (e.g. `data.pokemon.stats`), or a hex address. **Live GUI only** — returns an error in headless mode.

```json
{"name": "goto", "arguments": {"target": "data.pokemon.stats"}}
```

## Undo / redo, select, copy/paste

**`undo`** / **`redo`** — apply up to `count` steps on the active tab's change history (same stack as Ctrl+Z / Ctrl+Y). One step reverts/replays a whole uncommitted batch of edits. Works live and headless. Returns `{"applied": N}`.

```json
{"name": "undo", "arguments": {"count": 1}}
{"name": "redo", "arguments": {"count": 1}}
```

**`select`** — select `count` rows from `index` in the GUI (or the whole table if `index` is omitted). **Live only.**

```json
{"name": "select", "arguments": {"table": "data.pokemon.stats", "index": 1, "count": 3}}
```

**`copy_rows`** / **`paste_rows`** — copy `count` rows to a hex byte string (cached), then paste onto another row index (undoable). Clones rows. Works live and headless.

```json
{"name": "copy_rows", "arguments": {"table": "data.pokemon.stats", "index": 6, "count": 1}}
{"name": "paste_rows", "arguments": {"table": "data.pokemon.stats", "index": 151}}
```

**`clipboard_copy`** / **`clipboard_paste`** — drive the GUI's real Copy / Paste over the current selection, sharing the system clipboard with Ctrl+C / Ctrl+V. **Live only.**

## ROM/tab lifecycle

**`open_rom`** — open a `.gba` file. Live: new GUI tab; headless: single session ROM. See "Open a ROM first" for metadata options.

**`list_open_roms`** — list currently open ROMs: GUI tabs if live, otherwise the headless session ROM (or empty).

**`save_rom`** — write the ROM to disk. Two modes:
- Pass `outPath` to save a copy to a new path (safe).
- Pass `overwrite=true` (no `outPath`) to save over the loaded/open ROM in place.
- With neither, it refuses — it will not silently overwrite the source.

Before overwriting an existing file, `save_rom` automatically backs up the current `.gba`, its sidecar `.toml`, and its `.sav` into a timestamped `backups/` subdirectory next to the target. The result includes a `backedUp` list of the files written.

```json
{"name": "save_rom", "arguments": {"outPath": "C:/out/firered_edited.gba"}}
{"name": "save_rom", "arguments": {"overwrite": true}}
```

**`backup_rom`** — create an explicit backup of the loaded ROM (`.gba` + sidecar `.toml` + `.sav`) into a timestamped `backups/` subdirectory next to the ROM, without saving any pending edits. Returns the list of files written. Works live and headless.

```json
{"name": "backup_rom", "arguments": {}}
```

**`close_tab`** — close one tab in the live GUI (default: active tab). Refuses if the tab has unsaved changes unless `force=true` (which discards them). **Live only.**

**`close_rom`** — close ALL tabs showing the resolved tab's ROM. Same `force` semantics. **Live only.**

**`duplicate_tab`** — open a second tab on the same ROM (like Ctrl+T), sharing its model and undo history. **Live only.** `close_rom` then closes all such tabs at once.

**`launch_rom`** — shell-opens the resolved ROM's on-disk file in your default GBA program (like HexManiacAdvance's play button). The ROM must already be saved; pass `force=true` to launch the last-saved file even when there are unsaved edits in the current session. Returns `{ ok, launched, mode }` with the path that was opened. Works live and headless.

```json
{"name": "launch_rom", "arguments": {}}
{"name": "launch_rom", "arguments": {"force": true}}
```

## ROM reference

**`supported_roms`** — list the supported Pokémon GBA ROMs with their header codes, No-Intro names, and checksums. Call with no arguments to get all 9 ROMs, or pass `code` to look up a single entry:

```json
{"name": "supported_roms", "arguments": {}}
{"name": "supported_roms", "arguments": {"code": "BPRE0"}}
```

Does not require a ROM to be open. The same data is available as the `hexmaniac://supported-roms` MCP resource. Human-readable table: see [docs/SUPPORTED-ROMS.md](../docs/SUPPORTED-ROMS.md).

**`identify_rom`** — check the currently open ROM against known clean dumps. Returns:
- `isCleanDump` — `true` if MD5, SHA1, and CRC32 all match the known clean dump for this game.
- `baseGame` — the recognized base game (e.g. `"FireRed Rev 0 (v1.0)"`), or `null` if the header code is not recognized.
- `headerCode` — the 5-character code read from the ROM (e.g. `"BPRE0"`).

```json
{"name": "identify_rom", "arguments": {}}
```

This is useful for detecting whether a loaded ROM is an unmodified clean dump or a romhack derived from a known base game. A romhack will show `isCleanDump: false` but still report the correct `baseGame` (since HMA identifies by header code, not hash).

## Built-in reference docs

HexManiacAdvance ships the reference data it uses for parsing/autocomplete. Two tools expose it so you can look up command syntax, constants, anchors, and specials without guessing — useful before writing a script or searching the ROM.

**`reference`** — serve a bundled reference, optionally filtered. `kind`: `script` | `battle` | `ai` | `animation` | `constants` | `arm` | `pcs` | `tables` | `doc` (aliases: `xse`/`overworld`→script, `thumb`/`asm`→arm, `text`→pcs, `docs`/`tutorials`→doc, …). `query`: case-insensitive substring (a command name, constant, or anchor). No `kind` lists the kinds. Does not require an open ROM.

```json
{"name": "reference", "arguments": {"kind": "script", "query": "applymovement"}}
{"name": "reference", "arguments": {"kind": "tables", "query": "pokedex"}}
{"name": "reference", "arguments": {"kind": "doc"}}
```

**`list_specials`** — the script "specials" for the **open** ROM, resolved by name (romhack-aware). A script calls these as `special <index>`; the index is the position returned here. Optional `filter`.

```json
{"name": "list_specials", "arguments": {"filter": "Roamer"}}
```

## Sprites & palettes

Round-trip GBA graphics through standard files: sprites as **indexed PNG** (palette indices preserved exactly; index 0 transparent), palettes as **JSON** (`#RRGGBB`, quantized to GBA 5-bit on import). Address a sprite/palette by anchor name, hex address, or `table/index` (HMA goto syntax — follows the pointer in that element).

**`export_sprite`** — write a sprite to an indexed PNG; the palette is auto-paired (HMA's own logic) unless you pass an explicit `palette`. `page`/`palettePage` pick a frame / palette page.

```json
{"name": "export_sprite", "arguments": {"sprite": "graphics.pokemon.sprites.front/1", "outPath": "bulbasaur.png"}}
```

**`import_sprite`** — write a PNG back into a sprite (one undo step). Must match the sprite's pixel size. An indexed PNG uses its indices directly (`applyPalette: true` also writes its palette); a truecolor PNG is nearest-matched to the sprite's palette.

```json
{"name": "import_sprite", "arguments": {"sprite": "graphics.pokemon.sprites.front/1", "inPath": "bulbasaur.png"}}
```

**`export_palette`** / **`import_palette`** — a palette to/from JSON. `page: -1` (export) dumps all pages; import takes `colors` (a `#RRGGBB` list) or `inPath` to an exported file.

```json
{"name": "export_palette", "arguments": {"palette": "graphics.pokemon.palettes.normal/1", "outPath": "pal.json"}}
{"name": "import_palette", "arguments": {"palette": "graphics.pokemon.palettes.normal/1", "page": 0, "colors": ["#000000", "#F8F8F8"]}}
```

## Scripts

**`run_script`** — run an HMA script. Provide inline `script` text or a `path` to a `.hma` file (`path` takes precedence). Works live and headless.

```json
{"name": "run_script", "arguments": {"script": "#pokemon\n@data.pokemon.stats[1]/hp = 55"}}
{"name": "run_script", "arguments": {"path": "resources/Scripts/Add Mechanics From Later Generations/AnyGame_PixilateStyleAbilities.hma"}}
```

Returns `{"ok": true/false, "errors": [...], "messages": [...]}`.

## Python scripting (run_python / python_introspect)

`run_python` runs Python against the open ROM using HexManiacAdvance's built-in
pythonnet engine (real CPython). A script can read **and** edit; a trailing
expression is returned REPL-style, and `print(...)` output is captured.

In scope:
- `data.<group>.<table>` anchor groups, so `data.pokemon.stats[1].hp` works;
- `table['<anchor>']` (e.g. `table['data.pokemon.stats']`);
- `model` (the `IDataModel`) and `editor`.

Edits commit as **one undo step** (a single `undo`), exactly like `write_value`.

```json
{"name": "python_introspect", "arguments": {"target": "data.pokemon.stats"}}
{"name": "run_python", "arguments": {"code": "for mon in data.pokemon.stats:\n  mon.catchRate = 255\nlen(data.pokemon.stats)"}}
```

Returns `{ ok, result, prints, error }`.

**Discovery workflow (do this first).** You can't see the object model the way the
GUI's autocomplete shows it to a human, so query it:
- `python_introspect` with **no target** → the top-level namespace:
  `{ namespaces: [data, scripts, ...], globals: [editor, table, model], hint }`.
- `python_introspect` with a **table anchor** (e.g. `data.pokemon.stats`) →
  `{ kind: "table", count, fields: [{name, type, length, enumSource?}], sample }`
  (`sample` is a dump of row 0).
- `python_introspect` with **any other expression** (e.g. `data.pokemon.stats[0]`) →
  `{ kind: "value", members: [dir() names], value }`.
- In a script: `print(data.pokemon.stats[0])` dumps a row's fields; a wrong field
  name raises an error that lists the valid ones; `model.Anchors` lists every table.

`python_introspect` is read-only (it never edits).

**pythonnet note:** C# extension methods are static-only here (no
`clr.ImportExtensions`) — call them as `SomeClass.Method(obj, ...)`.

## Metadata & unrecognized ROMs

When `open_rom` is called with `metadata="auto"` (the default) on a ROM that is not a recognized base game and has no sidecar `.toml`, it returns a warning instead of opening:

```json
{"ok": false, "needsMetadataChoice": true, "gameCode": "ZZZZ", "recognized": false, ...}
```

Re-call with:
- `metadata="find_toml"` — expects a `.toml` file next to the ROM; fails if missing.
- `metadata="guess"` — opens the ROM with guessed metadata; reads/writes may be inaccurate.
- `metadata="guess_offsets"` — auto-detect offsets (not yet implemented; returns an error).

To supply your own metadata, place a `.toml` next to the ROM and use `metadata="find_toml"`.

## Saving & backups safety

`save_rom` will not silently overwrite the source ROM. Without an `outPath` or `overwrite=true`, it refuses with an error. This prevents accidental data loss when the AI calls `save_rom` without an explicit intent to overwrite.

**Auto-backup on overwrite:** when `save_rom` would overwrite an existing file (via `outPath` pointing to an existing path, or `overwrite=true`), it first copies the current `.gba`, its sidecar `.toml`, and its `.sav` into a timestamped `backups/` subdirectory next to the target (e.g. `backups/firered_20240615_123456.gba`). The result includes a `backedUp` list of the paths written. This means you can always recover the previous state even after an in-place save.

**`backup_rom`** — explicitly snapshot the loaded ROM without saving any edits. Copies the `.gba`, sidecar `.toml`, and `.sav` into a timestamped `backups/` subdirectory next to the ROM and returns the list of files written. Useful as a checkpoint before a risky batch of edits.

```json
{"name": "backup_rom", "arguments": {}}
```

Recommended workflow:
1. Open the ROM.
2. Optionally call `backup_rom` for a pre-edit checkpoint.
3. Edit with `write_value` / `run_script`.
4. Save a copy: `save_rom(outPath="path/to/output.gba")`.
5. Or save in place explicitly: `save_rom(overwrite=true)` — the previous state is auto-backed-up before overwriting.

## Recipes

**Rename a move (headless):**
```json
{"name": "open_rom", "arguments": {"path": "C:/roms/firered.gba"}}
{"name": "write_value", "arguments": {"table": "data.pokemon.moves.names", "index": 1, "field": "name", "value": "MYNAME"}}
{"name": "save_rom", "arguments": {"outPath": "C:/out/firered_edited.gba"}}
```

**Make a move Fire-type with 120 power:**
```json
{"name": "write_value", "arguments": {"table": "data.pokemon.moves.stats.battle", "index": 5, "field": "power", "value": 120}}
{"name": "write_value", "arguments": {"table": "data.pokemon.moves.stats.battle", "index": 5, "field": "type", "value": "FIRE"}}
```

**Clone a row (copy Bulbasaur stats to slot 152):**
```json
{"name": "copy_rows", "arguments": {"table": "data.pokemon.stats", "index": 1, "count": 1}}
{"name": "paste_rows", "arguments": {"table": "data.pokemon.stats", "index": 152}}
```

**Open and edit a second ROM (live, using tabFile):**
```json
{"name": "open_rom", "arguments": {"path": "C:/roms/emerald.gba"}}
{"name": "write_value", "arguments": {"table": "data.pokemon.stats", "index": 1, "field": "hp", "value": 60, "tabFile": "emerald"}}
```

**Toggle a move contact flag:**
```json
{"name": "write_value", "arguments": {"table": "data.pokemon.moves.stats.battle", "index": 1, "field": "info", "flag": "Makes Contact", "value": false}}
```

## Tools index

All 25 tools exposed by this MCP server:

| Tool | Description |
|---|---|
| `open_rom` | Open a GBA ROM. Live: new GUI tab; headless: single session. For ROMs that are NOT a recognized base game (FireRed/Emerald/...) AND have no sidecar .toml, HexManiac guesses table offsets; under metadata='auto' this returns a warning with choices instead of opening. metadata: 'auto' (default) \| 'find_toml' \| 'guess_offsets' \| 'guess'. |
| `list_open_roms` | List the ROMs currently open: the running GUI's tabs if a GUI is up, otherwise the headless-loaded ROM (or empty). |
| `list_shortcuts` | List the GUI 'Goto' shortcut buttons (e.g. Pokemon, Trainers) as {display, anchor}. Targets the GUI's active tab when live; else headless. |
| `goto` | Navigate the live GUI to a target: a shortcut label (e.g. Pokemon), an anchor name (e.g. data.pokemon.stats), or a hex address. Live GUI only; headless returns an error. |
| `list_tables` | List the named data tables (anchors) in the open ROM. Targets the GUI's active tab when live; optional substring filter and tab selector. |
| `list_specials` | List the script 'specials' for the open ROM (resolved by name, romhack-aware). A script calls these as `special <index>`; the index is the position in this list. Optional substring filter. |
| `reference` | Look up HMA's built-in reference docs (script/battle/ai/animation commands, constants, arm, pcs, tables, doc links). kind + optional case-insensitive query; no kind lists the kinds. Does not require an open ROM. |
| `export_sprite` | Export a sprite to an indexed PNG (palette indices preserved; index 0 transparent). Address by anchor, hex, or table/index; palette auto-paired (or explicit). page/palettePage for multi-page runs. |
| `import_sprite` | Write a PNG into a sprite (one undo step); must match the sprite's dimensions. Indexed PNG uses its indices (applyPalette also writes its palette); truecolor is nearest-matched. |
| `export_palette` | Export a palette to JSON ({address, bits, pages, palettes:[{page, colors:[#RRGGBB]}]}). page=-1 for all pages. |
| `import_palette` | Write colors into a palette page (one undo step). Provide a #RRGGBB list (colors) or inPath to an export_palette JSON; quantized to GBA 5-bit. |
| `read_table` | Read a named table as JSON rows. Targets the GUI's active tab when live. Use start/count to page; tab/tabFile to pick a tab. Species-indexed tables omit the ~25 placeholder/limbo slots by default (`excludedPlaceholders`, pass includePlaceholders=true to keep them) and add a canonical `slug` (+ `forms`/`defaultForm` for Deoxys/Castform). |
| `write_value` | Set a field on a table row. value is a string (text/enum name), number (integer/enum index), or true/false. For a bit-array checkbox, pass flag="<name>" with value true/false. Live GUI when present (visible+undoable); else headless. |
| `export_table` | Export an entire table (all rows, no paging) to a JSON file on disk. Targets the GUI's active tab when live; else headless. Species-indexed tables omit the ~25 placeholder/limbo slots by default (`excludedPlaceholders`, pass includePlaceholders=true to keep them) and add a canonical `slug` (+ `forms`/`defaultForm` for Deoxys/Castform). |
| `export_trainers` | Export every trainer and their team to a JSON file. Each party member has a `hardcodedMoves` flag; members without hardcoded moves get the in-game default level-up moveset filled in (includeDefaultMoves=false to skip). Each trainer also gets a `uses` array of every map-script `trainerbattle` (0x5C) reference — script offset, subtype, map bank/number/name, and intro/win/lose dialogue; an empty array means the trainer is unreferenced (includeUses=false to omit). |
| `export_script_encounters` | Export script-granted Pokemon the wild/trainer/evolution tables miss. Walks every top-level map script for `givePokemon` (gifts: starters, fossils, Eevee, the Magikarp sale, the Dojo Hitmons), `setwildbattle` (statics: birds, Mewtwo, Snorlax), and the `StartLegendaryBattle`/`InitRoamer` specials (ticket legendaries / roamers). Resolves `givePokemon VAR` from a one-time `setvar`. Each site has kind (gift/static/legendary/roaming), species, level, held item, map, script offset; also grouped `bySpecies`. **Commands + specials are resolved by name, so it works on other games/romhacks.** |
| `export_species_sources` | HMA "Show Uses" for every species at once: `tableRefs` (array fields typed `data.pokemon.names` — catches the starters in `scripts.newgame.starters.*`, trades, evolutions, prizes) + `scriptRefs` (every map-script command with a species arg, generalizing givePokemon/setwildbattle to all species-typed commands incl. `giveEgg`) with map + offset. Literal args only; var-loaded species (some Game Corner prizes) aren't resolved. |
| `export_coin_prizes` | Read coin-prize Pokemon (the Celadon Game Corner) live from the ROM. Their cost isn't a numeric field — it's baked into the prize menu's option text (`<SPECIES> <n> COINS`) in `scripts.text.multichoice`. Reports `{species, speciesId, coins, optionText}`; reflects an edited ROM's own prizes/costs. |
| `read_script` | Decode the script at an address to HMA's readable script text — the same listing the code sidebar shows (commands, `# address` section labels, inlined `{text}`/movement). `type`: `xse` (default, overworld event scripts) / `battle` / `animation` / `ai`. Returns `{ address, type, length, script }`. Live or headless. |
| `export_pokedex` | Export Pokédex flavor from `data.pokedex.stats`, grouped `byNationalDex`: category (e.g. "Seed Pokémon"), height (m), weight (kg), and the dex entry text (line breaks flattened). |
| `export_moves` | Export every move: name, power, type, accuracy, pp, priority, effect chance, resolved description, the effect NAME from HMA's `moveeffectoptions` enum (e.g. SleepPrimary) + raw effectId, decoded target/flags, and TM/HM/tutor membership. Also emits the full effectId→name legend (`moveEffects`). TMs/HMs are items — pair with `export_item_locations` for where they're sold/found. |
| `export_move_tutors` | Export move-tutor NPC locations: walks map scripts for the move-tutor specials (SelectMoveTutorMon/ChooseMonForMoveTutor/DisplayMoveTutorMenu, by name). Each site: special, taught move (back-scanned, or null when picked from a menu), map + script offset; plus candidateTutorMoves (data.pokemon.moves.tutors). FRLG = the Two Island tutor; Emerald's Battle Frontier tutors are denser. |
| `export_items` | Export every item from `data.items.stats`: id, name, price, the **resolved** description text (`read_table`/`export_table` give only the raw description text-pointer; this decodes it like HMA's sidebar), `pocketId`, hold effect + param, and type. |
| `export_item_locations` | Export where each item is **bought or found** by walking every top-level map script + signpost: `pokemart` product lists (kind=`mart`; the buy price is in `data.items.stats`), item-ball / found-item scripts (kind=`field`), NPC give-item scripts (kind=`gift`), raw `additem` (kind=`scripted`), and hidden-item signposts (kind=`hidden`). Each site has item, itemId, map bank/number/name, quantity, x/y (field/hidden), and the script/event offset; also grouped `byItem`. **Commands resolved by name, so it works on other games/romhacks.** |
| `run_script` | Run an HMA script. Provide inline 'script' text OR 'path' to a .hma file. Targets the GUI's active tab when live; else headless. |
| `run_python` | Run Python against the open ROM via HMA's pythonnet engine (real CPython). Reads AND edits; a trailing expression is returned, print(...) captured; edits commit as one undo step. In scope: editor, model, table['<anchor>'], and anchor groups (data.pokemon.stats[1].hp). Call python_introspect first to discover the model. Live or headless. Returns { ok, result, prints, error }. |
| `python_introspect` | Discover the run_python object model (the agent's autocomplete). No target: { namespaces, globals, hint }. A table anchor: { kind:"table", count, fields, sample }. Any expression: { kind:"value", members, value }. Read-only. Live or headless. |
| `undo` | Undo up to 'count' steps on the active tab's change history (same stack as Ctrl+Z). One step reverts the whole uncommitted batch of edits since the last commit boundary (a save_rom, run_script, or prior undo/redo), not necessarily a single write_value. Live GUI when present; else headless. |
| `redo` | Redo up to 'count' steps on the active tab's change history (same stack as Ctrl+Y); a step replays a whole previously-undone batch. Live GUI when present; else headless. |
| `select` | Select rows in the live GUI: 'count' rows from 'index', or the whole table if 'index' is omitted. Live GUI only. |
| `copy_rows` | Copy 'count' table rows starting at 'index' as a hex byte string (cached for paste_rows). Live GUI when present; else headless. |
| `paste_rows` | Paste row bytes onto the table starting at 'index' (undoable). 'data' is hex; if omitted, uses the last copy_rows result. Live GUI when present; else headless. |
| `clipboard_copy` | Copy the live GUI's current selection to the system clipboard; returns the copied text. Live GUI only. |
| `clipboard_paste` | Paste the system clipboard at the live GUI's current selection (undoable). Live GUI only. |
| `close_tab` | Close one tab in the live GUI (default: active tab). Refuses if the tab has unsaved changes unless force=true (which discards them). Live GUI only. |
| `close_rom` | Close ALL tabs showing the resolved tab's ROM in the live GUI. Refuses if any of those tabs has unsaved changes unless force=true (which discards them). Live GUI only. |
| `duplicate_tab` | Open a second tab on the same ROM as the resolved tab (like Ctrl+T) — shares the ROM's model and undo history; close_rom then closes all such tabs. Live GUI only. |
| `save_rom` | Write the ROM to disk. Pass outPath to save a COPY there; pass overwrite=true (no outPath) to save over the loaded/open ROM. With neither, it refuses (won't silently overwrite the source). Before overwriting an existing file, auto-backs-up the current .gba + .toml + .sav into a timestamped backups/ subdirectory; result includes backedUp list. Live targets the resolved tab; else headless. |
| `backup_rom` | Explicitly snapshot the loaded ROM (.gba + sidecar .toml + .sav) into a timestamped backups/ subdirectory next to the ROM, without saving any pending edits. Returns the list of files written. Live or headless. |
| `launch_rom` | Shell-open the resolved ROM's on-disk file in the default GBA program (like HexManiacAdvance's play button). The ROM must be saved; pass force=true to launch the last-saved file even with unsaved edits. Returns { ok, launched, mode }. Live or headless. |
| `supported_roms` | List the supported Pokémon GBA ROMs (header codes, No-Intro names, checksums). Pass code="BPRE0" to look up a single entry. Does not require an open ROM. |
| `sheet_push` | Push a ROM table to a Google Sheet via a bound Apps Script web app (no Google Cloud/OAuth) — header row of fields + one row per record; clears the tab. table + optional tab + url. Headless. See [docs/GOOGLE-SHEETS.md](GOOGLE-SHEETS.md). |
| `sheet_pull` | Pull a Google Sheet tab back into a ROM table as one undo step (via the same web app). Matches rows by the `index` column; writes only changed cells via write_value semantics. Headless. |
| `sheet_push_json` | Push records from a JSON file (e.g. an `export_moves`/`export_items` output) to a sheet tab as a flattened READ-ONLY reference view (resolved names/descriptions/flags; lists joined). path + key (the array property) + tab. Not pulled back. |
| `identify_rom` | Identify the currently open ROM: reports isCleanDump (MD5/SHA1/CRC32 match), baseGame (e.g. "FireRed Rev 0 (v1.0)"), and headerCode. Detects clean dumps vs romhacks of a known base. |
| `help` | Return the full MCP guide or a specific section. Call with no arguments for the full guide; pass topic="<section-heading>" for a specific section (e.g. topic="Saving & backups safety"). |

Call `help` with no arguments for the full guide, or `help(topic="<section>")` for a specific section.

## Troubleshooting

**Server not found / tools don't appear:**
After building the MCP project, reconnect with `/mcp` in Claude Code. The server is a stdio executable — the MCP client launches it automatically.

**"needsMetadataChoice" warning on open_rom:**
The ROM is not a recognized base game and has no sidecar `.toml`. Re-call with `metadata="guess"` to open anyway, or place a `.toml` next to the ROM and use `metadata="find_toml"`.

**Live vs headless confusion:**
Check the `mode` field in any tool response. If you expect `"live"` but see `"headless"`, the GUI is not running or is not built from this fork. Launch `artifacts/HexManiac.WPF/bin/Release/net6.0-windows/HexManiacAdvance.exe` and wait for it to start before calling tools.

**Edits not appearing in the GUI (live mode):**
Make sure `tab` or `tabFile` targets the correct tab. Use `list_open_roms` to see all open tabs and their indices.

**goto / select / clipboard tools return "live GUI only" error:**
These tools require the live GUI. They cannot operate in headless mode — there is no GUI view to navigate or select in.

**Rebuild and reconnect:**
If you rebuild the MCP server, the running process must be restarted. Claude Code will relaunch it automatically on the next `/mcp` reconnect.
