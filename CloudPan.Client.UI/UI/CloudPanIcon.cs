using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CloudPan.Client.UI;

/// <summary>
/// 运行时自绘 CloudPan 图标（无需外部 .ico 文件）。
/// 支持多尺寸（16/32/64/256），高 DPI 适配，提供蓝色通用、蓝色+C 客户端、绿色+S 服务端三种变体。
/// </summary>
public static class CloudPanIcon
{
    private static readonly (Color Top, Color Bottom) BlueTheme = (
        Color.FromArgb(0x1E, 0x88, 0xE5),
        Color.FromArgb(0x15, 0x65, 0xC0));

    private static readonly (Color Top, Color Bottom) GreenTheme = (
        Color.FromArgb(0x43, 0xA0, 0x47),
        Color.FromArgb(0x2E, 0x7D, 0x32));

    // 云朵相对坐标（相对于 32×32，缩放至目标尺寸）
    private static readonly (float X, float Y, float W, float H)[] CloudParts =
    [
        (3f  / 32f, 13f / 32f, 26f / 32f, 13f / 32f), // 底部主体
        (5f  / 32f,  9f / 32f, 10f / 32f, 11f / 32f), // 左鼓包
        (11f / 32f,  6f / 32f, 12f / 32f, 13f / 32f), // 中鼓包（最高）
        (18f / 32f,  9f / 32f, 10f / 32f, 10f / 32f), // 右鼓包
    ];

    /// <summary>生成默认蓝色多尺寸图标（16/32/64/256 px）。</summary>
    public static Icon Create()
    {
        return IconFromBytes(BuildIcoBytes(BlueTheme, null));
    }

    /// <summary>生成蓝色 + 右下角 "C" 标记的客户端图标。</summary>
    public static Icon CreateClient()
    {
        return IconFromBytes(BuildIcoBytes(BlueTheme, 'C'));
    }

    /// <summary>生成绿色 + 右下角 "S" 标记的服务端图标。</summary>
    public static Icon CreateServer()
    {
        return IconFromBytes(BuildIcoBytes(GreenTheme, 'S'));
    }

    // ---- 私有实现 ----

    private static Icon IconFromBytes(byte[] icoData)
    {
        using MemoryStream ms = new MemoryStream(icoData, writable: false);
        return new Icon(ms);
    }

    private static byte[] BuildIcoBytes((Color Top, Color Bottom) theme, char? badge)
    {
        int[] sizes = { 16, 32, 64, 256 };

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter bw = new BinaryWriter(ms);

        // ICO 文件头
        bw.Write((short)0);          // reserved
        bw.Write((short)1);          // ICO type = 1
        bw.Write((short)sizes.Length); // image count

        // 预渲染所有帧
        byte[][] frameData = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using var bmp = RenderBitmap(sizes[i], theme, badge);
            frameData[i] = BitmapToIcoData(bmp);
        }

        // 目录条目
        int dataOffset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte wh = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
            bw.Write(wh);                      // width  (0 = 256)
            bw.Write(wh);                      // height (0 = 256)
            bw.Write((byte)0);                 // palette colors
            bw.Write((byte)0);                 // reserved
            bw.Write((short)1);                // color planes
            bw.Write((short)32);               // bits per pixel
            bw.Write((int)frameData[i].Length); // DIB + AND mask size
            bw.Write(dataOffset);              // offset in file
            dataOffset += frameData[i].Length;
        }

        // 写入帧数据
        foreach (byte[] data in frameData)
        {
            bw.Write(data);
        }

        return ms.ToArray();
    }

    private static Bitmap RenderBitmap(int size, (Color Top, Color Bottom) theme, char? badge)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float s = size;

        // 1. 背景渐变圆形
        using (LinearGradientBrush bgBrush = new LinearGradientBrush(
            new PointF(0, 0), new PointF(s, s), theme.Top, theme.Bottom))
        {
            g.FillEllipse(bgBrush, 0, 0, s, s);
        }

        // 2. 大尺寸叠加径向高光
        if (size >= 64)
        {
            using GraphicsPath radPath = new GraphicsPath();
            radPath.AddEllipse(0, 0, s, s);
            using PathGradientBrush radBrush = new PathGradientBrush(radPath)
            {
                CenterColor = Color.FromArgb(size >= 256 ? 100 : 60, Color.White),
                SurroundColors = [Color.FromArgb(0, Color.White)]
            };
            g.FillPath(radBrush, radPath);

            // 边缘加深
            using SolidBrush edgeBrush = new SolidBrush(Color.FromArgb(size >= 256 ? 40 : 20, 0, 0, 0));
            g.FillEllipse(edgeBrush, 0, 0, s, s);
        }

        // 3. 绘制云朵
        DrawCloud(g, size, theme);

        // 4. 大尺寸云朵细节（高光 + 阴影）
        if (size >= 64)
        {
            DrawCloudHighlights(g, size);
        }

        // 5. 徽章
        if (badge.HasValue)
        {
            DrawBadge(g, size, badge.Value, theme);
        }

        return bmp;
    }

    private static void DrawCloud(Graphics g, int size, (Color Top, Color Bottom) theme)
    {
        float s = size;

        if (size >= 128)
        {
            // 大尺寸：云朵投影
            using SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
            float off = s / 40f;
            foreach (var (rx, ry, rw, rh) in CloudParts)
            {
                g.FillEllipse(shadowBrush, rx * s + off, ry * s + off, rw * s, rh * s);
            }
        }

        if (size >= 64)
        {
            // 云朵渐变（顶部白 → 底部浅灰增强立体感）
            using LinearGradientBrush cloudGrad = new LinearGradientBrush(
                new PointF(0, s * 6f / 32f),
                new PointF(0, s * 26f / 32f),
                Color.White,
                Color.FromArgb(215, 215, 215));
            foreach (var (rx, ry, rw, rh) in CloudParts)
            {
                g.FillEllipse(cloudGrad, rx * s, ry * s, rw * s, rh * s);
            }

            // 云朵底部柔光（环境反光）
            using SolidBrush rimBrush = new SolidBrush(Color.FromArgb(20, theme.Top));
            g.FillEllipse(rimBrush, s * 4f / 32f, s * 20f / 32f, s * 24f / 32f, s * 6f / 32f);
        }
        else
        {
            // 小尺寸用纯色
            using SolidBrush cloudBrush = new SolidBrush(Color.FromArgb(245, 245, 245));
            foreach (var (rx, ry, rw, rh) in CloudParts)
            {
                g.FillEllipse(cloudBrush, rx * s, ry * s, rw * s, rh * s);
            }
        }
    }

    private static void DrawCloudHighlights(Graphics g, int size)
    {
        float s = size;

        // 主高光：中鼓包顶部（最亮）
        using (SolidBrush hlMain = new SolidBrush(Color.FromArgb(120, Color.White)))
        {
            g.FillEllipse(hlMain, s * 12.5f / 32f, s * 7.2f / 32f, s * 9f / 32f, s * 3f / 32f);
        }

        // 辅助高光：左鼓包顶部
        using (SolidBrush hlLeft = new SolidBrush(Color.FromArgb(80, Color.White)))
        {
            g.FillEllipse(hlLeft, s * 5.5f / 32f, s * 10.2f / 32f, s * 8f / 32f, s * 2.5f / 32f);
        }

        // 辅助高光：右鼓包顶部
        using (SolidBrush hlRight = new SolidBrush(Color.FromArgb(80, Color.White)))
        {
            g.FillEllipse(hlRight, s * 19f / 32f, s * 10.2f / 32f, s * 8f / 32f, s * 2.5f / 32f);
        }

        // 云底阴影
        using (SolidBrush shBrush = new SolidBrush(Color.FromArgb(35, 0, 0, 0)))
        {
            g.FillEllipse(shBrush, s * 5f / 32f, s * 22.5f / 32f, s * 22f / 32f, s * 4f / 32f);
        }

        // 256px 额外细节：顶部斜高光
        if (size >= 256)
        {
            using SolidBrush rimLine = new SolidBrush(Color.FromArgb(50, Color.White));
            g.FillEllipse(rimLine, s * 3.5f / 32f, s * 14f / 32f, s * 25f / 32f, s * 2.5f / 32f);

            using SolidBrush glowBrush = new SolidBrush(Color.FromArgb(30, Color.FromArgb(0xBB, 0xDE, 0xFB)));
            g.FillEllipse(glowBrush, s * 2f / 32f, s * 5f / 32f, s * 28f / 32f, s * 22f / 32f);
        }
    }

    private static void DrawBadge(Graphics g, int size, char letter, (Color Top, Color Bottom) theme)
    {
        if (size < 32)
        {
            return; // 16px 太小，省略徽章
        }

        float s = size;
        float badgeRadius = s * 5f / 32f;
        float cx = s - badgeRadius - s * 0.5f / 32f;
        float cy = s - badgeRadius - s * 0.5f / 32f;
        float d = badgeRadius * 2;

        // 白色圆形背景
        g.FillEllipse(Brushes.White, cx - badgeRadius, cy - badgeRadius, d, d);

        // 彩色细边框
        float borderWidth = Math.Max(1f, s / 64f);
        using Pen borderPen = new Pen(Color.FromArgb(120, theme.Top), borderWidth);
        g.DrawEllipse(borderPen, cx - badgeRadius, cy - badgeRadius, d, d);

        // 文字
        float fontSize = size switch
        {
            <= 32 => 8f,
            <= 64 => 14f,
            <= 128 => 26f,
            _ => 38f
        };
        using Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using SolidBrush textBrush = new SolidBrush(theme.Top);
        using StringFormat fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(letter.ToString(), font, textBrush,
            new RectangleF(cx - badgeRadius, cy - badgeRadius, d, d), fmt);
    }

    /// <summary>
    /// 将 32bpp ARGB Bitmap 转换为 ICO 帧字节（BITMAPINFOHEADER + XOR 像素数据 + AND 掩码）。
    /// </summary>
    private static byte[] BitmapToIcoData(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter bw = new BinaryWriter(ms);

        // BITMAPINFOHEADER
        bw.Write(40);          // biSize
        bw.Write(w);           // biWidth
        bw.Write(h * 2);       // biHeight (×2 因为包含 AND 掩码)
        bw.Write((short)1);    // biPlanes
        bw.Write((short)32);   // biBitCount
        bw.Write(0);           // biCompression (BI_RGB)
        bw.Write(0);           // biSizeImage (可忽略 for BI_RGB)
        bw.Write(0);           // biXPelsPerMeter
        bw.Write(0);           // biYPelsPerMeter
        bw.Write(0);           // biClrUsed
        bw.Write(0);           // biClrImportant

        // XOR 像素数据（倒序、BGRA）
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, w * 4);
                bw.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // AND 掩码（1bpp，32bpp 图标全部为 0，透明由 alpha 通道控制）
        int maskRowSize = ((w + 31) / 32) * 4;
        for (int y = 0; y < h; y++)
        {
            bw.Write(new byte[maskRowSize]);
        }

        return ms.ToArray();
    }

    /// <summary>获取或创建持久化的 .ico 文件路径。</summary>
    public static string GetIconPath()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudPan", "app.ico");
        if (!File.Exists(path))
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, BuildIcoBytes(BlueTheme, null));
        }
        return path;
    }
}
