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
        "RPSTATS",
        "RPDICE",
        "RPSKILLS",
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
            case 1: DrawRpStats(scale);       break;
            case 2: DrawRpDice(scale);        break;
            case 3: DrawRpSkills(scale);      break;
            case 4: DrawRpInitiative(scale);  break;
            case 5: DrawRpBgm(scale);         break;
            case 6: DrawRpInventory(scale);   break;
            case 7: DrawRpHub(scale);         break;
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
        Bullet("Open /rphub, connect, then create or join a party.");
        Bullet("Your DM can build the party's character sheet in /rpstats and publish it to everyone.");
        Bullet("Roll initiative from /rphub when combat begins.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("All commands");
        Cmd("/rphub",          "- connection, parties, trading");
        Cmd("/rpstats  /rpsheet  /rpcs", "- character sheet");
        Cmd("/rpdice",         "- dice roller");
        Cmd("/rpskills  /rpsk","- skills and passives");
        Cmd("/rpini",          "- initiative tracker");
        Cmd("/rpbgm",          "- background music player");
        Cmd("/rpinventory  /rpinv", "- inventory and trading");
        Cmd("/rpsettings",     "- server URL and plugin options");
        Cmd("/rphelp",         "- this window");
    }

    private static void DrawRpStats(float scale)
    {
        Title("RPSTATS - Character Sheet", "/rpstats  •  /rpsheet  •  /rpcs");

        Body("Your character sheet holds all stats, resource bars, and specializations. Every field is defined by the party's DM - they control the layout through the template editor.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Field types");
        Bullet("Number - a single integer value (e.g. STR 14). Optionally shows a D&D-style modifier: (value − 10) ÷ 2, rounded down.");
        Bullet("Bar - a current / max pair (e.g. HP 40 / 50). The HP bar is tracked in the initiative window. The AP bar drives exhaustion penalties in RPDICE.");
        Bullet("Checkbox - a proficiency or boolean flag. Checked means proficient, which grants advantage in RPDICE.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("For DMs - template editor");
        Bullet("Click the pencil icon (✏) in the title bar to open the editor.");
        Bullet("Add, remove, rename, and reorder groups and fields freely.");
        Bullet("Each field can have a tooltip that players see when hovering its name.");
        Bullet("Mark one Number field as Init to use it as the initiative roll bonus.");
        Bullet("Mark one Bar as HP and one as AP so the system knows which pool is which.");
        Bullet("Click Publish ▶ Party to push the template to every party member instantly.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Viewing a party member's sheet");
        Body("Right-click a party member in RPHUB and choose View Sheet or View Skills to open a read-only window populated from the server.");
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
        Bullet("Trigger automatically: either on every turn end, or whenever all conditions are met.");
        Bullet("Conditions check any sheet field against a value using <, ≤, =, ≥, or >. Percentage mode checks relative to the field's max (bars only).");
        Bullet("Effects apply +, −, or = to a target field when the passive fires. Also supports percentage values.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Cooldown tracking in initiative");
        Body("When you click End Turn in RPINITIATIVE, cooldowns and durations on your skills automatically decrease by 1. Passives flagged Trigger on Turn End fire at that moment if their conditions are met.");
    }

    private static void DrawRpInitiative(float scale)
    {
        Title("RPINITIATIVE - Initiative Tracker", "/rpini");

        Body("Tracks turn order for combat encounters. All party members see the same live state.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Starting a round");
        Bullet("The DM clicks Start Initiative in RPHUB for the relevant party.");
        Bullet("Each player rolls by clicking Roll Initiative in RPHUB. The roll is d24 + SPD modifier (or whichever stat the DM marked as initiative).");
        Bullet("Combatants are sorted by total roll (ties broken by raw die result).");

        ImGuiHelpers.ScaledDummy(6f);
        Section("During combat");
        Bullet("The current combatant is highlighted with a ▶ arrow.");
        Bullet("HP and AP are visible if the DM has enabled Show HP / AP in settings (⚙ icon in the title bar).");
        Bullet("End Turn advances to the next combatant and ticks your skill cooldowns and passive effects.");
        Bullet("The DM can end anyone's turn and has an End Combat button to close the round.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Incapacitation");
        Body("A combatant whose HP or AP bar reaches 0 is shown with a strikethrough and greyed-out name.");
    }

    private static void DrawRpBgm(float scale)
    {
        Title("RPBGM - Background Music", "/rpbgm");

        Body("Share atmospheric music with party members in real time using YouTube links. The room owner controls playback and everyone hears the same track at the same position.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Setting up");
        Bullet("Open /rpbgm and create a room (or join one using the room code).");
        Bullet("Add songs by pasting a YouTube URL and giving them a title.");
        Bullet("The room owner can play, pause, seek, stop, and change loop mode.");
        Bullet("Audio is downloaded locally and streamed through the plugin - no external software needed.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("BGM and parties");
        Body("Party members who have joined a BGM room are shown in RPHUB next to their name. DMs and Co-DMs in the same party automatically have room owner privileges.");
    }

    private static void DrawRpInventory(float scale)
    {
        Title("RPINVENTORY - Inventory & Trading", "/rpinventory  •  /rpinv");

        Body("Manage personal bags of items and trade with other players online in the same party.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Personal bags");
        Bullet("Create multiple named bags to organise your items.");
        Bullet("Each item has a name, description, FFXIV icon ID, and quantity.");
        Bullet("Items are stored locally - they are not synced to the server.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Trading");
        Bullet("Right-click a party member in RPHUB and choose Offer Item.");
        Bullet("Pick an item and choose whether to send a copy (keeping your original) or transfer it.");
        Bullet("The recipient sees a trade notification and can accept or reject it.");
        Bullet("Accepted transfers remove the item from your bag; copies leave it in place.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Shared bags");
        Bullet("Create a shared bag and invite another player - both can add, remove, and modify items in real time.");
        Bullet("Changes are synced through the server and visible to all participants instantly.");
        Bullet("The bag owner can dissolve it; any participant can leave at any time.");
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
        Section("Parties");
        Bullet("Create a party with a display name and a password (minimum 4 characters). Share the code and password with your group.");
        Bullet("Join an existing party using its code and password.");
        Bullet("You can be a member of multiple parties simultaneously.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Roles");
        Bullet("Owner (DM) - full control: start/end initiative, publish sheet templates, kick members, promote Co-DMs.");
        Bullet("Co-DM - shared DM abilities: start/end initiative, end anyone's turn, publish templates. Cannot kick other Co-DMs.");
        Bullet("Member - standard player: roll initiative, end own turn.");

        ImGuiHelpers.ScaledDummy(6f);
        Section("Player interactions");
        Body("Right-click any online party member to view their character sheet, view their skills, or offer them an item from your inventory.");
    }
}
