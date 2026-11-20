using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>
/// Which addresses a URL sent by a client may make the server connect to:
/// globally reachable ones only, after IANA's special-purpose address
/// registries. Never the server itself, its network, or link-local,
/// documentation, translation or reserved ranges.
/// </summary>
public static class PublicAddress
{
    /// <summary>Whether an address is on the public internet.</summary>
    /// <param name="address">The address.</param>
    /// <returns>True only for a globally reachable unicast address.</returns>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0                                          // "this" network
                || b[0] == 10                                           // private
                || (b[0] == 100 && (b[1] & 0xC0) == 64)                 // shared address space (CGNAT)
                || b[0] == 127                                          // loopback
                || (b[0] == 169 && b[1] == 254)                         // link-local
                || (b[0] == 172 && (b[1] & 0xF0) == 16)                 // private
                || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) // protocol assignments, documentation
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)            // 6to4 relay anycast (deprecated)
                || (b[0] == 192 && b[1] == 168)                         // private
                || (b[0] == 198 && (b[1] & 0xFE) == 18)                 // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)           // documentation
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)            // documentation
                || b[0] >= 224);                                        // multicast, reserved, broadcast
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // NAT64's well-known prefix, 64:ff9b::/96, carries an IPv4 address: judge
        // that one. Its local-use prefix, 64:ff9b:1::/48, falls outside 2000::/3 below.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublic(new IPAddress(b.AsSpan(12, 4)));
        }

        // Only global unicast, 2000::/3. That leaves out loopback, unspecified,
        // discard-only (100::/64), unique local (fc00::/7), link-local, site-local,
        // multicast and SRv6 SIDs (5f00::/16).
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        // 6to4 (2002::/16) carries an IPv4 address in its next 32 bits.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublic(new IPAddress(b.AsSpan(2, 4)));
        }

        return !((b[0] == 0x20 && b[1] == 0x01 && b[2] < 0x02)          // IETF protocol assignments, 2001::/23 (Teredo, ORCHID and others)
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // documentation, 2001:db8::/32
            || (b[0] == 0x3f && (b[1] & 0xF0) == 0xf0));               // documentation, 3fff::/20
    }
}
