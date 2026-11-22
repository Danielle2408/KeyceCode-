using System.Text;
using System.Text.Json;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var sequence = 0;
while (await ReadMessageAsync(input) is { } payload)
{
    using var message = JsonDocument.Parse(payload);
    var root = message.RootElement;
    var command = root.GetProperty("command").GetString()!;
    var requestSequence = root.GetProperty("seq").GetInt32();
    object body = command switch
    {
        "initialize" => new { supportsConfigurationDoneRequest = true, supportsTerminateRequest = true },
        "launch" => new { processId = 4242 },
        _ => new { }
    };
    await WriteMessageAsync(output, new { seq = ++sequence, type = "response", request_seq = requestSequence, success = true, command, body });
    if (command == "launch") await WriteMessageAsync(output, new { seq = ++sequence, type = "event", @event = "initialized", body = new { } });
    if (command == "disconnect") return;
}

static async Task<byte[]?> ReadMessageAsync(Stream input)
{
    using var header = new MemoryStream();
    var one = new byte[1];
    var matched = 0;
    byte[] separator = [13, 10, 13, 10];
    while (matched < separator.Length)
    {
        if (await input.ReadAsync(one) == 0) return null;
        header.WriteByte(one[0]);
        matched = one[0] == separator[matched] ? matched + 1 : one[0] == separator[0] ? 1 : 0;
    }
    var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
    var length = int.Parse(line[(line.IndexOf(':') + 1)..].Trim());
    var body = new byte[length];
    var offset = 0;
    while (offset < body.Length)
    {
        var read = await input.ReadAsync(body.AsMemory(offset));
        if (read == 0) throw new EndOfStreamException();
        offset += read;
    }
    return body;
}

static async Task WriteMessageAsync(Stream output, object message)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(message);
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await output.WriteAsync(header);
    await output.WriteAsync(body);
    await output.FlushAsync();
}
