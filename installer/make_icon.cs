// make_icon.cs — 生成 installer/lecoofan.ico（与托盘同款三叶风扇徽标）。
// 一次性工具：画 16~256 多个尺寸，按传统 DIB（BMP）格式打包成 ICO——
// 不用 PNG 压缩条目：老版 csc /win32icon 与 .NET 的 Icon.ToBitmap 都不认 PNG 条目。
// 用法：
//   csc -nologo -out:%TEMP%\make_icon.exe installer\make_icon.cs -r:System.Drawing.dll
//   %TEMP%\make_icon.exe installer\lecoofan.ico
// 几何/配色刻意与 lecoofan_gui.cs 的 BuildFanIcon 一致（32 单位为基准）。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

static class MakeIcon
{
    static readonly Color Blade = Color.FromArgb(238, 243, 250);
    static readonly Color Hub   = Blend(Color.FromArgb(34, 160, 94), Color.FromArgb(214, 69, 65), 0.15);
    static readonly Color Ring  = Blend(Hub, Color.Black, 0.3);

    static Color Blend(Color a, Color b, double t)
    {
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "lecoofan.ico";
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] dats = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) dats[i] = Render(sizes[i]);

        using (FileStream fs = new FileStream(outPath, FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));   // 256 记 0
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)dats[i].Length);
                w.Write((uint)offset);
                offset += dats[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++) w.Write(dats[i]);
        }
        Console.WriteLine("wrote " + Path.GetFullPath(outPath));
        return 0;
    }

    /// <summary>画一张 size×size 的位图，转成 ICO 里的 DIB 数据块（BITMAPINFOHEADER + BGRA + AND 掩码）。</summary>
    static byte[] Render(int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            double s = size / 32.0;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush blade = new SolidBrush(Blade))
                using (SolidBrush hub = new SolidBrush(Hub))
                using (Pen ring = new Pen(Ring, (float)(3 * s)))
                {
                    g.DrawEllipse(ring, (float)(2 * s), (float)(2 * s), (float)(29 * s), (float)(29 * s));
                    for (int i = 0; i < 3; i++)
                    {
                        double a = i * 2 * Math.PI / 3 - Math.PI / 2;
                        float bx = (float)((16 + Math.Cos(a) * 8) * s);
                        float by = (float)((16 + Math.Sin(a) * 8) * s);
                        float br = (float)(5 * s);
                        g.FillEllipse(blade, bx - br, by - br, br * 2, br * 2);
                    }
                    g.FillEllipse(hub, (float)(11 * s), (float)(11 * s), (float)(10 * s), (float)(10 * s));
                    g.FillEllipse(blade, (float)(14 * s), (float)(14 * s), (float)(4 * s), (float)(4 * s));
                }
            }
            return ToDib(bmp, size);
        }
    }

    static byte[] ToDib(Bitmap bmp, int size)
    {
        int xorBytes = size * size * 4;
        int andRow = ((size + 31) / 32) * 4;          // AND 掩码每行按 4 字节对齐
        int andBytes = andRow * size;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] dib = new byte[40 + xorBytes + andBytes];
            using (MemoryStream ms = new MemoryStream(dib))
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(40);                 // biSize
                w.Write(size);               // biWidth
                w.Write(size * 2);           // biHeight（XOR + AND 两半）
                w.Write((ushort)1);          // biPlanes
                w.Write((ushort)32);         // biBitCount
                w.Write(0);                  // biCompression = BI_RGB
                w.Write(xorBytes);           // biSizeImage
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                byte[] row = new byte[size * 4];
                for (int y = size - 1; y >= 0; y--)          // XOR 位图自下而上
                {
                    IntPtr src = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    Marshal.Copy(src, row, 0, row.Length);
                    w.Write(row);
                }
                // AND 掩码也自下而上：alpha < 128 置位（老式渲染器靠它，现代 Windows 靠 alpha）
                byte[] andRowBuf = new byte[andRow];
                for (int y = size - 1; y >= 0; y--)
                {
                    Array.Clear(andRowBuf, 0, andRow);
                    IntPtr src = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    Marshal.Copy(src, row, 0, row.Length);
                    for (int x = 0; x < size; x++)
                        if (row[x * 4 + 3] < 128) andRowBuf[x / 8] |= (byte)(0x80 >> (x % 8));
                    w.Write(andRowBuf);
                }
            }
            return dib;
        }
        finally { bmp.UnlockBits(data); }
    }
}
