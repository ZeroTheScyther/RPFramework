using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using SamplePlugin.Services;

namespace SamplePlugin.Windows;

public class SettingsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string serverUrl = string.Empty;

    public SettingsWindow(Plugin plugin)
        : base("RPFramework Settings##RPFramework.Settings",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(600, 500),
        };
        Size          = new Vector2(400, 240);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        serverUrl = plugin.Configuration.ServerUrl;
    }

    public override void Draw()
    {
        // ── Server ───────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Relay Server");
        ImGui.Separator();

        ImGui.TextDisabled("URL:");
        ImGui.SameLine();

        bool connected = plugin.Network.IsConnected;
        var  dot       = connected
            ? new Vector4(0.3f, 0.85f, 0.3f, 1f)
            : new Vector4(0.55f, 0.55f, 0.55f, 1f);
        ImGui.TextColored(dot, "●");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(connected ? "Connected" : "Disconnected");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##rpserverurl", ref serverUrl, 256);

        float btnW = 100 * ImGuiHelpers.GlobalScale;

        bool urlChanged = serverUrl != plugin.Configuration.ServerUrl;
        if (!urlChanged) ImGui.BeginDisabled();
        if (ImGui.Button("Save##rpsrvsave", new Vector2(btnW, 0)))
        {
            plugin.Configuration.ServerUrl = serverUrl.Trim();
            plugin.Configuration.Save();
        }
        if (!urlChanged) ImGui.EndDisabled();

        ImGui.SameLine();

        if (connected)
        {
            if (ImGui.Button("Disconnect##rpsrvdisconn", new Vector2(btnW, 0)))
                Task.Run(() => plugin.Network.DisconnectAsync());
        }
        else
        {
            if (ImGui.Button("Connect##rpsrvconn", new Vector2(btnW, 0)))
            {
                string url     = plugin.Configuration.ServerUrl;
                string? id     = plugin.LocalPlayerId;
                string  name   = plugin.LocalDisplayName;
                if (id != null)
                    Task.Run(() => plugin.Network.ConnectAsync(url, id, name));
                else
                    ImGui.SetTooltip("Log in first");
            }
        }

        ImGui.Spacing();

        // ── BGM cache ────────────────────────────────────────────────────────
        ImGui.TextUnformatted("BGM Cache");
        ImGui.Separator();

        long bytes = plugin.BgmService.GetCacheSizeBytes();
        string sizeStr = bytes switch
        {
            < 1024              => $"{bytes} B",
            < 1024 * 1024       => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _                   => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };

        ImGui.TextDisabled($"Cached audio: {sizeStr}");
        ImGui.SameLine();

        if (bytes == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Clear Cache##rpclrcache", new Vector2(btnW, 0)))
            plugin.BgmService.ClearCache();
        if (bytes == 0) ImGui.EndDisabled();
    }
}
