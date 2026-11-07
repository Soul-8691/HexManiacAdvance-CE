using System.Collections.Generic;

namespace HavenSoft.HexManiac.Core.Models.Images {
   /// <summary>
   /// WPF's JPEG decoder throws (FileFormatException "Unexpected property type or value") on some images' metadata
   /// before it ever reads a pixel - e.g. the Exif block of YGOProDeck's cropped art for Red Gadget. The pixels don't
   /// depend on those segments, so a decoder can retry with them removed.
   /// </summary>
   public static class JpegMetadata {
      // APP1 (Exif/XMP), APP13 (Photoshop/IPTC); with includeColorProfile, APP2 (ICC) too
      private static bool IsMetadata(byte marker, bool includeColorProfile) => marker == 0xE1 || marker == 0xED || (includeColorProfile && marker == 0xE2);

      public static bool IsJpeg(byte[] data) => data != null && data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;

      /// <summary>The same JPEG without its metadata segments (everything up to the scan is walked; the scan is kept as is), or null if it isn't a JPEG this can walk.</summary>
      public static byte[] StripMetadata(byte[] data, bool includeColorProfile = false) {
         if (!IsJpeg(data)) return null;
         var result = new List<byte>(data.Length) { 0xFF, 0xD8 };
         var i = 2;
         while (i + 3 < data.Length && data[i] == 0xFF) {
            var marker = data[i + 1];
            if (marker == 0xDA) break; // start of scan: the rest is image data
            if (marker == 0xFF) { i++; continue; } // fill byte
            var length = data[i + 2] << 8 | data[i + 3];
            if (length < 2 || i + 2 + length > data.Length) return null;
            if (!IsMetadata(marker, includeColorProfile)) {
               for (int k = 0; k < length + 2; k++) result.Add(data[i + k]);
            }
            i += length + 2;
         }
         for (int k = i; k < data.Length; k++) result.Add(data[k]);
         return result.ToArray();
      }
   }
}
