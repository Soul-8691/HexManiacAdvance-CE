using System.Text.Json.Serialization;

namespace HavenSoft.HexManiac.Core.Models.Code {
   /// <summary>
   /// A single card entry from https://ygoprodeck.com/api-guide/ (db.ygoprodeck.com/api/v7/cardinfo.php).
   /// Field names/shape match the API's JSON exactly (only the subset this app uses is modeled).
   /// </summary>
   public class YgoProDeckCard {
      [JsonPropertyName("id")]
      public int Id { get; set; } // the real-world card password

      [JsonPropertyName("name")]
      public string Name { get; set; }

      [JsonPropertyName("type")]
      public string Type { get; set; } // e.g. "Effect Monster", "Spell Card", "Trap Card"

      [JsonPropertyName("desc")]
      public string Desc { get; set; }

      [JsonPropertyName("atk")]
      public int? Atk { get; set; }

      [JsonPropertyName("def")]
      public int? Def { get; set; }

      [JsonPropertyName("level")]
      public int? Level { get; set; }

      // for a monster, the monster's race (e.g. "Dragon", "Spellcaster").
      // for a spell/trap, this is instead the spell/trap SUBTYPE (e.g. "Normal", "Quick-Play", "Counter").
      [JsonPropertyName("race")]
      public string Race { get; set; }

      [JsonPropertyName("attribute")]
      public string Attribute { get; set; }

      // a Link Monster's Link Rating (it has no level) and its Link Arrows ("Top-Left", "Bottom", ...)
      [JsonPropertyName("linkval")]
      public int? LinkVal { get; set; }

      [JsonPropertyName("linkmarkers")]
      public string[] LinkMarkers { get; set; }

      [JsonPropertyName("card_images")]
      public YgoProDeckCardImage[] CardImages { get; set; }

      public bool IsSpellOrTrap => Type != null && (Type.Contains("Spell") || Type.Contains("Trap"));

      public string ImageUrlCropped => CardImages != null && CardImages.Length > 0 ? CardImages[0].ImageUrlCropped : null;
   }

   public class YgoProDeckCardImage {
      [JsonPropertyName("image_url_cropped")]
      public string ImageUrlCropped { get; set; }
   }

   public class YgoProDeckResponse {
      [JsonPropertyName("data")]
      public YgoProDeckCard[] Data { get; set; }
   }
}
