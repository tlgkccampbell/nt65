using System.Buffers;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Norristown.LanguageServer;

/// <summary>
/// Implements the transport, which carries LSP's <c>Content-Length</c> frames over a pair of
/// streams and is the single place that decides which messages get through. StreamJsonRpc's own
/// handler reads a frame and hands it straight to the formatter. Anything the formatter rejects
/// comes out of the read loop as an exception and ends the connection. Examples are a body that
/// is not JSON, and a <c>"params": null</c> whose arguments the request type cannot count.
/// Nothing a client sends should end a server, so the frames are read here instead, as follows.
/// <list type="bullet">
/// <item>A body that is not JSON is answered with <c>-32700</c>, and the next frame is read.</item>
/// <item><c>"params": null</c> is treated the same as no parameters at all, and is removed.</item>
/// <item>A request before <c>initialize</c> is answered with <c>-32002</c>, and one after
/// <c>shutdown</c> with <c>-32600</c>. A notification at either point is dropped, as the
/// protocol requires.</item>
/// </list>
/// A frame states its own length, so the frame after a bad one starts where the headers said it
/// would. The only unrecoverable case is headers that give no length, and there the connection
/// ends.
/// </summary>
internal sealed class Framing : MessageHandlerBase
{
    /// <summary>LSP's own code for a request that arrives before <c>initialize</c>.</summary>
    private const JsonRpcErrorCode ServerNotInitialized = (JsonRpcErrorCode)(-32002);

    private const string ContentLength = "Content-Length:";

    /// <summary>The value <see cref="NextLengthAsync"/> returns for a stream that has ended.</summary>
    private const int StreamOver = -1;

    /// <summary>
    /// The value <see cref="NextLengthAsync"/> returns for headers that give no length, after
    /// which the stream cannot be resynchronized.
    /// </summary>
    private const int NoLength = -2;

    private readonly Stream input;
    private readonly Stream output;
    private readonly byte[] buffer = new byte[8192];
    private readonly List<byte> header = new(64);

    // The part of `buffer`, from `at` up to `have`, read from the stream but not yet consumed.
    private int at;
    private int have;

    /// <param name="input">Where the client's frames arrive.</param>
    /// <param name="output">Where the server's frames go.</param>
    /// <param name="formatter">The formatter that converts a frame's bytes to a message and back.</param>
    public Framing(Stream input, Stream output, IJsonRpcMessageFormatter formatter)
        : base(formatter)
    {
        this.input = input;
        this.output = output;
    }

    /// <summary>
    /// Gets the server's lifecycle phase, which the <c>initialize</c> and <c>shutdown</c> messages
    /// that pass through advance.
    /// </summary>
    public ServerPhase Phase { get; private set; }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var length = await NextLengthAsync(cancellationToken).ConfigureAwait(false);
            if (length == StreamOver)
                return null;
            if (length == NoLength)
            {
                await RefuseAsync(
                    RequestId.Null, JsonRpcErrorCode.ParseError,
                    "a frame must give its Content-Length", cancellationToken).ConfigureAwait(false);
                return null;
            }

            var content = new byte[length];
            if (!await FillAsync(content, cancellationToken).ConfigureAwait(false))
                return null;
            if (await PassedAsync(content, cancellationToken).ConfigureAwait(false) is { } passed)
                return Formatter.Deserialize(new ReadOnlySequence<byte>(passed));
        }
    }

    protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        var written = new ArrayBufferWriter<byte>();
        Formatter.Serialize(written, content);
        var head = Encoding.ASCII.GetBytes($"{ContentLength} {written.WrittenCount}\r\n\r\n");
        await output.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(written.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    protected override ValueTask FlushAsync(CancellationToken cancellationToken) =>
        new(output.FlushAsync(cancellationToken));

    protected override void DisposeReader() => input.Dispose();

    protected override void DisposeWriter() => output.Dispose();

    /// <summary>
    /// Returns the id a message carries, or null for a notification and for a message whose id is
    /// not a number, a string or null.
    /// </summary>
    private static RequestId? IdOf(JsonElement message) =>
        !message.TryGetProperty("id", out var id) ? null : id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var number) => new RequestId(number),
            JsonValueKind.String => new RequestId(id.GetString()),
            JsonValueKind.Null => RequestId.Null,
            _ => null,
        };

    /// <summary>
    /// Returns <paramref name="content"/> with a null <c>params</c> removed. A client may send
    /// <c>"params": null</c> for a request that takes none, which the formatter's request type
    /// cannot read. The frame is returned unchanged when there is nothing to remove.
    /// </summary>
    private static byte[] WithoutNullParameters(JsonElement message, byte[] content)
    {
        if (!message.TryGetProperty("params", out var given) || given.ValueKind != JsonValueKind.Null)
            return content;
        var written = new ArrayBufferWriter<byte>(content.Length);
        using (var writer = new Utf8JsonWriter(written))
        {
            writer.WriteStartObject();
            foreach (var property in message.EnumerateObject())
            {
                if (property.NameEquals("params"))
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return written.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Returns the frame to pass on, or null for a frame this layer has answered itself. Only the
    /// messages passed on advance the server's lifecycle, so the phase is tracked here.
    /// </summary>
    private async ValueTask<byte[]?> PassedAsync(byte[] content, CancellationToken cancellationToken)
    {
        JsonDocument message;
        try
        {
            message = JsonDocument.Parse(content);
        }
        catch (JsonException e)
        {
            await RefuseAsync(RequestId.Null, JsonRpcErrorCode.ParseError, e.Message, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        using (message)
        {
            if (message.RootElement.ValueKind != JsonValueKind.Object)
            {
                await RefuseAsync(
                    RequestId.Null, JsonRpcErrorCode.ParseError, "a message is a JSON object", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            // A message with no method is a response to a request the server sent, and does not
            // affect the lifecycle.
            if (!message.RootElement.TryGetProperty("method", out var named)
                || named.ValueKind != JsonValueKind.String || named.GetString() is not { } method)
            {
                return content;
            }

            if (Refusal(method) is var (code, why))
            {
                // A refused notification cannot be answered, so it is dropped, as the protocol
                // requires for one that arrives before the server is initialized.
                if (IdOf(message.RootElement) is { } id)
                    await RefuseAsync(id, code, why, cancellationToken).ConfigureAwait(false);
                return null;
            }

            Phase = method switch
            {
                "initialize" => ServerPhase.Running,
                "shutdown" => ServerPhase.ShuttingDown,
                _ => Phase,
            };
            return WithoutNullParameters(message.RootElement, content);
        }
    }

    /// <summary>Returns why <paramref name="method"/> may not be handled now, or null when it may.</summary>
    private (JsonRpcErrorCode Code, string Why)? Refusal(string method) => (Phase, method) switch
    {
        // Cancellation is never refused, in any phase. A client that has stopped waiting for a
        // request has stopped waiting regardless of the server's state.
        (_, "$/cancelRequest") => null,
        (ServerPhase.Starting, not ("initialize" or "exit")) =>
            (ServerNotInitialized, "the server has not been initialized"),
        (ServerPhase.Running, "initialize") =>
            (JsonRpcErrorCode.InvalidRequest, "the server is already initialized"),
        (ServerPhase.ShuttingDown, not "exit") =>
            (JsonRpcErrorCode.InvalidRequest, "the server has been shut down"),
        _ => null,
    };

    /// <summary>
    /// Answers a frame this layer will not pass on. The answer goes out through the same lock as
    /// every reply.
    /// </summary>
    private ValueTask RefuseAsync(
        RequestId id, JsonRpcErrorCode code, string why, CancellationToken cancellationToken) =>
        WriteAsync(
            new JsonRpcError
            {
                RequestId = id,
                Error = new JsonRpcError.ErrorDetail { Code = code, Message = why },
            },
            cancellationToken);

    /// <summary>
    /// Returns the length of the next frame's body, read from its headers. Returns
    /// <see cref="StreamOver"/> at the end of the stream and <see cref="NoLength"/> for headers
    /// that give no length. Other headers are read and ignored.
    /// </summary>
    private async ValueTask<int> NextLengthAsync(CancellationToken cancellationToken)
    {
        var length = NoLength;
        header.Clear();
        while (true)
        {
            var next = await NextByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
                return StreamOver;
            if (next != '\n')
            {
                if (next != '\r')
                    header.Add((byte)next);
                continue;
            }
            if (header.Count == 0)
                return length;

            var line = Encoding.ASCII.GetString([.. header]);
            if (line.StartsWith(ContentLength, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[ContentLength.Length..].Trim(), out var given) && given >= 0)
            {
                length = given;
            }
            header.Clear();
        }
    }

    /// <summary>Returns the next byte of the stream, or -1 once it has ended.</summary>
    private async ValueTask<int> NextByteAsync(CancellationToken cancellationToken)
    {
        if (at == have && !await ReadMoreAsync(cancellationToken).ConfigureAwait(false))
            return -1;
        return buffer[at++];
    }

    /// <summary>
    /// Fills <paramref name="content"/> from the stream, and returns false if the stream ended
    /// first.
    /// </summary>
    private async ValueTask<bool> FillAsync(byte[] content, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < content.Length)
        {
            if (at == have && !await ReadMoreAsync(cancellationToken).ConfigureAwait(false))
                return false;
            var take = Math.Min(have - at, content.Length - written);
            buffer.AsSpan(at, take).CopyTo(content.AsSpan(written));
            at += take;
            written += take;
        }
        return true;
    }

    private async ValueTask<bool> ReadMoreAsync(CancellationToken cancellationToken)
    {
        at = 0;
        have = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return have > 0;
    }
}
