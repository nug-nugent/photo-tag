# Third-party software in PhotoTag

PhotoTag itself is MIT-licensed (see `LICENSE`). Release builds include the following software,
each under its own licence.

## Bundled program

| Software | Licence |
|---|---|
| [ExifTool](https://exiftool.org) by Phil Harvey, in the `exiftool` folder | Same terms as Perl itself: the [Artistic License](https://dev.perl.org/licenses/artistic.html) or the [GNU GPL](https://www.gnu.org/licenses/gpl-1.0.html). PhotoTag runs it as a separate program; its full licence and documentation are in that folder. |

## Libraries

| Library | Licence |
|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | MIT |
| [Avalonia](https://avaloniaui.net) (UI framework, incl. ItemsRepeater) | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [Skia](https://skia.org) (image decoding and drawing) | MIT; Skia: BSD-3-Clause |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) and [HarfBuzz](https://harfbuzz.github.io) (text shaping) | MIT; HarfBuzz: "Old MIT" |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (Windows graphics, packaged by Avalonia) | BSD-3-Clause |
| [Inter](https://rsms.me/inter/) typeface (packaged by Avalonia) | SIL Open Font License 1.1 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) (reading EXIF/IPTC/XMP) | Apache-2.0 |
| [XmpCore](https://github.com/drewnoakes/xmp-core-dotnet) (XMP parsing, a port of Adobe's XMP Toolkit) | [BSD-style Adobe XMP licence](https://www.adobe.com/devnet/xmp/library/eula-xmp-library-java.html) |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Apache-2.0 |
| [SQLite](https://sqlite.org) | Public domain |
| [Tmds.DBus](https://github.com/tmds/Tmds.DBus) (Linux desktop integration) | MIT |
| [Velopack](https://velopack.io) (installer and updates) | MIT |
| [Material Design Icons](https://pictogrammers.com/library/mdi/) by Pictogrammers (the app's icons, as vector paths) | Apache-2.0 |

## Data

| Data | Licence |
|---|---|
| Place names from [GeoNames](https://www.geonames.org) (towns, counties/states and countries, used by "Fill from GPS"; built into `places.bin` by `build/MakePlaces.cs`) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
