# PhotoTag

[![CI](https://github.com/nug-nugent/photo-tag/actions/workflows/ci.yml/badge.svg)](https://github.com/nug-nugent/photo-tag/actions/workflows/ci.yml)

A fast, cross-platform desktop app for browsing and tagging photo folders.
Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it runs on Windows, macOS and Linux.

## Status

Early days. Today it can:

- Browse a folder tree (subfolders load lazily, off the UI thread)
- Show a virtualized thumbnail grid that stays responsive with thousands of photos
- Show a details panel for the selected photo: preview, date taken, camera, exposure, size and GPS
- Edit tags, title, description and rating. Changes are written straight into the photo file.

Tags are written as XMP (read by Lightroom, digiKam, Windows and macOS) and, for JPEGs, also as IPTC for older
software. Pixels are never re-encoded, and each file's modified time is preserved.

## Running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for tag editing,
[ExifTool](https://exiftool.org/) on your PATH (Windows: `winget install OliverBetz.ExifTool`; macOS:
`brew install exiftool`; Debian/Ubuntu: `sudo apt install libimage-exiftool-perl`). Without ExifTool the app
still browses, read-only. Release builds will bundle ExifTool, so end users won't need to install it.

```bash
dotnet run --project src/PhotoTag.App
```

You can also pass a folder on the command line: `dotnet run --project src/PhotoTag.App -- "D:\Photos"`.
Otherwise it reopens the last folder you used.

```bash
dotnet test --solution PhotoTag.slnx
```

Tests that need ExifTool skip when it isn't installed. CI sets `PHOTOTAG_REQUIRE_EXIFTOOL=1`, so there they fail instead.
To use a specific ExifTool, set `PHOTOTAG_EXIFTOOL` to its full path.

## How it's put together

| Project | What it does |
|---|---|
| `src/PhotoTag.Core` | No UI code. Finds files (`PhotoFiles`), renders and caches thumbnails (`ImageRenderer`, `ThumbnailCache`), reads EXIF/IPTC/XMP (`PhotoMetadata`), writes it (`PhotoMetadataWriter` via a long-running `ExifTool` process). |
| `src/PhotoTag.App` | Avalonia UI with MVVM (CommunityToolkit.Mvvm) and compiled bindings. |
| `tests/PhotoTag.Core.Tests` | xUnit v3 tests. They build real JPEGs with hand-made EXIF/XMP segments, and round-trip writes through real ExifTool. |
| `tests/PhotoTag.App.Tests` | Headless UI tests that type, click and move focus in the real main window, then check the file. |

### Why it's fast

- **Only on-screen tiles exist.** `ItemsRepeater` with `UniformGridLayout` virtualizes the grid. Thumbnails load
  when a tile appears (`ElementPrepared`) and are freed when it scrolls away (`ElementClearing`).
- **Photos are never fully decoded for a thumbnail.** JPEGs are decoded at 1/8–1/2 scale via libjpeg's DCT
  scaling (SkiaSharp), then resized and rotated per EXIF orientation.
- **Thumbnails are cached on disk** under the local app-data folder (`PhotoTag/thumbnails`). The cache is keyed on
  path, size and modified time, so an edited photo gets a fresh thumbnail automatically.
- **Loading is bounded and cancellable.** At most (cores − 1) thumbnails are generated at once. Requests for tiles
  that scroll off screen are cancelled before any work is done.

## Roadmap

1. **Keyboard navigation and multi-select**, including applying tags to many photos at once.
2. **SQLite index** of tags per folder, for tag counts in the tree, search/filter across folders, and tag suggestions
   from your whole library (today suggestions only cover tags seen this session).
3. **More formats**: HEIC and camera RAW, probably via embedded previews.
4. **Packaging** for Windows, macOS and Linux, with ExifTool bundled.
