using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The server as an editor really starts it: the shipped executable, over its own standard
/// input and output. The frames are written and read by hand rather than through a JSON-RPC
/// library, because what is under test is what the server does with what it is sent, including
/// what no library would send.
/// </summary>
internal sealed class StdioServer : IDisposable
{
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;

    private StdioServer(Process process)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput.BaseStream;
    }

    /// <summary>The server's process id, for a test about who is watching whom.</summary>
    public int Id => process.Id;

    /// <summary>What the process left with, once it has.</summary>
    public int ExitCode => process.ExitCode;

    /// <summary>Starts the shipped executable over a pipe, as an editor does.</summary>
    public static StdioServer Start()
    {
        var exe = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "Norristown.LanguageServer.exe" : "Norristown.LanguageServer");
        Assert.True(File.Exists(exe), $"the server executable is not beside the tests: {exe}");
        var started = Process.Start(new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(started);
        return new StdioServer(started);
    }

    /// <summary>Sends <paramref name="json"/> as one frame.</summary>
    public async Task SendAsync(string json, CancellationToken cancellation)
    {
        var body = Encoding.UTF8.GetBytes(json);
        await input.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), cancellation);
        await input.WriteAsync(body, cancellation);
        await input.FlushAsync(cancellation);
    }

    /// <summary>Sends a frame whose body is not JSON, which no library would.</summary>
    public Task SendBrokenAsync(CancellationToken cancellation) =>
        SendAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":", cancellation);

    /// <summary>The next frame, read as JSON.</summary>
    public async Task<JsonDocument> ReceiveAsync(CancellationToken cancellation)
    {
        var length = -1;
        var header = new List<byte>();
        while (true)
        {
            var next = await NextByteAsync(cancellation);
            Assert.True(next >= 0, "the server closed its output before answering");
            if (next != '\n')
            {
                if (next != '\r')
                    header.Add((byte)next);
                continue;
            }
            if (header.Count == 0)
                break;
            var line = Encoding.ASCII.GetString([.. header]);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim(), null);
            header.Clear();
        }

        var body = new byte[length];
        for (var at = 0; at < length;)
        {
            var read = await output.ReadAsync(body.AsMemory(at), cancellation);
            Assert.True(read > 0, "the server closed its output part way through a frame");
            at += read;
        }
        return JsonDocument.Parse(body);
    }

    /// <summary>The next frame that answers <paramref name="id"/>, passing over notifications.</summary>
    public async Task<JsonElement> AnswerToAsync(int id, CancellationToken cancellation)
    {
        while (true)
        {
            var frame = await ReceiveAsync(cancellation);
            if (frame.RootElement.TryGetProperty("id", out var given)
                && given.ValueKind == JsonValueKind.Number && given.GetInt32() == id)
            {
                return frame.RootElement.Clone();
            }
        }
    }

    /// <summary>Waits for the process to leave; false when it is still running.</summary>
    public bool Left(TimeSpan within) => process.WaitForExit((int)within.TotalMilliseconds);

    /// <summary>Stops the process the way an editor that crashed does: without a word.</summary>
    public void Kill()
    {
        process.Kill();
        process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException)
        {
            // The process was never started, or has already been reaped.
        }
        process.Dispose();
    }

    private async ValueTask<int> NextByteAsync(CancellationToken cancellation)
    {
        var one = new byte[1];
        return await output.ReadAsync(one, cancellation) == 0 ? -1 : one[0];
    }
}
