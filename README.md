# Stremio Xbox Prototype

A controller-first UWP/XAML streaming application based on the supplied TV UI mockups. Catalogs, metadata, and streams come from add-ons the user installs; the app does not invent media or diagnostic measurements.

## Build

Open `xbox-streaming-app.sln` in Visual Studio 2022 17.12 or newer with the **Universal Windows Platform tools** workload, Windows SDK 10.0.26100 or newer, and .NET 9 SDK installed. Select **Debug / x64**, build, and launch the packaged application with F5. Do not run the loose EXE: UWP XAML requires package activation. The project uses the matching Windows SDK's x64 `MakeAppx.exe`. Both Debug and Release builds produce MSIX packages. Native AOT is not enabled for ordinary builds because that configuration does not generate the EXE needed by the package target. For Xbox deployment, switch to an Xbox device target in Visual Studio, enable Developer Mode on the console, pair it, and deploy.

## Add-ons and playback

Open **Add-ons**, enter an HTTP or HTTPS Stremio add-on manifest URL, and install it. The app reads declared movie and series catalogs, metadata, search catalogs, and streams through the Stremio add-on protocol. Individual add-on failures appear in Diagnostics while other providers continue loading. No add-on endpoint or account credentials are embedded in the source.

The built-in player accepts direct HTTP or HTTPS media URLs. Torrent/info-hash sources are listed but require an external stream engine and cannot play in this build. Codec and DRM support depend on the device and stream. Settings, installed add-on URLs, the watchlist, and the optional server endpoint persist locally through `ApplicationData.Current.LocalSettings`.

Diagnostics reports the operating system's actual connectivity status and app events. Storage, memory, decoder latency, frame drops, local server discovery, playback history, and downloads stay marked unavailable until their respective probes or providers are implemented.
