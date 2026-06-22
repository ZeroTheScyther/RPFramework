using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace RPFramework.Windows;

public class HelpWindow : Window
{
    private int _selected = 0;

    private static readonly string[] Labels =
    [
        "Overview",
        "RPCHARACTER",
        "RPSTATS",
        "RPDICE",
        "RPSKILLS",
        "RPNPC",
        "RPINITIATIVE",
        "RPBGM",
        "RPINVENTORY",
        "RPHUB",
    ];

    public HelpWindow()
        : base("RP Help##RPFramework.Help",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 400),
            MaximumSize = new Vector2(960, 860),
        };
        Size          = new Vector2(660, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        float scale = ImGuiHelpers.GlobalScale;
        float leftW = 130f * scale;

        // ── Left panel - topic list ───────────────────────────────────────────
        using (var left = ImRaii.Child("##helplist", new Vector2(leftW, -1), true))
        {
            if (left)
            {
                for (int i = 0; i < Labels.Length; i++)
                {
                    bool sel = _selected == i;
                    if (sel)
                        ImGui.PushStyleColor(ImGuiCol.Header,
                            new Vector4(0.26f, 0.59f, 0.98f, 0.35f));

                    if (ImGui.Selectable(Labels[i], sel,
                            ImGuiSelectableFlags.None,
                            new Vector2(0, 22f * scale)))
                        _selected = i;

                    if (sel) ImGui.PopStyleColor();
                }
            }
        }

        ImGui.SameLine();

        // ── Right panel - content ─────────────────────────────────────────────
        using var right = ImRaii.Child("##helpcontent", new Vector2(-1, -1), false);
        if (!right) return;

        DrawTopic(_selected, scale);
    }

    // ── Topic renderer ────────────────────────────────────────────────────────

    private static void DrawTopic(int index, float scale)
    {
        switch (index)
        {
            case 0: DrawOverview(scale);      break;
            case 1: DrawRpCharacter(scale);   break;
            case 2: DrawRpStats(scale);       break;
            case 3: DrawRpDice(scale);        break;
            case 4: DrawRpSkills(scale);      break;
            case 5: DrawRpNpc(scale);         break;
            case 6: DrawRpInitiative(scale);  break;
            case 7: DrawRpBgm(scale);         break;
            case 8: DrawRpInventory(scale);   break;
            case 9: DrawRpHub(scale);         break;
        }
    }

    // ── Rendering helpers ─────────────────────────────────────────────────────

    private static void Title(string text, string subtitle)
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.50f, 1f), text);
        if (subtitle.Length > 0)
            ImGui.TextDisabled(subtitle);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(6f);
    }

    private static void Section(string heading)
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextColored(new Vector4(0.60f, 0.85f, 1.00f, 1f), heading);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private static void Body(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void Bullet(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void Cmd(string cmd, string description)
    {
        ImGui.TextColored(new Vector4(0.55f, 0.90f, 0.55f, 1f), cmd);
        ImGui.SameLine();
        ImGui.TextDisabled(description);
    }

    // ── Topics ────────────────────────────────────────────────────────────────

    private static void DrawOverview(float scale)
    {
        Title("RPFramework", "Multiplayer roleplaying companion for FFXIV");

        Body("RPFramework lets you and your party track characters, roll dice, run combat initiative, share atmospheric music, and trade items - all synced in real time through a relay server.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Getting started");
        Bullet("Open /rpsettings and enter your relay server URL.");
        Bullet("Open /rphub, connect, then create or join a campaign.");
        Bullet("Right-click a campaign and choose Set as Active Campaign - the inventory, BGM and character windows all follow your active campaign.");
        Bullet("Your DM can build the campaign's character sheet in /rpstats and publish it to everyone.");
        Bullet("Open /rpini and Join an encounter when combat begins.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("All commands");
        Cmd("/rpcharacter",    "- your character window (all tabs)");
        Cmd("/rpstats  /rpsheet  /rpcs", "- character sheet (Stats tab)");
        Cmd("/rpskills  /rpsk","- skills and passives (Skills tab)");
        Cmd("/rpequipment",    "- equipment (Equipment tab)");
        Cmd("/rpnpc",          "- companion / NPC vault");
        Cmd("/rpdice",         "- dice roller");
        Cmd("/rpini",          "- initiative tracker");
        Cmd("/rpbgm",          "- background music player");
        Cmd("/rpinventory  /rpinv", "- inventory and trading");
        Cmd("/rphub",          "- connection and campaigns");
        Cmd("/rpsettings",     "- server URL and plugin options");
        Cmd("/rphelp",         "- this window");

        ImGuiHelpers.ScaledDummy(4f);
        Body("Tip: /rpsheet <name> and /rpskills <name> open another online party member's sheet or skills read-only.");
    }

    private static void DrawRpCharacter(float scale)
    {
        Title("RPCHARACTER - Your Character", "/rpcharacter  •  tabs: Profile / Stats / Skills / Equipment / Companion");

        Body("One window for everything about your character. The stat, skill, and equipment commands all open this window focused on the matching tab. You must be connected with an active campaign selected - the campaign picker sits at the top of the window.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Tabs");
        Bullet("Profile - your character's text fields (name, race, job, and free-form background notes), as laid out by the DM's template.");
        Bullet("Stats - the editable character sheet (see RPSTATS).");
        Bullet("Skills - your active abilities and passives (see RPSKILLS).");
        Bullet("Equipment - gear slots; equipping an item can grant the passives a DM embedded in it.");
        Bullet("Companion - your active companion's read-only sheet and skills, built in the RPNPC vault.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Shortcuts");
        Bullet("/rpcharacter opens the Profile tab.");
        Bullet("/rpstats (/rpsheet, /rpcs) jumps to Stats; /rpskills (/rpsk) to Skills; /rpequipment to Equipment.");
        Bullet("The pen icon in the title bar toggles editing of your own Profile. DMs also get a gear icon to edit the campaign's sheet template.");
    }

    private static void DrawRpNpc(float scale)
    {
        Title("RPNPC - Companions & NPCs", "/rpnpc");

        Body("Build full characters that aren't your main: personal companions (pets, allies, summons) and, for DMs, campaign NPCs. Each has its own stat sheet and skills, built with the same tools as your own character.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Companions (everyone)");
        Bullet("Create a companion and fill in its sheet and skills in the vault.");
        Bullet("Set one companion as active to surface it on your RPCHARACTER Companion tab and add it to encounters in RPINITIATIVE.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("NPCs (DMs)");
        Bullet("DMs get a Companion / NPC choice when building. NPCs are DM-owned and can be added to any encounter.");
        Bullet("DM-only NPCs stay hidden from players until the DM reveals them.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Export / import codes");
        Bullet("Export any entity to a shareable code (prefix RPNPC1:) copied to your clipboard.");
        Bullet("Paste a code to import it. Players always import as their own companion; DMs can import as a companion or an NPC. Cooldowns and durations reset on import.");
    }

    private static void DrawRpStats(float scale)
    {
        Title("RPSTATS - Character Sheet", "/rpstats  •  /rpsheet  •  /rpcs");

        Body("Your character sheet holds all stats, resource bars, and specializations. Every field is defined by the campaign's DM - they control the layout through the template editor.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Field types");
        Bullet("Number - a single integer value (e.g. STR 14). Optionally shows a D&D-style modifier: (value − 10) ÷ 2, rounded down.");
        Bullet("Bar - a current / max pair (e.g. HP 40 / 50). The HP bar is tracked in the initiative window. The AP bar drives exhaustion penalties in RPDICE.");
        Bullet("Checkbox - a proficiency or boolean flag. Checked means proficient, which grants advantage in RPDICE.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("For DMs - template editor");
        Bullet("Click the gear icon (Cog) in the title bar to open the editor, or right-click your campaign in RPHUB and choose Edit Sheet Template.");
        Bullet("Add, remove, rename, and reorder groups and fields freely.");
        Bullet("Each field can have a tooltip that players see when hovering its name.");
        Bullet("Mark one Number field as Init to use it as the initiative roll bonus.");
        Bullet("Mark one Bar as HP and one as AP so the system knows which pool is which.");
        Bullet("Click Publish ▶ Party to push the template to every campaign member instantly.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Viewing another member's sheet");
        Body("Right-click an online member in RPHUB and choose Open Character Sheet or Open Skills to open a read-only window populated from the server. You can also type /rpsheet <name> or /rpskills <name>.");
    }

    private static void DrawRpDice(float scale)
    {
        Title("RPDICE - Dice Roller", "/rpdice  •  /rpdice d20  •  /rpdice d6");

        Body("Roll any standard die or a custom size. Results appear in the chat log with a full breakdown.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Options");
        Bullet("Stat Modifier - select a Number field from the sheet. Its modifier ((value − 10) ÷ 2) is added to the roll.");
        Bullet("Specialization - select a Checkbox field. If you have proficiency in it you roll twice and keep the higher result.");
        Bullet("Advantage - roll twice, keep the higher. Disadvantage - roll twice, keep the lower.");
        Bullet("Proficiency cancels Disadvantage: they offset each other and you roll once normally.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("AP Exhaustion");
        Body("When your AP bar is low, an automatic penalty is applied to all stat rolls:");
        Bullet("≤ 40% AP - −1");
        Bullet("≤ 30% AP - −2");
        Bullet("≤ 20% AP - −4");
        Bullet("≤ 10% AP - −5");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Quick roll from chat");
        Body("Pass a die size to roll immediately without opening the window: /rpdice d20, /rpdice d6, /rpdice d100, /rpdice d3, etc.");
    }

    private static void DrawRpSkills(float scale)
    {
        Title("RPSKILLS - Skills & Passives", "/rpskills  •  /rpsk");

        Body("Define active abilities and passive effects tied to your character's sheet fields. Skills are personal - each player manages their own.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Active skills");
        Bullet("Manually triggered during RP (no mechanical enforcement - the system tracks cooldowns and durations, but activation is honour-based).");
        Bullet("Cooldown - turns remaining before the skill can be used again. Ticks down when you end your turn in initiative.");
        Bullet("Duration - turns the skill's effects remain active. Also ticks on turn end.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Passive skills");
        Bullet("Toggled on/off with the Enable / Disable button. An active passive is marked with a * in the list and '● Active' in its view.");
        Bullet("While enabled, a passive's effects apply live (non-destructively) whenever its conditions are met - they are never baked into your raw stats, so disabling or deleting the passive removes its contribution instantly.");
        Bullet("Conditions check any sheet field against a value using <, ≤, =, ≥, or >. Percentage mode checks relative to the field's max (bars only). Empty conditions = always on while enabled.");
        Bullet("Effects apply +, −, or = to a target field. Also supports percentage values. Use an 'On Turn End' block to tick a value once per turn instead.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Building effects");
        Bullet("Each effect row starts with a Category: Stat (numeric op), Spec. (grant a specialization), or - on active skills - Passive (grant one of your own passives for N turns).");
        Bullet("Conditional blocks each carry a Trigger: On Active (applies while engaged) or On Turn End (fires once per turn). A block's effects apply only while its own conditions hold.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("DM skills & item-granted passives");
        Bullet("DMs can tick 'DM' on a skill to file it in a separate DM Skills vault.");
        Bullet("When building an equippable item, a DM can attach DM-vault passives to it. The passive definition travels with the item, so trading the item to a player lets them inherit the passive (shown under 'From Equipment') - active while the item is equipped.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Cooldown tracking in initiative");
        Body("When you click End Turn in RPINITIATIVE, cooldowns and durations on your skills automatically decrease by 1. On Turn End blocks fire at that moment if their conditions are met.");
    }

    private static void DrawRpInitiative(float scale)
    {
        Title("RPINITIATIVE - Initiative Tracker", "/rpini");

        Body("Tracks turn order for combat encounters. A campaign can run several encounters at once - two separate fights become two independent trackers. All party members see the same live state.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Creating an encounter");
        Bullet("The DM types a name and clicks Create in /rpini to open a new encounter. Pick the encounter to view from the dropdown.");
        Bullet("Players click Join Encounter to roll in (d24 + SPD modifier, or whichever stat the DM marked as initiative). Click Leave Encounter to drop out.");
        Bullet("Anyone can add their own companions via + Add combatant. The DM can also add other players, vault NPCs, or an ad-hoc NPC by name.");
        Bullet("Combatants are sorted by total roll (ties broken by raw die result).");

        ImGuiHelpers.ScaledDummy(6f);
        Section("During combat");
        Bullet("The current combatant is highlighted with a > arrow.");
        Bullet("HP and AP are visible if the DM has enabled Show HP / AP in settings (gear icon in the title bar).");
        Bullet("End Turn advances to the next combatant and ticks that combatant's skill cooldowns and passive effects. You can end your own (or your companion's) turn; the DM can end anyone's.");
        Bullet("The DM removes a combatant by right-clicking their row, and deletes the whole encounter with the trash button next to the encounter picker.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Incapacitation");
        Body("A combatant whose HP or AP bar reaches 0 is shown with a strikethrough and greyed-out name.");
    }

    private static void DrawRpBgm(float scale)
    {
        Title("RPBGM - Background Music", "/rpbgm");

        Body("Share atmospheric music with your party in real time using YouTube links. Everyone hears the same track at the same position - the server prepares the audio, so no extra software is needed on your machine.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Setting up");
        Bullet("Open /rpbgm and create a room, or join one by code. Rooms are scoped to your active campaign (or your personal scope when no campaign is active).");
        Bullet("Add songs by pasting a YouTube URL and giving them a title.");
        Bullet("The room owner (and party DMs/Co-DMs) can play, pause, stop, skip, and change loop mode (None / Single / All).");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Synchronized playback");
        Bullet("The server downloads and transcodes each track once and streams it to everyone - identical playback on any OS, no client-side tools or codecs.");
        Bullet("When a song starts, the room briefly shows 'Waiting for members' while everyone downloads it, then begins together. A slow member won't stall the room for more than a few seconds.");
        Bullet("Playback stays clock-synced to the room, and anyone who joins mid-song jumps straight to the current position.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Volume & cache");
        Bullet("Each listener sets their own volume locally - it never affects anyone else.");
        Bullet("Downloaded tracks are cached; clear the local cache from /rpsettings.");
    }

    private static void DrawRpInventory(float scale)
    {
        Title("RPINVENTORY - Inventory & Trading", "/rpinventory  •  /rpinv");

        Body("Roleplay item bags for things that don't exist in the real FFXIV inventory. Inventories are saved on the server and scoped to your Active Campaign, so you must be connected with a campaign selected.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Inventories");
        Bullet("Each inventory is a tab. Use the + tab to create one; drag tabs to reorder, right-click a tab to Rename, Share or Delete it.");
        Bullet("Personal Inventory is your own. DMs can create a DM Inventory, automatically shared with all DMs/Co-DMs of the campaign.");
        Bullet("Each item has a name, description, FFXIV icon ID, and quantity; matching stackable items stack together.");
        Bullet("Right-click an item to move it into a sub-bag (Put into), move it to another inventory (Move to inventory), Offer it to a member, or Discard it.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Trading");
        Bullet("Right-click an item and open Offer to, then pick a member - (copy) sends a duplicate and keeps yours, (give) hands over the original.");
        Bullet("The recipient sees a trade notification and can accept or reject it.");
        Bullet("Accepted (give) transfers remove the item from your inventory; copies leave it in place.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Shared inventories");
        Bullet("Right-click a personal inventory tab and Share it with another member - both can add, remove, and modify items in real time.");
        Bullet("Every change is synced through the server and visible to all participants instantly.");
    }

    private static void DrawRpHub(float scale)
    {
        Title("RPHUB - Hub & Connection", "/rphub");

        Body("The central panel for server connection, party management, and player interactions. Most multiplayer features flow through here.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Connection");
        Bullet("Set your server URL in /rpsettings before connecting.");
        Bullet("The hub shows the current connection status and lets you connect or disconnect manually.");
        Bullet("The plugin auto-connects on login if a server URL is configured.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Campaigns");
        Bullet("Create a campaign with a name and an optional password. Share the campaign code (and password, if you set one) with your group.");
        Bullet("Join an existing campaign using its code and password. Right-click a campaign for options: Copy Campaign Code, Set as Active Campaign, Leave Campaign.");
        Bullet("You can belong to multiple campaigns at once. The inventory, BGM and character windows all follow whichever one is your Active Campaign.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Roles");
        Bullet("Owner (DM) - full control: manage encounters, publish sheet templates, kick members, promote Co-DMs.");
        Bullet("Co-DM - shared DM abilities: manage encounters, end anyone's turn, publish templates, add NPCs. Cannot kick other Co-DMs.");
        Bullet("Member - standard player: join encounters, end own turn.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Member interactions");
        Body("Right-click any online member to Open Character Sheet or Open Skills (read-only). DMs can also Promote to Co-DM, Demote to Member, or Kick. To give someone an item, right-click it in /rpinventory and use Offer to.");
    }
}
