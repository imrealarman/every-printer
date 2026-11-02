using System.IO.Pipes;

namespace EveryPrinter.Services;

/// <summary>Keeps one Every Printer window per user: a later launch (a right-click Print, a print job) hands its files to the running one.</summary>
public static class SingleInstance
{
    private const string Name = "EveryPrinter.SingleInstance.B4E7C2D1";
    private static Mutex? mutex;

    /// <summary>True when this process is the first instance and now owns the name.</summary>
    public static bool TryBecomePrimary()
    {
        mutex = new Mutex(true, Name, out var created);
        return created;
    }

    /// <summary>Sends file paths to the primary instance. An empty list just brings its window forward.</summary>
    public static bool Forward(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            foreach (var path in paths) writer.WriteLine(path);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Listens for files forwarded by later launches; <paramref name="received"/> runs on a thread-pool thread.</summary>
    public static void Listen(Action<List<string>> received)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var paths = new List<string>();
                    while (await reader.ReadLineAsync() is { Length: > 0 } line) paths.Add(line);
                    received(paths);
                }
                catch (IOException) { await Task.Delay(500); }
            }
        });
    }
}
