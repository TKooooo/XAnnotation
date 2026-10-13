using System.Reflection;
using System.Runtime.InteropServices;
#if ZWCAD
using ZwSoft.ZwCAD.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif

[assembly: AssemblyTitle("X-批注")]
[assembly: AssemblyDescription("AutoCAD and ZWCAD drawing annotation plug-in")]
[assembly: AssemblyCompany("影子")]
[assembly: AssemblyProduct("X-批注")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: AssemblyInformationalVersion("1.0.2")]
[assembly: ComVisible(false)]
[assembly: ExtensionApplication(typeof(XAnnotation.PluginEntry))]
[assembly: CommandClass(typeof(XAnnotation.Commands))]
