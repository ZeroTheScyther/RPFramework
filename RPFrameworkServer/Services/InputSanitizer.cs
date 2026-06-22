using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RPFramework.Contracts;

namespace RPFrameworkServer.Services;

/// <summary>
/// Central resource caps. Every client-controlled collection or string that the
/// server stores or relays must be bounded by one of these.
/// </summary>
public static class Limits
{
    public const int NameMax          = 64;    // display names, party names, bag names, item names
    public const int CodeMax          = 16;    // party / room codes
    public const int PlayerIdMax      = 96;    // "Firstname Lastname@World"
    public const int DescriptionMax   = 2048;  // item descriptions, skill descriptions
    public const int DiceMessageMax   = 256;
    public const int PasswordMin      = 4;
    public const int PasswordMax      = 128;
    public const int UrlMax           = 512;

    public const int PartiesPerPlayer = 16;
    public const int MembersPerParty  = 32;
    public const int TotalParties     = 10_000;

    public const int TotalRooms       = 2_000;
    public const int MembersPerRoom   = 64;
    public const int PlaylistMax      = 200;

    public const int BagsPerOwner     = 32;
    public const int BagItemsMax      = 500;
    public const int BagParticipants  = 16;

    public const int ItemTreeDepthMax = 4;     // nested bag-in-bag depth
    public const int ItemTreeNodesMax = 600;   // total items in one payload
    public const int ItemAmountMax    = 9_999_999;
    public const int ItemCapacityMax  = 1_000;
    public const int ItemEffectsMax   = 16;    // stat effects per item (equip / consumable)
    public const int ItemConditionsMax = 8;    // gate conditions per equippable item
    public const int ItemGrantedPassivesMax = 4; // passive definitions an item may grant its wearer
    public const int EffectBlocksMax  = 8;     // independent conditional effect-blocks per skill / item

    public const int ProfileTextKeysMax = 64;   // Text/notes fields per character
    public const int TextValueMax       = 4096; // a single Text/notes field's content
    public const int EquipSlotsMax       = 16;  // equipped items per character (≥ slot count)

    public const int PendingTradesPerPlayer = 8;

    public const int InitiativeEntriesMax = 64;
    public const int EncountersPerCampaign = 16;  // simultaneous live encounters per campaign

    public const int CompanionsPerPlayer = 20;   // companion entities one player may own in a campaign
    public const int NpcsPerCampaign     = 200;  // DM-owned NPC entities per campaign

    public const int ProfileStatKeysMax  = 500;
    public const int ProfileCheckKeysMax = 500;
    public const int ProfileSkillsMax    = 100;
    public const int SkillPartsMax       = 32;  // conditions / effects per skill
    public const int FieldIdMax          = 64;  // GUID strings / "builtin:*" ids
}

/// <summary>
/// Consistent sanitization and validation rules for all client-supplied input.
/// Sanitizers return a cleaned value; validators return false when the payload
/// must be rejected outright.
/// </summary>
public static partial class InputSanitizer
{
    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,16}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{6,16}$")]
    private static partial Regex YoutubeIdRegex();

    /// <summary>Strips control characters, trims, and caps length. Returns "" for null.</summary>
    public static string SanitizeName(string? value, int maxLength = Limits.NameMax)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Like <see cref="SanitizeName"/> but PRESERVES line breaks for multiline notes/descriptions
    /// (newlines normalized to \n; all other control characters stripped). Without this, multiline
    /// text fields collapse to a single run because \n is itself a control character.
    /// </summary>
    public static string SanitizeMultiline(string? value, int maxLength = Limits.TextValueMax)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (c == '\r') continue;                       // normalize CRLF -> LF
            if (c != '\n' && char.IsControl(c)) continue;  // keep newlines, drop other control chars
            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Party / room codes: alphanumeric + dash, max 16 chars.</summary>
    public static bool IsValidCode(string? code)
        => code != null && CodeRegex().IsMatch(code);

    /// <summary>
    /// Player IDs are "Name@World" strings chosen by the client. They are an identity
    /// claim, not an authenticated identity — but they must at least be printable,
    /// bounded, and non-empty so they can't poison logs, dictionaries, or group names.
    /// </summary>
    public static bool IsValidPlayerId(string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || playerId.Length > Limits.PlayerIdMax)
            return false;
        foreach (char c in playerId)
            if (char.IsControl(c))
                return false;
        return true;
    }

    /// <summary>Accepts only youtube.com / youtu.be URLs (or bare 11-char video IDs).</summary>
    public static bool IsAllowedYoutubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > Limits.UrlMax) return false;
        if (YoutubeIdRegex().IsMatch(url)) return true; // bare video id

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        string host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
                    or "music.youtube.com" or "youtu.be" or "www.youtu.be";
    }

    /// <summary>
    /// Validates an item tree (names/description lengths, amount/capacity ranges,
    /// nesting depth, total node count). Returns a sanitized copy, or null if the
    /// payload is structurally unacceptable.
    /// </summary>
    public static RpItemDto? SanitizeItem(RpItemDto? item)
    {
        int nodes = 0;
        return SanitizeItemInner(item, depth: 0, ref nodes);
    }

    private static RpItemDto? SanitizeItemInner(RpItemDto? item, int depth, ref int nodes)
    {
        if (item == null) return null;
        if (depth >= Limits.ItemTreeDepthMax) return null;
        if (++nodes > Limits.ItemTreeNodesMax) return null;

        List<RpItemDto>? contents = null;
        if (item.Contents is { Count: > 0 })
        {
            contents = new List<RpItemDto>(Math.Min(item.Contents.Count, Limits.BagItemsMax));
            foreach (var child in item.Contents)
            {
                if (contents.Count >= Limits.BagItemsMax) break;
                var clean = SanitizeItemInner(child, depth + 1, ref nodes);
                if (clean == null) return null; // structurally bad subtree → reject whole item
                contents.Add(clean);
            }
        }

        List<SkillEffect>? effects = null;
        if (item.Effects is { Count: > 0 })
        {
            if (item.Effects.Count > Limits.ItemEffectsMax) return null;
            effects = new List<SkillEffect>(item.Effects.Count);
            foreach (var fx in item.Effects)
            {
                if (fx == null) continue;
                fx.FieldId = string.IsNullOrEmpty(fx.FieldId) ? "" : fx.FieldId[..Math.Min(fx.FieldId.Length, Limits.FieldIdMax)];
                effects.Add(fx);
            }
        }

        List<SkillCondition>? conditions = null;
        if (item.Conditions is { Count: > 0 })
        {
            if (item.Conditions.Count > Limits.ItemConditionsMax) return null;
            conditions = new List<SkillCondition>(item.Conditions.Count);
            foreach (var c in item.Conditions)
            {
                if (c == null) continue;
                c.FieldId = string.IsNullOrEmpty(c.FieldId) ? "" : c.FieldId[..Math.Min(c.FieldId.Length, Limits.FieldIdMax)];
                conditions.Add(c);
            }
        }

        if (!TrySanitizeBlocks(item.Blocks, out var blocks)) return null;

        // Embedded passives an equipped item grants its wearer. Forced to Passive with reset runtime —
        // they are definitions, driven live by the equip while the item is active.
        List<RpSkill>? granted = null;
        if (item.GrantedPassives is { Count: > 0 })
        {
            if (item.GrantedPassives.Count > Limits.ItemGrantedPassivesMax) return null;
            granted = new List<RpSkill>(item.GrantedPassives.Count);
            foreach (var gp in item.GrantedPassives)
            {
                var cs = SanitizeSkill(gp);
                if (cs == null) return null;
                cs.Type = SkillType.Passive;
                cs.IsDmSkill = false;   // "vault" flag only applies to a character's authoring list, not embedded copies
                cs.CooldownRemaining = 0; cs.DurationRemaining = 0; cs.Active = false;
                granted.Add(cs);
            }
        }

        return item with
        {
            Name        = SanitizeName(item.Name),
            Description = SanitizeMultiline(item.Description, Limits.DescriptionMax),
            Amount      = Math.Clamp(item.Amount, 0, Limits.ItemAmountMax),
            Capacity    = Math.Clamp(item.Capacity, 0, Limits.ItemCapacityMax),
            Contents    = contents,
            Effects     = effects,
            Conditions  = conditions,
            Blocks      = blocks,
            GrantedPassives = granted,
        };
    }

    /// <summary>Bounds + sanitizes a single skill definition (parts caps, blocks, text). Returns null to
    /// signal the payload should be rejected (over a hard cap). Shared by state and item-granted passives.</summary>
    private static RpSkill? SanitizeSkill(RpSkill? s)
    {
        if (s == null) return null;
        if (s.Conditions == null || s.Conditions.Count > Limits.SkillPartsMax) return null;
        if (s.Effects    == null || s.Effects.Count    > Limits.SkillPartsMax) return null;
        if (!TrySanitizeBlocks(s.ConditionalBlocks, out var sblocks)) return null;
        s.ConditionalBlocks = sblocks ?? new();
        s.Name        = SanitizeName(s.Name);
        s.Description = SanitizeMultiline(s.Description, Limits.DescriptionMax);
        return s;
    }

    /// <summary>
    /// Bounds and truncates a list of conditional <see cref="EffectBlock"/>s (per-block condition/effect
    /// counts + field-id lengths). Returns false to signal the whole payload should be rejected (over a
    /// hard cap); on success <paramref name="clean"/> is the sanitized list, or null when there are none.
    /// </summary>
    private static bool TrySanitizeBlocks(List<EffectBlock>? blocks, out List<EffectBlock>? clean)
    {
        clean = null;
        if (blocks is not { Count: > 0 }) return true;
        if (blocks.Count > Limits.EffectBlocksMax) return false;

        var result = new List<EffectBlock>(blocks.Count);
        foreach (var b in blocks)
        {
            if (b == null) continue;
            if (b.Conditions  == null || b.Conditions.Count  > Limits.ItemConditionsMax) return false;
            if (b.Effects     == null || b.Effects.Count     > Limits.ItemEffectsMax)    return false;
            if (b.ElseEffects == null || b.ElseEffects.Count > Limits.ItemEffectsMax)    return false;
            foreach (var c in b.Conditions)
                c.FieldId = string.IsNullOrEmpty(c.FieldId) ? "" : c.FieldId[..Math.Min(c.FieldId.Length, Limits.FieldIdMax)];
            foreach (var fx in b.Effects)
                fx.FieldId = string.IsNullOrEmpty(fx.FieldId) ? "" : fx.FieldId[..Math.Min(fx.FieldId.Length, Limits.FieldIdMax)];
            foreach (var fx in b.ElseEffects)
                fx.FieldId = string.IsNullOrEmpty(fx.FieldId) ? "" : fx.FieldId[..Math.Min(fx.FieldId.Length, Limits.FieldIdMax)];
            result.Add(b);
        }
        clean = result.Count > 0 ? result : null;
        return true;
    }

    /// <summary>
    /// Validates and sanitizes an authoritative character state (StatValues / CheckValues /
    /// Skills). Returns a cleaned copy, or null when the payload exceeds hard caps. Identity
    /// is no longer carried inside the state — the server binds it from the session — so this
    /// only bounds collection sizes, key lengths, and skill text.
    /// </summary>
    public static CharacterState? SanitizeState(CharacterState? state)
    {
        if (state == null) return null;
        if (state.StatValues  == null || state.StatValues.Count  > Limits.ProfileStatKeysMax)  return null;
        if (state.CheckValues == null || state.CheckValues.Count > Limits.ProfileCheckKeysMax) return null;
        if (state.Skills      == null || state.Skills.Count      > Limits.ProfileSkillsMax)    return null;
        if (state.TextValues  == null || state.TextValues.Count  > Limits.ProfileTextKeysMax)  return null;
        if (state.Equipment   == null || state.Equipment.Count   > Limits.EquipSlotsMax)       return null;

        var stats = new Dictionary<string, int>(state.StatValues.Count);
        foreach (var (k, v) in state.StatValues)
        {
            if (string.IsNullOrEmpty(k) || k.Length > Limits.FieldIdMax + 8) continue; // +8 for ":cur"/":max"
            stats[k] = v;
        }

        var checks = new Dictionary<string, bool>(state.CheckValues.Count);
        foreach (var (k, v) in state.CheckValues)
        {
            if (string.IsNullOrEmpty(k) || k.Length > Limits.FieldIdMax * 2) continue;
            checks[k] = v;
        }

        var skills = new List<RpSkill>(state.Skills.Count);
        foreach (var s in state.Skills)
        {
            if (s == null) continue;
            var cs = SanitizeSkill(s);
            if (cs == null) return null;
            skills.Add(cs);
        }

        var texts = new Dictionary<string, string>(state.TextValues.Count);
        foreach (var (k, v) in state.TextValues)
        {
            if (string.IsNullOrEmpty(k) || k.Length > Limits.FieldIdMax) continue;
            texts[k] = SanitizeMultiline(v, Limits.TextValueMax);
        }

        // Equipped items: each must be an equippable type matching its slot key; sanitize the item tree.
        var equip = new Dictionary<RpItemType, RpItemDto>(state.Equipment.Count);
        foreach (var (slot, it) in state.Equipment)
        {
            if (!slot.IsEquippable()) continue;
            var clean = SanitizeItem(it);
            if (clean == null || clean.Type != slot) continue;
            equip[slot] = clean;
        }

        return new CharacterState { StatValues = stats, CheckValues = checks, TextValues = texts, Skills = skills, Equipment = equip };
    }

    /// <summary>Validates and sanitizes a SheetField. Caps name/tooltip and clamps numeric ranges.</summary>
    private static void SanitizeField(SheetField f)
    {
        f.Id      = string.IsNullOrEmpty(f.Id) ? Guid.NewGuid().ToString() : f.Id[..Math.Min(f.Id.Length, Limits.FieldIdMax)];
        f.Name    = SanitizeName(f.Name);
        f.Tooltip = SanitizeName(f.Tooltip, Limits.DescriptionMax);
    }

    /// <summary>Validates and sanitizes a full SheetTemplate. Returns null if structurally over-cap.</summary>
    public static SheetTemplate? SanitizeTemplate(SheetTemplate? template)
    {
        if (template?.Groups == null || template.Groups.Count > 64) return null;
        int fieldCount = 0;
        foreach (var g in template.Groups)
        {
            g.Name = SanitizeName(g.Name);
            if (g.Fields == null || g.Fields.Count > 256) return null;
            fieldCount += g.Fields.Count;
            if (fieldCount > Limits.ProfileStatKeysMax) return null;
            foreach (var f in g.Fields) SanitizeField(f);
        }
        return template;
    }

    // ── Password hashing ──────────────────────────────────────────────────────
    // Legacy format: unsalted lowercase-hex SHA-256 (kept verifiable for parties
    // persisted before the upgrade). New format: "v2:<salt-b64>:<pbkdf2-b64>".

    private const int Pbkdf2Iterations = 100_000;

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"v2:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        if (stored.StartsWith("v2:", StringComparison.Ordinal))
        {
            var parts = stored.Split(':', 3);
            if (parts.Length != 3) return false;
            try
            {
                byte[] salt     = Convert.FromBase64String(parts[1]);
                byte[] expected = Convert.FromBase64String(parts[2]);
                byte[] actual   = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException) { return false; }
        }

        // Legacy unsalted SHA-256 hex
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(legacy), Encoding.UTF8.GetBytes(stored));
    }
}
