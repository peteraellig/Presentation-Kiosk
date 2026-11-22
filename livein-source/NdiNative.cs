using System.Runtime.InteropServices;

namespace LiveIn;

/// <summary>
/// NDI receive side through the installed NDI runtime (NDI Tools or NDI Runtime from ndi.video; the DLL is not shipped).
/// Loaded at run time: without NDI installed, LiveIn simply offers no NDI sources. Struct layouts follow Processing.NDI.Lib.h.
/// NDI® is a registered trademark of Vizrt NDI AB.
/// </summary>
internal static unsafe class NdiNative
{
    public const int FrameNone = 0, FrameVideo = 1, FrameAudio = 2, FrameMetadata = 3, FrameError = 4, FrameStatusChange = 100;
    public const int ColorUyvyBgra = 1;            // UYVY without alpha, BGRA with alpha
    public const int BandwidthHighest = 100;
    public const int FormatInterleaved = 0, FormatProgressive = 1;
    public const uint FourCcUyvy = 0x59565955, FourCcBgra = 0x41524742, FourCcBgrx = 0x58524742;
    public const long TimestampUndefined = long.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct Source { public IntPtr Name; public IntPtr Url; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FindSettings { public byte ShowLocal; public IntPtr Groups; public IntPtr ExtraIps; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvSettings { public Source Source; public int ColorFormat; public int Bandwidth; public byte AllowFields; public IntPtr Name; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrame
    {
        public int Width, Height;
        public uint FourCc;
        public int RateN, RateD;
        public float Aspect;
        public int FrameFormat;
        public long Timecode;
        public IntPtr Data;
        public int Stride;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrame
    {
        public int SampleRate, Channels, Samples;
        public long Timecode;
        public IntPtr Data;          // planar float32
        public int ChannelStride;    // bytes from one channel to the next
        public IntPtr Metadata;
        public long Timestamp;
    }

    private static delegate* unmanaged[Cdecl]<byte> _initialize;
    private static delegate* unmanaged[Cdecl]<FindSettings*, IntPtr> _findCreate;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint*, Source*> _findSources;
    private static delegate* unmanaged[Cdecl]<RecvSettings*, IntPtr> _recvCreate;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _recvDestroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, AudioFrame*, IntPtr, uint, int> _capture;
    private static delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void> _freeVideo;
    private static delegate* unmanaged[Cdecl]<IntPtr, AudioFrame*, void> _freeAudio;
    private static IntPtr _finder;

    public static bool Available { get; private set; }
    // address of each source from the search: passing it lets the receiver connect at once instead of looking the name up again
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Urls = new();
    public static string Status { get; private set; } = "not loaded";

    /// <summary>Loads the NDI runtime and starts the source search. Returns false (with <see cref="Status"/>) when NDI is not installed.</summary>
    public static bool Load()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6") ?? "", "Processing.NDI.Lib.x64.dll"),
            Path.Combine(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5") ?? "", "Processing.NDI.Lib.x64.dll"),
            @"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll",
            @"C:\Program Files\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll",
        };
        string? path = candidates.FirstOrDefault(p => p.Length > 30 && File.Exists(p));
        if (path == null) { Status = "NDI runtime not installed (NDI Tools from ndi.video)"; return false; }
        try
        {
            var lib = NativeLibrary.Load(path);
            _initialize = (delegate* unmanaged[Cdecl]<byte>)NativeLibrary.GetExport(lib, "NDIlib_initialize");
            _findCreate = (delegate* unmanaged[Cdecl]<FindSettings*, IntPtr>)NativeLibrary.GetExport(lib, "NDIlib_find_create_v2");
            _findSources = (delegate* unmanaged[Cdecl]<IntPtr, uint*, Source*>)NativeLibrary.GetExport(lib, "NDIlib_find_get_current_sources");
            _recvCreate = (delegate* unmanaged[Cdecl]<RecvSettings*, IntPtr>)NativeLibrary.GetExport(lib, "NDIlib_recv_create_v3");
            _recvDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "NDIlib_recv_destroy");
            _capture = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, AudioFrame*, IntPtr, uint, int>)NativeLibrary.GetExport(lib, "NDIlib_recv_capture_v2");
            _freeVideo = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void>)NativeLibrary.GetExport(lib, "NDIlib_recv_free_video_v2");
            _freeAudio = (delegate* unmanaged[Cdecl]<IntPtr, AudioFrame*, void>)NativeLibrary.GetExport(lib, "NDIlib_recv_free_audio_v2");
            if (_initialize() == 0) { Status = "NDI initialization failed (CPU not supported?)"; return false; }
            var settings = new FindSettings { ShowLocal = 1 };
            _finder = _findCreate(&settings);
            if (_finder == IntPtr.Zero) { Status = "NDI source search could not start"; return false; }
            Available = true;
            Status = "NDI runtime: " + path;
            return true;
        }
        catch (Exception e)
        {
            Status = "NDI runtime could not be loaded: " + e.Message;
            return false;
        }
    }

    /// <summary>Names of the NDI sources found so far ("HOST (Name)").</summary>
    public static List<string> Discover()
    {
        var list = new List<string>();
        if (!Available) return list;
        uint count = 0;
        Source* s = _findSources(_finder, &count);
        for (int i = 0; i < count && s != null; i++)
        {
            string? name = Marshal.PtrToStringUTF8(s[i].Name);
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(name);
            string? url = Marshal.PtrToStringUTF8(s[i].Url);
            if (!string.IsNullOrEmpty(url)) Urls[name] = url;
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static IntPtr ReceiveCreate(string sourceName)
    {
        if (!Available) return IntPtr.Zero;
        IntPtr name = Marshal.StringToCoTaskMemUTF8(sourceName);
        IntPtr url = Urls.TryGetValue(sourceName, out var u) ? Marshal.StringToCoTaskMemUTF8(u) : IntPtr.Zero;
        IntPtr recvName = Marshal.StringToCoTaskMemUTF8("Kiosk3 Live");
        try
        {
            // allow_fields = 0: interlaced sources arrive as woven frames (blended later, like OMT)
            var settings = new RecvSettings { Source = new Source { Name = name, Url = url }, ColorFormat = ColorUyvyBgra, Bandwidth = BandwidthHighest, AllowFields = 0, Name = recvName };
            return _recvCreate(&settings);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            if (url != IntPtr.Zero) Marshal.FreeCoTaskMem(url);
            Marshal.FreeCoTaskMem(recvName);
        }
    }

    public static void ReceiveDestroy(IntPtr recv) { if (recv != IntPtr.Zero) _recvDestroy(recv); }

    public static int Capture(IntPtr recv, VideoFrame* video, AudioFrame* audio, uint timeoutMs) => _capture(recv, video, audio, IntPtr.Zero, timeoutMs);
    public static void FreeVideo(IntPtr recv, VideoFrame* video) => _freeVideo(recv, video);
    public static void FreeAudio(IntPtr recv, AudioFrame* audio) => _freeAudio(recv, audio);
}
