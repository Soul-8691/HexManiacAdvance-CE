using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using ModelContextProtocol.Server;

namespace HavenSoft.HexManiac.Mcp;

// MCP tools for reading/editing GBA Pokémon ROM data via HexManiac.Core.
//
// The RomSession parameter is supplied by dependency injection (a registered
// singleton); the remaining parameters are the tool's arguments. Each tool
// returns a JSON string with a "mode" field ("headless" here; "live" once the
// GuiBridge is added). Shared table logic lives in HexManiac.Core/RomAutomation.
[McpServerToolType]
public sealed class RomTools {
   // Relaxed encoder so apostrophes, <>, and non-ASCII (accents, gender symbols) come back
   // literally in tool responses (e.g. FARFETCH'D) instead of as \uXXXX escapes.
   private static readonly JsonSerializerOptions Json = new() {
      WriteIndented = false,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
   };

   [McpServerTool(Name = "open_rom")]
   [Description("Open a GBA ROM. Live: new GUI tab; headless: single session. For ROMs that are NOT a recognized base game (FireRed/Emerald/...) AND have no sidecar .toml, HexManiac guesses table offsets; under metadata='auto' this returns a warning with choices instead of opening. metadata: 'auto' (default) | 'find_toml' | 'guess_offsets' | 'guess'.")]
   public string OpenRom(RomSession session,
      [Description("Absolute path to a .gba ROM file")] string path,
      [Description("auto | find_toml | guess_offsets | guess")] string metadata = "auto") {
      string mode = GuiBridge.IsGuiRunning() ? "live" : "headless";
      string err(string m) => Stamp(JsonSerializer.Serialize(RomAutomation.Err(m), Json), mode);
      if (!File.Exists(path)) return err($"ROM not found: {path}");
      string gameCode;
      try { gameCode = ((IReadOnlyList<byte>)File.ReadAllBytes(path)).GetGameCode(); }
      catch (System.Exception ex) { return err($"Could not read ROM header: {ex.Message}"); }
      bool recognized = session.Singletons.GameReferenceTables.TryGetValue(gameCode, out _);
      var tomlPath = Path.ChangeExtension(path, ".toml");
      bool hasToml = File.Exists(tomlPath);

      switch (metadata) {
         case "guess_offsets":
            return err("Auto-detecting table offsets for an unrecognized ROM is not yet implemented. Please bug jmynes on GitHub or jordank.memes on Discord for this feature.");
         case "find_toml":
            if (!hasToml) return err($"No sidecar .toml found next to '{path}' (looked for '{tomlPath}'). Use metadata='guess' to open with guessed offsets, or metadata='guess_offsets'.");
            return OpenProceed(session, path, gameCode, recognized, "toml", File.ReadAllLines(tomlPath), false);
         case "guess":
            return OpenProceed(session, path, gameCode, recognized, hasToml ? "toml" : (recognized ? "builtin" : "guessed"), hasToml ? File.ReadAllLines(tomlPath) : null, warnGuess: !recognized && !hasToml);
         case "auto":
         default:
            if (recognized || hasToml)
               return OpenProceed(session, path, gameCode, recognized, hasToml ? "toml" : "builtin", hasToml ? File.ReadAllLines(tomlPath) : null, false);
            var warn = new Dictionary<string, object?> {
               ["ok"] = false, ["needsMetadataChoice"] = true, ["gameCode"] = gameCode,
               ["recognized"] = false, ["hasToml"] = false,
               ["warning"] = $"'{path}' is not a recognized base game (e.g. FireRed/Emerald) and has no sidecar .toml, so HexManiac would guess table offsets — reads and writes may be inaccurate.",
               ["options"] = new Dictionary<string, object?> {
                  ["find_toml"] = "Re-call open_rom with metadata='find_toml' to use a .toml placed next to the ROM.",
                  ["guess_offsets"] = "Re-call with metadata='guess_offsets' to auto-detect offsets (not yet implemented).",
                  ["guess"] = "Re-call with metadata='guess' to open anyway with guessed metadata.",
               },
            };
            return Stamp(JsonSerializer.Serialize(warn, Json), mode);
      }
   }

   private static string OpenProceed(RomSession session, string path, string gameCode, bool recognized, string metadataSource, string[]? tomlLines, bool warnGuess) {
      var p = new Dictionary<string, object?> { ["path"] = path };
      var json = Dispatch("open_rom", p, null, null, () => {
         if (tomlLines != null) session.Load(path, tomlLines); else session.Load(path);
         var model = session.Require();
         return new { ok = true, path, length = model.Count, anchorCount = model.Anchors.Count };
      });
      var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
      node["gameCode"] = gameCode;
      node["recognized"] = recognized;
      node["metadataSource"] = metadataSource;
      if (warnGuess) node["warning"] = "Opened with guessed metadata — this ROM is not a recognized base game and has no sidecar .toml; offsets may be inaccurate.";
      return node.ToJsonString();
   }

   [McpServerTool(Name = "list_open_roms")]
   [Description("List the ROMs currently open: the running GUI's tabs if a GUI is up, otherwise the headless-loaded ROM (or empty).")]
   public string ListOpenRoms(RomSession session) {
      var result = GuiBridge.TryCall("list_tabs", new Dictionary<string, object?>(), out var json, out _);
      if (result == GuiCallResult.Ok)
         return Stamp(json, "live");
      // For both Error and Unreachable, fall back to the headless tab listing.
      // A list_tabs error from the GUI is not worth surfacing here.
      object open = session.Model != null
         ? new { tabs = new[] { new { index = 0, file = session.RomPath, selected = true } } }
         : new { tabs = System.Array.Empty<object>() };
      return Stamp(JsonSerializer.Serialize(open, Json), "headless");
   }

   [McpServerTool(Name = "list_shortcuts")]
   [Description("List the GUI 'Goto' shortcut buttons (e.g. Pokemon, Trainers) as {display, anchor}. Targets the GUI's active tab when live; else headless.")]
   public string ListShortcuts(
      RomSession session,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      return Dispatch("list_shortcuts", p, tab, tabFile, () => RomAutomation.ListShortcuts(session.Require()));
   }

   [McpServerTool(Name = "goto")]
   [Description("Navigate the live GUI to a target: a shortcut label (e.g. Pokemon), an anchor name (e.g. data.pokemon.stats), or a hex address. Live GUI only; headless returns an error.")]
   public string Goto(
      RomSession session,
      [Description("Shortcut label, anchor name, or hex address to navigate to")] string target,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["target"] = target };
      return Dispatch("goto", p, tab, tabFile,
         () => RomAutomation.Err("goto requires the live GUI (no view to navigate in headless mode)."));
   }

   [McpServerTool(Name = "list_tables")]
   [Description("List the named data tables (anchors) in the open ROM. Targets the GUI's active tab when live; optional substring filter and tab selector.")]
   public string ListTables(
      RomSession session,
      [Description("Optional substring to filter anchor names")] string? filter = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(filter)) p["filter"] = filter;
      return Dispatch("list_tables", p, tab, tabFile, () => RomAutomation.ListTables(session.Require(), filter));
   }

   [McpServerTool(Name = "read_table")]
   [Description("Read a named table as JSON rows. Targets the GUI's active tab when live. Use start/count to page; tab/tabFile to pick a tab. For species-indexed tables, the ~25 placeholder/limbo slots (unused Unown-variant indices, not real species) are excluded by default and reported as excludedPlaceholders (pass includePlaceholders=true to keep them), and each row gains a canonical 'slug' (e.g. MR. MIME -> mr-mime, NIDORAN female/male -> nidoran-f/nidoran-m) plus 'forms'/'defaultForm' for multi-form species (Deoxys, Castform) to line up with external sources.")]
   public string ReadTable(
      RomSession session,
      [Description("Anchor/table name, e.g. data.pokemon.stats")] string name,
      [Description("First row index to return")] int start = 0,
      [Description("Maximum rows to return")] int count = 25,
      [Description("Include the placeholder/limbo species slots (default false: they are omitted from species-indexed tables)")] bool includePlaceholders = false,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["name"] = name, ["start"] = start, ["count"] = count, ["includePlaceholders"] = includePlaceholders };
      return Dispatch("read_table", p, tab, tabFile, () => RomAutomation.ReadTable(session.Require(), name, start, count, includePlaceholders));
   }

   [McpServerTool(Name = "write_value")]
   [Description("Set a field on a table row. value is a string (text/enum name), number (integer/enum index), or true/false. For a bit-array checkbox, pass flag=\"<name>\" with value true/false. Live GUI when present (visible+undoable); else headless.")]
   public string WriteValue(
      RomSession session,
      [Description("Anchor/table name")] string table,
      [Description("Row index")] int index,
      [Description("Field name within the row")] string field,
      [Description("New value: string, number, or true/false (type decided by the field)")] JsonElement value,
      [Description("Optional: name of one checkbox within a bit-array field")] string? flag = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var v = JsonToValue(value);
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["field"] = field, ["value"] = v };
      if (!string.IsNullOrEmpty(flag)) p["flag"] = flag;
      return Dispatch("write_value", p, tab, tabFile,
         () => RomAutomation.WriteValue(session.Require(), () => session.Token, table, index, field, v, flag));
   }

   [McpServerTool(Name = "undo")]
   [Description("Undo up to 'count' steps on the active tab's change history (same stack as Ctrl+Z). One step reverts the whole uncommitted batch of edits since the last commit boundary (a save_rom, run_script, or prior undo/redo), not necessarily a single write_value. Live GUI when present; else headless.")]
   public string Undo(RomSession session, [Description("How many steps to undo")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["count"] = count };
      return Dispatch("undo", p, tab, tabFile, () => HistoryHeadless(session, count, redo: false));
   }

   [McpServerTool(Name = "redo")]
   [Description("Redo up to 'count' steps on the active tab's change history (same stack as Ctrl+Y); a step replays a whole previously-undone batch. Live GUI when present; else headless.")]
   public string Redo(RomSession session, [Description("How many steps to redo")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["count"] = count };
      return Dispatch("redo", p, tab, tabFile, () => HistoryHeadless(session, count, redo: true));
   }

   private static object HistoryHeadless(RomSession session, int count, bool redo) {
      var vp = session.RequireViewPort();
      vp.ChangeHistory.ChangeCompleted();
      var cmd = redo ? vp.Redo : vp.Undo;
      int applied = 0;
      while (applied < count && cmd.CanExecute(null)) { cmd.Execute(null); applied++; }
      return new { ok = true, applied };
   }

   // Convert a JSON scalar argument to the CLR value the engine expects.
   private static object? JsonToValue(JsonElement v) => v.ValueKind switch {
      JsonValueKind.String => v.GetString(),
      JsonValueKind.Number => v.TryGetInt32(out var i) ? (object?)i : v.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => v.ToString(),
   };

   private static string _lastCopiedHex = "";

   [McpServerTool(Name = "copy_rows")]
   [Description("Copy 'count' table rows starting at 'index' as a hex byte string (cached for paste_rows). Live GUI when present; else headless.")]
   public string CopyRows(RomSession session, [Description("Anchor/table name")] string table,
      [Description("First row index")] int index, [Description("How many rows")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["count"] = count };
      var json = Dispatch("copy_rows", p, tab, tabFile, () => RomAutomation.CopyRows(session.Require(), table, index, count));
      try { var hex = System.Text.Json.Nodes.JsonNode.Parse(json)?["bytes"]?.GetValue<string>(); if (!string.IsNullOrEmpty(hex)) _lastCopiedHex = hex; } catch { }
      return json;
   }

   [McpServerTool(Name = "paste_rows")]
   [Description("Paste row bytes onto the table starting at 'index' (undoable). 'data' is hex; if omitted, uses the last copy_rows result. Live GUI when present; else headless.")]
   public string PasteRows(RomSession session, [Description("Anchor/table name")] string table,
      [Description("Destination row index")] int index, [Description("Hex bytes to paste (default: last copy_rows)")] string? data = null,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var hex = string.IsNullOrEmpty(data) ? _lastCopiedHex : data;
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["data"] = hex };
      return Dispatch("paste_rows", p, tab, tabFile, () => RomAutomation.PasteRows(session.Require(), () => session.Token, table, index, hex));
   }

   [McpServerTool(Name = "close_tab")]
   [Description("Close one tab in the live GUI (default: active tab). Refuses if the tab has unsaved changes unless force=true (which discards them). Live GUI only.")]
   public string CloseTab(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Discard unsaved changes")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("close_tab", p, tab, tabFile, () => RomAutomation.Err("close_tab requires the live GUI."));
   }

   [McpServerTool(Name = "close_rom")]
   [Description("Close ALL tabs showing the resolved tab's ROM in the live GUI. Refuses if any of those tabs has unsaved changes unless force=true (which discards them). Live GUI only.")]
   public string CloseRom(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Discard unsaved changes")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("close_rom", p, tab, tabFile, () => RomAutomation.Err("close_rom requires the live GUI."));
   }

   [McpServerTool(Name = "duplicate_tab")]
   [Description("Open a second tab on the same ROM as the resolved tab (like Ctrl+T) — shares the ROM's model and undo history; close_rom then closes all such tabs. Live GUI only.")]
   public string DuplicateTab(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("duplicate_tab", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("duplicate_tab requires the live GUI."));
   }

   [McpServerTool(Name = "clipboard_copy")]
   [Description("Copy the live GUI's current selection to the system clipboard; returns the copied text. Live GUI only.")]
   public string ClipboardCopy(RomSession session, [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("clipboard_copy", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("clipboard_copy requires the live GUI."));
   }

   [McpServerTool(Name = "clipboard_paste")]
   [Description("Paste the system clipboard at the live GUI's current selection (undoable). Live GUI only.")]
   public string ClipboardPaste(RomSession session, [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("clipboard_paste", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("clipboard_paste requires the live GUI."));
   }

   [McpServerTool(Name = "select")]
   [Description("Select rows in the live GUI: 'count' rows from 'index', or the whole table if 'index' is omitted. Live GUI only.")]
   public string Select(RomSession session, [Description("Anchor/table name")] string table,
      [Description("First row index (omit for the whole table)")] int? index = null, [Description("How many rows")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["table"] = table, ["count"] = count };
      if (index.HasValue) p["index"] = index.Value;
      return Dispatch("select", p, tab, tabFile, () => RomAutomation.Err("select requires the live GUI (no view to select in headless mode)."));
   }

   [McpServerTool(Name = "export_table")]
   [Description("Export an entire table (all rows, no paging) to a JSON file on disk. Targets the GUI's active tab when live; else headless. For species-indexed tables, the ~25 placeholder/limbo slots (unused Unown-variant indices, not real species) are excluded by default and reported as excludedPlaceholders (pass includePlaceholders=true to keep them), and each row gains a canonical 'slug' (e.g. MR. MIME -> mr-mime, NIDORAN female/male -> nidoran-f/nidoran-m) plus 'forms'/'defaultForm' for multi-form species (Deoxys, Castform) to line up with external sources.")]
   public string ExportTable(
      RomSession session,
      [Description("Anchor/table name to export")] string name,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Include the placeholder/limbo species slots (default false: they are omitted from species-indexed tables)")] bool includePlaceholders = false,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["name"] = name, ["outPath"] = outPath, ["includePlaceholders"] = includePlaceholders };
      return Dispatch("export_table", p, tab, tabFile,
         () => RomAutomation.ExportToFile(session.Require(), name, outPath, includePlaceholders));
   }

   [McpServerTool(Name = "export_trainers")]
   [Description("Export every trainer and their team to a JSON file: trainer-level fields plus each party member (level, species, IVs, held item, moves). Each member has a 'hardcodedMoves' flag; members WITHOUT hardcoded moves get the in-game default level-up moveset filled in (the last <=4 level-up moves at or below the mon's level), unless includeDefaultMoves=false. With includeUses=true (default), each trainer also gets a 'uses' array of every map-script trainerbattle (opcode 0x5C) reference: script offset, subtype, map bank/number/name, and the intro/win/lose dialogue; an empty array flags an unused/placeholder trainer. Targets the GUI's active tab when live; else headless.")]
   public string ExportTrainers(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Fill default level-up movesets for members without hardcoded moves (default true)")] bool includeDefaultMoves = true,
      [Description("Add a 'uses' array per trainer: every map-script trainerbattle reference with map context + battle dialogue (default true; off skips the script walk and omits the arrays)")] bool includeUses = true,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath, ["includeDefaultMoves"] = includeDefaultMoves, ["includeUses"] = includeUses };
      return Dispatch("export_trainers", p, tab, tabFile,
         () => TrainerTeamExport.Export(session.Require(), session.RequireViewPort().Tools.CodeTool.ScriptParser, outPath, includeDefaultMoves, includeUses));
   }

   [McpServerTool(Name = "export_script_encounters")]
   [Description("Export script-granted Pokemon that the wild/trainer/evolution tables miss: walks every top-level map script for givePokemon (gifts: starters, fossils, Eevee, the Magikarp sale, the Fighting-Dojo Hitmons - including 'givePokemon VAR' resolved from a one-time setvar), setwildbattle (statics: birds, Mewtwo, Snorlax), and the StartLegendaryBattle/InitRoamer specials (ticket legendaries / roamers). Each site has kind (gift/static/legendary/roaming), species, level, held item, map bank/number/name, and the script offset; output is also grouped 'bySpecies'. Also emits 'tradeLocations' (in-game trade index -> map, resolved from the CreateInGameTradePokemon special). Commands and specials are resolved BY NAME from the engine, so it works on other base games and romhacks. Targets the GUI's active tab when live; else headless.")]
   public string ExportScriptEncounters(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_script_encounters", p, tab, tabFile,
         () => EncounterScriptExport.Export(session.Require(), session.RequireViewPort().Tools.CodeTool.ScriptParser, outPath));
   }

   [McpServerTool(Name = "export_species_sources")]
   [Description("Cross-reference every species like HMA's 'Show Uses', for all species at once. Two buckets per species: tableRefs = array fields typed data.pokemon.names (anchor + field + index) - this is where give-command walks miss obtain sources like the starters (scripts.newgame.starters.*), in-game trades, evolutions, battle-tower prizes; scriptRefs = every map-script command with a species arg (generalizes givePokemon/setwildbattle to all species-typed commands) with command + map + offset. Literal args only (a species loaded into a variable first, e.g. some Game Corner prizes, is not resolved); the species' own movesets/trainer teams are omitted. Targets the GUI's active tab when live; else headless.")]
   public string ExportSpeciesSources(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_species_sources", p, tab, tabFile,
         () => SpeciesSourceExport.Export(session.Require(), session.RequireViewPort().Tools.CodeTool.ScriptParser, outPath));
   }

   [McpServerTool(Name = "export_coin_prizes")]
   [Description("Read coin-prize Pokemon (the Celadon Game Corner) live from the ROM. Their cost isn't a numeric field - it's baked into the prize menu's option text ('<SPECIES> <n> COINS') in scripts.text.multichoice. This walks every multichoice option, keeps the ones that name a real species and end in COINS, and reports {species, speciesId, coins, optionText}. Works on an edited ROM (it reports that ROM's own prizes/costs). Targets the GUI's active tab when live; else headless.")]
   public string ExportCoinPrizes(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_coin_prizes", p, tab, tabFile,
         () => CoinPrizeExport.Export(session.Require(), outPath));
   }

   [McpServerTool(Name = "export_items")]
   [Description("Export every item from data.items.stats to a JSON file: id, name, price, the RESOLVED description text (read_table/export_table only give the raw description text-pointer; this decodes it the same way HMA's sidebar does), pocket (+ pocketId), hold effect + param, and type. Targets the GUI's active tab when live; else headless.")]
   public string ExportItems(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_items", p, tab, tabFile, () => ItemExport.Export(session.Require(), outPath));
   }

   [McpServerTool(Name = "export_moves")]
   [Description("Export every move to a JSON file: name, power, type, accuracy, pp, priority, effect chance, the RESOLVED description text, the effect NAME from HMA's own dropdown enum (e.g. SleepPrimary) + raw effectId, decoded target and flag lists, and TM/HM/tutor membership. Also emits the full effectId->name legend (moveEffects). TMs/HMs are items - pair with export_item_locations for where they're sold/found. Targets the GUI's active tab when live; else headless.")]
   public string ExportMoves(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_moves", p, tab, tabFile, () => MoveExport.Export(session.Require(), outPath));
   }

   [McpServerTool(Name = "export_move_tutors")]
   [Description("Export move-tutor NPC locations: walks map scripts for the move-tutor specials (SelectMoveTutorMon / ChooseMonForMoveTutor / DisplayMoveTutorMenu, resolved by name). Each site has the special, the taught move (back-scanned from VAR_0x8005/0x8004, or null when the tutor picks the move in script logic - e.g. the Cape Brink starter-move tutor), and map bank/number/name + script offset; also lists candidateTutorMoves (data.pokemon.moves.tutors). NOTE: vanilla FRLG locates only a couple; Emerald's Battle Frontier tutors are denser. Targets the GUI's active tab when live; else headless.")]
   public string ExportMoveTutors(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_move_tutors", p, tab, tabFile,
         () => MoveTutorLocationExport.Export(session.Require(), session.RequireViewPort().Tools.CodeTool.ScriptParser, outPath));
   }

   [McpServerTool(Name = "export_item_locations")]
   [Description("Export where each item is BOUGHT or FOUND by walking every top-level map script + signpost: pokemart product lists (kind=mart; the buy price is in data.items.stats), item-ball / found-item scripts (kind=field), NPC give-item scripts (kind=gift), raw additem commands (kind=scripted), and hidden-item signposts (kind=hidden). Each site has item, itemId, map bank/number/name, quantity, x/y (for field/hidden), and the script/event offset; output is also grouped 'byItem'. Commands are resolved BY NAME from the engine, so it works on other base games and romhacks. Targets the GUI's active tab when live; else headless.")]
   public string ExportItemLocations(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_item_locations", p, tab, tabFile,
         () => ItemLocationExport.Export(session.Require(), session.RequireViewPort().Tools.CodeTool.ScriptParser, outPath));
   }

   [McpServerTool(Name = "export_pokedex")]
   [Description("Export Pokedex flavor from data.pokedex.stats: per national-dex number, the category (e.g. 'Seed Pokémon'), height (m), weight (kg), and the dex entry text. Output is grouped 'byNationalDex'. Targets the GUI's active tab when live; else headless.")]
   public string ExportPokedex(
      RomSession session,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["outPath"] = outPath };
      return Dispatch("export_pokedex", p, tab, tabFile, () => PokedexExport.Export(session.Require(), outPath));
   }

   [McpServerTool(Name = "read_script")]
   [Description("Decode the script at an address to HMA's readable script text — the same listing the code sidebar shows (commands, '# address' section labels, and inlined {text}/movement). type selects the engine: 'xse' (default; overworld event scripts), 'battle', 'animation', 'ai'. Returns { address, type, length, script }. Works live (active tab) or headless.")]
   public string ReadScript(
      RomSession session,
      [Description("Script address as hex, e.g. '0x16C474' or '16C474'")] string address,
      [Description("Script engine: xse | battle | animation | ai (default xse)")] string type = "xse",
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["address"] = address, ["type"] = type };
      return Dispatch("read_script", p, tab, tabFile,
         () => RomAutomation.ReadScript(session.Require(), PickScriptParser(session.RequireViewPort().Tools.CodeTool, type), address, type));
   }

   // Select the script engine for read_script. Shared shape with the GUI's AutomationPipeServer.
   internal static ScriptParser PickScriptParser(CodeTool codeTool, string type) => (type ?? "xse").ToLowerInvariant() switch {
      "battle" => codeTool.BattleScriptParser,
      "animation" or "anim" => codeTool.AnimationScriptParser,
      "ai" or "trainerai" or "battleai" => codeTool.BattleAIScriptParser,
      _ => codeTool.ScriptParser,
   };

   [McpServerTool(Name = "run_script")]
   [Description("Run an HMA script. Provide inline 'script' text OR 'path' to a .hma file. Targets the GUI's active tab when live; else headless.")]
   public string RunScript(
      RomSession session,
      [Description("Inline HMA script text")] string script = "",
      [Description("Path to a .hma script file (takes precedence over inline text)")] string? path = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(script)) p["script"] = script;
      if (!string.IsNullOrEmpty(path)) p["path"] = path;
      return Dispatch("run_script", p, tab, tabFile, () => RunScriptHeadless(session, script, path));
   }

   private static object RunScriptHeadless(RomSession session, string script, string? path) {
      var vp = session.RequireViewPort();
      session.Errors.Clear();
      session.Messages.Clear();
      if (!string.IsNullOrEmpty(path)) {
         var full = Path.GetFullPath(path);
         if (!File.Exists(full)) return RomAutomation.Err($"Script file not found: {full}");
         vp.TryImport(new LoadedFile(full, File.ReadAllBytes(full)), session.FileSystem);
      } else if (!string.IsNullOrEmpty(script)) {
         vp.Edit(script);
      } else {
         return RomAutomation.Err("Provide 'script' text or 'path' to a .hma file.");
      }
      vp.ChangeHistory.ChangeCompleted();
      var errors = session.Errors.ToList();
      return new { ok = errors.Count == 0, errors, messages = session.Messages.ToList() };
   }

   [McpServerTool(Name = "run_python")]
   [Description("Run Python against the open ROM using HexManiacAdvance's built-in pythonnet engine (real CPython). The script can read AND edit; a final expression is returned (REPL-style). In scope: 'editor', 'model', 'table' (table['data.pokemon.stats']), and anchor groups so 'data.pokemon.stats[1].hp' works; print(...) is captured. Edits commit as ONE undo step (like write_value). DISCOVERY: call python_introspect first - python_introspect with no target lists namespaces; with a table anchor it returns the field schema. print(row) dumps a row's fields; a wrong field name's error lists the valid ones; model.Anchors lists every table. Note (pythonnet): C# extension methods are static-only, e.g. SomeClass.Method(obj, ...). Live GUI when present (visible+undoable); else headless. Returns { ok, result, prints[], error }.")]
   public string RunPython(
      RomSession session,
      [Description("Python source: statements plus an optional final expression to return")] string code,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["code"] = code };
      return Dispatch("run_python", p, tab, tabFile, () => {
         var (ok, result, prints, error) = session.RunPython(code);
         return new Dictionary<string, object?> { ["ok"] = ok, ["result"] = result, ["prints"] = prints, ["error"] = error };
      });
   }

   [McpServerTool(Name = "python_introspect")]
   [Description("Discover the Python object model for run_python (the agent's equivalent of the GUI autocomplete). No target: the top-level namespace { namespaces:[data, scripts, ...], globals:[editor, table, model], hint }. A table anchor (e.g. data.pokemon.stats): { kind:'table', count, fields:[{name,type,length,enumSource?}], sample } - sample is a dump of row 0. Any other expression (e.g. data.pokemon.stats[0]): { kind:'value', members:[dir() names], value }. Read-only (never edits). Live GUI when present; else headless.")]
   public string PythonIntrospect(
      RomSession session,
      [Description("A table anchor, a Python expression, or omit for the top-level namespace")] string? target = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(target)) p["target"] = target;
      return Dispatch("python_introspect", p, tab, tabFile, () => session.Introspect(target));
   }

   [McpServerTool(Name = "backup_rom")]
   [Description("Make a timestamped backup of the ROM (plus its .toml and matching .sav, if present) under a backups/ folder next to it. Live targets the resolved tab; else headless.")]
   public string BackupRom(RomSession session,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("backup_rom", new Dictionary<string, object?>(), tab, tabFile, () => {
         if (string.IsNullOrEmpty(session.RomPath)) return RomAutomation.Err("No ROM loaded to back up.");
         var stamp = System.DateTime.Now;
         var files = RomBackup.Create(session.RomPath, stamp);
         if (files.Count == 0) return RomAutomation.Err($"Nothing to back up (file not found: {session.RomPath}).");
         return new { ok = true, timestamp = stamp.ToString("yyyyMMdd-HHmmss"), files };
      });
   }

   [McpServerTool(Name = "save_rom")]
   [Description("Write the ROM to disk. Pass outPath to save a COPY there; pass overwrite=true (no outPath) to save over the loaded/open ROM. With neither, it refuses (won't silently overwrite the source). Live targets the resolved tab; else headless.")]
   public string SaveRom(
      RomSession session,
      [Description("Output path to save a COPY to")] string? outPath = null,
      [Description("Save over the loaded/open ROM in place (only used when outPath is omitted)")] bool overwrite = false,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["overwrite"] = overwrite };
      if (!string.IsNullOrEmpty(outPath)) p["outPath"] = outPath;
      return Dispatch("save_rom", p, tab, tabFile, () => SaveRomHeadless(session, outPath, overwrite));
   }

   private static object SaveRomHeadless(RomSession session, string? outPath, bool overwrite) {
      var model = session.Require();
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      IReadOnlyList<string> Backup(string target) =>
         File.Exists(target) ? RomBackup.Create(target, System.DateTime.Now) : System.Array.Empty<string>();
      if (!string.IsNullOrEmpty(outPath)) {
         var backed = Backup(outPath);
         File.WriteAllBytes(outPath, model.RawData);
         return new { ok = true, path = outPath, overwrote = false, length = model.RawData.Length, backedUp = backed };
      }
      if (!overwrite)
         return RomAutomation.Err("Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
      if (string.IsNullOrEmpty(session.RomPath)) return RomAutomation.Err("No loaded ROM path to overwrite.");
      var backed2 = Backup(session.RomPath);
      File.WriteAllBytes(session.RomPath, model.RawData);
      return new { ok = true, path = session.RomPath, overwrote = true, length = model.RawData.Length, backedUp = backed2 };
   }

   [McpServerTool(Name = "launch_rom")]
   [Description("Launch the ROM in your default GBA program (like HexManiacAdvance's play button) by shell-opening the on-disk file. Refuses if the ROM has unsaved edits unless force=true (launches the last-saved file). Returns the launched path. Live or headless.")]
   public string LaunchRom(RomSession session,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Launch the last-saved file even if there are unsaved edits")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("launch_rom", p, tab, tabFile, () => LaunchRomHeadless(session, force));
   }

   private static object LaunchRomHeadless(RomSession session, bool force) {
      var path = session.RomPath;
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
         return RomAutomation.Err("No saved ROM on disk to launch.");
      if (session.RequireViewPort().ChangeHistory.HasDataChange && !force)
         return RomAutomation.Err("ROM has unsaved changes; save first (save_rom), or pass force=true to launch the last-saved file on disk.");
      var full = Path.GetFullPath(path);
      try {
         HavenSoft.HexManiac.Core.NativeProcess.Start(full);
      } catch (System.ComponentModel.Win32Exception ex) {
         return RomAutomation.Err($"Could not launch '{full}': {ex.Message} (is a program associated with .gba?).");
      }
      return new { ok = true, launched = full };
   }

   [McpServerTool(Name = "help")]
   [Description("Show how to use this MCP. No topic: the full guide. topic: a tool name or section heading (e.g. 'write_value', 'lifecycle') returns that section. Works live or headless.")]
   public string Help([Description("Optional tool name or section heading")] string? topic = null) {
      var text = string.IsNullOrWhiteSpace(topic) ? Docs.Guide() : Docs.Section(topic);
      return Stamp(JsonSerializer.Serialize(new { ok = true, text }, Json), GuiBridge.IsGuiRunning() ? "live" : "headless");
   }

   [McpServerTool(Name = "reference")]
   [Description("Look up HexManiacAdvance's built-in reference docs (the data HMA itself uses for parsing/autocomplete). kind: script | battle | ai | animation | constants | arm | pcs | tables | doc (+ aliases like xse/thumb/text). query: optional case-insensitive filter, e.g. a command name ('applymovement'), a constant, or a table anchor. No kind: lists the kinds. For the loaded ROM's game-specific specials, use list_specials.")]
   public string Reference(
      [Description("Reference kind: script|battle|ai|animation|constants|arm|pcs|tables|doc")] string? kind = null,
      [Description("Optional case-insensitive substring filter (command/constant/anchor name)")] string? query = null,
      [Description("Max lines to return (default 400)")] int maxLines = 400) {
      return Stamp(JsonSerializer.Serialize(ReferenceDocs.Lookup(kind, query, maxLines), Json),
         GuiBridge.IsGuiRunning() ? "live" : "headless");
   }

   [McpServerTool(Name = "list_specials")]
   [Description("List the script 'specials' for the open ROM (resolved by name, so romhack-aware). A script calls these as `special <index>`; the index is the position in this list. Optional substring filter. Pair with reference(kind=script) for command syntax.")]
   public string ListSpecials(
      RomSession session,
      [Description("Optional substring to filter special names")] string? filter = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(filter)) p["filter"] = filter;
      return Dispatch("list_specials", p, tab, tabFile, () => RomAutomation.ListSpecials(session.Require(), filter));
   }

   [McpServerTool(Name = "export_sprite")]
   [Description("Export a sprite to an indexed PNG (palette indices preserved exactly; index 0 transparent). sprite: anchor name or hex address (e.g. graphics.pokemon.sprites.front or 0x2350F0). palette: optional explicit palette anchor/address (default: HMA auto-pairs it). page/palettePage select a frame / palette page for multi-page runs. Edit the PNG and bring it back with import_sprite.")]
   public string ExportSprite(
      RomSession session,
      [Description("Sprite anchor name or hex address")] string sprite,
      [Description("Output .png file path")] string outPath,
      [Description("Optional explicit palette anchor/address (default: auto-paired)")] string? palette = null,
      [Description("Sprite page/frame index (default 0)")] int page = 0,
      [Description("Palette page index (default 0)")] int palettePage = 0,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["sprite"] = sprite, ["outPath"] = outPath, ["page"] = page, ["palettePage"] = palettePage };
      if (!string.IsNullOrEmpty(palette)) p["palette"] = palette;
      return Dispatch("export_sprite", p, tab, tabFile, () => SpriteIO.ExportSprite(session.Require(), sprite, palette, page, palettePage, outPath));
   }

   [McpServerTool(Name = "import_sprite")]
   [Description("Write a PNG into a sprite in the ROM (one undo step). The image must match the sprite's pixel dimensions (export it first to see them). An INDEXED PNG uses its indices directly (pass applyPalette=true to also write its PLTE into the paired palette); a truecolor PNG is nearest-matched to the sprite's palette. Live GUI when present (visible+undoable); else headless.")]
   public string ImportSprite(
      RomSession session,
      [Description("Sprite anchor name or hex address")] string sprite,
      [Description("Input .png file path")] string inPath,
      [Description("Sprite page/frame index (default 0)")] int page = 0,
      [Description("Also write the PNG's palette into the paired palette (indexed PNG only)")] bool applyPalette = false,
      [Description("Optional explicit palette anchor/address (default: auto-paired)")] string? palette = null,
      [Description("Palette page index (default 0)")] int palettePage = 0,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["sprite"] = sprite, ["inPath"] = inPath, ["page"] = page, ["applyPalette"] = applyPalette, ["palettePage"] = palettePage };
      if (!string.IsNullOrEmpty(palette)) p["palette"] = palette;
      return Dispatch("import_sprite", p, tab, tabFile,
         () => SpriteIO.ImportSprite(session.Require(), () => session.Token, sprite, inPath, page, applyPalette, palette, palettePage));
   }

   [McpServerTool(Name = "export_palette")]
   [Description("Export a palette to JSON ({address, bits, pages, palettes:[{page, colors:[#RRGGBB,...]}]}). palette: anchor name or hex address. page: a single page, or -1 (default) for all pages.")]
   public string ExportPalette(
      RomSession session,
      [Description("Palette anchor name or hex address")] string palette,
      [Description("Output .json file path")] string outPath,
      [Description("Palette page, or -1 for all (default)")] int page = -1,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["palette"] = palette, ["outPath"] = outPath, ["page"] = page };
      return Dispatch("export_palette", p, tab, tabFile, () => SpriteIO.ExportPalette(session.Require(), palette, page, outPath));
   }

   [McpServerTool(Name = "import_palette")]
   [Description("Write colors into a palette page (one undo step). Provide `colors` (a list of #RRGGBB) OR `inPath` to a palette JSON from export_palette. Colors are quantized to GBA 5-bit/channel. Live GUI when present; else headless.")]
   public string ImportPalette(
      RomSession session,
      [Description("Palette anchor name or hex address")] string palette,
      [Description("Palette page to write (default 0)")] int page = 0,
      [Description("Colors as #RRGGBB strings (omit if using inPath)")] string[]? colors = null,
      [Description("Path to a palette JSON from export_palette (omit if using colors)")] string? inPath = null,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["palette"] = palette, ["page"] = page };
      if (colors != null) p["colors"] = colors;
      if (!string.IsNullOrEmpty(inPath)) p["inPath"] = inPath;
      return Dispatch("import_palette", p, tab, tabFile,
         () => SpriteIO.ImportPalette(session.Require(), () => session.Token, palette, page, colors, inPath));
   }

   [McpServerTool(Name = "sheet_push")]
   [Description("Push a ROM table to a Google Sheet via a bound Apps Script web app (no Google Cloud/OAuth): a header row of field names then one row per record (clears the tab first). table: anchor name (e.g. data.pokemon.stats). tab: sheet/tab name (default: first tab). url: the web-app URL (default: HEXMANIAC_SHEETS_URL / <config>/sheets.json). Deploy the script from docs/google-sheets-webapp.gs - see docs/GOOGLE-SHEETS.md. Requires an open ROM (headless).")]
   public string SheetPush(
      RomSession session,
      [Description("ROM table/anchor name, e.g. data.pokemon.stats")] string table,
      [Description("Sheet/tab name (default: first tab)")] string? tab = null,
      [Description("Apps Script web-app URL (default: configured HEXMANIAC_SHEETS_URL)")] string? url = null) {
      if (session.Model == null) return Stamp(JsonSerializer.Serialize(RomAutomation.Err("Open a ROM first (open_rom)."), Json), "headless");
      try { return Stamp(JsonSerializer.Serialize(GoogleSheetsService.PushTable(session.Require(), table, tab ?? "", url ?? ""), Json), "headless"); }
      catch (System.Exception ex) { return Stamp(JsonSerializer.Serialize(RomAutomation.Err(ex.Message), Json), "headless"); }
   }

   [McpServerTool(Name = "sheet_pull")]
   [Description("Pull a Google Sheet tab back into a ROM table as ONE undo step, via the bound Apps Script web app. The sheet's header row maps columns to fields and must include an 'index' column (the ROM row id) to match rows - run sheet_push first to get the layout. 'slug'/'index' are treated as read-only keys. table/tab/url as in sheet_push. Requires an open ROM (headless).")]
   public string SheetPull(
      RomSession session,
      [Description("ROM table/anchor name, e.g. data.pokemon.stats")] string table,
      [Description("Sheet/tab name (default: first tab)")] string? tab = null,
      [Description("Apps Script web-app URL (default: configured HEXMANIAC_SHEETS_URL)")] string? url = null) {
      if (session.Model == null) return Stamp(JsonSerializer.Serialize(RomAutomation.Err("Open a ROM first (open_rom)."), Json), "headless");
      try { return Stamp(JsonSerializer.Serialize(GoogleSheetsService.PullTable(session.Require(), () => session.Token, table, tab ?? "", url ?? ""), Json), "headless"); }
      catch (System.Exception ex) { return Stamp(JsonSerializer.Serialize(RomAutomation.Err(ex.Message), Json), "headless"); }
   }

   [McpServerTool(Name = "sheet_push_json")]
   [Description("Push records from a JSON file (e.g. an export_moves / export_items / export_pokedex output) to a Google Sheet tab as a flattened, READ-ONLY reference view: scalars as-is, lists joined with ', ', nested objects as compact JSON. path: the .json file. key: the array property to push (e.g. 'moves', 'items', 'pokemon'); omit if the file's root is itself an array. tab + url as in sheet_push. explode: a list-valued column to split into one ✓/✗ column per distinct value (e.g. 'flags'). style=true makes it doc-ready: bold+frozen header, ✓/✗ cells colored green/red and centered, auto-sized columns (needs the latest docs/google-sheets-webapp.gs deployed). NOT pulled back (columns are derived). Does not require an open ROM.")]
   public string SheetPushJson(
      [Description("Path to the .json file (an export_* tool's output)")] string path,
      [Description("Array property to push, e.g. 'moves' / 'items' / 'pokemon' (omit if the root is an array)")] string? key = null,
      [Description("Sheet/tab name (default: first tab)")] string? tab = null,
      [Description("A list-valued column to split into one ✓/✗ column per distinct value, e.g. 'flags'")] string? explode = null,
      [Description("Style the sheet for docs (bold/frozen header, type/category/✓✗ colors, auto-width)")] bool style = false,
      [Description("Freeze the first N columns so they stay visible when scrolling right (0 = none)")] int freezeColumns = 0,
      [Description("Apps Script web-app URL (default: configured HEXMANIAC_SHEETS_URL)")] string? url = null) {
      try { return Stamp(JsonSerializer.Serialize(GoogleSheetsService.PushJson(path, key ?? "", tab ?? "", url ?? "", explode ?? "", style, freezeColumns), Json), "headless"); }
      catch (System.Exception ex) { return Stamp(JsonSerializer.Serialize(RomAutomation.Err(ex.Message), Json), "headless"); }
   }

   [McpServerTool(Name = "supported_roms")]
   [Description("Reference of the Pokemon GBA base games HexManiacAdvance supports: header codes, No-Intro names, md5/sha1/crc32, and support tier. No code: the whole reference. code (e.g. 'BPRE0' or 'bpre'): matching entries.")]
   public string SupportedRoms_([Description("Optional header code filter, e.g. BPRE0")] string? code = null) {
      var text = string.IsNullOrWhiteSpace(code) ? SupportedRoms.Json() : SupportedRoms.Lookup(code);
      return Stamp(JsonSerializer.Serialize(new { ok = true, text }, Json), GuiBridge.IsGuiRunning() ? "live" : "headless");
   }

   [McpServerTool(Name = "identify_rom")]
   [Description("Identify the loaded/open ROM: header game code, base game + revision, HMA support level, and whether it's a clean No-Intro dump or an edited/romhack (and what base it's built on). Hashes the in-memory ROM (unsaved edits read as not-clean).")]
   public string IdentifyRom(RomSession session,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("identify_rom", new Dictionary<string, object?>(), tab, tabFile, () => IdentifyResult(session.Require()));
   }

   private static object IdentifyResult(IDataModel model) {
      var bytes = model.RawData is byte[] b ? b : model.RawData.ToArray();
      var code = model.GetGameCode();
      var md5 = string.Concat(System.Security.Cryptography.MD5.HashData(bytes).Select(x => x.ToString("x2")));
      var sha1 = string.Concat(System.Security.Cryptography.SHA1.HashData(bytes).Select(x => x.ToString("X2")));
      var crc32 = Force.Crc32.Crc32Algorithm.Compute(bytes).ToString("X8");
      return SupportedRoms.Match(code, md5, sha1, crc32);
   }

   // ---- helpers ----

   // Prefer the live GUI (if reachable) for `method`; fall back to a headless
   // computation only when the GUI is truly unreachable. A live error response
   // is surfaced directly (mode:"live") rather than discarded.
   private static string Dispatch(string method, Dictionary<string, object?> liveParams, int? tab, string? tabFile, System.Func<object> headless) {
      if (tab.HasValue) liveParams["tab"] = tab.Value;
      else if (!string.IsNullOrEmpty(tabFile)) liveParams["tab"] = tabFile;
      var result = GuiBridge.TryCall(method, liveParams, out var json, out var gerror);
      return result switch {
         GuiCallResult.Ok          => Stamp(json, "live"),
         GuiCallResult.Error       => Stamp(JsonSerializer.Serialize(RomAutomation.Err(gerror), Json), "live"),
         _  /* Unreachable */      => Stamp(JsonSerializer.Serialize(headless(), Json), "headless"),
      };
   }

   // Serialize a result object and stamp mode:"headless".
   private static string Headless(object result) => Stamp(JsonSerializer.Serialize(result, Json), "headless");

   // Parse a JSON object string and add/overwrite a "mode" field. Re-serializes with the
   // relaxed encoder (Json), so this is the single spot that controls escaping for every
   // tool response, in both live and headless mode.
   internal static string Stamp(string json, string mode) {
      var node = JsonNode.Parse(json)!.AsObject();
      node["mode"] = mode;
      return node.ToJsonString(Json);
   }
}
