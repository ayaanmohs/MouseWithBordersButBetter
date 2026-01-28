using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputForwarder.Common;

public enum MessageType
{
    Hello,
    Key,
    Mouse,
    Mode,
    Ping,
    Pong,
    Goodbye,
    Status
}

public enum LockState
{
    Locked,
    Local
}

public sealed record Envelope(
    [property: JsonPropertyName("type")] MessageType Type,
    [property: JsonPropertyName("seq")] ulong Seq,
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("hmac")] string Hmac
);

public sealed record HelloPayload(
    [property: JsonPropertyName("client")] string Client,
    [property: JsonPropertyName("secret")] string Secret
);

public sealed record ModePayload(
    [property: JsonPropertyName("state")] LockState State
);

public sealed record ModifierState(bool Alt, bool Ctrl, bool Shift, bool Win);

public sealed record KeyPayload(
    [property: JsonPropertyName("vk")] int VirtualKey,
    [property: JsonPropertyName("scan")] int ScanCode,
    [property: JsonPropertyName("isDown")] bool IsDown,
    [property: JsonPropertyName("modifiers")] ModifierState Modifiers
);

public sealed record MouseButtons(string Left, string Right, string Middle, string X1, string X2);

public sealed record WheelPayload(int Vertical, int Horizontal);

public sealed record MousePayload(
    int Dx,
    int Dy,
    MouseButtons Buttons,
    WheelPayload Wheel
);

public sealed record StatusPayload(bool Connected, int? LatencyMs);

public static class ProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string ComputeHmac(string secret, string content)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Serialize<T>(MessageType type, ulong seq, T payload, string secret)
    {
        var body = new
        {
            type,
            seq,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload
        };

        var withoutHmac = JsonSerializer.Serialize(body, Options);
        var hmac = ComputeHmac(secret, withoutHmac);
        var final = new
        {
            type,
            seq,
            ts = body.ts,
            payload,
            hmac
        };
        return JsonSerializer.Serialize(final, Options);
    }

    public static bool TryDeserialize(string line, string secret, out Envelope? envelope)
    {
        envelope = null;
        try
        {
            var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("hmac", out var hmacProp) ||
                !doc.RootElement.TryGetProperty("type", out _) ||
                !doc.RootElement.TryGetProperty("seq", out _) ||
                !doc.RootElement.TryGetProperty("ts", out _) ||
                !doc.RootElement.TryGetProperty("payload", out _))
            {
                return false;
            }

            var hmac = hmacProp.GetString() ?? "";
            // compute HMAC over body without hmac
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                doc.RootElement.WriteTo(writer);
            }
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            var recomputed = ComputeHmac(secret, RemoveHmacField(text));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(hmac),
                    Encoding.UTF8.GetBytes(recomputed)))
            {
                return false;
            }

            envelope = JsonSerializer.Deserialize<Envelope>(line, Options);
            return envelope != null;
        }
        catch
        {
            return false;
        }
    }

    private static string RemoveHmacField(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("hmac")) continue;
            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public sealed class EndpointConfig
{
    public IPAddress Host { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 49152;
    public string Secret { get; init; } = "changeme";
}

