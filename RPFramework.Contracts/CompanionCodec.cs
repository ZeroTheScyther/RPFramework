using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace RPFramework.Contracts;

/// <summary>A portable companion/NPC export: a format-versioned name + full character state.</summary>
public sealed record CompanionExport(int FormatVersion, string DisplayName, CharacterState State);

/// <summary>
/// Encodes a companion (name + <see cref="CharacterState"/>) to a shareable text code and back, so a
/// built-out companion/NPC can be lifted out of one campaign's vault and imported into another's. The
/// payload is GZip-compressed JSON, Base64-wrapped, with a short magic prefix. Lives in Contracts so the
/// client (encode for export, decode for preview) and the server (decode for import) share one codec.
/// </summary>
public static class CompanionCodec
{
    private const int    Version = 1;
    private const string Prefix  = "RPNPC1:";

    /// <summary>Hard cap on decompressed payload size. A sanitized <see cref="CharacterState"/> is far
    /// smaller than this; the cap exists purely to defuse a GZip decompression bomb (DEFLATE reaches
    /// ~1000:1, so a 512 KB code could otherwise inflate to hundreds of MB).</summary>
    private const int MaxDecompressedBytes = 512 * 1024;

    private static readonly JsonSerializerOptions Json = new();

    public static string Encode(string displayName, CharacterState state)
    {
        var dto  = new CompanionExport(Version, displayName, state);
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(json, 0, json.Length);
        return Prefix + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Decodes a code produced by <see cref="Encode"/>. Tolerant of surrounding whitespace and a
    /// missing prefix; returns false (never throws) on any malformed or wrong-version input.</summary>
    public static bool TryDecode(string? code, out CompanionExport export)
    {
        export = null!;
        if (string.IsNullOrWhiteSpace(code)) return false;
        var trimmed = code.Trim();
        if (trimmed.StartsWith(Prefix, StringComparison.Ordinal)) trimmed = trimmed[Prefix.Length..];
        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            using var ms    = new MemoryStream(bytes);
            using var gz    = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();

            // Length-limited copy: read one byte past the cap so we can distinguish "exactly at cap"
            // from "over cap", and bail the moment the bomb exceeds it instead of inflating it all.
            var buffer = new byte[8192];
            int read;
            while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (outMs.Length + read > MaxDecompressedBytes) return false;
                outMs.Write(buffer, 0, read);
            }

            var dto = JsonSerializer.Deserialize<CompanionExport>(outMs.ToArray(), Json);
            if (dto is null || dto.FormatVersion != Version || dto.State is null) return false;
            export = dto;
            return true;
        }
        catch { return false; }
    }
}
