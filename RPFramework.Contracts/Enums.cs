namespace RPFramework.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Shared enums. Single source of truth for both client and server — there is no
// "mirror" to keep in sync anymore. Ordinals are wire-significant; only append.
// ─────────────────────────────────────────────────────────────────────────────

// ── Sheet / character ──────────────────────────────────────────────────────────

public enum FieldType { Number, Checkbox, Bar, Dot, Text }

public enum SkillType   { Active, Passive }
public enum ConditionOp { Less, LessEqual, Equal, GreaterEqual, Greater }
public enum EffectOp    { Add, Subtract, Set, Multiply, Divide }

// ── Dice ────────────────────────────────────────────────────────────────────────

/// <summary>How a dice roll handles its two-roll modes.</summary>
public enum RollMode { Normal, Advantage, Disadvantage }

// ── Parties ──────────────────────────────────────────────────────────────────────

public enum PartyRole { Member, CoDm, Owner }

// ── Inventory ─────────────────────────────────────────────────────────────────────

// Normal = generic/stackable (shown as "Misc" in UI; currency lives here). Bag = nested container.
// MainHand..SoulCrystal = equip-slot categories (equippable into the matching slot; Effects are
// always-on). Consumable = stackable, carries Effects applied once on "Use" (like an active skill)
// then decrements. APPEND-ONLY (ordinals are wire-significant).
public enum RpItemType
{
    Normal, Bag,
    MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ring, Necklace, Bracelet, SoulCrystal,
    Consumable,
}

/// <summary>Helpers for the item-category / equip-slot taxonomy.</summary>
public static class ItemSlots
{
    /// <summary>The equippable slot categories, in display order (paper-doll layout order).</summary>
    public static readonly RpItemType[] EquipOrder =
    {
        RpItemType.MainHand, RpItemType.OffHand, RpItemType.Head, RpItemType.Body,
        RpItemType.Hands, RpItemType.Legs, RpItemType.Feet, RpItemType.Ring,
        RpItemType.Necklace, RpItemType.Bracelet, RpItemType.SoulCrystal,
    };

    public static bool IsEquippable(this RpItemType t) => t is >= RpItemType.MainHand and <= RpItemType.SoulCrystal;
    public static bool IsStackable(this RpItemType t)  => t is RpItemType.Normal or RpItemType.Consumable;

    public static string Label(this RpItemType t) => t switch
    {
        RpItemType.Normal      => "Misc",
        RpItemType.Bag         => "Bag",
        RpItemType.MainHand    => "Main Hand",
        RpItemType.OffHand     => "Off Hand",
        RpItemType.SoulCrystal => "Soul Crystal",
        _                      => t.ToString(),
    };
}

// ── BGM ────────────────────────────────────────────────────────────────────────────

public enum RoomRole            { Member, Admin, Owner }
public enum LoopMode            { None, Single, All }
public enum PlaybackCommandType { Play, Pause, Resume, Seek, Stop, LoopChanged }
