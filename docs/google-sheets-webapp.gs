/**
 * HexManiacAdvance MCP <-> Google Sheets bridge (no Google Cloud / OAuth needed).
 *
 * Setup (in the sheet you want to sync):
 *   1. Extensions -> Apps Script. Delete the stub, paste this whole file.
 *   2. Change SECRET below to a long random string of your own.
 *   3. Limit the script to THIS sheet only (not all your spreadsheets): Project Settings (gear) ->
 *      check "Show appsscript.json manifest file in editor", open appsscript.json, and add:
 *        "oauthScopes": ["https://www.googleapis.com/auth/spreadsheets.currentonly"]
 *      (per-file scope - the consent then says "only the specific spreadsheet you use this app with").
 *      This works because the script only touches getActiveSpreadsheet(); never use openById/openByUrl,
 *      which would force the all-spreadsheets scope.
 *   4. Deploy -> New deployment -> type "Web app":
 *        Execute as: Me        Who has access: Anyone with the link
 *      Authorize when prompted (this is the Apps Script editor's own consent - NOT Google Cloud).
 *   6. Copy the Web app URL.
 *   7. Tell the MCP: set HEXMANIAC_SHEETS_URL = that URL and HEXMANIAC_SHEETS_TOKEN = your SECRET,
 *      or write <AppData>/HexManiacMcp/google/sheets.json = {"url":"...","token":"..."}.
 *
 * The MCP POSTs {action, token, tab, values}. push writes the grid; pull returns it. The script runs
 * as you, so it edits your private sheet; the caller only needs the secret URL + token.
 */
var SECRET = 'change-me-to-a-long-random-string';

function doPost(e) {
  try {
    var req = JSON.parse(e.postData.contents);
    if (req.token !== SECRET) return json({ error: 'bad token' });

    var ss = SpreadsheetApp.getActiveSpreadsheet();
    var sheet = req.tab ? ss.getSheetByName(req.tab) : ss.getSheets()[0];

    if (req.action === 'pull') {
      if (!sheet) return json({ error: 'no such tab: ' + req.tab });
      return json({ ok: true, values: sheet.getDataRange().getValues() });
    }

    if (req.action === 'push') {
      if (!sheet) sheet = req.tab ? ss.insertSheet(req.tab) : ss.getSheets()[0];
      sheet.clear();   // wipe values AND old formatting so each push restyles cleanly
      var values = req.values || [];
      if (values.length > 0) {
        var cols = values[0].length;
        for (var i = 0; i < values.length; i++) {
          while (values[i].length < cols) values[i].push('');   // setValues needs equal-length rows
        }
        sheet.getRange(1, 1, values.length, cols).setValues(values);
        if (req.format) applyStyle(sheet, values, req.format);
      }
      return json({ ok: true, rows: Math.max(0, values.length - 1) });
    }

    return json({ error: 'unknown action: ' + req.action });
  } catch (err) {
    return json({ error: String(err) });
  }
}

// GENERIC style applier. The MCP computes the whole look (per-cell colors, alignments, widths, etc.) and
// sends it as `format`, so styling tweaks happen MCP-side and DON'T need this script re-deployed. This just
// applies whatever it's handed:
//   format = { headerBold, headerBackground, freezeHeader, verticalAlign,
//              backgrounds:[[..]], fontColors:[[..]],   // per data-row cell (rows 2..n)
//              columnAligns:["center"|"left"|..],        // per column
//              autoResize, widthPadding, widthCap }
function applyStyle(sheet, values, fmt) {
  if (!fmt) return;
  var nRows = values.length, nCols = values[0].length;

  if (fmt.headerBold) sheet.getRange(1, 1, 1, nCols).setFontWeight('bold');
  if (fmt.headerBackground) sheet.getRange(1, 1, 1, nCols).setBackground(fmt.headerBackground);
  if (fmt.freezeHeader) sheet.setFrozenRows(1);
  if (fmt.freezeColumns) sheet.setFrozenColumns(fmt.freezeColumns);   // keep the left N columns visible while scrolling right
  if (fmt.headerAlign) sheet.getRange(1, 1, 1, nCols).setHorizontalAlignment(fmt.headerAlign);
  if (fmt.verticalAlign) sheet.getRange(1, 1, nRows, nCols).setVerticalAlignment(fmt.verticalAlign);

  if (nRows > 1) {
    var data = sheet.getRange(2, 1, nRows - 1, nCols);
    if (fmt.backgrounds) data.setBackgrounds(fmt.backgrounds);
    if (fmt.fontColors) data.setFontColors(fmt.fontColors);
    if (fmt.columnAligns) {
      for (var c = 0; c < nCols && c < fmt.columnAligns.length; c++) {
        if (fmt.columnAligns[c]) sheet.getRange(2, c + 1, nRows - 1, 1).setHorizontalAlignment(fmt.columnAligns[c]);
      }
    }
  }

  if (fmt.autoResize) {
    var pad = fmt.widthPadding || 0, cap = fmt.widthCap || 100000;
    for (var c = 1; c <= nCols; c++) {
      sheet.autoResizeColumn(c);
      sheet.setColumnWidth(c, Math.min(sheet.getColumnWidth(c) + pad, cap));
    }
  }
  if (fmt.columnWidths) {   // explicit per-column widths (0-based index -> px), applied after autoResize
    for (var k in fmt.columnWidths) sheet.setColumnWidth(Number(k) + 1, fmt.columnWidths[k]);
  }
  if (fmt.columnGroups && fmt.columnGroups.length) {   // 0-based column indices to make collapsible groups
    try {
      sheet.setColumnGroupControlPosition(SpreadsheetApp.GroupControlTogglePosition.BEFORE);   // toggle on the left (parent) side
      for (var i = 0; i < fmt.columnGroups.length; i++) {
        var col = fmt.columnGroups[i] + 1;
        var d = 0; try { d = sheet.getColumnGroupDepth(col); } catch (e) {}
        if (d > 0) sheet.getRange(1, col, 1, 1).shiftColumnGroupDepth(-d);   // reset on re-push so groups don't stack
        sheet.getRange(1, col, 1, 1).shiftColumnGroupDepth(1);
      }
    } catch (e) {}
  }
}

function doGet() {
  return json({ ok: true, note: 'HexManiac MCP sheet bridge is deployed. POST {action,token,tab,values}.' });
}

function json(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
