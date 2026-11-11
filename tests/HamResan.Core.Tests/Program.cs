using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HamResan.Core;

// Dependency-free test runner, so the core can be tested without NuGet packages.
var tests = new (string Name, Func<Task> Run)[]
{
    ("automatic reception requires explicit session trust", SessionTrustTest),
    ("invitation encoding and one-use redemption", InvitationTest),
    ("shareable IPv4 and IPv6 route candidates", NetworkAddressesTest),
    ("invitation pairing followed by transfer", PairingTest),
    ("IPv6 invitation pairing followed by transfer", PairingIpv6Test),
    ("invitation pairing rejection", PairingRejectionTest),
    ("protocol round trip and version", ProtocolTest),
    ("framing bounds and truncated messages", FramingTest),
    ("safe names and collisions", NamesTest),
    ("mutually pinned TLS streaming and empty file", TransferTest),
    ("receiver rejection before file open", RejectionTest),
    ("untrusted certificate rejected", PinTest),
    ("cancellation while waiting for a message", CancellationTest),
    ("hash mismatch deletes partial file", HashFailureTest),
    ("early disconnect deletes partial file", DisconnectTest),
    ("cancel receiving deletes partial file", CancelTransferTest),
    ("write error is propagated", WriteErrorTest),
};
int failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static X509Certificate2 Identity(string name)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
}
static Task SessionTrustTest()
{
    var trust = new SessionTrust();
    string first = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    string changed = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    Check(!trust.Enabled && !trust.CanReceiveAutomatically(first));
    trust.Enabled = true;
    Check(!trust.CanReceiveAutomatically(first));
    trust.AllowAutomaticForApprovedPeer(first);
    Check(trust.CanReceiveAutomatically(first.ToLowerInvariant()));
    Check(!trust.CanReceiveAutomatically(changed));
    trust.Enabled = false;
    Check(!trust.CanReceiveAutomatically(first));
    trust.Enabled = true;
    trust.Clear();
    Check(!trust.CanReceiveAutomatically(first));
    Check(!trust.CanReceiveAutomatically("not-a-pin"));
    return Task.CompletedTask;
}
static Task NetworkAddressesTest()
{
    foreach (string value in new[] { "192.168.1.8", "10.8.0.2", "169.254.1.2", "fd00::42", "2001:db8::1" })
        Check(NetworkAddresses.IsShareable(IPAddress.Parse(value)), $"Expected shareable: {value}");
    foreach (string value in new[] { "0.0.0.0", "127.0.0.1", "224.0.0.1", "255.255.255.255", "::", "::1", "ff02::1", "fe80::1", "fe80::1%1", "::ffff:192.168.1.8" })
        Check(!NetworkAddresses.IsShareable(IPAddress.Parse(value)), $"Expected unusable: {value}");
    return Task.CompletedTask;
}
static async Task InvitationTest()
{
    string pin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    ConnectionInvitation invitation = ConnectionInvitation.Create(IPAddress.Parse("192.168.1.8"), 45678, pin);
    string code = invitation.Encode();
    ConnectionInvitation decoded = ConnectionInvitation.Decode(code);
    Check(decoded.Address.Equals(invitation.Address) && decoded.Port == 45678 && decoded.CertificateFingerprint == pin);
    Check(decoded.GetSecret().SequenceEqual(invitation.GetSecret()));
    var routed = decoded.WithAddress(IPAddress.Parse("10.8.0.9"));
    Check(routed.Address.Equals(IPAddress.Parse("10.8.0.9")) && routed.Port == decoded.Port);
    Check(routed.CertificateFingerprint == decoded.CertificateFingerprint && routed.GetSecret().SequenceEqual(decoded.GetSecret()));
    Check(decoded.Address.Equals(IPAddress.Parse("192.168.1.8")));
    await Throws<ArgumentException>(() => Task.Run(() => decoded.WithAddress(IPAddress.Any)));
    await Throws<FormatException>(() => Task.Run(() => ConnectionInvitation.Decode(code + "!")));
    await Throws<FormatException>(() => Task.Run(() => ConnectionInvitation.Decode("A")));
    await Throws<ArgumentException>(() => Task.Run(() => ConnectionInvitation.Create(IPAddress.Any, 45678, pin)));
    var ipv6 = decoded.WithAddress(IPAddress.Parse("fd00::42"));
    var ipv6Decoded = ConnectionInvitation.Decode(ipv6.Encode());
    Check(ipv6Decoded.Address.Equals(ipv6.Address) && ipv6Decoded.Port == decoded.Port &&
        ipv6Decoded.CertificateFingerprint == decoded.CertificateFingerprint && ipv6Decoded.GetSecret().SequenceEqual(decoded.GetSecret()));
    var clock = new TestClock();
    var verifier = new InvitationVerifier(invitation, TimeSpan.FromMinutes(1), clock);
    Check(!verifier.TryRedeem(RandomNumberGenerator.GetBytes(32)));
    byte[] secret = decoded.GetSecret();
    bool[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => verifier.TryRedeem(secret))));
    Check(results.Count(x => x) == 1, "Invitation must be redeemable once even under concurrency");
    var expired = new InvitationVerifier(invitation, TimeSpan.FromMinutes(1), clock);
    clock.Advance(TimeSpan.FromMinutes(1));
    Check(!expired.TryRedeem(secret));
    CryptographicOperations.ZeroMemory(secret);
}
static async Task PairingSession(bool accept, bool ipv6 = false)
{
    IPAddress loopback = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    using var serverId = Identity("receiver"); using var clientId = Identity("sender");
    using var listener = new TcpListener(loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var invitation = ConnectionInvitation.Create(loopback, port, PinnedTls.Fingerprint(serverId));
    var verifier = new InvitationVerifier(invitation, TimeSpan.FromMinutes(1));
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var client = new TcpClient(loopback.AddressFamily);
    Task connect = client.ConnectAsync(loopback, port, cts.Token).AsTask();
    using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token); await connect;
    bool approvalCalled = false;
    Task<PinnedConnection> receiving = InvitationPairing.AcceptAsync(server, serverId, verifier, (request, _) =>
    {
        approvalCalled = true;
        Check(request.SenderName == "sender" && request.SenderFingerprint == PinnedTls.Fingerprint(clientId));
        return Task.FromResult(accept);
    }, cts.Token);
    Task<PinnedConnection> sending = InvitationPairing.ConnectAsync(client, clientId, ConnectionInvitation.Decode(invitation.Encode()), "sender", cts.Token);
    if (!accept)
    {
        await Throws<System.Security.Authentication.AuthenticationException>(async () => { await sending; });
        await Throws<System.Security.Authentication.AuthenticationException>(async () => { await receiving; });
        Check(approvalCalled);
        return;
    }
    await using var receiver = await receiving; await using var sender = await sending;
    Check(approvalCalled);
    string dir = Temp();
    try
    {
        Task<bool> receiveFile = TransferEngine.ReceiveAsync(receiver, dir, 10, (_, _) => Task.FromResult(true), ct: cts.Token);
        Check(await TransferEngine.SendAsync(sender, [new(new("ok.txt", 2, null), () => new MemoryStream([4, 2]))], ct: cts.Token));
        Check(await receiveFile && File.ReadAllBytes(Path.Combine(dir, "ok.txt")).SequenceEqual(new byte[] { 4, 2 }));
    }
    finally { Directory.Delete(dir, true); }
}
static Task PairingTest() => PairingSession(true);
static Task PairingIpv6Test() => PairingSession(true, ipv6: true);
static Task PairingRejectionTest() => PairingSession(false);
static async Task ProtocolTest()
{
    var input = new Offer(Protocol.Version, Guid.NewGuid(), [new FileOffer("hello.txt", 42, "text/plain")]);
    using var stream = new MemoryStream();
    await Protocol.WriteAsync(stream, input); stream.Position = 0;
    Offer result = await Protocol.ReadAsync<Offer>(stream);
    Check(result.TransferId == input.TransferId && result.Files[0] == input.Files[0]);
    Check(OfferValidator.Validate(result, 42) == 42);
    await Throws<InvalidDataException>(() => Task.Run(() => OfferValidator.Validate(result with { Version = 999 }, 42)));
    await Throws<InvalidDataException>(() => Task.Run(() => OfferValidator.Validate(result, 41)));
    await Throws<InvalidDataException>(() => Task.Run(() => OfferValidator.Validate(result with { Files = [new("x", long.MaxValue, null), new("y", 1, null)] }, long.MaxValue)));
}
static async Task FramingTest()
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, Protocol.MaxControlBytes + 1);
    await Throws<InvalidDataException>(() => Protocol.ReadAsync<Offer>(new MemoryStream(header)));
    BinaryPrimitives.WriteInt32BigEndian(header, 10);
    await Throws<EndOfStreamException>(() => Protocol.ReadAsync<Offer>(new MemoryStream(header)));
}
static async Task NamesTest()
{
    Check(SafeFiles.Sanitize("CON.txt") == "_CON.txt");
    Check(SafeFiles.Sanitize("a:b?.txt") == "a_b_.txt");
    await Throws<InvalidDataException>(() => Task.Run(() => SafeFiles.Sanitize("../bad")));
    string dir = Temp();
    try
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "original");
        string temp = Path.Combine(dir, "partial"); File.WriteAllText(temp, "new");
        string output = SafeFiles.Commit(temp, dir, "a.txt");
        Check(Path.GetFileName(output) == "a (1).txt");
        Check(File.ReadAllText(Path.Combine(dir, "a.txt")) == "original");
    }
    finally { Directory.Delete(dir, true); }
}
static string Temp() { string p = Path.Combine(Path.GetTempPath(), "hamresan-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
static async Task WithTls(Func<PinnedConnection, PinnedConnection, Task> body, bool wrongPin = false)
{
    using var serverId = Identity("server"); using var clientId = Identity("client"); using var other = Identity("other");
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var client = new TcpClient();
    Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cts.Token).AsTask();
    using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token); await connect;
    Task<PinnedConnection> s = PinnedTls.AcceptAsync(server, serverId, PinnedTls.Fingerprint(clientId), cts.Token);
    Task<PinnedConnection> c = PinnedTls.ConnectAsync(client, clientId, PinnedTls.Fingerprint(wrongPin ? other : serverId), cts.Token);
    if (wrongPin)
    {
        bool failed = false;
        try { await c; } catch (Exception) { failed = true; }
        Check(failed, "Wrong pin was accepted");
        client.Close();
        try { await using var unused = await s; } catch (Exception) { }
        return;
    }
    await using PinnedConnection serverTls = await s;
    await using PinnedConnection clientTls = await c;
    await body(clientTls, serverTls);
}
static async Task TransferTest()
{
    string dir = Temp(); byte[] payload = RandomNumberGenerator.GetBytes(700_000);
    try
    {
        await WithTls(async (client, server) =>
        {
            var files = new OutgoingFile[] { new(new("file.bin", payload.LongLength, null), () => new MemoryStream(payload)), new(new("empty", 0, null), () => new MemoryStream()) };
            Task<bool> receive = TransferEngine.ReceiveAsync(server, dir, 1_000_000, (offer, _) => Task.FromResult(offer.Files.Count == 2));
            Check(await TransferEngine.SendAsync(client, files)); Check(await receive);
        });
        Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "file.bin"))).SequenceEqual(SHA256.HashData(payload)));
        Check(new FileInfo(Path.Combine(dir, "empty")).Length == 0);
    }
    finally { Directory.Delete(dir, true); }
}
static async Task RejectionTest()
{
    string dir = Temp(); bool opened = false;
    try
    {
        await WithTls(async (client, server) =>
        {
            Task<bool> receive = TransferEngine.ReceiveAsync(server, dir, 100, (_, _) => Task.FromResult(false));
            bool sent = await TransferEngine.SendAsync(client, [new(new("no.txt", 1, null), () => { opened = true; return new MemoryStream([1]); })]);
            Check(!sent && !await receive && !opened);
        });
        Check(!Directory.EnumerateFileSystemEntries(dir).Any());
    }
    finally { Directory.Delete(dir, true); }
}
static async Task PinTest() => await WithTls((_, _) => Task.CompletedTask, true);
static async Task CancellationTest()
{
    string dir = Temp();
    try
    {
        await WithTls(async (_, server) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Throws<OperationCanceledException>(() => TransferEngine.ReceiveAsync(server, dir, 100, (_, _) => Task.FromResult(true), ct: cts.Token));
        });
    }
    finally { Directory.Delete(dir, true); }
}
static async Task HashFailureTest()
{
    string dir = Temp();
    try
    {
        await WithTls(async (client, server) =>
        {
            Task<bool> receive = TransferEngine.ReceiveAsync(server, dir, 100, (_, _) => Task.FromResult(true));
            await Protocol.WriteAsync(client.Stream, new Offer(Protocol.Version, Guid.NewGuid(), [new("bad", 3, null)]));
            Check((await Protocol.ReadAsync<Decision>(client.Stream)).Accepted);
            await client.Stream.WriteAsync(new byte[] { 1, 2, 3 });
            await Protocol.WriteAsync(client.Stream, new Digest(new string('0', 64)));
            Check(!(await Protocol.ReadAsync<FileResult>(client.Stream)).Accepted);
            await Throws<InvalidDataException>(async () => { await receive; });
        });
        Check(!Directory.EnumerateFileSystemEntries(dir).Any());
    }
    finally { Directory.Delete(dir, true); }
}
static async Task DisconnectTest()
{
    string dir = Temp();
    try
    {
        await WithTls(async (client, server) =>
        {
            Task<bool> receive = TransferEngine.ReceiveAsync(server, dir, 100, (_, _) => Task.FromResult(true));
            await Throws<EndOfStreamException>(async () => { await TransferEngine.SendAsync(client, [new(new("bad", 3, null), () => new MemoryStream([1]))]); });
            await client.DisposeAsync();
            await Throws<EndOfStreamException>(async () => { await receive; });
        });
        Check(!Directory.EnumerateFileSystemEntries(dir).Any());
    }
    finally { Directory.Delete(dir, true); }
}
static async Task WriteErrorTest()
{
    string dir = Temp(); string notDirectory = Path.Combine(dir, "file"); File.WriteAllText(notDirectory, "x");
    try
    {
        await WithTls(async (client, server) =>
        {
            Task<bool> receive = TransferEngine.ReceiveAsync(server, notDirectory, 100, (_, _) => Task.FromResult(true));
            await Protocol.WriteAsync(client.Stream, new Offer(Protocol.Version, Guid.NewGuid(), [new("x", 1, null)]));
            Check((await Protocol.ReadAsync<Decision>(client.Stream)).Accepted);
            await Throws<IOException>(async () => { await receive; });
        });
    }
    finally { Directory.Delete(dir, true); }
}

static async Task CancelTransferTest()
{
    string dir = Temp();
    try
    {
        await WithTls(async (client, server) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new InlineProgress<TransferProgress>(_ => progress.TrySetResult());
            Task<bool> receive = TransferEngine.ReceiveAsync(server, dir, 100, (_, _) => Task.FromResult(true), observer, cts.Token);
            await Protocol.WriteAsync(client.Stream, new Offer(Protocol.Version, Guid.NewGuid(), [new("incomplete", 3, null)]));
            Check((await Protocol.ReadAsync<Decision>(client.Stream)).Accepted);
            await client.Stream.WriteAsync(new byte[] { 1 });
            await progress.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Throws<OperationCanceledException>(async () => { await receive; });
        });
        Check(!Directory.EnumerateFileSystemEntries(dir).Any());
    }
    finally { Directory.Delete(dir, true); }
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

sealed class TestClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
}
