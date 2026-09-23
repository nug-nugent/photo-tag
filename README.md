# PhotoTag

A fast, cross-platform desktop app for browsing and tagging photo folders.
Built with [Avalonia](https://avaloniaui.net/) on .NET 10, so it runs on Windows, macOS and Linux.

## Status

Early days. Today it can:

- Browse a folder tree (subfolders load lazily, off the UI thread)
- Show a virtualized thumbnail grid that stays responsive with thousands of photos
- Show a details panel for the selected photo: preview, tags, rating, date taken, camera, exposure, size and GPS

It **can't edit or save tags yet**. That's next (see the roadmap).

## Running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/PhotoTag.App
```

You can also pass a folder on the command line: `dotnet run --project src/PhotoTag.App -- "D:\Photos"`.
Otherwise it reopens the last folder you used.

```bash
dotnet test --project tests/PhotoTag.Core.Tests
```

## How it's put together

| Project | What it does |
|---|---|
| `src/PhotoTag.Core` | No UI code. Finds files (`PhotoFiles`), renders and caches thumbnails (`ImageRenderer`, `ThumbnailCache`), reads EXIF/IPTC/XMP (`PhotoMetadata`). |
| `src/PhotoTag.App` | Avalonia UI with MVVM (CommunityToolkit.Mvvm) and compiled bindings. |
| `tests/PhotoTag.Core.Tests` | xUnit v3 tests. They build real JPEGs with hand-made EXIF/XMP segments. |

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

1. **Tag editing.** Add/remove keywords, edit title/description/rating, and write them back as XMP + IPTC via
   [ExifTool](https://exiftool.org/), the most reliable way to write metadata without corrupting files.
2. **Keyboard navigation and multi-select**, including applying tags to many photos at once.
3. **SQLite index** of tags per folder, for tag counts in the tree and search/filter across folders.
4. **More formats**: HEIC and camera RAW, probably via embedded previews.
5. **Packaging** for Windows, macOS and Linux.
