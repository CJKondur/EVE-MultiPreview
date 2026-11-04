using System.Net.Sockets;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #39 on Linux (the Windows CommandPipe): commands from other programs, a Stream Deck app (StreamController,
/// streamdeck-ui, OpenDeck's "run a command"), a desktop shortcut or a script, run as a second start:
/// <c>eve-multipreview --focus Alpha</c>. The second start hands the command to the running MultiPreview over a Unix socket in
/// the account's own runtime folder ($XDG_RUNTIME_DIR, mode 0700, else a 0700 folder of this account under /tmp) with mode
/// 0600, so only programs of this account can reach it; nothing listens on the network. One frame each way: the command's
/// message (RemoteCommand.ToMessage, length byte first) and the outcome byte. The running MultiPreview takes a command only
/// while "Take commands from other programs" (AcceptCommands) is on; otherwise it answers Refused.</summary>
public static class CommandSocket
{
    /// <summary>How long a second start waits for its answer.</summary>
    public static readonly TimeSpan AnswerTime = TimeSpan.FromSeconds(4);

    /// <summary>The socket's path, or null when no folder of this account's own can be had.</summary>
    public static string? SocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string dir;
        if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)) dir = runtime;
        else
        {
            dir = Path.Combine(Path.GetTempPath(), $"eve-multipreview-{Environment.UserName}");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                // A folder someone else made there, or one others can enter, is not used.
                if (File.GetUnixFileMode(dir) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                    || new DirectoryInfo(dir).LinkTarget != null) return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return Path.Combine(dir, "eve-multipreview.sock");
    }

    // ── The second start ──

    /// <summary>Main's first look: a command line that is a command is sent and its outcome is the exit code (0 done, 1
    /// refused, 2 no such character/group/profile, 3 MultiPreview not running). Null: no arguments, start the app.</summary>
    public static int? SendFromCommandLine(string[] args)
    {
        if (args.Length == 0) return null;
        bool help = args is ["--help"] or ["-h"];
        if (help || RemoteCommand.FromArgs(args) is not { } command)
        {
            Console.Error.WriteLine(help ? "The commands are:" : "Not a MultiPreview command. The commands are:");
            foreach (var line in RemoteCommand.CommandLines(_ => "x"))
            {
                var f = line.Split('|');   // word|kind|label: the kind says what name it takes
                var name = f[1] switch { "c" => " <character>", "g" => " <group>", "p" => " <profile>", _ => "" };
                Console.Error.WriteLine($"  eve-multipreview --{f[0]}{name}");
            }
            return help ? 0 : (int)RemoteOutcome.Refused;
        }
        var outcome = Send(command);
        if (outcome == RemoteOutcome.NotReachable) Console.Error.WriteLine("MultiPreview is not running.");
        else if (outcome == RemoteOutcome.Refused) Console.Error.WriteLine("MultiPreview did not take it. Is \"Take commands from other programs\" on (Settings > General)?");
        else if (outcome == RemoteOutcome.NotFound) Console.Error.WriteLine("MultiPreview has no such character, group or profile.");
        return (int)outcome;
    }

    public static RemoteOutcome Send(RemoteCommand command)
    {
        var message = command.ToMessage();
        if (message.Length is 0 or > RemoteCommand.MaxMessageBytes || SocketPath() is not { } path || !File.Exists(path)) return RemoteOutcome.NotReachable;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = socket.SendTimeout = (int)AnswerTime.TotalMilliseconds;
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send([(byte)message.Length, .. message]);
            var answer = new byte[1];
            return socket.Receive(answer) == 1 && Enum.IsDefined((RemoteOutcome)answer[0]) ? (RemoteOutcome)answer[0] : RemoteOutcome.NotReachable;
        }
        catch (SocketException) { return RemoteOutcome.NotReachable; }
    }

    // ── The running MultiPreview ──

    /// <summary>Listens for the second starts' commands until the app ends, one at a time, on a thread of its own.
    /// <paramref name="run"/> is asked on that thread; it runs the command on the UI thread and answers. False when no socket
    /// could be made (the reason is written out); MultiPreview runs on without it.</summary>
    public static bool Serve(Func<RemoteCommand, RemoteOutcome> run)
    {
        if (SocketPath() is not { } path) { Console.WriteLine("Commands from other programs: no private folder for the socket."); return false; }
        Socket listener;
        try
        {
            // A socket left by a MultiPreview that ended without closing it: nobody answers on it, so it is replaced.
            if (File.Exists(path) && Send(new RemoteCommand(RemoteVerb.Hello, "")) == RemoteOutcome.NotReachable) File.Delete(path);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Commands from other programs: the socket could not be made ({e.Message}).");
            return false;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { listener.Close(); File.Delete(path); } catch (Exception) { } };
        new Thread(() =>
        {
            while (true)
            {
                Socket client;
                try { client = listener.Accept(); }
                catch (Exception) { return; }   // closed: the app is ending
                using (client)
                {
                    try
                    {
                        client.ReceiveTimeout = client.SendTimeout = 2000;
                        var length = new byte[1];
                        if (client.Receive(length) != 1) continue;
                        var message = new byte[length[0]];
                        int got = 0;
                        while (got < message.Length) { int n = client.Receive(message, got, message.Length - got, SocketFlags.None); if (n == 0) break; got += n; }
                        var command = got == message.Length ? RemoteCommand.FromMessage(message) : null;
                        var outcome = command == null ? RemoteOutcome.Refused
                            : command.Verb == RemoteVerb.Hello ? RemoteOutcome.Done   // "are you there?": the stale-socket check
                            : run(command);
                        client.Send([(byte)outcome]);
                    }
                    catch (SocketException) { }
                }
            }
        }) { IsBackground = true, Name = "Command socket" }.Start();
        return true;
    }
}
