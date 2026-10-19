using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #39: the named pipe another program's command comes through. Its name holds the Windows session and the
/// account's SID, so two accounts signed in at once never meet. Only this account can open it: its owner is the token's
/// default owner (what .NET's CurrentUserOnly check on the client compares with), the account and that owner are allowed,
/// the NETWORK group is denied, and nothing is inherited. A client opens it with CurrentUserOnly, so a pipe of that name
/// made by another account is refused. No TCP port, nothing from the network.</summary>
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
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!owner.Equals(user)) security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Sends one command and waits for the answer, at most <paramref name="timeout"/> in all. Off the caller's
    /// thread, so a window's thread can wait for it without waiting for itself.</summary>
    public static RemoteOutcome Send(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront = false) =>
        Task.Run(() => SendAsync(pipeName, command, timeout, letTakeTheFront)).GetAwaiter().GetResult();

    public static async Task<RemoteOutcome> SendAsync(string pipeName, RemoteCommand command, TimeSpan timeout, bool letTakeTheFront = false)
    {
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(limit.Token);
            if (letTakeTheFront) LetTakeTheFront(pipe.SafePipeHandle);
            var message = command.ToMessage();
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
/// answers one byte, waits at most the read time for the client to hang up, and serves the next. A message that is not on
/// the allow-list is answered "refused"; a client that goes quiet is let go. Dispose stops it.</summary>
public sealed class CommandServer : IDisposable
{
    public static readonly TimeSpan ReadTime = TimeSpan.FromSeconds(2);
    private readonly string _name;
    private readonly PipeSecurity _security;
    private readonly Func<RemoteCommand, Task<RemoteOutcome>> _run;
    private readonly Action<Exception> _failed;
    private readonly TimeSpan _readTime;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public CommandServer(string name, PipeSecurity security, Func<RemoteCommand, Task<RemoteOutcome>> run, Action<Exception> failed, TimeSpan? readTime = null)
    {
        _name = name;
        _security = security;
        _run = run;
        _failed = failed;
        _readTime = readTime ?? ReadTime;
    }

    public void Start() => _loop ??= Task.Run(() => Loop(_stop.Token));

    /// <summary>Stops listening: a wait for a client ends at once. Does not wait for the loop.</summary>
    public void Dispose() => _stop.Cancel();

    private async Task Loop(CancellationToken stop)
    {
        int refused = 0;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, _security);
                refused = 0;
            }
            catch (IOException ex)
            {
                // The name is still held for a moment after the switch went off and on again: a few tries, then give up.
                if (++refused > 8) { _failed(ex); return; }
                try { await Task.Delay(250, stop); } catch (OperationCanceledException) { return; }
                continue;
            }
            catch (Exception ex) { _failed(ex); return; }

            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var outcome = await Serve(pipe, stop);
                    await pipe.WriteAsync(new[] { (byte)outcome }, stop);
                    await pipe.FlushAsync(stop);
                    await HangUp(pipe, stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }   // a client that said nothing in time
                catch (IOException) { }                  // the client went away
                catch (Exception ex) { _failed(ex); }
            }
        }
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

    /// <summary>The client hangs up once it has read the answer; the server waits at most the read time for that, so the
    /// answer is never lost to a closed pipe and a client that stays on costs at most that.</summary>
    private async Task HangUp(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        wait.CancelAfter(_readTime);
#pragma warning disable CA2022 // any byte or the end of the pipe is the hang-up: how many bytes came does not matter
        try { await pipe.ReadAsync(new byte[1], wait.Token); }
#pragma warning restore CA2022
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
    }
}
