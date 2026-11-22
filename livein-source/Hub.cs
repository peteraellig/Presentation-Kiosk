using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LiveIn;

/// <summary>
/// Receives one OMT source (UYVY video + planar float audio) on its own thread, encodes the pictures to H.264 on a second thread
/// and hands every access unit and audio block to the clients that watch this source. Only the newest picture waits for the
/// encoder (a slow encoder drops pictures instead of building up delay). Runs until no client has watched it for a while.
/// </summary>
internal sealed class SourceReceiver
{
    public string Address { get; }
    private readonly List<Client> _clients = new();
    private volatile bool _stop;
    public DateTime EmptySince = DateTime.UtcNow;

    private VideoFrame? _latest;
    private readonly SemaphoreSlim _frameReady = new(0);
    private volatile bool _keyRequested = true;

    // statistics for the status line
    public int Frames;            // received pictures since the last status line
    public int Encoded;           // encoded pictures since the last status line
    public int Skipped;           // pictures the encoder never saw (it was busy)
    public long EncodedBytes;
    public int Width, Height;
    public bool Interlaced;
    public DateTime LastFrame = DateTime.MinValue;
    public string EncoderName = "";
    public H264Encoder? Encoder;

    // still picture for the remote page, about once per second
    public volatile byte[]? PreviewJpeg;
    private long _lastPreviewTicks;
    private int _previewBusy;
    public string Error = "";

    public SourceReceiver(string address)
    {
        Address = address;
        new Thread(Receive) { IsBackground = true, Name = "OMT " + address }.Start();
        new Thread(Encode) { IsBackground = true, Name = "H.264 " + address }.Start();
    }

    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    public void Add(Client c)
    {
        lock (_clients) if (!_clients.Contains(c)) _clients.Add(c);
        RequestKeyFrame();
    }

    public void Remove(Client c)
    {
        lock (_clients)
        {
            if (_clients.Remove(c) && _clients.Count == 0) EmptySince = DateTime.UtcNow;
        }
    }

    public void RequestKeyFrame() => _keyRequested = true;

    public void Stop()
    {
        _stop = true;
        _frameReady.Release();
    }

    private Client[] Snapshot() { lock (_clients) return _clients.ToArray(); }

    public const string NdiPrefix = "ndi:";

    private void Receive()
    {
        try
        {
            if (Address.StartsWith(NdiPrefix, StringComparison.OrdinalIgnoreCase)) ReceiveNdi(Address[NdiPrefix.Length..]);
            else ReceiveOmt();
        }
        catch (Exception e)
        {
            Error = e.Message;
            Console.WriteLine("Receive failed for " + Address + ": " + e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _latest, null)?.Release();
        }
    }

    private void ReceiveOmt()
    {
        long inst = OmtNative.omt_receive_create(Address, OmtNative.FrameTypeVideo | OmtNative.FrameTypeAudio,
                                                 OmtNative.PreferredUyvy, OmtNative.ReceiveFlagsNone);
        if (inst == 0) { Error = "omt_receive_create failed"; return; }
        try
        {
            while (!_stop)
            {
                IntPtr p = OmtNative.omt_receive(inst, OmtNative.FrameTypeVideo | OmtNative.FrameTypeAudio, 200);
                if (p == IntPtr.Zero) continue;
                var f = Marshal.PtrToStructure<OmtNative.MediaFrame>(p);
                if (f.Type == OmtNative.FrameTypeVideo)
                {
                    if (f.Codec != OmtNative.CodecUyvy && f.Codec != OmtNative.CodecUyva) { Error = "unexpected OMT pixel format"; continue; }
                    OnVideo(f.Data, f.Width, f.Height, f.Stride > 0 ? f.Stride : f.Width * 2, f.DataLength, bgra: false,
                            (f.Flags & OmtNative.FlagInterlaced) != 0, f.Timestamp, f.FrameRateN, f.FrameRateD);
                }
                else if (f.Type == OmtNative.FrameTypeAudio && f.Codec == OmtNative.CodecFpa1)
                {
                    if ((long)f.SamplesPerChannel * f.Channels * 4 > f.DataLength) continue;
                    OnAudio(f.Data, f.Channels, f.SamplesPerChannel, f.SamplesPerChannel * 4, f.SampleRate);
                }
            }
        }
        finally { OmtNative.omt_receive_destroy(inst); }
    }

    /// <summary>NDI: UYVY (BGRA only for sources with alpha), woven frames for interlaced sources, planar float audio.</summary>
    private unsafe void ReceiveNdi(string name)
    {
        if (!NdiNative.Available) { Error = NdiNative.Status; return; }
        IntPtr recv = NdiNative.ReceiveCreate(name);
        if (recv == IntPtr.Zero) { Error = "NDI receiver could not start"; return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();   // own clock: NDI timestamps may be undefined
        try
        {
            while (!_stop)
            {
                NdiNative.VideoFrame v;
                NdiNative.AudioFrame a;
                int type = NdiNative.Capture(recv, &v, &a, 200);
                if (type == NdiNative.FrameVideo)
                {
                    try
                    {
                        bool bgra = v.FourCc == NdiNative.FourCcBgra || v.FourCc == NdiNative.FourCcBgrx;
                        if (v.FourCc != NdiNative.FourCcUyvy && !bgra) { Error = "unexpected NDI pixel format"; continue; }
                        int stride = v.Stride > 0 ? v.Stride : v.Width * (bgra ? 4 : 2);
                        OnVideo(v.Data, v.Width, v.Height, stride, stride * v.Height, bgra,
                                v.FrameFormat == NdiNative.FormatInterleaved, clock.Elapsed.Ticks, v.RateN, v.RateD);
                    }
                    finally { NdiNative.FreeVideo(recv, &v); }
                }
                else if (type == NdiNative.FrameAudio)
                {
                    try { OnAudio(a.Data, a.Channels, a.Samples, a.ChannelStride, a.SampleRate); }
                    finally { NdiNative.FreeAudio(recv, &a); }
                }
                else if (type == NdiNative.FrameError)
                {
                    Error = "NDI connection lost";
                    Thread.Sleep(200);
                }
            }
        }
        finally { NdiNative.ReceiveDestroy(recv); }
    }

    /// <summary>Copies the picture (valid only until the SDK's next call) as tight UYVY and offers it to the encoder.</summary>
    private unsafe void OnVideo(IntPtr data, int width, int height, int stride, int dataLength, bool bgra, bool interlaced, long time, int rateN, int rateD)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 2304 || (width & 1) != 0 || (height & 1) != 0 || data == IntPtr.Zero) return;
        int row = width * 2;
        int srcRow = width * (bgra ? 4 : 2);
        if (stride < srcRow || (long)stride * (height - 1) + srcRow > dataLength) return;
        var frame = new VideoFrame
        {
            Pixels = ArrayPool<byte>.Shared.Rent(row * height),
            Width = width, Height = height, Interlaced = interlaced,
            Time = time, RateN = rateN, RateD = rateD,
        };
        byte* src = (byte*)data;
        if (bgra)
            BgraToUyvy(src, stride, frame.Pixels, width, height);
        else if (stride == row)
            new ReadOnlySpan<byte>(src, row * height).CopyTo(frame.Pixels);
        else
            for (int y = 0; y < height; y++)
                new ReadOnlySpan<byte>(src + (long)y * stride, row).CopyTo(frame.Pixels.AsSpan(y * row));
        if (Error is "unexpected OMT pixel format" or "unexpected NDI pixel format" or "NDI connection lost") Error = "";
        Interlocked.Increment(ref Frames);
        Width = width; Height = height; Interlaced = interlaced;
        LastFrame = DateTime.UtcNow;
        long now = Environment.TickCount64;
        if (now - _lastPreviewTicks >= 1000 && Interlocked.CompareExchange(ref _previewBusy, 1, 0) == 0)
        {
            _lastPreviewTicks = now;
            int bytes = row * height;
            var copy = ArrayPool<byte>.Shared.Rent(bytes);
            Buffer.BlockCopy(frame.Pixels, 0, copy, 0, bytes);
            Task.Run(() =>
            {
                try { PreviewJpeg = Preview.Make(copy, width, height); }
                catch (Exception e) { Console.WriteLine("Preview failed: " + e.Message); }
                finally { Volatile.Write(ref _previewBusy, 0); }
            });
        }
        var old = Interlocked.Exchange(ref _latest, frame);
        if (old != null) { old.Release(); Interlocked.Increment(ref Skipped); }
        _frameReady.Release();
    }

    /// <summary>Rare case (NDI source with alpha): BGRA to UYVY, BT.709 limited range, alpha dropped.</summary>
    private static unsafe void BgraToUyvy(byte* src, int stride, byte[] dst, int w, int h)
    {
        fixed (byte* d0 = dst)
        {
            nint s = (nint)src, d = (nint)d0;
            Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = 4 }, y =>
            {
                byte* line = (byte*)s + (long)y * stride;
                byte* o = (byte*)d + (long)y * w * 2;
                for (int x = 0; x < w; x += 2)
                {
                    byte* p = line + x * 4;
                    float b0 = p[0], g0 = p[1], r0 = p[2], b1 = p[4], g1 = p[5], r1 = p[6];
                    float y0 = 16 + 0.1826f * r0 + 0.6142f * g0 + 0.0620f * b0;
                    float y1 = 16 + 0.1826f * r1 + 0.6142f * g1 + 0.0620f * b1;
                    float r = (r0 + r1) * 0.5f, g = (g0 + g1) * 0.5f, b = (b0 + b1) * 0.5f;
                    float u = 128 - 0.1006f * r - 0.3386f * g + 0.4392f * b;
                    float v = 128 + 0.4392f * r - 0.3989f * g - 0.0403f * b;
                    o[x * 2] = (byte)Math.Clamp(u + 0.5f, 0, 255);
                    o[x * 2 + 1] = (byte)Math.Clamp(y0 + 0.5f, 0, 255);
                    o[x * 2 + 2] = (byte)Math.Clamp(v + 0.5f, 0, 255);
                    o[x * 2 + 3] = (byte)Math.Clamp(y1 + 0.5f, 0, 255);
                }
            });
        }
    }

    /// <summary>Audio packet for the clients: first two channels (mono on both sides), planar float.</summary>
    private void OnAudio(IntPtr data, int channels, int samples, int channelStride, int sampleRate)
    {
        var packet = AudioPacket(data, channels, samples, channelStride, sampleRate);
        if (packet == null) return;
        foreach (var c in Snapshot()) c.Enqueue(packet.AddRef(), isVideo: false, key: false);
        packet.Release();
    }

    private VideoFrame? WaitFrame()
    {
        while (!_stop)
        {
            var f = Interlocked.Exchange(ref _latest, null);
            if (f != null) return f;
            _frameReady.Wait(200);
        }
        return null;
    }

    private void Encode()
    {
        VideoFrame? carry = null;
        while (!_stop)
        {
            var first = carry ?? WaitFrame();
            carry = null;
            if (first == null) break;
            H264Encoder enc;
            try
            {
                enc = new H264Encoder(first.Width, first.Height, first.RateN, first.RateD);
                EncoderName = enc.Name + (enc.Hardware ? "" : " (software)");
                Error = "";
                Console.WriteLine($"Encoder for {Address}: {EncoderName}, {first.Width}x{first.Height} @ {first.RateN}/{first.RateD}");
            }
            catch (Exception e)
            {
                Error = e.Message;
                Console.WriteLine("Encoder failed: " + e.Message);
                first.Release();
                Thread.Sleep(2000);
                continue;
            }
            _keyRequested = true;
            Encoder = enc;
            try
            {
                enc.Run(() =>
                {
                    var x = first ?? WaitFrame();
                    first = null;
                    if (x == null) return null;
                    if (x.Width != enc.Width || x.Height != enc.Height || x.RateN != enc.RateN || x.RateD != enc.RateD)
                    {
                        carry = x;     // new format: start a new encoder with this picture
                        return null;
                    }
                    if (_keyRequested) { _keyRequested = false; enc.ForceKeyFrame(); }
                    return x;
                }, (data, length, key, time) => Output(data, length, key, time, enc.Width, enc.Height));
            }
            catch (Exception e)
            {
                Error = e.Message;
                Console.WriteLine("Encoder stopped: " + e.Message);
                Thread.Sleep(500);
            }
            finally
            {
                Encoder = null;
                first?.Release();
                enc.Dispose();
            }
        }
        carry?.Release();
    }

    /// <summary>Header: [0]=3, [1]=flags (bit0 keyframe), [2..3]=width, [4..5]=height, [8..15]=time in microseconds; then Annex B.</summary>
    private void Output(byte[] data, int length, bool key, long time, int width, int height)
    {
        var packet = Packet.Rent(Packet.HeaderSize + length);
        var b = packet.Buffer;
        b[0] = Packet.TypeH264;
        b[1] = (byte)(key ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 0);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), time / 10);
        data.AsSpan(0, length).CopyTo(b.AsSpan(Packet.HeaderSize));
        Interlocked.Increment(ref Encoded);
        Interlocked.Add(ref EncodedBytes, length);
        foreach (var c in Snapshot()) c.Enqueue(packet.AddRef(), isVideo: true, key: key);
        packet.Release();
    }

    /// <summary>Header: [0]=2, [1]=channels (always 2), [4..7]=sample rate, [8..11]=samples per channel; then planar float L, R.</summary>
    private static unsafe Packet? AudioPacket(IntPtr data, int channels, int n, int channelStride, int sampleRate)
    {
        if (data == IntPtr.Zero || channels <= 0 || n <= 0 || n > 96000 || channelStride < n * 4 || sampleRate <= 0) return null;
        var packet = Packet.Rent(Packet.HeaderSize + n * 8);
        var b = packet.Buffer;
        b[0] = Packet.TypeAudio;
        b[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), n);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 0);
        byte* src = (byte*)data;
        var left = new ReadOnlySpan<byte>(src, n * 4);
        var right = channels > 1 ? new ReadOnlySpan<byte>(src + channelStride, n * 4) : left;   // mono -> both sides
        left.CopyTo(b.AsSpan(Packet.HeaderSize));
        right.CopyTo(b.AsSpan(Packet.HeaderSize + n * 4));
        return packet;
    }
}

/// <summary>
/// One page connected over WebSocket. H.264 pictures depend on each other, so everything is sent in order;
/// a new page starts at a keyframe, and a page that falls behind (more than ~0.5 s queued) is reset to the next keyframe.
/// </summary>
internal sealed class Client
{
    private readonly WebSocket _ws;
    private readonly Hub _hub;
    private readonly ConcurrentQueue<Packet> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _waitKey = true;
    private readonly object _gate = new();
    public SourceReceiver? Source;
    public long Resets;

    public Client(WebSocket ws, Hub hub) { _ws = ws; _hub = hub; }

    public void Enqueue(Packet p, bool isVideo, bool key)
    {
        lock (_gate)
        {
            if (isVideo)
            {
                if (_waitKey && !key) { p.Release(); return; }
                if (key) _waitKey = false;
            }
            _queue.Enqueue(p);
            if (_queue.Count > 60)
            {
                while (_queue.TryDequeue(out var drop)) drop.Release();
                _waitKey = true;
                Interlocked.Increment(ref Resets);
                Source?.RequestKeyFrame();
            }
        }
        _signal.Release();
    }

    public async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        var send = SendLoop(cts.Token);
        try
        {
            var buf = new byte[4096];
            while (_ws.State == WebSocketState.Open)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > 65536) return;
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Text) OnText(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (Exception) { /* page closed */ }
        finally
        {
            _hub.Leave(this);
            cts.Cancel();
            _signal.Release();
            try { await send; } catch { }
            while (_queue.TryDequeue(out var a)) a.Release();
            try { _ws.Abort(); } catch { }
            _ws.Dispose();
        }
    }

    private void OnText(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
            {
                string source = s.GetString()!.Trim();
                if (source.Length is > 0 and <= 300)
                {
                    lock (_gate) { while (_queue.TryDequeue(out var drop)) drop.Release(); _waitKey = true; }
                    _hub.Watch(this, source);
                }
            }
            else if (doc.RootElement.TryGetProperty("keyframe", out _))
            {
                // the page's decoder failed: start again at the next keyframe
                lock (_gate) _waitKey = true;
                Source?.RequestKeyFrame();
            }
        }
        catch (JsonException) { }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);
            while (_ws.State == WebSocketState.Open && _queue.TryDequeue(out var p))
            {
                try { await _ws.SendAsync(p.Memory, WebSocketMessageType.Binary, true, ct); }
                finally { p.Release(); }
            }
        }
    }
}

/// <summary>Keeps one receiver per watched source; stops a receiver 10 s after its last client left.</summary>
internal sealed class Hub
{
    private readonly Dictionary<string, SourceReceiver> _receivers = new(StringComparer.Ordinal);
    private readonly List<Client> _clients = new();
    private readonly object _lock = new();

    public void Join(Client c) { lock (_lock) _clients.Add(c); }

    public void Watch(Client c, string address)
    {
        lock (_lock)
        {
            if (c.Source?.Address == address) { c.Source.RequestKeyFrame(); return; }
            c.Source?.Remove(c);
            if (!_receivers.TryGetValue(address, out var r))
            {
                r = new SourceReceiver(address);
                _receivers[address] = r;
            }
            c.Source = r;
            r.Add(c);
        }
    }

    /// <summary>Newest still picture (JPEG) of a source that is being received, or null.</summary>
    public byte[]? Preview(string address)
    {
        lock (_lock)
        {
            if (!_receivers.TryGetValue(address, out var r)) return null;
            return DateTime.UtcNow - r.LastFrame < TimeSpan.FromSeconds(3) ? r.PreviewJpeg : null;
        }
    }

    public void Leave(Client c)
    {
        lock (_lock)
        {
            c.Source?.Remove(c);
            c.Source = null;
            _clients.Remove(c);
        }
    }

    /// <summary>Called once per second: removes idle receivers and returns the status line for app.py.</summary>
    public string Tick()
    {
        lock (_lock)
        {
            foreach (var r in _receivers.Values.ToList())
            {
                if (r.ClientCount == 0 && DateTime.UtcNow - r.EmptySince > TimeSpan.FromSeconds(10))
                {
                    r.Stop();
                    _receivers.Remove(r.Address);
                }
            }
            var parts = new List<string>();
            foreach (var r in _receivers.Values)
            {
                int fps = Interlocked.Exchange(ref r.Frames, 0);
                int enc = Interlocked.Exchange(ref r.Encoded, 0);
                int skipped = Interlocked.Exchange(ref r.Skipped, 0);
                long bytes = Interlocked.Exchange(ref r.EncodedBytes, 0);
                bool signal = DateTime.UtcNow - r.LastFrame < TimeSpan.FromSeconds(2);
                parts.Add(!signal ? $"{r.Address} no signal{(r.Error != "" ? " (" + r.Error + ")" : "")}"
                    : $"{r.Address} {r.Width}x{r.Height}{(r.Interlaced ? "i" : "p")} {fps} fps, H.264 {enc} fps {bytes * 8 / 1_000_000.0:0.0} Mbit/s"
                      + (skipped > 0 ? $", skipped {skipped}" : "") + $", encode {r.Encoder?.TakeAverageDelayMs() ?? 0:0.0} ms, {r.EncoderName}" + (r.Error != "" ? $" ({r.Error})" : ""));
            }
            long resets = _clients.Sum(c => Interlocked.Exchange(ref c.Resets, 0));
            return $"live clients {_clients.Count} receivers {_receivers.Count} resets {resets} | " + string.Join("; ", parts);
        }
    }

    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                clients = _clients.Count,
                receivers = _receivers.Values.Select(r => new
                {
                    address = r.Address,
                    width = r.Width,
                    height = r.Height,
                    interlaced = r.Interlaced,
                    signal = DateTime.UtcNow - r.LastFrame < TimeSpan.FromSeconds(2),
                    encoder = r.EncoderName,
                    error = r.Error,
                    watchers = r.ClientCount,
                }).ToList(),
            };
        }
    }
}
