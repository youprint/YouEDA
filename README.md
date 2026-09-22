# YouEDA

YouEDA is a Windows desktop component loader for EasyEDA/LCSC parts. Enter one LCSC code or paste/import a batch, inspect the symbol and footprint preview, then add the selected components to two native Altium Designer libraries:

- `youeda.PcbLib` — footprint, copper pads, silkscreen, and embedded STEP model when EasyEDA supplies one.
- `youeda.SchLib` — schematic symbol selected from the bundled common-symbol catalog, or generated from EasyEDA pin records for IC/MCU-class parts.

Each import **upserts** the LCSC part number into those same two files. Existing components stay in place; importing the same LCSC code again refreshes only that component.

> Validate every generated footprint, electrical pin mapping, and 3D model against the manufacturer datasheet before using it in production.

## Screenshots

![YouEDA search, output directory, and generation log](docs/screenshots/youeda-search-and-batch.png)

*Part search and output workflow.*

![Generated Altium footprint on Top Overlay](docs/screenshots/altium-top-overlay.png)

*Example generated footprint in Altium Designer; the component outline is on Top Overlay (yellow), not a mid-layer.*

## Features

- LCSC part lookup using the public EasyEDA component payload.
- Single-code, pasted-list, and CSV/text batch input.
- Dark WPF/MVVM desktop interface with symbol and footprint previews.
- Native Altium `PcbLib` writer using [OriginalCircuit.Altium (AltiumSharp)](https://github.com/issus/AltiumSharp).
- Top/bottom copper and Top/Bottom Overlay mapping, plated and SMD pads, and embedded EasyEDA STEP 3D bodies where available.
- Common parts use the bundled symbol catalog (passives, diodes, crystals, connectors, sensors, transistors, LEDs, TVS/Zener, and diode arrays). IC/MCU-style parts are generated from EasyEDA pin information. Ambiguous non-IC matches present a picker.
- **Open in Altium** launches both `youeda.PcbLib` and `youeda.SchLib` through the Windows Altium file association.

## Build from source

### Prerequisites

- Windows 10/11.
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0).
- Git.

The Altium writer is currently referenced from the upstream source tree because the required writer package is not available as a stable NuGet package.

```powershell
git clone https://github.com/youprint/YouEDA.git
cd YouEDA
git clone https://github.com/issus/AltiumSharp.git third_party/AltiumSharp
dotnet restore .\src\EasyEdaAltiumGrabber.csproj
dotnet build .\src\EasyEdaAltiumGrabber.csproj -c Release
```

Run the development build:

```powershell
dotnet run --project .\src\EasyEdaAltiumGrabber.csproj -c Release
```

## Deploy a portable Windows build

Create a self-contained x64 folder that does not require a preinstalled .NET runtime:

```powershell
dotnet publish .\src\EasyEdaAltiumGrabber.csproj -c Release -r win-x64 --self-contained true -o .\dist\YouEDA
```

Copy the complete `dist\YouEDA` folder to the target computer. Do not copy only `YouEDA.exe`: the DLLs, native dependencies, templates, and optional bundled symbols beside it are required.

## Install and first use

1. Extract/copy the published `YouEDA` folder to a permanent location, for example `C:\Apps\YouEDA`.
2. Run `YouEDA.exe`. Windows may show a SmartScreen warning for an unsigned local build; review the source/release and follow your organization’s policy.
3. Choose an output directory, such as `C:\Users\<you>\Documents\YouEDA-Libraries`.
4. Search an LCSC part number (`C12345`), select the footprint/3D rows to add, and choose **Add selected to library**.
5. Use **Open in Altium** to load the shared `youeda.PcbLib` and `youeda.SchLib` files in Altium Designer. If a library is already open and locked in Altium, close it before updating it in YouEDA.
6. Inspect the imported footprint, symbol polarity/pin mapping, model orientation, and manufacturer datasheet before release.

## Optional private symbol catalog

The developer’s bundled `BundledUserSymbols.SchLib` is intentionally ignored by this public repository because it may contain private/custom library artwork. The application builds without it and falls back to EasyEDA-generated symbols when needed. To use your own approved catalog, place a compatible native Altium library at:

```text
src\Assets\BundledUserSymbols.SchLib
```

then rebuild/publish. Do not commit or publish third-party/proprietary symbol libraries unless their license allows redistribution.

## Third-party notices

- [OriginalCircuit.Altium / AltiumSharp](https://github.com/issus/AltiumSharp), Apache-2.0. It provides the native Altium file reader/writer used by YouEDA.
- EasyEDA/LCSC data is fetched from public web endpoints. Endpoint formats can change without notice.

YouEDA is an independent desktop tool; it is not affiliated with Altium, EasyEDA, JLCPCB, or LCSC.
