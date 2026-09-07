// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if IOS || TVOS
using System.Net;
using System.Net.Sockets;
using System.Text;
using CoreGraphics;
using ImageIO;
using Foundation;
#if IOS || TVOS
using UIKit;
using PlatformImage = UIKit.UIImage;
#else
using AppKit;
using PlatformImage = AppKit.NSImage;
#endif

namespace LottieSimTests;

/// <summary>
/// One canned HTTP response served by <see cref="LoopbackHttpServer"/>.
/// </summary>
public sealed class LoopbackResponse
{
    public int StatusCode { get; init; } = 200;
    public string ReasonPhrase { get; init; } = "OK";
    public string ContentType { get; init; } = "application/octet-stream";
    public byte[] Body { get; init; } = Array.Empty<byte>();

    /// <summary>Delay before the response head is written, in milliseconds.</summary>
    public int DelayMs { get; init; }

    /// <summary>
    /// When true the server accepts the connection, reads the request, and then
    /// never writes a response — the socket is held open until the server is
    /// disposed. This is what makes a mid-flight cancellation deterministic:
    /// the request is guaranteed to still be in flight when Cancel is called.
    /// </summary>
    public bool Stall { get; init; }
}

/// <summary>
/// A minimal in-process HTTP/1.1 server bound to 127.0.0.1 on an OS-assigned port.
/// Written on raw <see cref="TcpListener"/> rather than <c>HttpListener</c> so the
/// behaviour is identical under Mono JIT (simulator), Mono AOT and NativeAOT, and so a
/// route can stall or return a deliberately corrupt body — neither of which a
/// bundled file:// fixture can express.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, LoopbackResponse> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly List<TcpClient> _held = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    public LoopbackHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string Url(string path) => $"{BaseUrl}{path}";

    public void Map(string path, LoopbackResponse response)
    {
        lock (_lock)
        {
            _routes[path] = response;
            _hits.TryAdd(path, 0);
        }
    }

    /// <summary>Number of request lines the server has actually read for <paramref name="path"/>.</summary>
    public int HitsFor(string path)
    {
        lock (_lock) { return _hits.TryGetValue(path, out var n) ? n : 0; }
    }

    public int TotalHits
    {
        get { lock (_lock) { return _hits.Values.Sum(); } }
    }

    public void ResetHits()
    {
        lock (_lock)
        {
            foreach (var key in _hits.Keys.ToList())
                _hits[key] = 0;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var head = new StringBuilder();
            var deadline = DateTime.UtcNow.AddSeconds(10);

            // Read until end-of-headers.
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0) break;
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var requestLine = head.ToString().Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            // Strip a query string; routes are matched on path only.
            var qIndex = target.IndexOf('?');
            if (qIndex >= 0) target = target.Substring(0, qIndex);

            LoopbackResponse? response;
            lock (_lock)
            {
                _hits[target] = (_hits.TryGetValue(target, out var n) ? n : 0) + 1;
                _routes.TryGetValue(target, out response);
            }

            if (response is null)
            {
                await WriteAsync(stream, new LoopbackResponse
                {
                    StatusCode = 404,
                    ReasonPhrase = "Not Found",
                    ContentType = "text/plain",
                    Body = Encoding.UTF8.GetBytes("no route"),
                }).ConfigureAwait(false);
                client.Close();
                return;
            }

            if (response.Stall)
            {
                // Hold the connection open with no response until Dispose.
                lock (_lock) { _held.Add(client); }
                return;
            }

            if (response.DelayMs > 0)
                await Task.Delay(response.DelayMs).ConfigureAwait(false);

            await WriteAsync(stream, response).ConfigureAwait(false);
            client.Close();
        }
        catch
        {
            try { client.Close(); } catch { /* connection torn down */ }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, LoopbackResponse response)
    {
        var head =
            $"HTTP/1.1 {response.StatusCode} {response.ReasonPhrase}\r\n" +
            $"Content-Type: {response.ContentType}\r\n" +
            $"Content-Length: {response.Body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, 0, response.Body.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* already cancelled */ }
        lock (_lock)
        {
            foreach (var held in _held)
            {
                try { held.Close(); } catch { /* already closed */ }
            }
            _held.Clear();
        }
        try { _listener.Stop(); } catch { /* already stopped */ }
        _cts.Dispose();
    }
}

/// <summary>
/// Deterministic image fixtures built at runtime through CoreGraphics/ImageIO.
/// Generating them in-process (rather than shipping a binary blob) keeps the exact
/// pixel values known to the assertions and works identically on every TFM.
/// </summary>
public static class ImageFixtures
{
    /// <summary>Encodes a solid-colour RGBA image as PNG bytes.</summary>
    public static byte[] SolidPng(int width, int height, double r, double g, double b)
    {
        using var cgImage = SolidCGImage(width, height, r, g, b);
        var data = new NSMutableData();
        using (var dest = CGImageDestination.Create(data, "public.png", 1)
            ?? throw new InvalidOperationException("CGImageDestination.Create(public.png) returned null"))
        {
            dest.AddImage(cgImage);
            if (!dest.Close())
                throw new InvalidOperationException("CGImageDestination.Close() failed");
        }

        var bytes = data.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("PNG encode produced zero bytes");
        return bytes;
    }

    public static CGImage SolidCGImage(int width, int height, double r, double g, double b)
    {
        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        using var ctx = new CGBitmapContext(
            null, width, height, 8, 4 * width, colorSpace, CGImageAlphaInfo.PremultipliedLast);
        ctx.SetFillColor((nfloat)r, (nfloat)g, (nfloat)b, 1f);
        ctx.FillRect(new CGRect(0, 0, width, height));
        return ctx.ToImage() ?? throw new InvalidOperationException("CGBitmapContext.ToImage() returned null");
    }

#if IOS || TVOS
    public static UIImage SolidUIImage(int width, int height, double r, double g, double b)
        => UIImage.FromImage(SolidCGImage(width, height, r, g, b))
           ?? throw new InvalidOperationException("UIImage.FromImage returned null");
#endif

    /// <summary>
    /// Reads one pixel of an image as straight (un-premultiplied) 0..255 RGBA by
    /// redrawing the whole image at its natural pixel size and indexing the buffer.
    /// Unlike <see cref="CenterPixel(CGImage)"/> this can see per-pixel variation, so it
    /// is what processor-output assertions use.
    /// </summary>
    public static (int R, int G, int B, int A) PixelAt(PlatformImage image, int x, int y)
    {
        var cg = ToCGImage(image);
        int w = (int)cg.Width, h = (int)cg.Height;
        if (x < 0 || y < 0 || x >= w || y >= h)
            throw new ArgumentOutOfRangeException($"pixel ({x},{y}) outside {w}x{h}");

        var buffer = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                using var colorSpace = CGColorSpace.CreateDeviceRGB();
                using var ctx = new CGBitmapContext(
                    (IntPtr)p, w, h, 8, 4 * w, colorSpace, CGImageAlphaInfo.PremultipliedLast);
                ctx.DrawImage(new CGRect(0, 0, w, h), cg);
            }
        }

        // CGBitmapContext's origin is bottom-left; index from the top so callers can
        // think in image coordinates.
        int offset = ((h - 1 - y) * w + x) * 4;
        int a = buffer[offset + 3];
        if (a == 0) return (0, 0, 0, 0);
        int Unpremultiply(byte c) => (int)Math.Round(c * 255.0 / a);
        return (Unpremultiply(buffer[offset]), Unpremultiply(buffer[offset + 1]), Unpremultiply(buffer[offset + 2]), a);
    }

    /// <summary>Bytes that are definitely not a decodable image.</summary>
    public static byte[] CorruptImageBytes()
    {
        // A valid PNG signature followed by garbage: the decoder has to actually try
        // (and fail) rather than reject on the magic bytes alone.
        var bytes = new byte[512];
        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Array.Copy(signature, bytes, signature.Length);
        for (int i = signature.Length; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 37) & 0xFF);
        return bytes;
    }

    /// <summary>
    /// Reads the centre pixel of a UIImage as straight (un-premultiplied) 0..255 RGBA
    /// by redrawing it into a 1x1 bitmap context.
    /// </summary>
    public static (int R, int G, int B, int A) CenterPixel(PlatformImage image)
        => CenterPixel(ToCGImage(image));

    /// <summary>The platform's own image class, so assertions read the decoded result the
    /// binding actually hands back rather than a re-encoded copy.</summary>
#if IOS || TVOS
    public static CGImage ToCGImage(UIImage image)
        => image.CGImage ?? throw new InvalidOperationException("UIImage.CGImage was null");
#else
    public static CGImage ToCGImage(NSImage image)
    {
        var rect = new CGRect(CGPoint.Empty, image.Size);
        return image.AsCGImage(ref rect, null, null)
               ?? throw new InvalidOperationException("NSImage.AsCGImage returned null");
    }
#endif

    public static (int R, int G, int B, int A) CenterPixel(CGImage cg)
    {
        var pixel = new byte[4];
        unsafe
        {
            fixed (byte* p = pixel)
            {
                using var colorSpace = CGColorSpace.CreateDeviceRGB();
                using var ctx = new CGBitmapContext(
                    (IntPtr)p, 1, 1, 8, 4, colorSpace, CGImageAlphaInfo.PremultipliedLast);
                // Draw the source scaled down to the single destination pixel; for a
                // solid-colour fixture every source pixel carries the same value, so
                // the result is that colour regardless of the sampling used.
                ctx.DrawImage(new CGRect(0, 0, 1, 1), cg);
            }
        }

        int a = pixel[3];
        if (a == 0) return (0, 0, 0, 0);
        // Un-premultiply so the comparison is against the colour that was encoded.
        int Unpremultiply(byte c) => (int)Math.Round(c * 255.0 / a);
        return (Unpremultiply(pixel[0]), Unpremultiply(pixel[1]), Unpremultiply(pixel[2]), a);
    }

    /// <summary>Writes bytes to a fresh file under the app's temp directory and returns a file:// URL string.</summary>
    public static string WriteTempFile(string name, byte[] contents)
    {
        var dir = Path.Combine(Path.GetTempPath(), "swiftbindings-fixtures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, contents);
        var url = NSUrl.FromFilename(path)
            ?? throw new InvalidOperationException($"NSUrl.FromFilename returned null for {path}");
        return url.AbsoluteString
            ?? throw new InvalidOperationException($"NSUrl.AbsoluteString returned null for {path}");
    }
}

/// <summary>Bounded-await helpers so no test can pass by timing out.</summary>
public static class Await
{
    /// <summary>
    /// Waits until <paramref name="condition"/> is true or the timeout elapses, pumping
    /// the calling context via <c>Task.Delay</c> so main-thread callbacks can still run.
    /// Returns true only if the condition actually became true.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs, int pollMs = 25)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(pollMs).ConfigureAwait(true);
        }

        return condition();
    }

    /// <summary>Awaits <paramref name="task"/> with a bound; throws TimeoutException instead of hanging.</summary>
    public static async Task<T> WithTimeoutAsync<T>(Task<T> task, int timeoutMs, string what)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
        if (completed != task)
            throw new TimeoutException($"{what} did not complete within {timeoutMs}ms");
        return await task.ConfigureAwait(true);
    }

    public static async Task WithTimeoutAsync(Task task, int timeoutMs, string what)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
        if (completed != task)
            throw new TimeoutException($"{what} did not complete within {timeoutMs}ms");
        await task.ConfigureAwait(true);
    }
}
#endif
