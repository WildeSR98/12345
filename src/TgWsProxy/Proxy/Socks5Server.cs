using System.Net;
using System.Net.Sockets;

namespace TgWsProxy.Proxy;

public class ProxyStats
{
    public long TotalConnections;
    public long WsConnections;
    public long TcpFallback;
    public long Passthrough;
    public long WsErrors;
    public long BytesUp;
    public long BytesDown;

    public string Summary() =>
        $"total={TotalConnections} ws={WsConnections} tcp_fb={TcpFallback} " +
        $"pass={Passthrough} err={WsErrors} " +
        $"up={HumanBytes(BytesUp)} down={HumanBytes(BytesDown)}";

    private static string HumanBytes(long n)
    {
        foreach (var u in new[] { "B", "KB", "MB", "GB" })
        { if (Math.Abs(n) < 1024) return $"{n:F1}{u}"; n /= 1024; }
        return $"{n:F1}TB";
    }
}

/// <summary>
/// SOCKS5 proxy server — routes Telegram connections through WebSocket,
/// falls back to direct TCP if WS unavailable.
/// Ported from tg_ws_proxy.py.
/// </summary>
public class Socks5Server
{
    private readonly int _port;
    private readonly Dictionary<int, string> _dcOpt;
    private readonly ProxyStats _stats = new();
    private readonly ProxyLogger _log;

    // WS blacklist: DCs where all attempts returned 302
    private readonly HashSet<(int Dc, bool IsMedia)> _wsBlacklist = new();
    // Cooldown: DC -> fail_until (ticks)
    private readonly Dictionary<(int, bool), DateTime> _dcFailUntil = new();
    private static readonly TimeSpan FailCooldown = TimeSpan.FromSeconds(60);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public Socks5Server(int port, Dictionary<int, string> dcOpt, ProxyLogger log)
    {
        _port  = port;
        _dcOpt = dcOpt;
        _log   = log;
    }

    public async Task RunAsync(CancellationToken token)
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _log.Info($"SOCKS5 proxy listening on 127.0.0.1:{_port}");
        _log.Info($"DC mappings: {string.Join(", ", _dcOpt.Select(kv => $"DC{kv.Key}={kv.Value}"))}");

        // Stats printer
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(60_000, token).ConfigureAwait(false);
                _log.Info($"Stats: {_stats.Summary()}");
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch { break; }

            Interlocked.Increment(ref _stats.TotalConnections);
            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }

        _listener.Stop();
    }

    public void Stop() { _listener?.Stop(); }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            using var stream = client.GetStream();

            // SOCKS5 greeting
            var hdr = await ReadExactAsync(stream, 2, token);
            if (hdr[0] != 5) { _log.Debug($"[{ep}] Not SOCKS5"); return; }
            var methods = await ReadExactAsync(stream, hdr[1], token);
            await stream.WriteAsync(new byte[] { 5, 0 }, token); // no-auth

            // CONNECT request
            var req = await ReadExactAsync(stream, 4, token);
            if (req[1] != 1) // only CONNECT
            {
                await stream.WriteAsync(Socks5Reply(0x07), token);
                return;
            }

            string dst;
            switch (req[3])
            {
                case 1: // IPv4
                    var ip4 = await ReadExactAsync(stream, 4, token);
                    dst = new IPAddress(ip4).ToString();
                    break;
                case 3: // domain
                    var dlen = (await ReadExactAsync(stream, 1, token))[0];
                    dst = System.Text.Encoding.ASCII.GetString(await ReadExactAsync(stream, dlen, token));
                    break;
                case 4: // IPv6
                    var ip6 = await ReadExactAsync(stream, 16, token);
                    dst = new IPAddress(ip6).ToString();
                    break;
                default:
                    await stream.WriteAsync(Socks5Reply(0x08), token);
                    return;
            }

            var portBytes = await ReadExactAsync(stream, 2, token);
            int port = (portBytes[0] << 8) | portBytes[1];

            // Non-Telegram: passthrough
            if (!TgRanges.IsTelegram(dst))
            {
                Interlocked.Increment(ref _stats.Passthrough);
                _log.Debug($"[{ep}] passthrough -> {dst}:{port}");
                await TcpPassthroughAsync(stream, dst, port, token);
                return;
            }

            // Telegram: accept & read MTProto init
            await stream.WriteAsync(Socks5Reply(0x00), token);
            byte[] init;
            try { init = await ReadExactAsync(stream, 64, token); }
            catch { return; }

            // HTTP transport — reject
            if (init[0..5].SequenceEqual("POST "u8.ToArray()) ||
                init[0..4].SequenceEqual("GET "u8.ToArray()))
                return;

            // Extract DC
            var (dc, isMedia) = MtProtoDecoder.ExtractDc(init);
            if (dc == null || !_dcOpt.ContainsKey(dc.Value))
            {
                _log.Warn($"[{ep}] Unknown DC{dc} -> TCP passthrough");
                await TcpFallbackAsync(stream, dst, port, init, ep, null, false, token);
                return;
            }

            var dcKey = (dc.Value, isMedia);
            var now   = DateTime.UtcNow;

            // Blacklist check
            if (_wsBlacklist.Contains(dcKey))
            {
                _log.Debug($"[{ep}] DC{dc.Value} WS blacklisted -> TCP");
                await TcpFallbackAsync(stream, dst, port, init, ep, dc, isMedia, token);
                return;
            }

            // Cooldown check
            if (_dcFailUntil.TryGetValue(dcKey, out var failUntil) && now < failUntil)
            {
                _log.Debug($"[{ep}] DC{dc.Value} cooldown -> TCP");
                await TcpFallbackAsync(stream, dst, port, init, ep, dc, isMedia, token);
                return;
            }

            // Try WebSocket
            var domains = WsDomains(dc.Value, isMedia);
            var target  = _dcOpt[dc.Value];
            RawWebSocket? ws = null;
            bool allRedirects = true;

            foreach (var domain in domains)
            {
                _log.Info($"[{ep}] DC{dc.Value} ({dst}:{port}) -> wss://{domain}/apiws via {target}");
                try
                {
                    ws = await RawWebSocket.ConnectAsync(target, domain, timeoutMs: 10000);
                    allRedirects = false;
                    break;
                }
                catch (WsHandshakeException ex) when (ex.IsRedirect)
                {
                    Interlocked.Increment(ref _stats.WsErrors);
                    _log.Warn($"[{ep}] DC{dc.Value} got {ex.Status} from {domain}");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _stats.WsErrors);
                    allRedirects = false;
                    _log.Warn($"[{ep}] DC{dc.Value} WS failed: {ex.Message}");
                }
            }

            if (ws == null)
            {
                if (allRedirects) _wsBlacklist.Add(dcKey);
                else _dcFailUntil[dcKey] = now + FailCooldown;

                _log.Info($"[{ep}] DC{dc.Value} -> TCP fallback");
                await TcpFallbackAsync(stream, dst, port, init, ep, dc, isMedia, token);
                return;
            }

            // WS success
            _dcFailUntil.Remove(dcKey);
            Interlocked.Increment(ref _stats.WsConnections);
            await ws.SendAsync(init);
            await BridgeWsAsync(stream, ws, ep, dc, dst, port, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Debug($"[{ep}] Error: {ex.Message}"); }
        finally { client.Dispose(); }
    }

    private async Task TcpPassthroughAsync(Stream local, string dst, int port, CancellationToken token)
    {
        try
        {
            using var remote = new TcpClient();
            await remote.ConnectAsync(dst, port, token);
            using var remStream = remote.GetStream();
            await Task.WhenAll(
                PipeAsync(local, remStream, true, token),
                PipeAsync(remStream, local, false, token));
        }
        catch { }
    }

    private async Task TcpFallbackAsync(Stream local, string dst, int port,
        byte[] init, string ep, int? dc, bool isMedia, CancellationToken token)
    {
        try
        {
            using var remote = new TcpClient();
            var connectTask = remote.ConnectAsync(dst, port, token).AsTask();
            await connectTask.WaitAsync(TimeSpan.FromSeconds(10), token);
            Interlocked.Increment(ref _stats.TcpFallback);
            var remStream = remote.GetStream();
            await remStream.WriteAsync(init, token);
            _log.Info($"[{ep}] DC{dc?.ToString() ?? "?"} TCP fallback {dst}:{port}");
            await Task.WhenAll(
                PipeAsync(local, remStream, true, token),
                PipeAsync(remStream, local, false, token));
        }
        catch (Exception ex) { _log.Warn($"[{ep}] TCP fallback failed: {ex.Message}"); }
    }

    private async Task BridgeWsAsync(Stream local, RawWebSocket ws, string ep,
        int? dc, string dst, int port, CancellationToken token)
    {
        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(token);

        async Task LocalToWs()
        {
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    int n = await local.ReadAsync(buf, cts2.Token);
                    if (n == 0) break;
                    Interlocked.Add(ref _stats.BytesUp, n);
                    await ws.SendAsync(buf[..n]);
                }
            }
            catch { }
            finally { cts2.Cancel(); }
        }

        async Task WsToLocal()
        {
            try
            {
                while (true)
                {
                    var data = await ws.ReceiveAsync();
                    if (data == null) break;
                    Interlocked.Add(ref _stats.BytesDown, data.Length);
                    await local.WriteAsync(data, cts2.Token);
                }
            }
            catch { }
            finally { cts2.Cancel(); }
        }

        await Task.WhenAll(LocalToWs(), WsToLocal());
        await ws.DisposeAsync();
        _log.Info($"[{ep}] DC{dc?.ToString() ?? "?"} ({dst}:{port}) WS session closed");
    }

    private async Task PipeAsync(Stream src, Stream dst, bool isUp, CancellationToken token)
    {
        var buf = new byte[65536];
        try
        {
            while (true)
            {
                int n = await src.ReadAsync(buf, token);
                if (n == 0) break;
                if (isUp) Interlocked.Add(ref _stats.BytesUp, n);
                else Interlocked.Add(ref _stats.BytesDown, n);
                await dst.WriteAsync(buf.AsMemory(0, n), token);
            }
        }
        catch { }
    }

    private static byte[] Socks5Reply(byte status) =>
        new byte[] { 5, status, 0, 1, 0, 0, 0, 0, 0, 0 };

    private static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken token)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), token);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }

    private static string[] WsDomains(int dc, bool isMedia)
    {
        var base_ = dc > 5 ? "telegram.org" : "web.telegram.org";
        return isMedia
            ? new[] { $"kws{dc}-1.{base_}", $"kws{dc}.{base_}" }
            : new[] { $"kws{dc}.{base_}", $"kws{dc}-1.{base_}" };
    }
}
