using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace RPFramework.Windows;

public class SettingsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string serverUrlBuf = string.Empty;
    private bool   urlDirty;

    public SettingsWindow(Plugin plugin)
        : base("RPFramework Settings##RPFramework.Settings",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin    = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 180),
            MaximumSize = new Vector2(560, 360),
        };
        Size          = new Vector2(380, 220);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        serverUrlBuf = plugin.Configuration.ServerUrl;
        urlDirty     = false;
    }

    public void Dispose() { }

    public override void Draw()
    {
        float btnW = 110 * ImGuiHelpers.GlobalScale;

        // ── Server Connection ────────────────────────────────────────────────
        ImGui.TextUnformatted("Server Connection");
        ImGui.Separator();

        ImGui.TextDisabled("Server URL");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##serverurl", ref serverUrlBuf, 256))
            urlDirty = true;

        bool connected = plugin.Network.IsConnected;
        var  dotColor  = connected
            ? new Vector4(0.3f, 0.85f, 0.3f, 1f)
            : new Vector4(0.55f, 0.55f, 0.55f, 1f);

        ImGui.TextColored(dotColor, "●");
        ImGui.SameLine();
        ImGui.TextDisabled(connected ? "Connected" : "Disconnected");
        ImGui.SameLine();

        if (urlDirty)
        {
            if (ImGui.Button("Save & Reconnect##savereconnect", new Vector2(btnW * 1.4f, 0)))
            {
                plugin.Configuration.ServerUrl = serverUrlBuf.Trim();
                plugin.Configuration.Save();
                urlDirty = false;
                string url  = plugin.Configuration.ServerUrl;
                string? id  = plugin.LocalPlayerId;
                string name = plugin.LocalDisplayName;
                if (id != null)
                    Task.Run(() => plugin.Network.ConnectAsync(url, id, name));
            }
        }
        else if (connected)
        {
            if (ImGui.Button("Disconnect##disconnect", new Vector2(btnW, 0)))
                Task.Run(() => plugin.Network.DisconnectAsync());
        }
        else
        {
            if (ImGui.Button("Connect##connect", new Vector2(btnW, 0)))
            {
                string url  = plugin.Configuration.ServerUrl;
                string? id  = plugin.LocalPlayerId;
                string name = plugin.LocalDisplayName;
                if (id != null)
                    Task.Run(() => plugin.Network.ConnectAsync(url, id, name));
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // ── BGM Cache ────────────────────────────────────────────────────────
        ImGui.TextUnformatted("BGM Cache");
        ImGui.Separator();

        long bytes = plugin.BgmService.GetCacheSizeBytes();
        string sizeStr = bytes switch
        {
            < 1024                => $"{bytes} B",
            < 1024 * 1024         => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _                     => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };

        ImGui.TextDisabled($"Cached audio: {sizeStr}");
        ImGui.SameLine();

        if (bytes == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Clear Cache##rpclrcache", new Vector2(btnW, 0)))
            plugin.BgmService.ClearCache();
        if (bytes == 0) ImGui.EndDisabled();
    }
}
