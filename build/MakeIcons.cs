#:package SkiaSharp
#:property PublishAot=false

// Draws PhotoTag's icon and writes it as PNG, Windows .ico and macOS .icns.
// Run from the repo root:  dotnet run build/MakeIcons.cs
using System.Buffers.Binary;
using SkiaSharp;

var output = Path.Combine("src", "PhotoTag.App", "Assets");
Directory.CreateDirectory(output);

byte[] Png(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    Draw(surface.Canvas, size);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

File.WriteAllBytes(Path.Combine(output, "icon.png"), Png(1024));
File.WriteAllBytes(Path.Combine(output, "icon-256.png"), Png(256));
File.WriteAllBytes(Path.Combine(output, "icon.ico"), Ico([16, 24, 32, 48, 64, 128, 256]));
File.WriteAllBytes(Path.Combine(output, "icon.icns"), Icns());
Console.WriteLine($"Wrote icons to {output}");

// A rounded tile with a white luggage-style tag, holding a small landscape.
static void Draw(SKCanvas canvas, int size)
{
    var s = size / 1024f;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(s);

    using var tile = new SKPaint { IsAntialias = true };
    tile.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1024, 1024),
        [new SKColor(0x1F, 0xA2, 0x9A), new SKColor(0x2B, 0x5C, 0xC8)], SKShaderTileMode.Clamp);
    canvas.DrawRoundRect(new SKRect(64, 64, 960, 960), 200, 200, tile);

    // The tag, rotated so its point faces up-left.
    canvas.Save();
    canvas.RotateDegrees(-35, 512, 512);
    using var tag = new SKPath();
    tag.MoveTo(250, 512);
    tag.LineTo(370, 330);
    tag.LineTo(800, 330);
    tag.ArcTo(new SKRect(760, 330, 840, 410), -90, 90, false);
    tag.LineTo(840, 614);
    tag.ArcTo(new SKRect(760, 614, 840, 694), 0, 90, false);
    tag.LineTo(370, 694);
    tag.Close();
    using var white = new SKPaint { IsAntialias = true, Color = SKColors.White };
    using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 60), ImageFilter = SKImageFilter.CreateBlur(18, 18) };
    canvas.Save();
    canvas.Translate(0, 18);
    canvas.DrawPath(tag, shadow);
    canvas.Restore();
    canvas.DrawPath(tag, white);

    // Eyelet.
    using var hole = new SKPaint { IsAntialias = true, Color = new SKColor(0x2B, 0x7F, 0xB5) };
    canvas.DrawCircle(360, 512, 34, hole);

    // A little landscape: sun and two hills, clipped to a rounded "photo".
    var photo = new SKRect(450, 400, 770, 624);
    canvas.Save();
    canvas.ClipRoundRect(new SKRoundRect(photo, 28), antialias: true);
    using var sky = new SKPaint { IsAntialias = true, Color = new SKColor(0xE3, 0xF2, 0xF1) };
    canvas.DrawRect(photo, sky);
    using var sun = new SKPaint { IsAntialias = true, Color = new SKColor(0xF5, 0xB7, 0x2E) };
    canvas.DrawCircle(700, 462, 34, sun);
    using var far = new SKPaint { IsAntialias = true, Color = new SKColor(0x3F, 0xB8, 0xA8) };
    using var farHill = new SKPath();
    farHill.MoveTo(450, 624); farHill.LineTo(560, 480); farHill.LineTo(660, 624); farHill.Close();
    canvas.DrawPath(farHill, far);
    using var near = new SKPaint { IsAntialias = true, Color = new SKColor(0x2B, 0x6C, 0xC4) };
    using var nearHill = new SKPath();
    nearHill.MoveTo(540, 624); nearHill.LineTo(660, 510); nearHill.LineTo(790, 624); nearHill.Close();
    canvas.DrawPath(nearHill, near);
    canvas.Restore();

    canvas.Restore();
}

// Windows .ico with PNG-compressed entries (supported since Vista).
byte[] Ico(int[] sizes)
{
    var images = sizes.Select(Png).ToList();
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images) w.Write(image);
    return ms.ToArray();
}

// macOS .icns with PNG entries: ic07 128, ic08 256, ic09 512, ic10 1024 (512@2x).
byte[] Icns()
{
    (string Type, int Size)[] entries = [("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];
    var chunks = entries.Select(e => (e.Type, Data: Png(e.Size))).ToList();
    var total = 8 + chunks.Sum(c => 8 + c.Data.Length);
    var result = new byte[total];
    "icns"u8.CopyTo(result);
    BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), total);
    var position = 8;
    foreach (var (type, data) in chunks)
    {
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(result, position);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position + 4), 8 + data.Length);
        data.CopyTo(result, position + 8);
        position += 8 + data.Length;
    }
    return result;
}
