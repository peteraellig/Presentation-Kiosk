using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveIn;

/// <summary>
/// Kiosk3 live input: receives OMT sources and passes them uncompressed (UYVY + float audio) over a WebSocket
/// on 127.0.0.1 to the presentation page, which draws them with WebGL and plays the sound with Web Audio.
/// Routes: GET /sources (discovered OMT sources), GET /status, WebSocket /live (page sends {"source":"HOST (Name)"}).
/// Only loopback connections, only pages of the kiosk (Origin port = kiosk port).
/// </summary>
internal static class Program
{
    private static readonly Hub Hub = new();
    private static volatile List<string> _sources = new();
    private static int _kioskPort;

    private static int Main(string[] args)
    {
        int port = 53102;
        int parentPid = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--kiosk-port": _kioskPort = int.Parse(args[++i]); break;
                case "--parent-pid": parentPid = int.Parse(args[++i]); break;
                case "--encoder": H264Encoder.Preference = args[++i]; break;   // auto | intel | nvidia | amd | software
            }
        }
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--list-encoders"))
        {
            foreach (var e in H264Encoder.ListEncoders()) Console.WriteLine(e);
            return 0;
        }
        Console.WriteLine($"Kiosk3 LiveIn 3.24 · OMT / NDI input · port {port} · encoder {H264Encoder.Preference}");
        Console.WriteLine("Open Media Transport (OMT) SDK, MIT License, Copyright (c) 2025 Open Media Transport Contributors");
        NdiNative.Load();
        Console.WriteLine(NdiNative.Status + (NdiNative.Available ? " · NDI® is a registered trademark of Vizrt NDI AB" : ""));

        if (parentPid > 0) WatchParent(parentPid);

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException e)
        {
            Console.WriteLine($"Port {port} cannot be used: {e.Message}");
            return 3;
        }

        new Thread(DiscoveryLoop) { IsBackground = true, Name = "discovery" }.Start();
        new Thread(StatusLoop) { IsBackground = true, Name = "status" }.Start();

        while (true)
        {
            TcpClient tcp;
            try { tcp = listener.AcceptTcpClient(); }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleAsync(tcp));
        }
    }

    private static void WatchParent(int pid)
    {
        new Thread(() =>
        {
            try { Process.GetProcessById(pid).WaitForExit(); }
            catch (ArgumentException) { }
            Environment.Exit(0);
        }) { IsBackground = true, Name = "parent" }.Start();
    }

    private static void DiscoveryLoop()
    {
        while (true)
        {
            // OMT addresses stay as they are ("HOST (Name)"), NDI names get "ndi:" in front
            var list = new List<string>();
            try { list.AddRange(OmtNative.Discover()); }
            catch (Exception e) { Console.WriteLine("OMT discovery failed: " + e.Message); }
            try { list.AddRange(NdiNative.Discover().Select(n => SourceReceiver.NdiPrefix + n)); }
            catch (Exception e) { Console.WriteLine("NDI discovery failed: " + e.Message); }
            _sources = list;
            Thread.Sleep(2000);
        }
    }

    private static void StatusLoop()
    {
        while (true)
        {
            Thread.Sleep(1000);
            Console.WriteLine(Hub.Tick());
        }
    }

    private static async Task HandleAsync(TcpClient tcp)
    {
        try
        {
            tcp.NoDelay = true;
            tcp.SendBufferSize = 8 * 1024 * 1024;
            var stream = tcp.GetStream();
            var head = await ReadHeadAsync(stream);
            if (head == null) { tcp.Dispose(); return; }
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3 || first[0] != "GET") { await Reply(stream, 405, "text/plain", "Method not allowed"); tcp.Dispose(); return; }
            string path = first[1].Split('?')[0];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }
            if (!OriginAllowed(headers)) { await Reply(stream, 403, "text/plain", "Forbidden"); tcp.Dispose(); return; }

            if (path == "/live" && headers.TryGetValue("Upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase)
                && headers.TryGetValue("Sec-WebSocket-Key", out var key))
            {
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
                var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(10) });
                var client = new Client(ws, Hub);
                Hub.Join(client);
                await client.RunAsync();
                tcp.Dispose();
                return;
            }
            if (path == "/preview")
            {
                string query = first[1].Contains('?') ? first[1][(first[1].IndexOf('?') + 1)..] : "";
                string source = query.Split('&').Where(p => p.StartsWith("source=")).Select(p => Uri.UnescapeDataString(p[7..].Replace('+', ' '))).FirstOrDefault() ?? "";
                var jpeg = Hub.Preview(source);
                if (jpeg != null) await Reply(stream, 200, "image/jpeg", jpeg);
                else await Reply(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No preview"));
            }
            else if (path == "/sources")
                await Reply(stream, 200, "application/json", JsonSerializer.Serialize(new { sources = _sources, ndi = NdiNative.Available, ndi_status = NdiNative.Status }));
            else if (path == "/status")
                await Reply(stream, 200, "application/json", JsonSerializer.Serialize(Hub.Status()));
            else
                await Reply(stream, 404, "text/plain", "Not found");
            tcp.Dispose();
        }
        catch (Exception)
        {
            tcp.Dispose();
        }
    }

    /// <summary>Requests without Origin come from app.py; browsers send one and must be a kiosk page.</summary>
    private static bool OriginAllowed(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Origin", out var origin)) return true;
        if (_kioskPort == 0) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Port == _kioskPort;
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buf = new byte[8192];
        int n = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (n < buf.Length)
        {
            int r = await stream.ReadAsync(buf.AsMemory(n), timeout.Token);
            if (r == 0) return null;
            n += r;
            // stop exactly at the end of the header; a WebSocket client sends nothing more before the reply
            for (int i = Math.Max(0, n - r - 3); i + 3 < n; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    return Encoding.ASCII.GetString(buf, 0, i);
        }
        return null;
    }

    private static Task Reply(NetworkStream stream, int code, string type, string body)
        => Reply(stream, code, type + "; charset=utf-8", Encoding.UTF8.GetBytes(body));

    private static async Task Reply(NetworkStream stream, int code, string type, byte[] bytes)
    {
        string reason = code switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
    }
}
