namespace RPFramework.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// The hub contract surface as string constants, shared by NetworkService (client)
// and RpHub (server). Intents = client → server method names. Events = server →
// client message names. Using constants removes the "magic string typo" class of
// SignalR binding failures.
// ─────────────────────────────────────────────────────────────────────────────

public static class HubRoutes
{
    public const string Path = "/rphub";

    /// <summary>Client → server intent method names (must match RpHub method names).</summary>
    public static class Intents
    {
        public const string Identify        = nameof(Identify);

        // Parties
        public const string PartyCreate      = nameof(PartyCreate);
        public const string PartyJoin        = nameof(PartyJoin);
        public const string PartyLeave       = nameof(PartyLeave);
        public const string PartyDisband     = nameof(PartyDisband);
        public const string PartyKick        = nameof(PartyKick);
        public const string PartySetRole     = nameof(PartySetRole);
        public const string PartySetShowHpAp = nameof(PartySetShowHpAp);

        // Character & template
        public const string CharacterEditStat  = nameof(CharacterEditStat);
        public const string CharacterEditCheck = nameof(CharacterEditCheck);
        public const string CharacterEditText  = nameof(CharacterEditText);
        public const string CharacterSetSkills = nameof(CharacterSetSkills);
        public const string UseSkill           = nameof(UseSkill);
        public const string TemplatePublish    = nameof(TemplatePublish);

        // Dice
        public const string RollDice = nameof(RollDice);

        // Initiative
        public const string InitiativeStart      = nameof(InitiativeStart);
        public const string InitiativeEndTurn    = nameof(InitiativeEndTurn);
        public const string InitiativeEndCombat  = nameof(InitiativeEndCombat);
        public const string InitiativeAddNpc     = nameof(InitiativeAddNpc);
        public const string InitiativeRemove     = nameof(InitiativeRemove);

        // Inventory & trading
        public const string BagCreate        = nameof(BagCreate);
        public const string BagRename        = nameof(BagRename);
        public const string BagDelete        = nameof(BagDelete);
        public const string ItemAdd          = nameof(ItemAdd);
        public const string ItemUpdate       = nameof(ItemUpdate);
        public const string ItemRemove       = nameof(ItemRemove);
        public const string ItemMove         = nameof(ItemMove);
        public const string ItemSplit        = nameof(ItemSplit);
        public const string UseItem          = nameof(UseItem);
        public const string EquipItem        = nameof(EquipItem);
        public const string UnequipItem      = nameof(UnequipItem);
        public const string BagShareInvite   = nameof(BagShareInvite);
        public const string BagShareAccept   = nameof(BagShareAccept);
        public const string BagShareDecline  = nameof(BagShareDecline);
        public const string BagLeave         = nameof(BagLeave);
        public const string TradeOffer       = nameof(TradeOffer);
        public const string TradeAccept      = nameof(TradeAccept);
        public const string TradeDecline     = nameof(TradeDecline);

        // BGM
        public const string RoomCreate      = nameof(RoomCreate);
        public const string RoomJoin        = nameof(RoomJoin);
        public const string RoomLeave       = nameof(RoomLeave);
        public const string RoomPromote     = nameof(RoomPromote);
        public const string PlaylistAdd     = nameof(PlaylistAdd);
        public const string PlaylistRemove  = nameof(PlaylistRemove);
        public const string PlaybackCommand = nameof(PlaybackCommand);
    }

    /// <summary>Server → client event names (used by HubConnection.On&lt;...&gt;).</summary>
    public static class Events
    {
        public const string Snapshot            = nameof(Snapshot);            // SnapshotDto
        public const string PartyUpdated        = nameof(PartyUpdated);        // PartyDto
        public const string PartyDisbanded      = nameof(PartyDisbanded);      // string code
        public const string CharacterUpdated    = nameof(CharacterUpdated);    // CharacterDto
        public const string TemplateUpdated     = nameof(TemplateUpdated);     // TemplateDto
        public const string DiceRoll            = nameof(DiceRoll);            // DiceRollResultDto
        public const string InitiativeUpdated   = nameof(InitiativeUpdated);   // InitiativeStateDto
        public const string InitiativeEnded     = nameof(InitiativeEnded);     // string code
        public const string BagUpdated          = nameof(BagUpdated);          // BagDto
        public const string BagRemoved          = nameof(BagRemoved);          // Guid bagId
        public const string BagShareInvited     = nameof(BagShareInvited);     // BagShareInviteDto
        public const string BagShareDeclined    = nameof(BagShareDeclined);    // BagShareDeclinedDto
        public const string TradeOffered        = nameof(TradeOffered);        // TradeOfferDto
        public const string RoomUpdated         = nameof(RoomUpdated);         // RoomStateDto
        public const string RoomRemoved         = nameof(RoomRemoved);         // string code
        public const string Error               = nameof(Error);              // string context, string message
    }
}
