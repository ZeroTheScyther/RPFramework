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
            gz.CopyTo(outMs);
            var dto = JsonSerializer.Deserialize<CompanionExport>(outMs.ToArray(), Json);
            if (dto is null || dto.FormatVersion != Version || dto.State is null) return false;
            export = dto;
            return true;
        }
        catch { return false; }
    }
}
