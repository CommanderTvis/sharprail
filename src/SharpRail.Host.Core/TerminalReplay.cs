namespace SharpRail.Host.Core;

// Saved output paints history; queries in it must not send answers to a different foreground process.
internal static class TerminalReplay
{
    internal static byte[] WithoutQueries(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[bytes.Length];
        var written = 0;
        for (var index = 0; index < bytes.Length;)
        {
            var start = index++;
            var query = false;
            if (bytes[start] == 0x1b)
            {
                if (index == bytes.Length) break;
                var kind = bytes[index++];
                if (kind == '[')
                {
                    var parameters = index;
                    while (index < bytes.Length && bytes[index] is >= 0x20 and <= 0x3f) index++;
                    if (index == bytes.Length) break;
                    var body = bytes[parameters..index];
                    var final = bytes[index++];
                    query = final is (byte)'n' or (byte)'c' or (byte)'x' ||
                        final == 'u' && body.SequenceEqual("?"u8) ||
                        final == 'p' && body.EndsWith("$"u8) ||
                        final == 'q' && body.StartsWith(">"u8) ||
                        final == 't' && WindowQuery(body);
                }
                else if (kind is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
                {
                    var payload = index;
                    while (index < bytes.Length && !(kind == ']' && bytes[index] == 7) &&
                        !(bytes[index] == 0x1b && index + 1 < bytes.Length && bytes[index + 1] == '\\')) index++;
                    if (index == bytes.Length) break;
                    var body = bytes[payload..index];
                    query = kind == ']' ? OscQuery(body) : kind == 'P' &&
                        (body.StartsWith("$q"u8) || body.StartsWith("+q"u8));
                    index += bytes[index] == 7 ? 1 : 2;
                }
                else query = kind == 'Z';
            }
            if (query) continue;
            bytes[start..index].CopyTo(result.AsSpan(written));
            written += index - start;
        }
        return result[..written];
    }

    private static bool WindowQuery(ReadOnlySpan<byte> parameters)
    {
        var separator = parameters.IndexOf((byte)';');
        var code = separator < 0 ? parameters : parameters[..separator];
        return code.SequenceEqual("14"u8) || code.SequenceEqual("16"u8) || code.SequenceEqual("18"u8) ||
            code.SequenceEqual("19"u8) || code.SequenceEqual("20"u8) || code.SequenceEqual("21"u8);
    }

    private static bool OscQuery(ReadOnlySpan<byte> payload)
    {
        var separator = payload.IndexOf((byte)';');
        if (separator < 0) return false;
        var code = payload[..separator];
        if (!code.SequenceEqual("4"u8) && !code.SequenceEqual("52"u8) &&
            !(code.Length == 2 && code[0] == '1' && code[1] is >= (byte)'0' and <= (byte)'9')) return false;
        return payload[(separator + 1)..].IndexOf((byte)'?') >= 0;
    }
}