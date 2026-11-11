using System.Net;
using System.Net.Sockets;

namespace HamResan.Core;

/// <summary>Address candidates that can be shared between devices without an interface-specific scope ID.
/// This only filters unusable candidates; it does not prove that a route or firewall permits traffic.</summary>
public static class NetworkAddresses
{
    public static bool IsShareable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte first = address.GetAddressBytes()[0];
            return first is > 0 and < 224 && !address.Equals(IPAddress.Broadcast);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast &&
                !address.IsIPv6LinkLocal && !address.IsIPv4MappedToIPv6 && address.ScopeId == 0;
        return false;
    }
}
