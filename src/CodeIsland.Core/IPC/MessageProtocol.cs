using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace CodeIsland.Core.IPC;

/// <summary>
/// Named Pipe 消息协议：长度前缀 + UTF-8 JSON
/// </summary>
public static class MessageProtocol
{
    public const int MaxPayloadSize = 1_048_576; // 1MB

    /// <summary>
    /// 编码消息：4 字节长度前缀 + UTF-8 payload
    /// </summary>
    public static byte[] Encode(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(buffer, 4);
        return buffer;
    }

    /// <summary>
    /// 从流中读取消息
    /// </summary>
    public static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        // 读取长度前缀
        var lengthBuffer = new byte[4];
        await stream.ReadExactlyAsync(lengthBuffer, ct);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
        if (length > MaxPayloadSize)
            throw new ProtocolViolationException($"Payload too large: {length}");

        // 读取 payload
        var payloadBuffer = new byte[length];
        await stream.ReadExactlyAsync(payloadBuffer, ct);

        return Encoding.UTF8.GetString(payloadBuffer);
    }

    /// <summary>
    /// 向流写入消息
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, string json, CancellationToken ct = default)
    {
        var encoded = Encode(json);
        await stream.WriteAsync(encoded, ct);
        await stream.FlushAsync(ct);
    }
}
