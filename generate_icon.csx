// 生成 CloudPan 图标 .ico 文件（多个尺寸）
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

static void GenerateIcon(string outputPath)
{
    var sizes = new[] { 256, 128, 64, 48, 32, 16 };
    var images = new List<Bitmap>();

    foreach (var size in sizes)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // 背景圆
        int pad = size / 16;
        var rect = new Rectangle(pad, pad, size - 2 * pad, size - 2 * pad);
        using var bgBrush = new SolidBrush(Color.FromArgb(0, 120, 212));
        g.FillEllipse(bgBrush, rect);

        // 白色云朵线条（简化：两条弧形 + 底部平坦）
        int w = size / 2;
        int h = size / 4;
        int cx = size / 2;
        int cy = size / 2;
        using var cloudPen = new Pen(Color.White, Math.Max(2, size / 20));
        // 简化为箭头上下的弧形
        g.DrawArc(cloudPen, cx - w/2, cy - h, w, h * 2, 160, 220);
        g.DrawLine(cloudPen, cx - w/3, cy + h/2, cx + w/3, cy + h/2);

        images.Add(bmp);
    }

    // 保存为 ICO
    using var fs = new FileStream(outputPath, FileMode.Create);
    var writer = new BinaryWriter(fs);
    writer.Write((short)0); writer.Write((short)1); writer.Write((short)sizes.Length);

    var dataOffsets = new List<long>();
    foreach (var (bmp, i) in images.Select((b, i) => (b, i)))
    {
        byte w = (byte)(sizes[i] == 256 ? 0 : sizes[i]);
        byte h = (byte)(sizes[i] == 256 ? 0 : sizes[i]);
        writer.Write(w); writer.Write(h);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((short)0); writer.Write((short)32);
        var bmpStream = new MemoryStream();
        bmp.Save(bmpStream, ImageFormat.Png);
        var bmpData = bmpStream.ToArray();
        writer.Write((int)bmpData.Length);
        dataOffsets.Add(fs.Position);
        writer.Write((int)(6 + 16 * sizes.Length + dataOffsets.Select(o => (int)(o - 6 - 16 * sizes.Length)).Take(i).Sum()));
    }

    foreach (var (bmp, i) in images.Select((b, i) => (b, i)))
    {
        var bmpStream = new MemoryStream();
        bmp.Save(bmpStream, ImageFormat.Png);
        var bmpData = bmpStream.ToArray();
        fs.Seek(dataOffsets[i], SeekOrigin.Begin);
        writer.Write(bmpData);
    }
}

var output = args.Length > 0 ? args[0] : @"E:\XiaoFeng\云盘\CloudPan.Client\Resources\app.ico";
var dir = Path.GetDirectoryName(output);
if (dir != null) Directory.CreateDirectory(dir);
GenerateIcon(output);
Console.WriteLine($"Icon saved: {output} ({new FileInfo(output).Length} bytes)");
