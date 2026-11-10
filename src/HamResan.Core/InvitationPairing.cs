using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HamResan.Core;

public sealed record PairingRequest(string SenderName, string SenderFingerprint);
internal sealed record InvitationHello(int Version, string SenderName, string Secret);
internal sealed record InvitationDecision(int Version, bool Accepted);

/// <summary>
/// Establishes a single transfer session using a privately shared invitation. This is
/// not persistent pairing. Both callers must dispose the connection on failure.
/// </summary>
public static class InvitationPairing
{
    public static async Task<PinnedConnection> ConnectAsync(TcpClient client, X509Certificate2 identity,
        ConnectionInvitation invitation, string senderName, CancellationToken ct)
    {
        ValidateName(senderName);
        byte[] pin = Convert.FromHexString(invitation.CertificateFingerprint);
        // Receiver identity is pinned before the bearer secret is sent.
        var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), pin));
        byte[] secret = invitation.GetSecret();
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "hamresan.local", ClientCertificates = new X509CertificateCollection { identity },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            if (!tls.IsMutuallyAuthenticated) throw new AuthenticationException("Client certificate required.");
            await Protocol.WriteAsync(tls, new InvitationHello(Protocol.Version, senderName, Convert.ToHexString(secret)), ct);
            InvitationDecision decision = await Protocol.ReadAsync<InvitationDecision>(tls, ct);
            if (decision.Version != Protocol.Version || !decision.Accepted) throw new AuthenticationException("Invitation was rejected.");
            return new PinnedConnection(tls);
        }
        catch { await tls.DisposeAsync(); throw; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static async Task<PinnedConnection> AcceptAsync(TcpClient client, X509Certificate2 identity,
        InvitationVerifier verifier, Func<PairingRequest, CancellationToken, Task<bool>> approve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(approve);
        if (!verifier.MatchesIssuer(identity)) throw new ArgumentException("Invitation does not match the receiver identity.", nameof(identity));
        // This narrowly scoped bootstrap callback accepts a presented certificate only provisionally.
        // No PinnedConnection is returned until the secret and explicit consent are checked.
        var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            if (!tls.IsMutuallyAuthenticated || tls.RemoteCertificate is null) throw new AuthenticationException("Client certificate required.");
            InvitationHello hello = await Protocol.ReadAsync<InvitationHello>(tls, ct);
            ValidateName(hello.SenderName);
            if (hello.Version != Protocol.Version) throw new AuthenticationException("Unsupported version.");
            byte[] secret;
            try { secret = Convert.FromHexString(hello.Secret); }
            catch (Exception e) when (e is FormatException or ArgumentNullException) { throw new AuthenticationException("Invalid invitation.", e); }
            bool valid;
            try { valid = verifier.TryRedeem(secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            if (!valid) throw new AuthenticationException("Invalid or expired invitation.");
            var request = new PairingRequest(hello.SenderName, PinnedTls.Fingerprint(tls.RemoteCertificate));
            bool accepted = await approve(request, ct);
            await Protocol.WriteAsync(tls, new InvitationDecision(Protocol.Version, accepted), ct);
            if (!accepted) throw new AuthenticationException("Invitation was rejected.");
            return new PinnedConnection(tls);
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid sender name.");
    }
}
