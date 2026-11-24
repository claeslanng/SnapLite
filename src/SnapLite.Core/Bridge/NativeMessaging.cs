using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SnapLite.Core.Bridge;

/// <summary>
/// Chrome native messaging framing: each message is a 32-bit little-endian length followed by that
/// many bytes of UTF-8 JSON, on the host's stdin (from the browser) and stdout (to the browser).
/// </summary>
public static class NativeMessaging
{
    /// <summary>Messages from the browser can be up to 64 MiB; refuse anything bigger (a corrupt stream).</summary>
    public const int MaxIncoming = 64 * 1024 * 1024;

    /// <summary>The browser drops messages from the host larger than 1 MB.</summary>
    public const int MaxOutgoing = 1024 * 1024;

    /// <summary>Reads one message. Returns null at end of stream (the browser closed the port).</summary>
    public static byte[]? Read(Stream input)
    {
        Span<byte> header = stackalloc byte[4];
        if (!ReadExactly(input, header)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxIncoming) throw new InvalidDataException($"Bad native message length {length}.");
        var body = new byte[length];
        if (!ReadExactly(input, body)) throw new EndOfStreamException("The native message was cut off.");
        return body;
    }

    public static void Write(Stream output, ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxOutgoing) throw new ArgumentException("Native messages to the browser must be under 1 MB.", nameof(json));
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        output.Write(header);
        output.Write(json);
        output.Flush();
    }

    private static bool ReadExactly(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("The native message was cut off.");
            }
            read += n;
        }
        return true;
    }
}

/// <summary>Chrome extension IDs: derived from the extension's public key (the manifest "key").</summary>
public static class ExtensionId
{
    /// <summary>First 128 bits of SHA-256(public key, DER SubjectPublicKeyInfo), each nibble written as a letter a–p.</summary>
    public static string FromPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(subjectPublicKeyInfo, hash);
        var chars = new char[32];
        for (int i = 0; i < 16; i++)
        {
            chars[2 * i] = (char)('a' + (hash[i] >> 4));
            chars[2 * i + 1] = (char)('a' + (hash[i] & 15));
        }
        return new string(chars);
    }

    public static string FromManifestKey(string base64Key) => FromPublicKey(Convert.FromBase64String(base64Key));
}
