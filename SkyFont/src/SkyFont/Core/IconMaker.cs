using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SkyFont.Core;

/// <summary>
/// 生成多尺寸 .ico（16/24/32/48/64/128/256），用于可执行文件与窗口图标。
/// 通过 <c>SkyFont.exe --makeicon &lt;路径&gt;</c> 调用，产物提交到 assets 目录。
/// </summary>
public static class IconMaker
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Create(string outputPath)
    {
        var pngs = new List<byte[]>(Sizes.Length);
        foreach (int size in Sizes) pngs.Add(Render(size));

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)Sizes.Length);

        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            int size = Sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(pngs[i].Length);
            writer.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (byte[] png in pngs) writer.Write(png);
    }

    private static byte[] Render(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double radius = size * 0.24;
            var brush = new LinearGradientBrush(
                Color.FromRgb(0x63, 0xA0, 0xFF),
                Color.FromRgb(0x27, 0x57, 0xB8),
                new Point(0, 0),
                new Point(1, 1));

            dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, size, size), radius, radius);

            string text = size <= 20 ? "A" : "Aa";
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            var formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * (size <= 20 ? 0.62 : 0.44),
                Brushes.White,
                96);

            dc.DrawText(formatted, new Point(
                (size - formatted.Width) / 2,
                (size - formatted.Height) / 2 - size * 0.03));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}
