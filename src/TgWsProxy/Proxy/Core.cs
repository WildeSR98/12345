using System.Security.Cryptography;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TgWsProxy.Proxy;

// ── Telegram IP ranges ────────────────────────────────────────────────────────
public static class TgRanges
{
    private static readonly (uint Lo, uint Hi)[] Ranges =
    {
        (ToUint("185.76.151.0"),  ToUint("185.76.151.255")),
        (ToUint("149.154.160.0"), ToUint("149.154.175.255")),
        (ToUint("91.105.192.0"),  ToUint("91.105.193.255")),
        (ToUint("91.108.0.0"),    ToUint("91.108.255.255")),
    };

    private static uint ToUint(string ip)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static bool IsTelegram(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var b = addr.GetAddressBytes();
        if (b.Length != 4) return false;
        var n = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return Ranges.Any(r => n >= r.Lo && n <= r.Hi);
    }
}

// ── MTProto DC extractor ──────────────────────────────────────────────────────
public static class MtProtoDecoder
{
    public static (int? Dc, bool IsMedia) ExtractDc(byte[] data)
    {
        if (data.Length < 64) return (null, false);
        try
        {
            var key = data[8..40];
            var iv  = data[40..56];
            using var aes = Aes.Create();
            aes.Key  = key;
            aes.Mode = CipherMode.ECB;
            // CTR mode via manual counter
            var keystream = new byte[64];
            var counter   = new byte[16];
            Buffer.BlockCopy(iv, 0, counter, 0, 16);
            using var enc = aes.CreateEncryptor();
            var block = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                enc.TransformBlock(counter, 0, 16, block, 0);
                Buffer.BlockCopy(block, 0, keystream, i * 16, 16);
                IncrementCounter(counter);
            }
            // XOR bytes 56-64
            var plain = new byte[8];
            for (int i = 0; i < 8; i++) plain[i] = (byte)(data[56 + i] ^ keystream[56 + i]);

            var proto  = BitConverter.ToUInt32(plain, 0);
            var dcRaw  = BitConverter.ToInt16(plain, 4);

            if (proto is 0xEFEFEFEF or 0xEEEEEEEE or 0xDDDDDDDD)
            {
                var dc = Math.Abs(dcRaw);
                if (dc >= 1 && dc <= 1000) return (dc, dcRaw < 0);
            }
        }
        catch { }
        return (null, false);
    }

    private static void IncrementCounter(byte[] c)
    {
        for (int i = 15; i >= 0; i--) { if (++c[i] != 0) break; }
    }
}

// ── Raw WebSocket client ──────────────────────────────────────────────────────
public class RawWebSocket : IAsyncDisposable
{
    private readonly Stream _stream;
    private bool _closed;

    private RawWebSocket(Stream stream) => _stream = stream;

    public static async Task<RawWebSocket> ConnectAsync(string ip, string domain,
        string path = "/apiws", int timeoutMs = 10000)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, 443).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

        var ssl = new SslStream(tcp.GetStream(), false,
            (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = domain,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var req = Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: {domain}\r\nUpgrade: websocket\r\n" +
            $"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n" +
            $"Sec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n" +
            $"Origin: https://web.telegram.org\r\n" +
            $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n\r\n");
        await ssl.WriteAsync(req).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

        // Read HTTP response
        var headerBuf = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            await ssl.ReadAsync(oneByte.AsMemory(0, 1));
            headerBuf.Add(oneByte[0]);
            if (headerBuf.Count >= 4)
            {
                var tail = headerBuf.TakeLast(4).ToArray();
                if (tail[0] == '\r' && tail[1] == '\n' && tail[2] == '\r' && tail[3] == '\n')
                    break;
            }
            if (headerBuf.Count > 8192) throw new Exception("Headers too large");
        }

        var header = Encoding.ASCII.GetString(headerBuf.ToArray());
        var status = int.Parse(Regex.Match(header, @"HTTP/\S+ (\d+)").Groups[1].Value);

        if (status == 101) return new RawWebSocket(ssl);

        // Check redirect
        var location = Regex.Match(header, @"[Ll]ocation: (.+)\r\n").Groups[1].Value.Trim();
        throw new WsHandshakeException(status, location);
    }

    public async Task SendAsync(byte[] data)
    {
        if (_closed) throw new InvalidOperationException("Closed");
        var frame = BuildFrame(2, data, mask: true);
        await _stream.WriteAsync(frame);
    }

    public async Task<byte[]?> ReceiveAsync()
    {
        while (!_closed)
        {
            var (opcode, payload) = await ReadFrameAsync();
            switch (opcode)
            {
                case 0x8: _closed = true; return null;          // CLOSE
                case 0x9: await _stream.WriteAsync(BuildFrame(0xA, payload, true)); break; // PING→PONG
                case 0xA: break;                                // PONG ignore
                case 0x1: case 0x2: return payload;             // data
            }
        }
        return null;
    }

    private static byte[] BuildFrame(int opcode, byte[] data, bool mask)
    {
        var maskKey = mask ? RandomNumberGenerator.GetBytes(4) : Array.Empty<byte>();
        int len = data.Length;
        var header = new List<byte> { (byte)(0x80 | opcode) };
        byte maskBit = mask ? (byte)0x80 : (byte)0;
        if (len < 126)       header.Add((byte)(maskBit | len));
        else if (len < 65536){ header.Add((byte)(maskBit | 126)); header.AddRange(BitConverter.GetBytes((ushort)len).Reverse()); }
        else                 { header.Add((byte)(maskBit | 127)); header.AddRange(BitConverter.GetBytes((ulong)len).Reverse()); }
        if (mask) header.AddRange(maskKey);

        var result = new byte[header.Count + len];
        header.ToArray().CopyTo(result, 0);
        if (mask) { for (int i = 0; i < len; i++) result[header.Count + i] = (byte)(data[i] ^ maskKey[i & 3]); }
        else data.CopyTo(result, header.Count);
        return result;
    }

    private async Task<(int Opcode, byte[] Payload)> ReadFrameAsync()
    {
        var hdr = await ReadExactAsync(2);
        int opcode = hdr[0] & 0x0F;
        bool masked = (hdr[1] & 0x80) != 0;
        int length  = hdr[1] & 0x7F;
        if (length == 126) length = (int)BitConverter.ToUInt16((await ReadExactAsync(2)).Reverse().ToArray(), 0);
        else if (length == 127) length = (int)BitConverter.ToUInt64((await ReadExactAsync(8)).Reverse().ToArray(), 0);
        byte[]? maskKey = masked ? await ReadExactAsync(4) : null;
        var payload = await ReadExactAsync(length);
        if (maskKey != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= maskKey[i & 3];
        return (opcode, payload);
    }

    private async Task<byte[]> ReadExactAsync(int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(read, count - read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        try { await _stream.DisposeAsync(); } catch { }
    }
}

public class WsHandshakeException(int status, string location) : Exception($"HTTP {status}")
{
    public int Status   { get; } = status;
    public string Location { get; } = location;
    public bool IsRedirect => Status is 301 or 302 or 303 or 307 or 308;
}
