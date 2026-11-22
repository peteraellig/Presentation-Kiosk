using System.Runtime.InteropServices;

namespace LiveIn;

// Minimal P/Invoke layer for the receive side of libomt.dll (OMT SDK, MIT). Struct layout follows libomt.h (OMTMediaFrame).
internal static class OmtNative
{
    public const int FrameTypeVideo = 2;
    public const int FrameTypeAudio = 4;
    public const int CodecFpa1 = 0x31415046;   // planar float audio
    public const int CodecUyvy = 0x59565955;
    public const int CodecUyva = 0x41565955;   // UYVY followed by an alpha plane
    public const int FlagInterlaced = 1;
    public const int PreferredUyvy = 0;
    public const int ReceiveFlagsNone = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MediaFrame
    {
        public int Type;
        public long Timestamp;
        public int Codec;
        public int Width;
        public int Height;
        public int Stride;
        public int Flags;
        public int FrameRateN;
        public int FrameRateD;
        public float AspectRatio;
        public int ColorSpace;
        public int SampleRate;
        public int Channels;
        public int SamplesPerChannel;
        public IntPtr Data;
        public int DataLength;
        public IntPtr CompressedData;
        public int CompressedLength;
        public IntPtr FrameMetadata;
        public int FrameMetadataLength;
    }

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr omt_discovery_getaddresses(out int count);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern long omt_receive_create([MarshalAs(UnmanagedType.LPUTF8Str)] string address, int frameTypes, int format, int flags);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern void omt_receive_destroy(long instance);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr omt_receive(long instance, int frameTypes, int timeoutMilliseconds);

    /// <summary>Current list of OMT sources in the network ("HOST (Name)"). The memory belongs to libomt.</summary>
    public static List<string> Discover()
    {
        var list = new List<string>();
        IntPtr arr = omt_discovery_getaddresses(out int count);
        if (arr == IntPtr.Zero) return list;
        for (int i = 0; i < count; i++)
        {
            IntPtr s = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
            string? name = Marshal.PtrToStringUTF8(s);
            if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
