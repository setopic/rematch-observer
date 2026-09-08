using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace RematchObserver;

/// <summary>
/// 画面 1 枚。**BGRA を素のまま持つ。**
///
/// 照合に使うのは輝度だけなので <see cref="ToGray"/> で 1 面に落とす。
/// **色を見るのは黄色の縁取りを探すときだけ**（CON-09）で、そこでだけ BGRA を読む。
/// </summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>1 行あたりのバイト数。**幅 × 4 とは限らない**（キャプチャ側の都合で余る）。</summary>
    public int Stride { get; }
    public byte[] Bgra { get; }

    public Frame(int width, int height, int stride, byte[] bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("大きさが 0 以下です");
        if (stride < width * 4) throw new ArgumentException("stride が幅より狭いです");
        if (bgra.Length < stride * (long)height) throw new ArgumentException("バッファが足りません");
        Width = width; Height = height; Stride = stride; Bgra = bgra;
    }

    public (byte B, byte G, byte R, byte A) At(int x, int y)
    {
        int i = y * Stride + x * 4;
        return (Bgra[i], Bgra[i + 1], Bgra[i + 2], Bgra[i + 3]);
    }

    /// <summary>輝度 1 面にする。**照合はここから先しか見ない。**</summary>
    public GrayImage ToGray()
    {
        var g = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            int src = y * Stride, dst = y * Width;
            for (int x = 0; x < Width; x++, src += 4)
            {
                // BT.601。整数のまま済ませる（1 フレームあたり数百万回通る）
                g[dst + x] = (byte)((Bgra[src + 2] * 77 + Bgra[src + 1] * 150 + Bgra[src] * 29) >> 8);
            }
        }
        return new GrayImage(Width, Height, g);
    }

    [SupportedOSPlatform("windows")]
    public void SavePng(string path)
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, Width, Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    Bgra, y * Stride, data.Scan0 + y * data.Stride, Width * 4);
        }
        finally { bmp.UnlockBits(data); }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, ImageFormat.Png);
    }

    [SupportedOSPlatform("windows")]
    public static Frame LoadPng(string path)
    {
        using var loaded = new Bitmap(path);
        using var bmp = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(loaded, 0, 0);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var buf = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, buf, y * stride, stride);
            return new Frame(bmp.Width, bmp.Height, stride, buf);
        }
        finally { bmp.UnlockBits(data); }
    }

    public Frame Crop(Rect r)
    {
        var c = r.ClampTo(Width, Height);
        if (c.W <= 0 || c.H <= 0) throw new ArgumentException("切り出す範囲が画面の外です");
        int stride = c.W * 4;
        var buf = new byte[stride * c.H];
        for (int y = 0; y < c.H; y++)
            Array.Copy(Bgra, (c.Y + y) * Stride + c.X * 4, buf, y * stride, stride);
        return new Frame(c.W, c.H, stride, buf);
    }
}

/// <summary>輝度 1 面。**照合はすべてこの上で行う。**</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GrayImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length < width * (long)height) throw new ArgumentException("バッファが足りません");
        Width = width; Height = height; Pixels = pixels;
    }

    public byte this[int x, int y] => Pixels[y * Width + x];

    /// <summary>
    /// 面積平均で縮める。**テンプレートは基準の高さで作ってあるので、
    /// 画面のほうをその高さに合わせる**（テンプレートを拡大するより崩れない）。
    /// </summary>
    public GrayImage Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;
        if (width <= 0 || height <= 0) throw new ArgumentException("縮小後の大きさが 0 以下です");
        var dst = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int y0 = (int)((long)y * Height / height);
            int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * Height / height));
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)((long)x * Width / width);
                int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * Width / width));
                int sum = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++) { sum += Pixels[sy * Width + sx]; n++; }
                dst[y * width + x] = (byte)(sum / n);
            }
        }
        return new GrayImage(width, height, dst);
    }

    public GrayImage Crop(Rect r)
    {
        var c = r.ClampTo(Width, Height);
        if (c.W <= 0 || c.H <= 0) throw new ArgumentException("切り出す範囲が画面の外です");
        var dst = new byte[c.W * c.H];
        for (int y = 0; y < c.H; y++)
            Array.Copy(Pixels, (c.Y + y) * Width + c.X, dst, y * c.W, c.W);
        return new GrayImage(c.W, c.H, dst);
    }
}

/// <summary>画面の中の矩形。**左上が原点。**</summary>
public readonly record struct Rect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;

    public Rect ClampTo(int width, int height)
    {
        int x = Math.Clamp(X, 0, width), y = Math.Clamp(Y, 0, height);
        return new Rect(x, y, Math.Clamp(Right, x, width) - x, Math.Clamp(Bottom, y, height) - y);
    }

    public override string ToString() => $"{X},{Y},{W},{H}";

    public static Rect Parse(string s)
    {
        var p = s.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 4) throw new FormatException("範囲は x,y,w,h の形で書きます");
        return new Rect(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
    }
}
