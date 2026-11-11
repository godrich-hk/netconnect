using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HamResan.Core;

if (args.Length != 1) return 2;
try
{
    // Test invitation is stored in a private temporary file; it must not be printed.
    string code = (await File.ReadAllTextAsync(args[0])).Trim();
    ConnectionInvitation invitation = ConnectionInvitation.Decode(code);
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=HamResan CI", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using X509Certificate2 identity = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    using var client = new TcpClient(invitation.Address.AddressFamily);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await client.ConnectAsync(invitation.Address, invitation.Port, timeout.Token);
    await using PinnedConnection tls = await InvitationPairing.ConnectAsync(client, identity, invitation, "CI Sender", timeout.Token);
    byte[] payload = Encoding.UTF8.GetBytes("HamResan Android integration test\n");
    bool result = await TransferEngine.SendAsync(tls,
        [new OutgoingFile(new FileOffer("hamresan-ci.txt", payload.Length, "text/plain"), () => new MemoryStream(payload, false))],
        ct: timeout.Token);
    if (!result) throw new InvalidOperationException("The receiver declined the offer.");
    Console.WriteLine("PASS: receiver acknowledged the verified test file");
    return 0;
}
catch (Exception ex)
{
    // Do not log tokens, paths, peer metadata or exception messages.
    Console.Error.WriteLine($"FAIL: sender failed with {ex.GetType().Name}");
    return 1;
}
