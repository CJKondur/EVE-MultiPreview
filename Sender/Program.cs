using EveMultiPreview.Services;

namespace EveMultiPreview.Sender;

internal static class Program
{
    private static int Main(string[] args) =>
        SenderApp.Run(args, CommandPipe.CurrentName(), () => SenderApp.StartFull(AppContext.BaseDirectory, args));
}
