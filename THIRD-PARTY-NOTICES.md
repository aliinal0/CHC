# Third-party notices

CHC itself is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
It uses the following open source components, which keep their own licenses.
Their license terms apply to those components only, not to CHC's own code.

| Component | License | Project |
|---|---|---|
| LibreHardwareMonitorLib | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp | Apache-2.0 | https://software.seekye.com/hidsharp |
| WPF UI (WPF-UI) | MIT | https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Web.WebView2 | Microsoft WebView2 SDK license (BSD-style) | https://aka.ms/webview2 |
| System.Management, System.Diagnostics.EventLog, System.IO.Ports, System.CodeDom, System.IO.FileSystem.AccessControl, System.Reflection.TypeExtensions, Microsoft.Extensions.* | MIT | https://github.com/dotnet/runtime |
| Microsoft.Diagnostics.Tracing.TraceEvent, Microsoft.Diagnostics.FastSerialization (FPS measurement) | MIT | https://github.com/microsoft/perfview |
| Microsoft.Diagnostics.NETCore.Client | MIT | https://github.com/dotnet/diagnostics |
| Microsoft.Data.Sqlite (the history file) | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org |
| Inter typeface (bundled in the app; licence in src/CHC.App/Assets/Fonts/Inter-LICENSE.txt) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| Inno Setup (builds the installer) | Inno Setup License | https://jrsoftware.org/isinfo.php |

The MPL-2.0 components are used unmodified, as NuGet packages. Their source code is
available at the project links above.

CHC can read CPU and motherboard sensors through the optional
[PawnIO](https://pawnio.eu) driver. PawnIO is not shipped with CHC; users install it separately, or let the setup download it from
its official releases (a fixed version, checked against the SHA-256 listed on GitHub before it runs).
