using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #39: the named pipe another program's command comes through. Its name holds the Windows session and the
/// account's SID, so two accounts signed in at once never meet. Only this account can open it: its owner is the token's
/// default owner (what .NET's CurrentUserOnly check on the client compares with), only that owner is allowed (the account
/// itself for a normal run, the Administrators group for an elevated one, so the same account's non-elevated programs are
/// refused by the pipe's list), the NETWORK group is denied, and nothing is inherited. A client opens it with
/// CurrentUserOnly, so a pipe of that name made by another account is refused. No TCP port, nothing from the network.
/// IDEAS #15, for an elevated MultiPreview only and only when the owner asks (Security's alsoUser): the account itself is
/// allowed too, and a sender that is not elevated may connect without CurrentUserOnly, but writes only after the process
/// that made the pipe proved to be an elevated process of this same account (IsThisAccountsElevatedProcess).</summary>
public static class CommandPipe
{
    /// <summary>How long a second start waits for the running MultiPreview to answer, writing and answering together (IDEAS
    /// P130). App waits up to <see cref="CommandServer.ReadTime"/> for the window's thread to START a command and then awaits the
    /// started one, and the server serves one sender at a time: a sender queued behind another also sits out that sender's
    /// turn. So the clock covers two queue waits and two seconds for a started command to finish; a shorter one let a command
    /// run while its sender had already ended with exit 3, which this makes far rarer (three or more queued senders can still
    /// outlast it: a time budget, not a guarantee). The connect has its own, shorter clock (<see cref="ConnectTime"/>). No
    /// protocol change: the sender's deadline is not sent.</summary>
    public static readonly TimeSpan SendTime = CommandServer.ReadTime + CommandServer.ReadTime + TimeSpan.FromSeconds(2);

    /// <summary>How long a second start waits to find the pipe at all (both tries of it together), within the caller's clock:
    /// "nothing took it" (no pipe: the switch is off, or MultiPreview is still starting) ends with exit 3 after this long, as it
    /// did when the whole clock was this long.</summary>
    public static readonly TimeSpan ConnectTime = TimeSpan.FromSeconds(3);

    public static string Name(string userSid, int sessionId) => $"EveMultiPreview.Commands.{sessionId}.{userSid}";

    /// <summary>The Windows account this process runs as, and its token's default owner (the Administrators group for an
    /// elevated administrator, else the account).</summary>
    public static (SecurityIdentifier User, SecurityIdentifier Owner) CurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        return (user, identity.Owner ?? user);
    }

    /// <summary>True when this process runs elevated (its token's owner is not the account): the pipe is then closed to the
    /// account's non-elevated programs, so a button's program may get no answer.</summary>
    public static bool IsElevated()
    {
        var (user, owner) = CurrentAccount();
        return owner != user;
    }

    public static string CurrentName() => Name(CurrentAccount().User.Value, SessionOfThisProcess());

    /// <summary>The pipe's list. One allow rule: the owner. For an elevated run that is the Administrators group, not the
    /// account (<paramref name="user"/>, which is then left out): a pipe made by an elevated process carries no integrity
    /// label that would keep the same account's non-elevated programs out, so a rule for the account would let them in.
    /// <para><paramref name="alsoUser"/> (IDEAS #15, the owner's second switch) adds that account rule anyway, and only for
    /// an elevated run (owner != user; for a normal run the owner already is the account, so there is nothing to add). It
    /// trades the door's strictness for reach: any ordinary program of the account can then switch clients and profiles
    /// (the allow-list of RemoteCommand still names no file and runs nothing). The account gets read/write only, never
    /// FullControl: it can connect and send a command, but cannot change the door (WRITE_DAC), take ownership
    /// (WRITE_OWNER) or make a pipe instance under the elevated server's name (CreateNewInstance), which would let an
    /// ordinary program drop the NETWORK deny or stand in for the server. The NETWORK deny and the protected, empty
    /// inheritance stay whatever it says.</para></summary>
    public static PipeSecurity Security(SecurityIdentifier user, SecurityIdentifier owner, bool alsoUser = false)
    {
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (alsoUser && owner != user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>Sends one command and waits for the answer, at most <paramref name="timeout"/> in all. Off the caller's
    /// thread, so a window's thread can wait for it without waiting for itself. <paramref name="onWritten"/> is called once
    /// just before the message is written, never when the connect or the trust check fails: NotReachable without it means
    /// nothing was sent (the thin sender then hands the command to the full exe), NotReachable after it means it may have run.</summary>
    public static RemoteOutcome Send(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront = false, Action? onWritten = null) =>
        Task.Run(() => SendAsync(pipeName, command, timeout, letTakeTheFront, onWritten)).GetAwaiter().GetResult();

    /// <summary>Only what the running MultiPreview would take goes on the pipe: a command built past the allow-list's
    /// checks (a name too long for the length byte, a lone surrogate, one Clean refuses) is Refused here, never cut
    /// short and never sent. So is a query verb (IDEAS P126): its reply carries data this one-byte reader would leave
    /// unread, so a query goes through <see cref="QueryAsync"/> only.</summary>
    public static async Task<RemoteOutcome> SendAsync(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront = false, Action? onWritten = null)
    {
        if (RemoteCommand.IsQuery(command.Verb)) return RemoteOutcome.Refused;
        return (await RoundTrip(pipeName, command, timeout, letTakeTheFront, wantsLines: false, onWritten: onWritten)).Outcome;
    }

    /// <summary>IDEAS P126: sends one query verb and reads its reply: the outcome and, for Done, the lines of the answer. At most
    /// <paramref name="timeout"/> in all, the payload's read included, and never more than <see cref="QueryPayload.MaxBytes"/>
    /// of it. A command that is not a query is Refused here and never sent. A MultiPreview that does not know the query word
    /// answers Refused in one byte: no lines, no wait.</summary>
    public static async Task<QueryReply> QueryAsync(string pipeName, RemoteCommand command, TimeSpan timeout)
    {
        if (!RemoteCommand.IsQuery(command.Verb)) return QueryReply.Refused;
        return await RoundTrip(pipeName, command, timeout, letTakeTheFront: false, wantsLines: true);
    }

    /// <summary>The same off the caller's thread, like <see cref="Send"/>.</summary>
    public static QueryReply Query(string pipeName, RemoteCommand command, TimeSpan timeout) =>
        Task.Run(() => QueryAsync(pipeName, command, timeout)).GetAwaiter().GetResult();

    /// <summary>One command, one connection: connects (with the elevated server's second try), writes the message and reads the
    /// reply; <paramref name="wantsLines"/> reads the payload of a query's Done as well.</summary>
    private static async Task<QueryReply> RoundTrip(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront, bool wantsLines, Action? onWritten = null)
    {
        byte[] message;
        try { message = command.ToMessage(); }
        catch (EncoderFallbackException) { return QueryReply.Refused; }
        if (RemoteCommand.FromMessage(message) != command) return QueryReply.Refused;

        using var limit = new CancellationTokenSource(timeout);
        using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        connectLimit.CancelAfter(ConnectTime);
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            try { await pipe.ConnectAsync(connectLimit.Token); }
            catch (UnauthorizedAccessException) when (!IsElevated())
            {
                // IDEAS #15: a sender that is not elevated is refused by an elevated MultiPreview's pipe (its owner is the
                // Administrators group, which CurrentUserOnly does not accept, and its list may not name the account). ONE
                // more try without CurrentUserOnly, which trusts nothing the pipe's name says: nothing is written (the command
                // can hold a profile's or group's name) unless the process that made the pipe is an elevated process of this
                // very account. A pipe another account, or a program that is not elevated, made under this name fails that
                // test. Only for a sender that is not elevated itself: an elevated sender gets no second try.
                pipe.Dispose();
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(connectLimit.Token);   // refused again: the outer catch answers NotReachable
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcess) || !IsThisAccountsElevatedProcess(serverProcess))
                    return QueryReply.NotReachable;
            }
            return await Exchange(pipe);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return QueryReply.NotReachable;   // a reply cut short, or one that outlasts the clock, ends here too
        }
        finally { pipe?.Dispose(); }

        async Task<QueryReply> Exchange(NamedPipeClientStream connected)
        {
            if (letTakeTheFront) LetTakeTheFront(connected.SafePipeHandle);
            var frame = new byte[message.Length + 1];
            frame[0] = (byte)message.Length;
            message.CopyTo(frame, 1);
            onWritten?.Invoke();   // the thin sender: past this point the command may have run, so it is never sent again
            await connected.WriteAsync(frame, limit.Token);
            await connected.FlushAsync(limit.Token);
            var reply = new byte[1];
            int read = await connected.ReadAsync(reply, limit.Token);
            if (read != 1 || reply[0] > (byte)RemoteOutcome.NotFound) return QueryReply.NotReachable;
            var outcome = (RemoteOutcome)reply[0];
            // IDEAS P126: only a query's Done has more to read (an older server refuses the word in one byte); the length is
            // checked against the cap before a byte of text is read, and every read ends with the clock.
            if (!wantsLines || outcome != RemoteOutcome.Done) return new QueryReply(outcome, Array.Empty<string>());
            var head = new byte[2];
            await connected.ReadExactlyAsync(head, limit.Token);
            int length = (head[0] << 8) | head[1];
            if (length > QueryPayload.MaxBytes) return QueryReply.NotReachable;
            var text = new byte[length];
            await connected.ReadExactlyAsync(text, limit.Token);
            return QueryPayload.Parse(text) is { } lines ? new QueryReply(RemoteOutcome.Done, lines) : QueryReply.NotReachable;
        }
    }

    /// <summary>--focus (ruling 6): Windows lets only a program that has the front give it away, so the second start lets
    /// the process at the other end of the pipe take it. Never called by a test.</summary>
    public static void LetTakeTheFront(SafePipeHandle pipe)
    {
        if (GetNamedPipeServerProcessId(pipe, out uint serverProcess)) AllowSetForegroundWindow(serverProcess);
    }

    /// <summary>True only when the process <paramref name="processId"/> runs as this very Windows account (its token's user
    /// is the current account's user) with an elevated token. A process that cannot be opened or read (gone, another
    /// account's, protected) is false: every failure is "not trusted". The second try of <see cref="SendAsync"/> asks it of
    /// the process that made a pipe before it writes anything there.</summary>
    public static bool IsThisAccountsElevatedProcess(uint processId)
    {
        if (processId == 0) return false;
        IntPtr process = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return false;
            if (!OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero) return false;
            return TokenUserIs(token, CurrentAccount().User) && TokenIsElevated(token);
        }
        catch (Exception) { return false; }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    /// <summary>The token's TOKEN_USER is read out of a buffer sized by the first call; the SID it points at must lie
    /// inside that buffer before it is read, and the buffer is freed.</summary>
    private static bool TokenUserIs(IntPtr token, SecurityIdentifier expected)
    {
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);   // fails by design: it only says how big
        if (needed < 2 * IntPtr.Size || needed > 4096) return false;
        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return false;
            IntPtr sid = Marshal.ReadIntPtr(buffer);     // TOKEN_USER starts with SID_AND_ATTRIBUTES.Sid
            long at = sid.ToInt64() - buffer.ToInt64();
            if (sid == IntPtr.Zero || at < 2 * IntPtr.Size || at >= needed) return false;
            return new SecurityIdentifier(sid) == expected;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TokenIsElevated(IntPtr token) =>
        GetTokenInformationElevation(token, TokenElevation, out int elevation, sizeof(int), out int returned) && returned == sizeof(int) && elevation != 0;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;

    private static int SessionOfThisProcess() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint session) ? (int)session : 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformationElevation(IntPtr token, int informationClass, out int information, int length, out int returnLength);
}

/// <summary>IDEAS #39: the running MultiPreview's end of the pipe, on a pool thread. One client at a time: it reads one
/// length byte and at most 255 bytes within the read time, has the command run (App runs it on the window's thread),
/// answers one byte (IDEAS P126: a query verb's Done is followed by its payload, at most 4,096 bytes, in the same write) and
/// waits for the client to hang up (the two together at most the read time), and serves the next. A
/// message that is not on the allow-list is answered "refused"; a client that goes quiet is let go. Dispose stops it.
/// <para>The name is never free while it runs: the first instance is made with FirstPipeInstance, so a pipe of that
/// name another account made first is never joined (that account's door would govern it); and the next instance is made
/// as soon as a client connects, before the one serving it goes, so nobody can make the name in between. A name already
/// taken is tried again for two seconds (it is still held for a moment after the switch went off and on), then reported.
/// What reaches <c>failed</c> never carries the pipe's name: it holds the account's SID.</para>
/// <para>Known limits: a client that says nothing, or only part of a message, holds the door at most the read time; one
/// that sends a command and never reads the answer at most the read time, App's run time and the read time again. A run
/// of App's that hangs holds the server (App only posts the command to the window's thread, so it stays quick). A program
/// of this same account that connects again and again can keep the one door busy: the same account is inside the trust
/// boundary. If the next instance cannot be made, the server stops and reports. Another account that takes the name
/// before the server starts only keeps the feature off (reported); it cannot take over the pipe.</para></summary>
public sealed class CommandServer : IDisposable
{
    public static readonly TimeSpan ReadTime = TimeSpan.FromSeconds(2);
    /// <summary>Instances of the pipe at once: the one serving a client and the one standing for the next.</summary>
    public const int Instances = 2;
    private readonly string _name;
    private readonly PipeSecurity _security;
    private readonly Func<RemoteCommand, Task<RemoteOutcome>> _run;
    private readonly Func<RemoteCommand, Task<QueryReply>>? _query;
    private readonly Action<Exception> _failed;
    private readonly TimeSpan _readTime;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile bool _gaveUp;

    /// <param name="query">IDEAS P126: answers a query verb (its reply carries data). Null: a query is answered Refused. A
    /// query verb never reaches <paramref name="run"/>, and any other verb never reaches this.</param>
    public CommandServer(string name, PipeSecurity security, Func<RemoteCommand, Task<RemoteOutcome>> run, Action<Exception> failed, TimeSpan? readTime = null,
        Func<RemoteCommand, Task<QueryReply>>? query = null)
    {
        _name = name;
        _security = security;
        _run = run;
        _query = query;
        _failed = failed;
        _readTime = readTime ?? ReadTime;
    }

    /// <summary>True once the server gave up for good: the name stayed taken, or the next instance could not be made
    /// (set before that is reported). A command App's run threw on is reported too, but the server goes on and this stays
    /// false. App lets a server that gave up go, so that the next settings change makes a new one.</summary>
    public bool Failed => _gaveUp;

    public void Start() => _loop ??= Task.Run(() => Loop(_stop.Token));

    /// <summary>Stops listening: a wait for a client ends at once. Does not wait for the loop.</summary>
    public void Dispose() => _stop.Cancel();

    private async Task Loop(CancellationToken stop)
    {
        NamedPipeServerStream? standing = null;
        try
        {
            standing = await OpenFirst(stop);
            while (standing != null && !stop.IsCancellationRequested)
            {
                using var current = standing;
                standing = null;
                bool connected = true;
                try { await current.WaitForConnectionAsync(stop); }
                catch (OperationCanceledException) { return; }      // stopped
                catch (IOException) { connected = false; }           // a client that came and went at once
                // The next instance stands before this one goes: the name is never free while the server runs.
                standing = Create(first: false);
                if (connected) await Answer(current, stop);
            }
        }
        catch (Exception ex) { _gaveUp = true; Report(ex); }
        finally { standing?.Dispose(); }
    }

    /// <summary>The first instance, or null (reported) when the name stays taken or the server was stopped.</summary>
    private async Task<NamedPipeServerStream?> OpenFirst(CancellationToken stop)
    {
        for (int tries = 1; !stop.IsCancellationRequested; tries++)
        {
            try { return Create(first: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held for a moment after the switch went off and on again, or made by someone else: a few tries.
                if (tries > 8) { _gaveUp = true; Report(ex); return null; }
            }
            try { await Task.Delay(250, stop); } catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private NamedPipeServerStream Create(bool first) =>
        NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte,
            first ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous, 0, 0, _security);

    /// <summary>Reads the command, has it run, answers, and waits for the client to hang up. The answer and the hang-up
    /// share one read time, so a client that never reads the answer is let go too.</summary>
    private async Task Answer(NamedPipeServerStream pipe, CancellationToken stop)
    {
        try
        {
            var (outcome, payload) = await Serve(pipe, stop);
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(stop);
            reply.CancelAfter(_readTime);
            // The outcome byte, and for a query's Done the payload after it, in one write. Any other reply is exactly one byte.
            var answer = new byte[1 + payload.Length];
            answer[0] = (byte)outcome;
            payload.CopyTo(answer, 1);
            await pipe.WriteAsync(answer, reply.Token);
            await pipe.FlushAsync(reply.Token);
#pragma warning disable CA2022 // any byte or the end of the pipe is the hang-up: how many bytes came does not matter
            await pipe.ReadAsync(new byte[1], reply.Token);
#pragma warning restore CA2022
        }
        catch (OperationCanceledException) { }   // stopped, or a client that said or read nothing in time
        catch (IOException) { }                  // the client went away
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Hands a failure on without the pipe's name (an exception of a pipe can carry its path, and the name holds
    /// the account's SID); a listener that throws never stops the loop.</summary>
    private void Report(Exception ex)
    {
        try
        {
            _failed(ex.ToString().Contains(_name, StringComparison.OrdinalIgnoreCase)
                ? new IOException($"The command pipe failed: {ex.GetType().Name} (0x{ex.HResult:X8}).")
                : ex);
        }
        catch (Exception) { }
    }

    private async Task<(RemoteOutcome Outcome, byte[] Payload)> Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
        read.CancelAfter(_readTime);
        var length = new byte[1];
        await pipe.ReadExactlyAsync(length, read.Token);
        if (length[0] == 0) return (RemoteOutcome.Refused, Array.Empty<byte>());
        var message = new byte[length[0]];
        await pipe.ReadExactlyAsync(message, read.Token);
        var command = RemoteCommand.FromMessage(message);
        if (command == null) return (RemoteOutcome.Refused, Array.Empty<byte>());
        if (!RemoteCommand.IsQuery(command.Verb)) return (await _run(command), Array.Empty<byte>());
        // IDEAS P126: only a query's Done carries a payload (cut at a whole line within the cap, a refused name left out)
        if (_query == null) return (RemoteOutcome.Refused, Array.Empty<byte>());
        var answer = await _query(command);
        return answer.Outcome == RemoteOutcome.Done ? (RemoteOutcome.Done, QueryPayload.Frame(answer.Lines)) : (answer.Outcome, Array.Empty<byte>());
    }
}
