using System.Buffers;
using Vortice.WIC;

namespace LiveIn;

/// <summary>
/// Small still picture of a live source for the remote page: about once per second the newest UYVY picture is copied,
/// scaled to 480 pixels wide (box average) and saved as JPEG with the Windows Imaging Component.
/// Runs on the thread pool, never on the receive or encode thread, so the live path is not delayed.
/// </summary>
internal static class Preview
{
    public const int Width = 480;
    private static readonly IWICImagingFactory Factory = new();
    private static readonly object FactoryLock = new();

    /// <summary>Takes ownership of <paramref name="uyvy"/> (pooled) and returns the JPEG.</summary>
    public static byte[] Make(byte[] uyvy, int w, int h)
    {
        try
        {
            int dw = Math.Min(Width, w);
            int dh = Math.Max(2, (int)Math.Round((double)h * dw / w) & ~1);
            var bgr = Scale(uyvy, w, h, dw, dh);
            return Jpeg(bgr, dw, dh);
        }
        finally { ArrayPool<byte>.Shared.Return(uyvy); }
    }

    private static byte[] Scale(byte[] src, int w, int h, int dw, int dh)
    {
        int row = w * 2;
        var dst = new byte[dw * 3 * dh];
        for (int dy = 0; dy < dh; dy++)
        {
            int y0 = dy * h / dh, y1 = Math.Max(y0 + 1, (dy + 1) * h / dh);
            for (int dx = 0; dx < dw; dx++)
            {
                int x0 = dx * w / dw, x1 = Math.Max(x0 + 1, (dx + 1) * w / dw);
                int sumY = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int line = y * row;
                    for (int x = x0; x < x1; x++) { sumY += src[line + x * 2 + 1]; n++; }
                }
                int cx = (x0 & ~1) * 2 + y0 * row;     // chroma of the first pixel pair of the block
                float Y = (sumY / (float)n - 16f) * 1.1644f;
                float U = (src[cx] - 128f) * 1.1384f, V = (src[cx + 2] - 128f) * 1.1384f;
                int o = (dy * dw + dx) * 3;
                dst[o] = Clamp(Y + 1.8556f * U);                      // B (BT.709)
                dst[o + 1] = Clamp(Y - 0.1873f * U - 0.4681f * V);    // G
                dst[o + 2] = Clamp(Y + 1.5748f * V);                  // R
            }
        }
        return dst;
    }

    private static byte Clamp(float v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5f);

    private static byte[] Jpeg(byte[] bgr, int w, int h)
    {
        using var ms = new MemoryStream();
        lock (FactoryLock)
        {
            using var encoder = Factory.CreateEncoder(ContainerFormat.Jpeg, ms, BitmapEncoderCacheOption.NoCache);
            using var frame = encoder.CreateNewFrame(out var options);
            frame.Initialize(options);
            frame.SetSize((uint)w, (uint)h);
            frame.SetPixelFormat(PixelFormat.Format24bppBGR);
            frame.WritePixels((uint)h, (uint)(w * 3), bgr);
            frame.Commit();
            encoder.Commit();
            options?.Dispose();
        }
        return ms.ToArray();
    }
}
