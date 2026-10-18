using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #47: one TCP connection of this PC, with the process that owns it.</summary>
public readonly record struct TcpLink(int Pid, IPAddress Remote, int RemotePort, bool Established);

/// <summary>IDEAS #47: reads this PC's TCP table (IPv4 and IPv6) so a client's connection to the game server can be seen.
/// The parsing takes bytes and never throws, so tests hand it tables they wrote; <see cref="Read"/> asks Windows.</summary>
public static class TcpTable
{
    /// <summary>Tranquility's port.</summary>
    public const int ServerPort = 26000;
    /// <summary>MIB_TCP_STATE_ESTAB.</summary>
    public const uint EstablishedState = 5;

    private const int RowSizeV4 = 24, RowSizeV6 = 56;

    /// <summary>The links of a TCP_TABLE_OWNER_PID_CONNECTIONS table of IPv4. A row that does not fit ends the parse.</summary>
    public static IReadOnlyList<TcpLink> ParseV4(ReadOnlySpan<byte> table)
    {
        var links = new List<TcpLink>();
        if (table.Length < 4) return links;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * RowSizeV4;
            if (o + RowSizeV4 > table.Length) break;
            var row = table.Slice(o, RowSizeV4);
            links.Add(new TcpLink(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(row[20..]),
                new IPAddress(row.Slice(12, 4)),
                row[16] << 8 | row[17],
                BinaryPrimitives.ReadUInt32LittleEndian(row) == EstablishedState));
        }
        return links;
    }

    /// <summary>The links of a TCP_TABLE_OWNER_PID_CONNECTIONS table of IPv6. A row that does not fit ends the parse.</summary>
    public static IReadOnlyList<TcpLink> ParseV6(ReadOnlySpan<byte> table)
    {
        var links = new List<TcpLink>();
        if (table.Length < 4) return links;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * RowSizeV6;
            if (o + RowSizeV6 > table.Length) break;
            var row = table.Slice(o, RowSizeV6);
            links.Add(new TcpLink(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(row[52..]),
                new IPAddress(row.Slice(24, 16)),
                row[44] << 8 | row[45],
                BinaryPrimitives.ReadUInt32LittleEndian(row[48..]) == EstablishedState));
        }
        return links;
    }

    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidConnections = 4;
    private const uint NoError = 0, InsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>Every TCP connection on this PC with its owning process, IPv4 and IPv6; null when Windows gave neither.
    /// A question to this PC's own Windows, not a network request, and no admin rights are needed. Called on the process
    /// monitor's pool thread only, never on the window's thread.</summary>
    public static IReadOnlyList<TcpLink>? Read()
    {
        var v4 = ReadFamily(AfInet);
        var v6 = ReadFamily(AfInet6);
        if (v4 == null && v6 == null) return null;
        var all = new List<TcpLink>();
        if (v4 != null) all.AddRange(ParseV4(v4));
        if (v6 != null) all.AddRange(ParseV6(v6));
        return all;
    }

    private static byte[]? ReadFamily(int af)
    {
        int size = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidConnections, 0);
            if ((result != InsufficientBuffer && result != NoError) || size <= 0) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidConnections, 0);
                if (result == NoError) { var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size); return bytes; }
                if (result != InsufficientBuffer) return null;   // otherwise the table grew between the two calls: ask again
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }
}
