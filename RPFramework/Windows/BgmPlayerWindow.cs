using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using RPFramework.Contracts;
using RPFramework.Services;

namespace RPFramework.Windows;

/// <summary>
/// BGM player UI for one room. Pure view: all playback + sync lives in <see cref="BgmCoordinator"/>
/// (driven every framework tick, independent of this window). Controls just call the coordinator.
/// </summary>
public class BgmPlayerWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly BgmService _bgm;
    private string? _code;
    private string  _addUrl = "";

    public BgmPlayerWindow(Plugin plugin) : base("BGM Player##RPFramework.BgmPlayer")
    {
        _plugin = plugin;
        _bgm    = plugin.BgmService;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(320, 280), MaximumSize = new Vector2(700, 900) };
        Size          = new Vector2(360, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void OpenRoom(string code) { _plugin.Bgm.SetActiveRoom(code); _code = code; IsOpen = true; }

    public override void Draw()
    {
        var room = _plugin.Store.Room(_code);
        if (_code == null || room == null) { ImGui.TextDisabled("Not in a BGM room."); return; }

        var   bgm     = _plugin.Bgm;
        bool  control = bgm.CanControl(_plugin.LocalPlayerId);
        float scale   = ImGuiHelpers.GlobalScale;

        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine(); ImGui.TextDisabled($"({room.Code})");
        ImGui.SameLine(); if (ImGui.SmallButton("Copy##bgmcopy")) ImGui.SetClipboardText(room.Code);
        ImGui.SameLine(); if (ImGui.SmallButton("Leave##bgmleave")) { bgm.LeaveActive(); IsOpen = false; }
        ImGui.Separator();

        using (var pl = ImRaii.Child("##bgmplaylist", new Vector2(-1, -110 * scale), true))
        {
            if (pl)
            {
                for (int i = 0; i < room.Playlist.Count; i++)
                {
                    var song = room.Playlist[i];
                    bool cur = i == room.CurrentIndex;
                    if (cur) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.85f, 1f, 1f));
                    if (ImGui.Selectable($"{(cur ? "♪ " : "  ")}{song.Title}##song_{song.Id}", cur) && control)
                        bgm.PlayIndex(i);
                    if (cur) ImGui.PopStyleColor();
                    if (control && ImGui.BeginPopupContextItem($"##sc_{song.Id}"))
                    {
                        if (ImGui.MenuItem("Remove")) _ = _plugin.Network.PlaylistRemove(room.Code, song.Id);
                        ImGui.EndPopup();
                    }
                }
                if (room.Playlist.Count == 0) ImGui.TextDisabled("Playlist is empty.");
            }
        }

        if (room.Preparing)
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"Waiting for members… {room.PrepareReady}/{room.PrepareTotal} ready");
        else if (_bgm.IsLoading) ImGui.TextDisabled($"Loading… {_bgm.DownloadProgress * 100:0}%");
        else if (_bgm.LoadError != null) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _bgm.LoadError);

        using (ImRaii.Disabled(!control))
        {
            if (ImGui.Button("<<##prev")) bgm.Prev();
            ImGui.SameLine();
            string ppLabel = room.Preparing ? "Preparing…##pp" : (room.IsPlaying ? "Pause##pp" : "Play##pp");
            using (ImRaii.Disabled(room.Preparing))
                if (ImGui.Button(ppLabel)) bgm.TogglePlayPause();
            ImGui.SameLine();
            if (ImGui.Button(">>##next")) bgm.Next();
            ImGui.SameLine();
            if (ImGui.Button("Stop##stop")) bgm.Stop();
            ImGui.SameLine();
            int loopI = (int)room.Loop;
            ImGui.SetNextItemWidth(90 * scale);
            if (ImGui.Combo("##loop", ref loopI, new[] { "No Loop", "Loop One", "Loop All" }, 3))
                bgm.SetLoop((LoopMode)loopI);
        }

        // Volume runs in 0–100% space (a 0–1 range + "%.0f" would round to only 0 and 1).
        float volPct = _plugin.Configuration.BgmVolume * 100f;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##vol", ref volPct, 0f, 100f, "Volume %.0f%%"))
        {
            _plugin.Configuration.BgmVolume = Math.Clamp(volPct / 100f, 0f, 1f);
            _bgm.SetVolume(_plugin.Configuration.BgmVolume);
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) _plugin.Configuration.Save();

        if (control)
        {
            ImGui.SetNextItemWidth(-70 * scale);
            ImGui.InputTextWithHint("##addurl", "YouTube URL…", ref _addUrl, 512);
            ImGui.SameLine();
            using (ImRaii.Disabled(!BgmService.IsAllowedYoutubeUrl(_addUrl)))
                if (ImGui.Button("Add##addsong", new Vector2(60 * scale, 0)))
                {
                    string url = _addUrl.Trim(); string code = room.Code;
                    _addUrl = "";
                    Task.Run(async () =>
                    {
                        string title = await BgmService.GetTitleAsync(url);
                        await Plugin.Framework.RunOnFrameworkThread(() => _plugin.Network.PlaylistAdd(code, title, url));
                    });
                }
        }
    }
}
