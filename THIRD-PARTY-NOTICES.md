# Third-party notices

CHC itself is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
It uses the following open source components, which keep their own licenses.
Their license terms apply to those components only, not to CHC's own code.

| Component | License | Project |
|---|---|---|
| LibreHardwareMonitorLib | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| HidSharp | Apache-2.0 | https://software.seekye.com/hidsharp |
| WPF UI (WPF-UI) | MIT | https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Web.WebView2 | Microsoft WebView2 SDK license (BSD-style) | https://aka.ms/webview2 |
| System.Management, System.Diagnostics.EventLog | MIT | https://github.com/dotnet/runtime |

The MPL-2.0 components are used unmodified, as NuGet packages. Their source code is
available at the project links above.

CHC can read CPU and motherboard sensors through the optional
[PawnIO](https://pawnio.eu) driver. PawnIO is not shipped with CHC; users install it separately.
