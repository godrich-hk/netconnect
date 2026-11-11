using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HamResan.Core;

if (args.Length != 3) return 2;
try
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=HamResan CI", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using X509Certificate2 identity = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    for (int i = 0; i < 2; i++)
    {
        while (!File.Exists(args[i])) await Task.Delay(200, timeout.Token);
        ConnectionInvitation invitation = ConnectionInvitation.Decode((await File.ReadAllTextAsync(args[i], timeout.Token)).Trim());
        using var client = new TcpClient(invitation.Address.AddressFamily);
        await client.ConnectAsync(invitation.Address, invitation.Port, timeout.Token);
        await using PinnedConnection tls = await InvitationPairing.ConnectAsync(client, identity, invitation, "CI Trusted Sender", timeout.Token);
        byte[] payload = Encoding.UTF8.GetBytes($"HamResan trusted transfer {i + 1}\n");
        bool sent = await TransferEngine.SendAsync(tls,
            [new OutgoingFile(new FileOffer($"auto-ci-{i + 1}.txt", payload.Length, "text/plain"), () => new MemoryStream(payload, false))],
            ct: timeout.Token);
        if (!sent) throw new InvalidOperationException("Receiver rejected transfer.");
        if (i == 0) await File.WriteAllTextAsync(args[2], "first complete", timeout.Token);
    }
    Console.WriteLine("PASS: the same sender completed two verified transfers");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: auto-receive sender failed with {ex.GetType().Name}");
    return 1;
}
