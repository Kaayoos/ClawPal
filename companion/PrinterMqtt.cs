using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace ClawPal.Companion;

// ClawPal's whole conversation with a Bambu printer. It may ask for the printer's full status and
// nothing else: no start, pause, stop, heating, movement, light, settings or uploads. Every MQTT
// PUBLISH is built by MqttPackets.Publish, which refuses anything but that one request.
public static class BambuReadOnly
{
    public const int Port = 8883;
    public const string User = "bblp";
    // The standard full-status request Bambu's own apps send. Its only effect is a full report.
    public const string PushAll = "{\"pushing\":{\"sequence_id\":\"0\",\"command\":\"pushall\"}}";
    public static string ReportTopic(string serial) => $"device/{serial}/report";
    public static string RequestTopic(string serial) => $"device/{serial}/request";
    public static bool ValidSerial(string? serial) => serial is { Length: >= 8 and <= 32 } && serial.All(char.IsAsciiLetterOrDigit);
    public static bool ValidAccessCode(string? code) => code is { Length: 8 } && code.All(char.IsAsciiLetterOrDigit);

    // The guard. True only for the full-status request on a printer's own request topic.
    public static bool Allowed(string topic, ReadOnlySpan<byte> payload)
    {
        const string prefix = "device/", suffix = "/request";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal) || !topic.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string serial = topic[prefix.Length..^suffix.Length];
        return ValidSerial(serial) && payload.SequenceEqual(Encoding.UTF8.GetBytes(PushAll));
    }
}

public sealed class ReadOnlyViolationException(string message) : InvalidOperationException(message);

// MQTT 3.1.1 packets, only the few a read-only subscriber needs.
public static class MqttPackets
{
    public const byte Connect = 0x10, ConnAck = 0x20, Publish = 0x30, PubAck = 0x40, Subscribe = 0x82, SubAck = 0x90,
        PingReq = 0xC0, PingResp = 0xD0, Disconnect = 0xE0;

    static void Length(List<byte> output, int length)
    {
        do { byte digit = (byte)(length % 128); length /= 128; if (length > 0) digit |= 0x80; output.Add(digit); } while (length > 0);
    }
    static void Str(List<byte> output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 0xFFFF) throw new ArgumentException("MQTT string too long");
        output.Add((byte)(bytes.Length >> 8)); output.Add((byte)bytes.Length); output.AddRange(bytes);
    }
    static byte[] Packet(byte header, List<byte> body)
    {
        var packet = new List<byte> { header };
        Length(packet, body.Count);
        packet.AddRange(body);
        return packet.ToArray();
    }

    public static byte[] ConnectPacket(string clientId, string user, string password, ushort keepAlive)
    {
        var body = new List<byte>();
        Str(body, "MQTT"); body.Add(4); // protocol level 3.1.1
        body.Add(0b1100_0010);         // user name, password, clean session
        body.Add((byte)(keepAlive >> 8)); body.Add((byte)keepAlive);
        Str(body, clientId); Str(body, user); Str(body, password);
        return Packet(Connect, body);
    }

    public static byte[] SubscribePacket(ushort packetId, string topic)
    {
        var body = new List<byte> { (byte)(packetId >> 8), (byte)packetId };
        Str(body, topic); body.Add(0); // QoS 0
        return Packet(Subscribe, body);
    }

    public static byte[] PingPacket() => [PingReq, 0];
    public static byte[] DisconnectPacket() => [Disconnect, 0];

    // The only way to build a PUBLISH. It throws for anything but the full-status request.
    public static byte[] PublishPacket(string topic, ReadOnlySpan<byte> payload)
    {
        if (!BambuReadOnly.Allowed(topic, payload))
            throw new ReadOnlyViolationException("ClawPal only asks printers for their status; refused to publish to " + topic);
        var body = new List<byte>();
        Str(body, topic); body.AddRange(payload.ToArray()); // QoS 0, so no packet id
        return Packet(Publish, body);
    }

    // Reads one packet: its first byte and its body. Bodies over `limit` are refused.
    public static async Task<(byte Header, byte[] Body)> ReadAsync(Stream stream, int limit, CancellationToken token)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, token);
        byte header = one[0];
        int length = 0, shift = 0;
        while (true)
        {
            await stream.ReadExactlyAsync(one, token);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 21) throw new IOException("Malformed MQTT length");
        }
        if (length > limit) throw new IOException("MQTT packet too large");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return (header, body);
    }

    // A PUBLISH body: topic, then (for QoS > 0) a packet id, then the payload.
    public static (string Topic, ReadOnlyMemory<byte> Payload) ParsePublish(byte header, byte[] body)
    {
        if (body.Length < 2) throw new IOException("Short MQTT publish");
        int topicLength = BinaryPrimitives.ReadUInt16BigEndian(body);
        if (2 + topicLength > body.Length) throw new IOException("Short MQTT publish");
        string topic = Encoding.UTF8.GetString(body, 2, topicLength);
        int offset = 2 + topicLength + (((header >> 1) & 3) > 0 ? 2 : 0);
        if (offset > body.Length) throw new IOException("Short MQTT publish");
        return (topic, body.AsMemory(offset));
    }
}

// The printer's certificate is self-signed. ClawPal trusts the first certificate a printer's
// address shows (trust on first use), keeps its SHA-256 with that printer, and refuses a different
// one later. The pin belongs to one printer entry, so it is only ever used for that printer's IP.
public sealed class CertificatePin
{
    public string? Fingerprint { get; private set; }
    public bool Changed { get; private set; }
    public CertificatePin(string? fingerprint) { Fingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint; }
    public static string Of(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
    public bool Check(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        string seen = Of(certificate);
        if (Fingerprint is null) { Fingerprint = seen; return true; }
        Changed = !string.Equals(seen, Fingerprint, StringComparison.Ordinal);
        return !Changed;
    }
}

public enum PrinterLink { Connecting, Online, Unreachable, Denied, Certificate, Busy }

// One connection to one Bambu printer: subscribe to its reports and keep a merged state.
public sealed class BambuSession
{
    readonly Func<CancellationToken, Task<Stream>> open;
    readonly string serial, accessCode;
    readonly JsonObject state = new();
    readonly object gate = new();
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30), Silence = TimeSpan.FromSeconds(90);
    public PrintStatus Status { get { lock (gate) return PrinterContract.FromBambu(state); } }
    public event Action? Updated;

    public BambuSession(string serial, string accessCode, Func<CancellationToken, Task<Stream>> open)
    {
        this.serial = serial; this.accessCode = accessCode; this.open = open;
    }

    // Opens TLS to the printer, checking its certificate against the pin.
    public static Func<CancellationToken, Task<Stream>> Tls(string address, CertificatePin pin) => async token =>
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(address, BambuReadOnly.Port, timeout.Token);
            }
            var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => pin.Check(certificate));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, timeout.Token);
            }
            return new OwnedStream(tls, tcp);
        }
        catch { tcp.Dispose(); throw; }
    };

    // Runs one connection until it drops. Throws UnauthorizedAccessException when the access code is
    // refused; the caller decides when to try again.
    public async Task RunAsync(Action<PrinterLink> link, CancellationToken token)
    {
        link(PrinterLink.Connecting);
        await using var stream = await open(token);
        using var writer = new SemaphoreSlim(1, 1);
        async Task Send(byte[] packet)
        {
            await writer.WaitAsync(token);
            try { await stream.WriteAsync(packet, token); await stream.FlushAsync(token); } finally { writer.Release(); }
        }
        await Send(MqttPackets.ConnectPacket("clawpal-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), BambuReadOnly.User, accessCode, (ushort)(KeepAlive.TotalSeconds * 2)));
        var (header, body) = await MqttPackets.ReadAsync(stream, 64, token);
        if ((header & 0xF0) != MqttPackets.ConnAck || body.Length < 2) throw new IOException("The printer did not accept the MQTT connection");
        if (body[1] is 4 or 5) throw new UnauthorizedAccessException("The printer refused the access code");
        if (body[1] == 3) throw new PrinterBusyException();
        if (body[1] != 0) throw new IOException("The printer refused the connection: " + body[1]);
        await Send(MqttPackets.SubscribePacket(1, BambuReadOnly.ReportTopic(serial)));
        // A fresh connection asks once for everything; after that the printer sends changes by itself.
        await Send(MqttPackets.PublishPacket(BambuReadOnly.RequestTopic(serial), Encoding.UTF8.GetBytes(BambuReadOnly.PushAll)));
        link(PrinterLink.Online);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        long lastHeard = Environment.TickCount64;
        var pinger = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(KeepAlive, stop.Token);
                if (Environment.TickCount64 - Interlocked.Read(ref lastHeard) > Silence.TotalMilliseconds) { stop.Cancel(); return; }
                await Send(MqttPackets.PingPacket());
            }
        }, stop.Token);
        try
        {
            while (true)
            {
                (header, body) = await MqttPackets.ReadAsync(stream, 512 * 1024, stop.Token);
                Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
                if ((header & 0xF0) != MqttPackets.Publish) continue;
                var (topic, payload) = MqttPackets.ParsePublish(header, body);
                if (topic != BambuReadOnly.ReportTopic(serial)) continue;
                bool merged;
                lock (gate) merged = PrinterContract.MergeReport(state, payload.Span);
                if (merged) Updated?.Invoke();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The printer went quiet"); }
        finally
        {
            stop.Cancel();
            try { await pinger; } catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            try { await stream.WriteAsync(MqttPackets.DisconnectPacket(), CancellationToken.None); } catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException) { }
        }
    }
}

public sealed class PrinterBusyException() : IOException("The printer has no free connection");

// Disposes the TCP connection along with the TLS stream on top of it.
sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
}
