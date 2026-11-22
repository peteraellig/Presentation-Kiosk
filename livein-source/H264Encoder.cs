using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace LiveIn;

/// <summary>
/// H.264 encoder on Windows Media Foundation, preferably the hardware encoder (NVENC / Quick Sync / AMF), software as fallback.
/// Input is the UYVY picture as OMT delivers it (VMX decodes to UYVY, so no BGRA detour); it is repacked to NV12 once,
/// straight into the encoder's input buffer. Low-latency mode, no B-frames, CBR, one keyframe per second plus one on request
/// (a page that connects gets a keyframe at once). Output: Annex B access units.
/// </summary>
internal sealed unsafe class H264Encoder : IDisposable
{
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    private const int MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

    private static readonly object StartupLock = new();
    private static bool _started;

    public int Width { get; }
    public int Height { get; }
    public int RateN { get; }
    public int RateD { get; }
    public string Name { get; private set; } = "";
    public bool Hardware { get; private set; }

    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator? _events;   // null = synchronous (software) encoder
    private readonly IntPtr _codecApi;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private byte[]? _sequenceHeader;
    private readonly long _duration;   // 100 ns per picture
    // encoder delay: time from ProcessInput to the access unit with the same sample time
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _fedAt = new();
    private double _delaySum;
    private int _delayCount;
    public double TakeAverageDelayMs()
    {
        double avg = _delayCount > 0 ? _delaySum / _delayCount : 0;
        _delaySum = 0; _delayCount = 0;
        return avg;
    }

    public H264Encoder(int width, int height, int rateN, int rateD)
    {
        Width = width; Height = height; RateN = rateN > 0 ? rateN : 50; RateD = rateD > 0 ? rateD : 1;
        _duration = 10_000_000L * RateD / RateN;
        lock (StartupLock)
        {
            if (!_started) { MediaFactory.MFStartup(false).CheckError(); _started = true; }
        }

        Exception? last = null;
        foreach (var (activate, hardware) in Candidates())
        {
            {
                try
                {
                    var mft = activate.ActivateObject<IMFTransform>();
                    string name = FriendlyName(activate);
                    try
                    {
                        Configure(mft, hardware, out _events, out _codecApi);
                        _mft = mft;
                        Name = name;
                        Hardware = hardware;
                        var info = mft.GetOutputStreamInfo(0);
                        _providesSamples = (info.Flags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
                        _outputSize = Math.Max(info.Size, width * height * 2);
                        return;
                    }
                    catch (Exception e)
                    {
                        last = new InvalidOperationException($"{name}: {e.Message}", e);
                        mft.Dispose();
                    }
                }
                catch (Exception e) { last = e; }
            }
        }
        throw new InvalidOperationException("No H.264 encoder could be started: " + last?.Message, last);
    }

    /// <summary>Encoder choice: "auto" (Windows order: hardware first, software last), "intel" (Quick Sync), "nvidia" (NVENC),
    /// "amd" (AMF) or "software". A chosen vendor that is missing or fails falls back to the automatic order;
    /// the status line always shows the encoder actually in use.</summary>
    public static string Preference { get; set; } = "auto";

    private static readonly Dictionary<string, string> VendorIds = new()
    {
        ["intel"] = "VEN_8086", ["nvidia"] = "VEN_10DE", ["amd"] = "VEN_1002",
    };
    private static readonly Dictionary<string, string> VendorNames = new()
    {
        ["intel"] = "Intel", ["nvidia"] = "NVIDIA", ["amd"] = "AMD",
    };

    private static string FriendlyName(IMFActivate a)
    {
        try { return a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { return ""; }
    }

    private static string VendorId(IMFActivate a)
    {
        try { return a.GetString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute); } catch { return ""; }
    }

    private static bool IsVendor(IMFActivate a, string vendor) =>
        (VendorIds.TryGetValue(vendor, out var id) && VendorId(a).Contains(id, StringComparison.OrdinalIgnoreCase))
        || (VendorNames.TryGetValue(vendor, out var n) && FriendlyName(a).Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Encoders in the order they are tried, according to <see cref="Preference"/>.</summary>
    private static List<(IMFActivate, bool)> Candidates()
    {
        var hw = Enumerate(true);
        var sw = Enumerate(false);
        var list = new List<(IMFActivate, bool)>();
        string p = (Preference ?? "auto").ToLowerInvariant();
        if (p == "software")
        {
            list.AddRange(sw.Select(a => (a, false)));
            list.AddRange(hw.Select(a => (a, true)));
            return list;
        }
        if (VendorIds.ContainsKey(p)) list.AddRange(hw.Where(a => IsVendor(a, p)).Select(a => (a, true)));
        list.AddRange(hw.Where(a => !list.Any(c => ReferenceEquals(c.Item1, a))).Select(a => (a, true)));
        list.AddRange(sw.Select(a => (a, false)));
        return list;
    }

    /// <summary>For --list-encoders: every H.264 encoder Windows offers on this PC.</summary>
    public static List<string> ListEncoders()
    {
        lock (StartupLock)
        {
            if (!_started) { MediaFactory.MFStartup(false).CheckError(); _started = true; }
        }
        var list = new List<string>();
        foreach (var a in Enumerate(true)) list.Add($"hardware  {FriendlyName(a)}  {VendorId(a)}");
        foreach (var a in Enumerate(false)) list.Add($"software  {FriendlyName(a)}");
        return list;
    }

    private static List<IMFActivate> Enumerate(bool hardware)
    {
        uint flags = (uint)(EnumFlag.EnumFlagSortandfilter | (hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft));
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var list = new List<IMFActivate>();
        foreach (var a in MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output)) list.Add(a);
        return list;
    }

    private void Configure(IMFTransform mft, bool hardware, out IMFMediaEventGenerator? events, out IntPtr codecApi)
    {
        var attrs = mft.Attributes;
        if (hardware) attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        attrs.Set(MF_LOW_LATENCY, 1u);

        codecApi = IntPtr.Zero;
        var iid = IID_ICodecAPI;
        if (Marshal.QueryInterface(mft.NativePointer, ref iid, out var api) == 0) codecApi = api;
        int bitrate = Bitrate();
        SetCodec(codecApi, CODECAPI_AVLowLatencyMode, true);
        SetCodec(codecApi, CODECAPI_AVEncCommonRateControlMode, 0u);            // CBR
        SetCodec(codecApi, CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
        SetCodec(codecApi, CODECAPI_AVEncMPVDefaultBPictureCount, 0u);
        SetCodec(codecApi, CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(1, (RateN + RateD - 1) / RateD));   // one keyframe per second

        // the output type must be set before the input type
        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        outType.Set(MediaTypeAttributeKeys.FrameRate, Pack(RateN, RateD));
        outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);                   // progressive
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u);                  // High
        mft.SetOutputType(0, outType, 0);

        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        inType.Set(MediaTypeAttributeKeys.FrameRate, Pack(RateN, RateD));
        inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        mft.SetInputType(0, inType, 0);

        events = hardware ? mft.QueryInterface<IMFMediaEventGenerator>() : null;
        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>About 0.2 bit per pixel and picture: 1080p50 = 20 Mbit/s (local only, quality before bandwidth).</summary>
    private int Bitrate() => (int)Math.Clamp((long)Width * Height * RateN / RateD / 5, 4_000_000, 40_000_000);

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public short Bool;
    }

    private static void SetCodec(IntPtr api, Guid key, uint value) => SetCodec(api, key, new Variant { Vt = 19, UInt = value });           // VT_UI4
    private static void SetCodec(IntPtr api, Guid key, bool value) => SetCodec(api, key, new Variant { Vt = 11, Bool = (short)(value ? -1 : 0) });   // VT_BOOL

    private static int SetCodec(IntPtr api, Guid key, Variant v)
    {
        if (api == IntPtr.Zero) return -1;
        // ICodecAPI::SetValue is slot 9 (IUnknown 3, IsSupported, IsModifiable, GetParameterRange, GetParameterValues, GetDefaultValue, GetValue)
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)(*(*(IntPtr**)api + 9));
        return setValue(api, &key, &v);
    }

    /// <summary>Next picture becomes a keyframe (a new page has connected).</summary>
    public void ForceKeyFrame() => SetCodec(_codecApi, CODECAPI_AVEncVideoForceKeyFrame, 1u);

    /// <summary>
    /// Runs the encoder. <paramref name="next"/> blocks until a UYVY picture is available and returns null to stop;
    /// <paramref name="output"/> gets each access unit (Annex B, SPS/PPS in front of every keyframe), keyframe flag and time (100 ns).
    /// </summary>
    public void Run(Func<VideoFrame?> next, Action<byte[], int, bool, long> output)
    {
        if (_events == null) { RunSync(next, output); return; }
        while (true)
        {
            using var ev = _events.GetEvent(0);
            switch (ev.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    var frame = next();
                    if (frame == null) return;
                    Feed(frame);
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    Drain(output);
                    break;
            }
        }
    }

    private void RunSync(Func<VideoFrame?> next, Action<byte[], int, bool, long> output)
    {
        while (true)
        {
            var frame = next();
            if (frame == null) return;
            Feed(frame);
            while (Drain(output)) { }
        }
    }

    private void Feed(VideoFrame frame)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();   // delay includes the UYVY->NV12 repacking
        int ySize = Width * Height;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(ySize * 3 / 2);
        buffer.Lock(out IntPtr dst, out _, out _);
        try
        {
            fixed (byte* src = frame.Pixels)
                UyvyToNv12(src, (byte*)dst, Width, Height, frame.Interlaced);
        }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = ySize * 3 / 2;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = frame.Time;
        sample.SampleDuration = _duration;
        frame.Release();
        if (_fedAt.Count > 200) _fedAt.Clear();
        _fedAt[sample.SampleTime] = start;
        _mft.ProcessInput(0, sample, 0);
    }

    /// <summary>Gets one access unit. Returns false when the encoder needs more input.</summary>
    private bool Drain(Action<byte[], int, bool, long> output)
    {
        IMFSample? own = null;
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(MediaFactory.MFCreateMemoryBuffer(_outputSize));
        }
        var data = new OutputDataBuffer { StreamID = 0, Sample = own! };
        var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
        try
        {
            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                var t = _mft.GetOutputAvailableType(0, 0);
                _mft.SetOutputType(0, t, 0);
                t.Dispose();
                _sequenceHeader = null;
                return true;
            }
            hr.CheckError();
            var sample = data.Sample;
            if (sample == null) return true;
            if (_sequenceHeader == null)
            {
                try
                {
                    using var cur = _mft.GetOutputCurrentType(0);
                    _sequenceHeader = cur.GetBlob(MF_MT_MPEG_SEQUENCE_HEADER);
                }
                catch { _sequenceHeader = Array.Empty<byte>(); }
            }
            using var buf = sample.ConvertToContiguousBuffer();
            buf.Lock(out IntPtr p, out _, out int len);
            try
            {
                var span = new ReadOnlySpan<byte>((void*)p, len);
                bool key = HasNal(span, 5);
                bool needHeader = key && !HasNal(span, 7) && _sequenceHeader is { Length: > 0 };
                int total = len + (needHeader ? _sequenceHeader!.Length : 0);
                var bytes = new byte[total];
                int at = 0;
                if (needHeader) { _sequenceHeader!.CopyTo(bytes, 0); at = _sequenceHeader.Length; }
                span.CopyTo(bytes.AsSpan(at));
                if (_fedAt.TryRemove(sample.SampleTime, out long fed))
                {
                    _delaySum += System.Diagnostics.Stopwatch.GetElapsedTime(fed).TotalMilliseconds;
                    _delayCount++;
                }
                output(bytes, total, key, sample.SampleTime);
            }
            finally { buf.Unlock(); }
            return true;
        }
        finally
        {
            if (data.Sample != null && !ReferenceEquals(data.Sample, own)) data.Sample.Dispose();
            own?.Dispose();
            data.Events?.Dispose();
        }
    }

    /// <summary>Annex B: is there a NAL unit of this type (5 = IDR slice, 7 = SPS)?</summary>
    private static bool HasNal(ReadOnlySpan<byte> s, int type)
    {
        for (int i = 0; i + 3 < s.Length; i++)
        {
            if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1)
            {
                if ((s[i + 3] & 0x1F) == type) return true;
                i += 2;
            }
        }
        return false;
    }

    /// <summary>
    /// UYVY (U Y0 V Y1) to NV12 in one pass. Chroma of two lines is averaged (4:2:2 -> 4:2:0).
    /// Interlaced pictures: each luma line is blended with the next one (no combing, no extra delay).
    /// </summary>
    private static void UyvyToNv12(byte* src, byte* dst, int w, int h, bool interlaced)
    {
        int row = w * 2;
        byte* yPlane = dst;
        byte* uv = dst + w * h;
        nint s = (nint)src, yp = (nint)yPlane, up = (nint)uv;
        Parallel.For(0, h / 2, new ParallelOptions { MaxDegreeOfParallelism = 4 }, pair =>
        {
            byte* srcB = (byte*)s, yB = (byte*)yp, uvB = (byte*)up;
            int y0 = pair * 2;
            byte* a = srcB + (long)y0 * row;
            byte* b = a + row;
            byte* c = y0 + 2 < h ? b + row : b;   // line below the pair (for the interlace blend)
            byte* ya = yB + (long)y0 * w;
            byte* yb = ya + w;
            byte* uvRow = uvB + (long)pair * w;
            if (!interlaced)
            {
                for (int x = 0; x < w; x += 2)
                {
                    int i = x * 2;
                    ya[x] = a[i + 1]; ya[x + 1] = a[i + 3];
                    yb[x] = b[i + 1]; yb[x + 1] = b[i + 3];
                    uvRow[x] = (byte)((a[i] + b[i] + 1) >> 1);
                    uvRow[x + 1] = (byte)((a[i + 2] + b[i + 2] + 1) >> 1);
                }
            }
            else
            {
                for (int x = 0; x < w; x += 2)
                {
                    int i = x * 2;
                    ya[x] = (byte)((a[i + 1] + b[i + 1] + 1) >> 1); ya[x + 1] = (byte)((a[i + 3] + b[i + 3] + 1) >> 1);
                    yb[x] = (byte)((b[i + 1] + c[i + 1] + 1) >> 1); yb[x + 1] = (byte)((b[i + 3] + c[i + 3] + 1) >> 1);
                    uvRow[x] = (byte)((a[i] + b[i] + 1) >> 1);
                    uvRow[x + 1] = (byte)((a[i + 2] + b[i + 2] + 1) >> 1);
                }
            }
        });
    }

    public void Dispose()
    {
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        if (_codecApi != IntPtr.Zero) Marshal.Release(_codecApi);
        _events?.Dispose();
        _mft.Dispose();
    }
}

/// <summary>One received UYVY picture (pooled buffer).</summary>
internal sealed class VideoFrame
{
    public byte[] Pixels = Array.Empty<byte>();
    public int Width, Height;
    public bool Interlaced;
    public long Time;
    public int RateN, RateD;

    public void Release()
    {
        if (Pixels.Length > 0) System.Buffers.ArrayPool<byte>.Shared.Return(Pixels);
        Pixels = Array.Empty<byte>();
    }
}
