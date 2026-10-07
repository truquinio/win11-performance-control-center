using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]

// Every P/Invoke in this assembly targets a Windows system library. Resolving
// them only from System32 keeps a DLL planted next to the executable from
// being loaded, which matters most for the elevated helper.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly: InternalsVisibleTo("App.Tests")]
