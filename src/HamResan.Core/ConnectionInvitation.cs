using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HamResan.Core;

/// <summary>
/// An invitation carries a TLS certificate pin and a bearer secret. The secret must be
/// delivered to the intended sender over a private, authenticated channel. This type
/// does not implement the network handshake or grant access to the filesystem.
/// </summary>
public sealed class ConnectionInvitation
{
    public const int SecretLength = 32;
    private const byte FormatVersion = 1;
    private readonly byte[] _fingerprint;
    private readonly byte[] _secret;

    public IPAddress Address { get; }
    public int Port { get; }
    public string CertificateFingerprint => Convert.ToHexString(_fingerprint);

    private ConnectionInvitation(IPAddress address, int port, byte[] fingerprint, byte[] secret)
    {
        Address = address;
        Port = port;
        _fingerprint = fingerprint;
        _secret = secret;
    }

    public static ConnectionInvitation Create(IPAddress address, int port, string certificateFingerprint)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValidateAddressAndPort(address, port);
        byte[] pin;
        try { pin = Convert.FromHexString(certificateFingerprint); }
        catch (Exception e) when (e is FormatException or ArgumentNullException)
        { throw new ArgumentException("Expected a SHA-256 certificate fingerprint.", nameof(certificateFingerprint), e); }
        if (pin.Length != 32) throw new ArgumentException("Expected a SHA-256 certificate fingerprint.", nameof(certificateFingerprint));
        return new ConnectionInvitation(address, port, pin, RandomNumberGenerator.GetBytes(SecretLength));
    }

    // The code is sensitive: possession of it can authorize an invitation redemption.
    public string Encode()
    {
        byte[] address = Address.GetAddressBytes();
        byte[] data = new byte[2 + address.Length + 2 + 32 + SecretLength];
        data[0] = FormatVersion;
        data[1] = (byte)address.Length;
        address.CopyTo(data, 2);
        int offset = 2 + address.Length;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), (ushort)Port);
        _fingerprint.CopyTo(data, offset + 2);
        _secret.CopyTo(data, offset + 34);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ConnectionInvitation Decode(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 128 || code.Length % 4 == 1 ||
            code.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new FormatException("Invalid invitation code.");
        byte[] data;
        try
        {
            string base64 = code.Replace('-', '+').Replace('_', '/');
            data = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException e) { throw new FormatException("Invalid invitation code.", e); }
        if (data.Length < 2 || data[0] != FormatVersion || data[1] is not (4 or 16) || data.Length != 2 + data[1] + 2 + 32 + SecretLength)
            throw new FormatException("Unsupported or invalid invitation code.");
        int offset = 2 + data[1];
        var address = new IPAddress(data.AsSpan(2, data[1]));
        int port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        try { ValidateAddressAndPort(address, port); }
        catch (ArgumentException e) { throw new FormatException("Invalid invitation endpoint.", e); }
        var invitation = new ConnectionInvitation(address, port, data.AsSpan(offset + 2, 32).ToArray(), data.AsSpan(offset + 34, SecretLength).ToArray());
        // Reject alternative encodings of the same bytes.
        if (!string.Equals(code, invitation.Encode(), StringComparison.Ordinal)) throw new FormatException("Non-canonical invitation code.");
        return invitation;
    }

    // Caller must transmit this value only inside TLS after verifying CertificateFingerprint.
    // The invitation issuer must independently approve and pin the sender identity.
    public byte[] GetSecret() => (byte[])_secret.Clone();

    private static void ValidateAddressAndPort(IPAddress address, int port)
    {
        if (port is <= 0 or > 65535 ||
            address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast ||
            (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239) ||
            (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0))
            throw new ArgumentException("An unicast address and valid port are required.");
    }
}

/// <summary>In-memory, one-use secret verifier. It is not a pairing protocol.</summary>
public sealed class InvitationVerifier
{
    private readonly object _lock = new();
    private readonly byte[] _digest;
    private readonly long _issuedTimestamp;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;
    private bool _used;

    public InvitationVerifier(ConnectionInvitation invitation, TimeSpan lifetime, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        _clock = clock ?? TimeProvider.System;
        _issuedTimestamp = _clock.GetTimestamp();
        _lifetime = lifetime;
        byte[] secret = invitation.GetSecret();
        try { _digest = SHA256.HashData(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    // Call only after authenticating the receiver's TLS certificate at the sender and
    // obtaining explicit local receiver consent. An issuer should rate-limit attempts.
    public bool TryRedeem(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != ConnectionInvitation.SecretLength) return false;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(secret, digest);
        lock (_lock)
        {
            if (_used || _clock.GetElapsedTime(_issuedTimestamp) >= _lifetime || !CryptographicOperations.FixedTimeEquals(digest, _digest))
                return false;
            _used = true;
            return true;
        }
    }
}
