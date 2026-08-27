namespace HavenSoft.HexManiac.Core.Models.Images {
   /// <summary>
   /// 8-bit-per-channel RGB, used as the working color space for resizing/quantizing images
   /// before they're snapped down to the GBA's 5-bit-per-channel (15-bit, 0RRRRRGGGGGBBBBB)
   /// color format - matching the bit layout IndexedPng/WindowsFileSystem.Convert already use
   /// elsewhere in this app (R in bits 10-14, G in bits 5-9, B in bits 0-4).
   /// </summary>
   public readonly struct Rgb {
      public readonly int R, G, B;
      public Rgb(int r, int g, int b) { R = r; G = g; B = b; }

      public static Rgb FromGbaColor(short color) {
         int r = (color >> 10) & 0x1F, g = (color >> 5) & 0x1F, b = color & 0x1F;
         return new Rgb((r * 255 + 15) / 31, (g * 255 + 15) / 31, (b * 255 + 15) / 31);
      }

      public short ToGbaColor() {
         int r = (R * 31 + 127) / 255, g = (G * 31 + 127) / 255, b = (B * 31 + 127) / 255;
         return (short)((r << 10) | (g << 5) | b);
      }

      public int DistanceSquaredTo(Rgb other) {
         var (dr, dg, db) = (R - other.R, G - other.G, B - other.B);
         return dr * dr + dg * dg + db * db;
      }
   }
}
