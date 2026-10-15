# Stremio Xbox Prototype

A controller-first UWP/XAML streaming application shell based on the supplied TV UI mockups.

## Build

Open `StremioXboxPrototype/StremioXboxPrototype.csproj` in Visual Studio 2022 17.12 or newer with the **Universal Windows Platform tools** workload, Windows SDK 10.0.26100 or newer, and .NET 9 SDK installed. This is a modern .NET UWP project (`UseUwp=true`) and uses Native AOT as required by the .NET 9 UWP toolchain. Select an x64 Windows target and build. For Xbox deployment, switch to an Xbox device target in Visual Studio, enable Developer Mode on the console, pair it, and deploy.

## Integration

The checked-in application deliberately shows `Unavailable`/empty states instead of fake catalog, stream, diagnostic, or hardware data. Implement Stremio/server adapters behind `ICatalogService` and `IStreamService`, then register those in `MainPage`. Do not add credentials to source.

Settings persist locally through `ApplicationData.Current.LocalSettings`. A real library provider should become the source of library state.
