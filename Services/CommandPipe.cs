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
/// CurrentUserOnly, so a pipe of that name made by another account is refused. No TCP port, nothing from the network.</summary>
public static class CommandPipe
{
    /// <summary>How long a second start waits for the running MultiPreview, connecting and answering together.</summary>
    public static readonly TimeSpan SendTime = TimeSpan.FromSeconds(3);

    public static string Name(string userSid, int sessionId) => $"EveMultiPreview.Commands.{sessionId}.{userSid}";

    /// <summary>The Windows account this process runs as, and its token's default owner (the Administrators group for an
    /// elevated administrator, else the account).</summary>
    public static (SecurityIdentifier User, SecurityIdentifier Owner) CurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        return (user, identity.Owner ?? user);
    }

    public static string CurrentName() => Name(CurrentAccount().User.Value, SessionOfThisProcess());

    public static PipeSecurity Security(SecurityIdentifier user, SecurityIdentifier owner)
    {
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        // One allow rule: the owner. For an elevated run that is the Administrators group, not the account (user, which is
        // then left out): a pipe made by an elevated process carries no integrity label that would keep the same account's
        // non-elevated programs out, so a rule for the account would let them in. MultiPreview's own sender refuses the mismatch.
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Sends one command and waits for the answer, at most <paramref name="timeout"/> in all. Off the caller's
    /// thread, so a window's thread can wait for it without waiting for itself.</summary>
    public static RemoteOutcome Send(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront = false) =>
        Task.Run(() => SendAsync(pipeName, command, timeout, letTakeTheFront)).GetAwaiter().GetResult();

    /// <summary>Only what the running MultiPreview would take goes on the pipe: a command built past the allow-list's
    /// checks (a name too long for the length byte, a lone surrogate, one Clean refuses) is Refused here, never cut
    /// short and never sent.</summary>
    public static async Task<RemoteOutcome> SendAsync(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront = false)
    {
        byte[] message;
        try { message = command.ToMessage(); }
        catch (EncoderFallbackException) { return RemoteOutcome.Refused; }
        if (RemoteCommand.FromMessage(message) != command) return RemoteOutcome.Refused;

        using var limit = new CancellationTokenSource(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(limit.Token);
            if (letTakeTheFront) LetTakeTheFront(pipe.SafePipeHandle);
            var frame = new byte[message.Length + 1];
            frame[0] = (byte)message.Length;
            message.CopyTo(frame, 1);
            await pipe.WriteAsync(frame, limit.Token);
            await pipe.FlushAsync(limit.Token);
            var reply = new byte[1];
            int read = await pipe.ReadAsync(reply, limit.Token);
            return read == 1 && reply[0] <= (byte)RemoteOutcome.NotFound ? (RemoteOutcome)reply[0] : RemoteOutcome.NotReachable;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return RemoteOutcome.NotReachable;
        }
    }

    /// <summary>--focus (ruling 6): Windows lets only a program that has the front give it away, so the second start lets
    /// the process at the other end of the pipe take it. Never called by a test.</summary>
    public static void LetTakeTheFront(SafePipeHandle pipe)
    {
        if (GetNamedPipeServerProcessId(pipe, out uint serverProcess)) AllowSetForegroundWindow(serverProcess);
    }

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
}

/// <summary>IDEAS #39: the running MultiPreview's end of the pipe, on a pool thread. One client at a time: it reads one
/// length byte and at most 255 bytes within the read time, has the command run (App runs it on the window's thread),
/// answers one byte and waits for the client to hang up (the two together at most the read time), and serves the next. A
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
    private readonly Action<Exception> _failed;
    private readonly TimeSpan _readTime;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile bool _gaveUp;

    public CommandServer(string name, PipeSecurity security, Func<RemoteCommand, Task<RemoteOutcome>> run, Action<Exception> failed, TimeSpan? readTime = null)
    {
        _name = name;
        _security = security;
        _run = run;
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
            var outcome = await Serve(pipe, stop);
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(stop);
            reply.CancelAfter(_readTime);
            await pipe.WriteAsync(new[] { (byte)outcome }, reply.Token);
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

    private async Task<RemoteOutcome> Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
        read.CancelAfter(_readTime);
        var length = new byte[1];
        await pipe.ReadExactlyAsync(length, read.Token);
        if (length[0] == 0) return RemoteOutcome.Refused;
        var message = new byte[length[0]];
        await pipe.ReadExactlyAsync(message, read.Token);
        var command = RemoteCommand.FromMessage(message);
        return command == null ? RemoteOutcome.Refused : await _run(command);
    }
}
