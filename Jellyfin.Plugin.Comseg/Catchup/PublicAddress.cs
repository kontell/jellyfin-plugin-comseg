using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Comseg.Catchup;

/// <summary>
/// Which addresses a URL sent by a client may make the server connect to:
/// public ones only, never the server itself, its network, or link-local and
/// reserved ranges.
/// </summary>
public static class PublicAddress
{
    /// <summary>Whether an address is on the public internet.</summary>
    /// <param name="address">The address.</param>
    /// <returns>False for loopback, private, shared, link-local, multicast and reserved addresses.</returns>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                  // "this" network
                || b[0] == 10                                   // private
                || (b[0] == 100 && (b[1] & 0xC0) == 64)         // shared, carrier-grade NAT
                || b[0] == 127                                  // loopback
                || (b[0] == 169 && b[1] == 254)                 // link-local
                || (b[0] == 172 && (b[1] & 0xF0) == 16)         // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)      // protocol assignments
                || (b[0] == 192 && b[1] == 168)                 // private
                || (b[0] == 198 && (b[1] & 0xFE) == 18)         // benchmarking
                || b[0] >= 224);                                // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // NAT64 (64:ff9b::/96) carries an IPv4 address: judge that one.
            var b = address.GetAddressBytes();
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                return IsPublic(new IPAddress(b[12..]));
            }

            return true;
        }

        return false;
    }
}
