# Google Sheets integration (`sheet_push` / `sheet_pull`)

Push a ROM table to a Google Sheet, edit it in the browser, and pull it back into the ROM — fully over
the internet, no manual CSV. **No Google Cloud project and no OAuth.** Instead of the MCP authenticating to
Google's API, you deploy a tiny **bound Apps Script web app** in your sheet, and the MCP just POSTs to it.
The script runs *as you*, so it edits your private sheet; the MCP only needs the secret URL + a token.
Portable: each user deploys their own script in their own sheet → their own URL. Nothing to verify, no
shared quota, no credentials shipped.

## One-time setup (~2 minutes, no Cloud Console)

1. Open the Google Sheet you want to sync.
2. **Extensions → Apps Script.** Delete the stub and paste all of [`docs/google-sheets-webapp.gs`](google-sheets-webapp.gs).
3. Change `SECRET` (top of the script) to a long random string of your own.
4. **Limit it to this one sheet** (otherwise the consent asks for *all* your spreadsheets): **⚙ Project
   Settings → "Show appsscript.json manifest file in editor"**, open `appsscript.json`, and add a per-file
   scope:
   ```json
   "oauthScopes": ["https://www.googleapis.com/auth/spreadsheets.currentonly"]
   ```
   The consent then reads *"only the specific spreadsheet you use this app with."* (Works because the
   script only calls `getActiveSpreadsheet()` — never `openById`/`openByUrl`, which force the all-sheets scope.)
5. **Deploy → New deployment → Web app**: *Execute as: Me*, *Who has access: Anyone with the link* → Deploy.
   Authorize when prompted — that's the **Apps Script editor's own consent screen** (it asks to manage *this*
   spreadsheet), *not* Google Cloud. Copy the **Web app URL**.
6. Tell the MCP the URL + secret, either by env vars:
   - `HEXMANIAC_SHEETS_URL` = the web-app URL
   - `HEXMANIAC_SHEETS_TOKEN` = your `SECRET`

   …or a config file `%AppData%\HexManiacMcp\google\sheets.json`:
   ```json
   { "url": "https://script.google.com/macros/s/AKfy…/exec", "token": "your-secret" }
   ```
   (`HEXMANIAC_GOOGLE_DIR` overrides the config folder.)

## Use it

```json
{"name": "open_rom",   "arguments": {"path": "C:/roms/firered.gba"}}
{"name": "sheet_push", "arguments": {"table": "data.pokemon.stats", "tab": "stats"}}
```
`sheet_push` writes a header row of field names then one row per record (clearing the tab first). The
web-app URL comes from your config; pass `url` to target a different deployed sheet.

Edit in Google Sheets, then:
```json
{"name": "sheet_pull", "arguments": {"table": "data.pokemon.stats", "tab": "stats"}}
```
`sheet_pull` matches rows by the **`index`** column (so keep it) and writes every other cell back to the ROM
as **one undo step**. The `slug` column is treated as a read-only key. Cell values use the same display form
as `read_table`/`write_value` (enum names like `FIRE`, numbers, text), so you edit them in the sheet
directly. Then `save_rom` when you're happy.

## Resolved reference views (`sheet_push_json`)

`sheet_push`/`sheet_pull` round-trip *raw editable* tables. For a nice **read-only reference** — resolved
move effect names, decoded flags, dex flavor, etc. — run one of the `export_*` tools to a JSON file, then
push its records:

```json
{"name": "export_moves",    "arguments": {"outPath": "C:/tmp/moves.json"}}
{"name": "sheet_push_json", "arguments": {"path": "C:/tmp/moves.json", "key": "moves", "tab": "moves-ref"}}
```

Each record's scalars go straight in; lists are joined with `, `; nested objects become compact JSON. This
is a *view* — it isn't pulled back, because the columns are derived (effect names, resolved text), not raw
table fields. Works for any export with a record array (`export_items` → key `items`, the FireRed pokémon
dump → key `pokemon`, etc.).

**Doc-ready formatting** — `explode` splits a list column into one **✓/✗ column per value**, and `style`
makes it presentable (bold + frozen header, ✓ green / ✗ red and centered, auto-sized columns):

```json
{"name": "sheet_push_json", "arguments": {"path": "C:/tmp/moves.json", "key": "moves", "tab": "moves", "explode": "flags", "style": true}}
```

> **Styling needs the generic web app.** The MCP computes the whole look (per-cell colors, type colors,
> alignments, widths) and ships it as a `format` spec; the script in `docs/google-sheets-webapp.gs` is a
> generic applier. Deploy that script **once** (re-paste it → **Deploy → Manage deployments → ✎ edit →
> New version**, same URL). After that, *style tweaks are MCP-side* — they never need another redeploy.

## Notes

- **One web app = one spreadsheet.** To sync several sheets, deploy the script in each and pass its `url`
  per call (or keep one as the configured default).
- **Security:** the web-app URL is a secret capability; the `token` check in the script is the second factor.
  Use a long random `SECRET` and don't share the URL.
- **Headless:** these tools run against the headless session (`open_rom` first); they don't target a live GUI tab.
- **Re-deploying:** if you change the script, use *Deploy → Manage deployments → edit → New version* (the URL
  stays the same).
