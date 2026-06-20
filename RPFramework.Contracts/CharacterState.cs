namespace RPFramework.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// The authoritative per-party character state. Owned and persisted by the server;
// clients receive it via CharacterDto and may apply optimistic edits that the
// server's broadcast then reconciles.
//
// StatValues key conventions:
//   • Number field:  "{fieldId}"
//   • Bar / Dot:     "{fieldId}:cur" and "{fieldId}:max"
// CheckValues keyed by "{fieldId}" for Checkbox fields.
// TextValues keyed by "{fieldId}" for Text (notes) fields.
// Equipment keyed by the equip-slot RpItemType (one item per slot); the item's Type == the slot.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CharacterState
{
    public Dictionary<string, int>           StatValues  { get; set; } = new();
    public Dictionary<string, bool>          CheckValues { get; set; } = new();
    public Dictionary<string, string>        TextValues  { get; set; } = new();
    public List<RpSkill>                     Skills      { get; set; } = new();
    public Dictionary<RpItemType, RpItemDto> Equipment   { get; set; } = new();
}
