using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Google Sheets push/pull WITHOUT Google Cloud / OAuth. Instead of the MCP authenticating to Google's
// Sheets API, the user deploys a tiny bound Apps Script web app in their sheet (docs/google-sheets-webapp.gs:
// Extensions -> Apps Script -> Deploy -> Web app), and the MCP just POSTs JSON to that URL. The script
// runs AS the sheet owner, so it edits their private sheet; the MCP only needs the secret URL + a shared
// token. Portable: each user deploys their own script -> their own URL, no Cloud project, no verification.
//
// Config: HEXMANIAC_SHEETS_URL + HEXMANIAC_SHEETS_TOKEN, or <AppData>/HexManiacMcp/google/sheets.json
// ({"url": "...", "token": "..."}); the url can also be passed per call. The ROM side reuses RomAutomation
// so the row<->field shape matches read_table/export_table/write_value.
public static class GoogleSheetsService {
   private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

   private static string ConfigDir {
      get {
         var baseDir = Environment.GetEnvironmentVariable("HEXMANIAC_GOOGLE_DIR");
         if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HexManiacMcp", "google");
         Directory.CreateDirectory(baseDir);
         return baseDir;
      }
   }

   private static (string url, string token) Config(string urlOverride) {
      string url = string.IsNullOrWhiteSpace(urlOverride) ? Environment.GetEnvironmentVariable("HEXMANIAC_SHEETS_URL") : urlOverride;
      string token = Environment.GetEnvironmentVariable("HEXMANIAC_SHEETS_TOKEN");
      var cfg = Path.Combine(ConfigDir, "sheets.json");
      if ((string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) && File.Exists(cfg)) {
         try {
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (string.IsNullOrEmpty(url) && doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
            if (string.IsNullOrEmpty(token) && doc.RootElement.TryGetProperty("token", out var t)) token = t.GetString();
         } catch { }
      }
      return (url, token);
   }

   private static JsonElement Post(string url, object payload) {
      var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
      var resp = Http.PostAsync(url, content).GetAwaiter().GetResult();            // HttpClient follows Apps Script's 302 to the result
      var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
      try { return JsonDocument.Parse(body).RootElement.Clone(); }
      catch { throw new InvalidOperationException($"Web app didn't return JSON (HTTP {(int)resp.StatusCode}). Check the deployment is a Web app with access 'Anyone with the link'. First 200 chars: {body[..Math.Min(200, body.Length)]}"); }
   }

   public static object PushTable(IDataModel model, string table, string tab, string urlOverride) {
      var (url, token) = Config(urlOverride);
      if (string.IsNullOrEmpty(url)) return RomAutomation.Err("No web-app URL. Set HEXMANIAC_SHEETS_URL (or <config>/sheets.json), or pass url. See docs/GOOGLE-SHEETS.md.");
      if (RomAutomation.ReadTable(model, table, 0, int.MaxValue) is not IDictionary<string, object?> read) return RomAutomation.Err("read failed");
      if (read.ContainsKey("error")) return read;
      var fields = ((System.Collections.IEnumerable)read["fields"]!).Cast<object>().Select(f => f?.ToString() ?? "").ToList();
      // 'index' (the ROM row id) isn't one of the table's fields - it's a separate key on each row - but it
      // MUST be the first column so sheet_pull can match rows back. Prepend it; 'slug' is already in fields.
      var columns = new List<string> { "index" };
      columns.AddRange(fields);
      var values = new List<List<object?>> { columns.Cast<object?>().ToList() };
      foreach (var rObj in (System.Collections.IEnumerable)read["rows"]!) {
         var row = (IDictionary<string, object?>)rObj;
         values.Add(columns.Select(f => row.TryGetValue(f, out var v) ? Cell(v) : "").ToList());
      }
      var res = Post(url, new { action = "push", token, tab, values });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["tab"] = tab, ["rowsWritten"] = values.Count - 1, ["columns"] = columns.Count };
   }

   public static object PullTable(IDataModel model, Func<ModelDelta> changeToken, string table, string tab, string urlOverride) {
      var (url, token) = Config(urlOverride);
      if (string.IsNullOrEmpty(url)) return RomAutomation.Err("No web-app URL. Set HEXMANIAC_SHEETS_URL (or <config>/sheets.json), or pass url. See docs/GOOGLE-SHEETS.md.");
      var res = Post(url, new { action = "pull", token, tab });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      if (!res.TryGetProperty("values", out var grid) || grid.ValueKind != JsonValueKind.Array || grid.GetArrayLength() < 2)
         return RomAutomation.Err("Sheet has no data rows (need a header row plus at least one record). Push first to lay it out.");

      var data = grid.EnumerateArray().Select(r => r.EnumerateArray().Select(CellStr).ToList()).ToList();
      var headers = data[0];
      int indexCol = headers.FindIndex(h => h.Equals("index", StringComparison.OrdinalIgnoreCase));
      if (indexCol < 0) return RomAutomation.Err("The sheet needs an 'index' column (the ROM row id) to match rows back. Push first to get the layout.");
      var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "slug" };  // key + read-only derived

      // current ROM values (same display form sheet_push wrote), so we only write cells that ACTUALLY
      // changed. Writing every cell back (10k+ for big tables) is needless and can exhaust the host;
      // a normal edit touches a handful.
      var current = new Dictionary<int, Dictionary<string, string>>();
      if (RomAutomation.ReadTable(model, table, 0, int.MaxValue) is IDictionary<string, object?> cur && !cur.ContainsKey("error")) {
         foreach (var rObj in (System.Collections.IEnumerable)cur["rows"]!) {
            var rd = (IDictionary<string, object?>)rObj;
            if (rd.TryGetValue("index", out var iv) && iv is int ci) {
               var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
               foreach (var kv in rd) m[kv.Key] = kv.Value?.ToString() ?? "";
               current[ci] = m;
            }
         }
      }

      int writes = 0, rowsTouched = 0, skipped = 0; var errors = new List<string>();
      for (int r = 1; r < data.Count; r++) {
         var row = data[r];
         if (row.Count <= indexCol || !int.TryParse(row[indexCol], out var index)) continue;
         current.TryGetValue(index, out var cur1);
         bool touched = false;
         for (int c = 0; c < headers.Count && c < row.Count; c++) {
            var field = headers[c];
            if (string.IsNullOrEmpty(field) || skip.Contains(field)) continue;
            if (cur1 != null && cur1.TryGetValue(field, out var old) && old == row[c]) { skipped++; continue; }  // unchanged
            var res2 = RomAutomation.WriteValue(model, changeToken, table, index, field, row[c]) as IDictionary<string, object?>;
            if (res2 != null && res2.ContainsKey("error")) { if (errors.Count < 10) errors.Add($"[{index}].{field}: {res2["error"]}"); }
            else { writes++; touched = true; }
         }
         if (touched) rowsTouched++;
      }
      return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["tab"] = tab, ["rowsUpdated"] = rowsTouched, ["cellsWritten"] = writes, ["cellsUnchanged"] = skipped, ["errors"] = errors };
   }

   // Push records from a JSON file (an export_* output) to a tab as a flattened READ-ONLY reference view:
   // lists are joined with ", ", nested objects become compact JSON. Not pulled back (columns are derived,
   // not raw table fields) - use sheet_push/sheet_pull for editable round-trips.
   public static object PushJson(string path, string key, string tab, string urlOverride, string explode, bool style, int freezeColumns) {
      var (url, token) = Config(urlOverride);
      if (string.IsNullOrEmpty(url)) return RomAutomation.Err("No web-app URL. Set HEXMANIAC_SHEETS_URL (or <config>/sheets.json), or pass url. See docs/GOOGLE-SHEETS.md.");
      if (!File.Exists(path)) return RomAutomation.Err($"File not found: {path}");
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      var root = doc.RootElement;
      JsonElement arr;
      if (string.IsNullOrWhiteSpace(key)) { if (root.ValueKind != JsonValueKind.Array) return RomAutomation.Err("Root isn't an array - pass key (the array property to push, e.g. 'moves')."); arr = root; }
      else if (!root.TryGetProperty(key, out arr) || arr.ValueKind != JsonValueKind.Array) return RomAutomation.Err($"'{key}' isn't an array in {Path.GetFileName(path)}.");

      var baseCols = new List<string>(); var seen = new HashSet<string>();
      foreach (var rec in arr.EnumerateArray())
         if (rec.ValueKind == JsonValueKind.Object)
            foreach (var p in rec.EnumerateObject()) if (seen.Add(p.Name)) baseCols.Add(p.Name);
      if (baseCols.Count == 0) return RomAutomation.Err("No object records to push.");

      // explode: turn a list-valued column (e.g. "flags") into one ✓/✗ column per distinct value.
      var explodeVals = new List<string>(); var evSeen = new HashSet<string>();
      bool doExplode = !string.IsNullOrWhiteSpace(explode) && baseCols.Contains(explode);
      if (doExplode) {
         foreach (var rec in arr.EnumerateArray())
            if (rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty(explode, out var lv) && lv.ValueKind == JsonValueKind.Array)
               foreach (var el in lv.EnumerateArray()) { var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText(); if (!string.IsNullOrEmpty(s) && evSeen.Add(s)) explodeVals.Add(s); }
         doExplode = explodeVals.Count > 0;
      }
      var explodeSet = new HashSet<string>(explodeVals);
      var columns = new List<string>();
      foreach (var col in baseCols) { if (doExplode && col == explode) columns.AddRange(explodeVals); else columns.Add(col); }

      var values = new List<List<object?>> { columns.Cast<object?>().ToList() };
      foreach (var rec in arr.EnumerateArray()) {
         var present = new HashSet<string>();
         if (doExplode && rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty(explode, out var lv2) && lv2.ValueKind == JsonValueKind.Array)
            foreach (var el in lv2.EnumerateArray()) present.Add(el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText());
         var row = new List<object?>();
         foreach (var oc in columns) {
            if (explodeSet.Contains(oc)) row.Add(present.Contains(oc) ? "✓" : "✗");
            else row.Add(rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty(oc, out var v) ? Flatten(v) : "");
         }
         values.Add(row);
      }
      // when styling, the MCP computes the WHOLE look (per-cell colors, alignments, widths) and ships it as
      // `format`; the web app just applies it. So tweaking the look is an MCP change - no Apps Script redeploy.
      object format = style ? BuildFormat(columns, values, freezeColumns) : null;
      var res = Post(url, new { action = "push", token, tab, values, format });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      return new Dictionary<string, object?> { ["ok"] = true, ["source"] = Path.GetFileName(path), ["key"] = key, ["tab"] = tab, ["rowsWritten"] = values.Count - 1, ["columns"] = columns.Count, ["explodedInto"] = doExplode ? explodeVals.Count : 0, ["styled"] = style, ["note"] = "Read-only reference view; not pulled back." };
   }

   // Canonical Pokemon type colors (HMA's type names + full-name aliases; Fairy for romhacks). Unknown -> fallback.
   private static readonly Dictionary<string, string> TypeColors = new(StringComparer.OrdinalIgnoreCase) {
      ["NORMAL"] = "#A8A878", ["FIGHT"] = "#C03028", ["FIGHTING"] = "#C03028", ["FLYING"] = "#A890F0",
      ["POISON"] = "#A040A0", ["GROUND"] = "#E0C068", ["ROCK"] = "#B8A038", ["BUG"] = "#A8B820",
      ["GHOST"] = "#705898", ["STEEL"] = "#B8B8D0", ["???"] = "#68A090", ["CURSE"] = "#68A090",
      ["FIRE"] = "#F08030", ["WATER"] = "#6890F0", ["GRASS"] = "#78C850",
      ["ELECTR"] = "#F8D030", ["ELECTRIC"] = "#F8D030", ["PSYCHC"] = "#F85888", ["PSYCHIC"] = "#F85888",
      ["ICE"] = "#98D8D8", ["DRAGON"] = "#7038F8", ["DARK"] = "#705848", ["FAIRY"] = "#EE99AC",
   };
   private const string TypeFallback = "#BFBFBF";
   private static string TypeColor(string v) => TypeColors.TryGetValue(v.Trim(), out var c) ? c : TypeFallback;
   private static string Contrast(string hex) {
      int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
      return (0.299 * r + 0.587 * g + 0.114 * b) > 150 ? "#000000" : "#ffffff";
   }

   private static bool IsYes(string s) => s != null && s.StartsWith("✓");
   private static bool IsNo(string s) => s != null && (s.StartsWith("✗") || s.StartsWith("✘"));
   private static object BuildFormat(List<string> columns, List<List<object?>> values, int freezeColumns) {
      int nCols = columns.Count;
      int typeCol = columns.FindIndex(h => string.Equals(h, "type", StringComparison.OrdinalIgnoreCase));
      int idCol   = columns.FindIndex(h => string.Equals(h, "id #", StringComparison.OrdinalIgnoreCase));
      int moveCol = columns.FindIndex(h => string.Equals(h, "move", StringComparison.OrdinalIgnoreCase));
      // a blank-header column immediately before Type is the type-color SWATCH (a solid color block, no text)
      int swatchCol = (typeCol > 0 && string.IsNullOrWhiteSpace(columns[typeCol - 1])) ? typeCol - 1 : -1;

      var bg = new List<List<string>>(); var fc = new List<List<string>>();
      for (int r = 1; r < values.Count; r++) {
         var brow = new List<string>(nCols); var frow = new List<string>(nCols);
         string typeName = typeCol >= 0 ? values[r][typeCol] as string : null;
         for (int c = 0; c < nCols; c++) {
            var s = values[r][c] as string; string col;
            if (c == swatchCol && !string.IsNullOrEmpty(typeName)) { col = TypeColor(typeName); brow.Add(col); frow.Add(col); }      // solid swatch (bg==fg)
            else if (c == typeCol && swatchCol < 0 && !string.IsNullOrEmpty(s)) { col = TypeColor(s); brow.Add(col); frow.Add(Contrast(col)); } // no swatch -> color the text
            else if (IsYes(s)) { brow.Add("#d9ead3"); frow.Add("#38761d"); }
            else if (IsNo(s)) { brow.Add("#f4cccc"); frow.Add("#cc0000"); }
            else { brow.Add("#ffffff"); frow.Add("#000000"); }
         }
         bg.Add(brow); fc.Add(frow);
      }
      // alignment: ID # right, Move left, everything else centered
      var aligns = new List<string>(nCols);
      for (int c = 0; c < nCols; c++) aligns.Add(c == idCol ? "right" : c == moveCol ? "left" : "center");
      // the blank-header spacer columns: the type swatch is a thin bar (18px), other blanks (emblem) 36px;
      // those same columns also become collapsible groups (swatch tucks under Move, emblem under Category).
      var widths = new Dictionary<string, int>();
      var groups = new List<int>();
      for (int c = 0; c < nCols; c++)
         if (string.IsNullOrWhiteSpace(columns[c])) { widths[c.ToString()] = c == swatchCol ? 18 : 36; groups.Add(c); }

      return new {
         headerBold = true, headerBackground = "#efefef", headerAlign = "center", freezeHeader = true,
         freezeColumns = freezeColumns > 0 ? (object)freezeColumns : null,
         verticalAlign = "middle", backgrounds = bg, fontColors = fc, columnAligns = aligns,
         autoResize = true, widthPadding = 20, widthCap = 420,
         columnWidths = widths.Count > 0 ? (object)widths : null,
         columnGroups = groups.Count > 0 ? (object)groups : null,
      };
   }

   private static object? Flatten(JsonElement v) => v.ValueKind switch {
      JsonValueKind.String => v.GetString(),
      JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
      JsonValueKind.True => "✓",     // render booleans as ✓/✗ so style colors them green/red like flags
      JsonValueKind.False => "✗",
      JsonValueKind.Null => "",
      JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
      JsonValueKind.Object => v.GetRawText(),
      _ => v.GetRawText(),
   };

   private static string CellStr(JsonElement c) => c.ValueKind switch {
      JsonValueKind.String => c.GetString() ?? "",
      JsonValueKind.Null => "",
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      _ => c.GetRawText(),
   };
   private static object? Cell(object? v) => v switch {
      null => "",
      string or bool or int or long or double or float => v,
      _ => v.ToString(),
   };
}
