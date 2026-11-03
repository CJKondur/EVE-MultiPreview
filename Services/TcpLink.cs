using System.Net;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #47: one TCP connection of this PC, with the process that owns it.</summary>
public readonly record struct TcpLink(int Pid, IPAddress Remote, int RemotePort, bool Established);
