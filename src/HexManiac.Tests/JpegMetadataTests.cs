using HavenSoft.HexManiac.Core.Models.Images;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class JpegMetadataTests {
      private static readonly byte[] Jpeg = {
         0xFF, 0xD8,                                     // SOI
         0xFF, 0xE0, 0x00, 0x04, 0xAA, 0xBB,             // APP0 (JFIF): kept
         0xFF, 0xE1, 0x00, 0x05, 0x01, 0x02, 0x03,       // APP1 (Exif): removed
         0xFF, 0xE2, 0x00, 0x03, 0x09,                   // APP2 (ICC): removed only with includeColorProfile
         0xFF, 0xDB, 0x00, 0x03, 0x44,                   // DQT: kept
         0xFF, 0xDA, 0x00, 0x03, 0x11, 0xE1, 0xE1, 0xFF, 0xD9, // SOS + scan data (not walked) + EOI
      };

      [Fact]
      public void StripMetadata_RemovesExifKeepsRest() {
         var stripped = JpegMetadata.StripMetadata(Jpeg);

         Assert.Equal(new byte[] {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0xAA, 0xBB, 0xFF, 0xE2, 0x00, 0x03, 0x09, 0xFF, 0xDB, 0x00, 0x03, 0x44,
            0xFF, 0xDA, 0x00, 0x03, 0x11, 0xE1, 0xE1, 0xFF, 0xD9,
         }, stripped);
      }

      [Fact]
      public void StripMetadata_IncludeColorProfile_RemovesIccToo() {
         var stripped = JpegMetadata.StripMetadata(Jpeg, includeColorProfile: true);

         Assert.DoesNotContain((byte)0x09, stripped);
      }

      [Fact]
      public void StripMetadata_NotJpeg_Null() {
         Assert.Null(JpegMetadata.StripMetadata(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
      }
   }
}
