using System.Security.Cryptography;

namespace HamResan.Core;

/// <summary>In-memory, opt-in receiver policy. A certificate fingerprint is not a discovery ID.</summary>
public sealed class SessionTrust
{
    private readonly object _gate = new();
    private readonly HashSet<string> _automatic = new(StringComparer.Ordinal);
    private bool _enabled;

    public bool Enabled { get { lock (_gate) return _enabled; } set { lock (_gate) _enabled = value; } }

    // Call only after the user has approved the actual TLS peer and opted in for this peer.
    public void AllowAutomaticForApprovedPeer(string fingerprint)
    {
        string pin = Normalize(fingerprint);
        lock (_gate)
        {
            if (_automatic.Count >= 128 && !_automatic.Contains(pin)) throw new InvalidOperationException("Too many peers.");
            _automatic.Add(pin);
        }
    }

    public bool CanReceiveAutomatically(string fingerprint)
    {
        string pin;
        try { pin = Normalize(fingerprint); }
        catch (ArgumentException) { return false; }
        lock (_gate) return _enabled && _automatic.Contains(pin);
    }

    public void Clear() { lock (_gate) _automatic.Clear(); }

    private static string Normalize(string value)
    {
        try
        {
            byte[] bytes = Convert.FromHexString(value);
            if (bytes.Length != SHA256.HashSizeInBytes) throw new FormatException();
            return Convert.ToHexString(bytes);
        }
        catch (Exception e) when (e is FormatException or ArgumentNullException)
        { throw new ArgumentException("Expected a SHA-256 certificate fingerprint.", nameof(value), e); }
    }
}
