using HavenSoft.HexManiac.Core.Models;
using System.Reflection;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class VersionNumberTests {
      // A CE point release past 4 parts (e.g. 0.5.7.2.1) can't be an assembly/file version, so it's the
      // informational version (SharedAssemblyInfo.cs): that's what the title, About, crash log and TOMLs show.
      [Fact]
      public void MetadataInfo_VersionNumber_IsInformationalVersion() {
         var informational = typeof(Singletons).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

         Assert.Equal(informational, BaseViewModelTestClass.Singletons.MetadataInfo.VersionNumber);
      }

      [Theory]
      [InlineData("0.5.7.2", "0.5.7.2.1", true)]
      [InlineData("0.5.7.2.1", "0.5.7.2", false)]
      [InlineData("0.5.7.2.1", "0.5.7.3", true)]
      [InlineData("0.5.7.2.1", "0.5.7.2.1", false)]
      public void NeedVersionUpdate_FivePartVersions(string previous, string current, bool expected) {
         Assert.Equal(expected, StoredMetadata.NeedVersionUpdate(previous, current));
      }
   }
}
