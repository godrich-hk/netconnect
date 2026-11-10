using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HamResan.Core;

/// <summary>Only for fingerprints verified independently by both users. Discovery is not trust.</summary>
public sealed class PinnedConnection : IAsyncDisposable
{
    internal SslStream Stream { get; }
    internal PinnedConnection(SslStream stream) => Stream = stream;
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public static class PinnedTls
{
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    private static byte[] ParsePin(string pin)
    {
        try
        {
            byte[] bytes = Convert.FromHexString(pin);
            if (bytes.Length != 32) throw new FormatException();
            return bytes;
        }
        catch (FormatException e) { throw new ArgumentException("A SHA-256 certificate fingerprint is required.", nameof(pin), e); }
    }

    private static bool Matches(X509Certificate? cert, byte[] pin) => cert is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), pin);

    public static async Task<PinnedConnection> ConnectAsync(TcpClient client, X509Certificate2 identity, string peerPin, CancellationToken ct)
    {
        byte[] pin = ParsePin(peerPin);
        var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => Matches(cert, pin));
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "hamresan.local", ClientCertificates = new X509CertificateCollection { identity },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            return new PinnedConnection(tls);
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    public static async Task<PinnedConnection> AcceptAsync(TcpClient client, X509Certificate2 identity, string peerPin, CancellationToken ct)
    {
        byte[] pin = ParsePin(peerPin);
        var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => Matches(cert, pin));
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            return new PinnedConnection(tls);
        }
        catch { await tls.DisposeAsync(); throw; }
    }
}
