using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Web;

namespace HavenSoft.HexManiac.Core.Models.Code {
   /// <summary>
   /// Thin client for https://ygoprodeck.com/api-guide/ - the only outside network access this
   /// editor makes, used by the "Import from YGOProDeck" card-row action. Synchronous (blocking):
   /// callers are expected to invoke this from a background thread (see IWorkDispatcher), not the
   /// UI thread, since this can take a real network round-trip.
   /// </summary>
   public static class YgoProDeckClient {
      private const string BaseUrl = "https://db.ygoprodeck.com/api/v7/cardinfo.php";
      private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

      /// <returns>Matching cards, or an empty list if none were found / the request failed.</returns>
      public static IReadOnlyList<YgoProDeckCard> Search(string nameOrPassword) {
         if (string.IsNullOrWhiteSpace(nameOrPassword)) return Array.Empty<YgoProDeckCard>();
         var text = nameOrPassword.Trim();
         var query = int.TryParse(text, out var id) ? $"id={id}" : $"fname={HttpUtility.UrlEncode(text)}";
         return Fetch($"{BaseUrl}?{query}");
      }

      /// <returns>The raw image bytes, or null if the download failed.</returns>
      public static byte[] DownloadImageBytes(string url) {
         if (string.IsNullOrWhiteSpace(url)) return null;
         try {
            var response = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
         } catch {
            return null;
         }
      }

      private static IReadOnlyList<YgoProDeckCard> Fetch(string url) {
         try {
            var response = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return Array.Empty<YgoProDeckCard>();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var parsed = JsonSerializer.Deserialize<YgoProDeckResponse>(body);
            return parsed?.Data ?? Array.Empty<YgoProDeckCard>();
         } catch {
            // network failure, timeout, malformed response, etc. - treat exactly like "no results"
            // so the caller can show a single, simple "card not found" message either way.
            return Array.Empty<YgoProDeckCard>();
         }
      }
   }
}
