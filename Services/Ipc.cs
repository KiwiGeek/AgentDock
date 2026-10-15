using System.IO.Pipes;
using System.Text;

namespace AgentDock.Services;

public enum DockCommandKind
{
    Show,
    Launch,
    Background,
    Quit
}

public readonly record struct DockCommand(DockCommandKind Kind, string? AgentId = null)
{
    public static DockCommand FromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--launch" when i + 1 < args.Length: return new(DockCommandKind.Launch, args[i + 1]);
                case "--background": return new(DockCommandKind.Background);
                case "--quit": return new(DockCommandKind.Quit);
            }
        }
        return new(DockCommandKind.Show);
    }

    public string ToMessage() => AgentId is null ? Kind.ToString() : $"{Kind} {AgentId}";

    public static DockCommand? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var parts = message.Trim().Split(' ', 2);
        return Enum.TryParse<DockCommandKind>(parts[0], ignoreCase: true, out var kind)
            ? new DockCommand(kind, parts.Length > 1 ? parts[1] : null)
            : null;
    }
}

/// <summary>One-line messages from a newly started copy to the running instance.</summary>
public static class Ipc
{
    private static string PipeName =>
        $"AgentDock.{Environment.UserName}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    public static bool TrySend(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // Generous timeout: the running instance may itself still be starting up.
            client.Connect(5000);

            // The pipe is unbuffered, so a write waits until the other side reads. A hung instance
            // never reads, and without this timeout the sender would hang with it.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            client.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    public static async Task ListenAsync(Action<DockCommand> onCommand, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                if (DockCommand.Parse(await reader.ReadLineAsync(cancellationToken)) is { } command)
                    onCommand(command);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away mid-message; wait for the next one.
            }
        }
    }
}
