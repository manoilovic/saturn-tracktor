# Build Saturn Tracktor

Requirements: Windows x64, .NET SDK 8.0 (validated with 8.0.426), Internet access to NuGet for the first restore.

```powershell
dotnet publish SaturnTracktor/SaturnTracktor.csproj -c Release -r win-x64 --self-contained true -o publish
```

The release artifact is `publish/SaturnTracktor.exe`; WebView2 XML documentation files in that directory are not runtime dependencies. The application includes .NET, its UI resources, icon, and the unchanged signed FxSound virtual audio driver/helper as embedded resources. WebView2 uses the machine's Evergreen runtime. The EXE is not Authenticode-signed; CompanyName metadata is Gevork Saturn.

Optional diagnostics:

```powershell
publish/SaturnTracktor.exe --self-test dsp-report.json
publish/SaturnTracktor.exe --devices devices.json
publish/SaturnTracktor.exe --audio-test audio-report.json
publish/SaturnTracktor.exe --ui-check main-window.png
```

The live test requires the virtual driver and a physical output. It emits a very quiet 997 Hz test signal, measures the physical loopback, checks idle rendering, and checks Windows default-route restoration. Do not run the live test during important playback. The UI test temporarily changes settings, restores the initial state, and closes its window.

ThirdParty/FxSound-helper contains the unmodified helper source and Visual Studio build files from the pinned FxSound app commit shown in its commit.txt. It uses Windows SDK/Visual Studio C++ tooling. ThirdParty/FxSound-driver/source.zip contains the upstream driver source and WDK build files. Third-party binaries in SaturnTracktor/Assets are extracted unchanged from the official signed FxSound installer; their hashes and upstream source locations are in ThirdPartyNotices.txt. Rebuilding a kernel driver does not reproduce the vendor's private signing keys.

The endpoint alias Saturn Audio Enhancer is set using Windows audio properties after installation. The driver INF/SYS/CAT and their signing identities are not modified.

## Build the installer

Install Inno Setup 6.7.3 from https://jrsoftware.org/isdl.php (non-commercial build). From the repository root run:

```powershell
./installer/Build-Installer.ps1 -Iscc 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
```

The script publishes the app, packages the corresponding source and licenses, downloads and verifies Microsoft's WebView2 bootstrapper if needed, and writes the installer, source archive and SHA-256 hashes to `artifacts/`. Supply `-DotNet` if the SDK is not on PATH. Generated `build/`, `artifacts/`, `installer/payload/` and `installer/output/` are excluded from Git.

The installer requests administrator rights and installs to Program Files with machine-wide uninstall registration and shortcuts. It detects an active virtual endpoint before invoking `--install-driver`. Existing user settings are preserved. Uninstall removes application files and shortcuts, retaining user settings and the shared audio driver. The bootstrapper only downloads WebView2 if no installed runtime is detected. Windows 11 x64 is the installer target. Fresh-machine driver/runtime installation has not been separately tested.
