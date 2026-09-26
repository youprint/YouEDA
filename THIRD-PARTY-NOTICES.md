# YouEDA binary distribution notices

YouEDA is MIT-licensed; see LICENSE. The Windows ZIP also includes these libraries:

- OriginalCircuit.Altium / AltiumSharp and its rendering adapters: Apache-2.0.
  Source: https://github.com/issus/AltiumSharp/tree/ce72437f30cd54f549601d4e0ca5846d21272150
  YouEDA modifications are supplied in this repository's `patches` directory.
- OriginalCircuit.Eda.Abstractions and OriginalCircuit.Eda.Rendering libraries:
  MIT, Copyright (c) Original Circuit. Their source is in AltiumSharp's shared
  dependency repositories; see the pinned upstream tree above.
- OpenMcdf 3.1.4: MPL-2.0. Copyright 2010-2026 Federico Blaseotto and Jeremy Powell.
  The unmodified library's corresponding source is available at
  https://github.com/openmcdf/openmcdf/tree/11ffb7cafff6eea99639df6b2b249dcf6bcca3ef
  License: https://www.mozilla.org/MPL/2.0/
- SkiaSharp and SkiaSharp.NativeAssets.Win32 3.116.1: MIT, with additional native
  third-party notices supplied in the ZIP's `licenses` directory.
  Source: https://github.com/mono/SkiaSharp/tree/v3.116.1

easyeda2kicad.py was consulted as a format reference. Its AGPL-licensed Python
implementation is not included in the source or binary release.

Component CAD data and user-provided libraries are not included in this release,
apart from the existing bundled common-symbol catalog. Supplier data remains
subject to its respective terms. YouEDA is not affiliated with those suppliers.
