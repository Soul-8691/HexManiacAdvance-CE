using System.Reflection;
using System.Diagnostics.CodeAnalysis;

// general info needed by all assemblies in the solution
[assembly: AssemblyCompany("HavenSoft")]
[assembly: AssemblyProduct("HavenSoft")]
[assembly: AssemblyCopyright("Copyright © HavenSoft 2023")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Haven's scheme: Major.Minor.Build.Revision, where Revision == 0 marks a public release
// (IsPublicRelease) and the displayed VersionNumber drops the trailing .0. The community
// fork is branded by name ("Community Edition" in the title / About), not by the version
// number, so these stay purely numeric and Haven-consistent. CE shows the full 4-part
// string (Singletons.MetadataInfo), so this displays as "0.5.7.2". Revision 2 (like the
// prior 0.5.6.1 release, any non-zero revision) means IsPublicRelease is false - that only
// gates a commented-out beta-features block, matching how every "Public Update x.y.z.1"
// build has shipped.
[assembly: AssemblyVersion("0.5.7.2")]
[assembly: AssemblyFileVersion("0.5.7.2")]


// AutoImplement style issues are expected, since it's compatible with earlier versions of C#.
[assembly: SuppressMessage("Style", "IDE0034:Simplify 'default' expression", Justification = "CodeGen", Scope = "module")]
[assembly: SuppressMessage("Style", "IDE1005:Delegate invocation can be simplified.", Justification = "CodeGen", Scope = "module")]

// Suppress rules that decrease code readability.
[assembly: SuppressMessage("Style", "IDE0017:Simplify object initialization", Justification = "Arrange / Act initialization can be separate", Scope = "module")]
