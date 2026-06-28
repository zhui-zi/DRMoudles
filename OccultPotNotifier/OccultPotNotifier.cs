using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Helpers;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;
using System.Text.RegularExpressions;
using Dalamud.Hooking;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DailyRoutines.ModulesPublic;

public class OccultPotNotifier : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "新月岛 魔法罐助手",
        Description = "新月岛「幸福的魔法罐」宝兔 FATE（北 / 南）刷新倒计时与方位提醒，可用悬浮窗或服务器信息栏显示，刷新前可 TTS / 通知 / 转发聊天（附带 <flag> 坐标）。\n" +
                      "另含地图标记功能：在地图与小地图上标注宝箱 / 魔法罐 / 续罐 / 萝卜点位，提供快速切换悬浮窗；携带「撒娇罐」时自动显示罐点标记并在最近的埋藏宝箱处绘制圆圈。",
        Category    = ModuleCategory.Notification,
        Author      = ["黑川启太"]
    };

    private const long Respawn = 1800;

    private Config        config = null!;
    private IDtrBarEntry? entry;

    private Hook<RaptureLogModule.Delegates.AddMsgSourceEntry>? addMsgSourceEntryHook;
    private long lastArchivistReplyAt;

    private Pot?   displayPot;
    private string displayText       = string.Empty;
    private long   notifiedSpawnTime = -1;

    private readonly Pot[] pots =
    [
        new() { FateID = 1976, World = new(204.66835f,  111.81729f, -204.96242f), DirName = "北" },
        new() { FateID = 1977, World = new(-479.8395f,  75f,         524.78894f), DirName = "南" }
    ];

    private static readonly (string Command, string Label)[] ChatChannels =
    [
        ("/s",    "说话"),
        ("/y",    "呼喊"),
        ("/sh",   "喊话"),
        ("/p",    "小队"),
        ("/a",    "团队"),
        ("/fc",   "部队"),
        ("/e",    "默语"),
        ("/l1",   "通讯贝 1"),
        ("/l2",   "通讯贝 2"),
        ("/l3",   "通讯贝 3"),
        ("/l4",   "通讯贝 4"),
        ("/l5",   "通讯贝 5"),
        ("/l6",   "通讯贝 6"),
        ("/l7",   "通讯贝 7"),
        ("/l8",   "通讯贝 8"),
        ("/cwl1", "跨界贝 1"),
        ("/cwl2", "跨界贝 2"),
        ("/cwl3", "跨界贝 3"),
        ("/cwl4", "跨界贝 4"),
        ("/cwl5", "跨界贝 5"),
        ("/cwl6", "跨界贝 6"),
        ("/cwl7", "跨界贝 7"),
        ("/cwl8", "跨界贝 8")
    ];

    private const string TrackerBaseURL     = "https://infi.ovh/api/";
    private const string TrackerTable       = "OccultTrackerV3";
    private const string TrackerAnonKey     = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiJ9.Ur6wgi_rD4dr3uLLvbLoaEvfLCu4QFWdrF-uHRtbl_s";
    private const string TrackerVersion     = "DR-OccultPotNotifier";
    private const int    SyncRefreshSeconds = 60;
    private const int    FastRetrySeconds   = 5;

    private static readonly HashSet<uint> OccultFateIds =
        [1962, 1963, 1964, 1965, 1966, 1967, 1968, 1969, 1970, 1971, 1972];

    private static readonly HttpClient Client = CreateClient();

    private          string lastFingerprint    = string.Empty;
    private          string createdFingerprint = string.Empty;
    private          long   lastSyncAt;
    private volatile bool   syncInFlight;
    private volatile bool   hasOnlineData;
    private readonly object syncLock = new();
    private (long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? pendingSync;

    #region 地图标记 - 常量

    // 新月岛「南方海角」 territory & 地图
    private const uint OccultTerritory = 1252;
    private const uint OccultMapID     = 967;

    // 携带「撒娇罐」时附加的状态 ID
    private const uint LureStatusID = 1531;

    // 地图标记图标 ID
    private const uint IconGoldChest = 60354;
    private const uint IconBronze    = 60356;
    private const uint IconSilver    = 60355;
    private const uint IconReroll    = 61473;
    private const uint IconCarrot    = 25207;

    private static readonly Vector4 SwitchActiveColor = KnownColor.SeaGreen.ToVector4();

    private const ImGuiWindowFlags SwitcherFlags =
        ImGuiWindowFlags.NoTitleBar          |
        ImGuiWindowFlags.NoResize            |
        ImGuiWindowFlags.NoScrollbar         |
        ImGuiWindowFlags.NoScrollWithMouse   |
        ImGuiWindowFlags.NoMove              |
        ImGuiWindowFlags.AlwaysAutoResize    |
        ImGuiWindowFlags.NoSavedSettings     |
        ImGuiWindowFlags.NoFocusOnAppearing  |
        ImGuiWindowFlags.NoNavFocus;

    private static readonly (string Label, MarkerSet Flag)[] SwitchButtons =
    [
        ("青铜", MarkerSet.BronzeTreasure),
        ("白银", MarkerSet.SilverTreasure),
        ("北罐", MarkerSet.NorthPot),
        ("南罐", MarkerSet.SouthPot),
        ("续罐", MarkerSet.Reroll),
        ("萝卜", MarkerSet.Bunny)
    ];

    #endregion

    #region 地图标记 - 状态

    private MarkerSet  currentMarkers = MarkerSet.None; // 当前生效的标记集合
    private MarkerSet? savedMarkers;                    // 携带撒娇罐临时覆盖前保存的用户集合
    private MarkerSet  placedMarkers  = MarkerSet.None; // 已实际放置在地图上的集合
    private uint       placedMapID;
    private bool       markersDirty;

    private bool    lureActive;
    private Vector3 cofferPos = Vector3.Zero;

    private static bool InOccultMapZone => GameState.TerritoryType == OccultTerritory;

    #endregion

    protected override unsafe void Init()
    {
        config = Config.Load(this) ?? new();

        addMsgSourceEntryHook ??= DService.Instance().Hook.HookFromMemberFunction<RaptureLogModule.Delegates.AddMsgSourceEntry>
        (
            typeof(RaptureLogModule.MemberFunctionPointers),
            nameof(RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry),
            AddMsgSourceEntryDetour
        );
        addMsgSourceEntryHook.Enable();

        currentMarkers = config.DefaultMarkers;

        Overlay        = new(this);
        Overlay.IsOpen = false;

        entry         ??= DService.Instance().DTRBar.Get("DailyRoutines-OccultPotNotifier");
        entry.Shown   =   false;
        entry.Tooltip =   "新月岛 魔法罐助手\n点击在地图上标记下一个魔法罐位置 (<flag>)";
        entry.OnClick =   OnDtrClick;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "AreaMap", OnAreaMapRefresh);
        WindowManager.Instance().PostDraw += OnPostDraw;

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        WindowManager.Instance().PostDraw                -= OnPostDraw;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAreaMapRefresh);
        FrameworkManager.Instance().Unreg(OnUpdate);

        addMsgSourceEntryHook?.Dispose();
        addMsgSourceEntryHook = null;

        ClearMapMarkers();

        if (entry != null)
        {
            entry.Remove();
            entry = null;
        }
    }

    private unsafe void AddMsgSourceEntryDetour(
        RaptureLogModule* thisPtr, ulong contentID, ulong accountID, int messageIndex, ushort worldID, ushort chatType)
    {
        addMsgSourceEntryHook!.Original(thisPtr, contentID, accountID, messageIndex, worldID, chatType);

        try
        {
            TryArchivistReply(messageIndex, chatType);
        }
        catch
        {
            // 忽略
        }
    }

    private unsafe void TryArchivistReply(int messageIndex, ushort chatType)
    {
        if (!config.EnableArchivist || !config.UseOnlineTracker) return;
        if (chatType != (ushort)XivChatType.Shout) return;
        if (!InOccultMapZone) return;

        var now = Environment.TickCount64;
        if (now - lastArchivistReplyAt < config.ArchivistCooldownSeconds * 1000) return;

        if (!RaptureLogModule.Instance()->GetLogMessageDetail(messageIndex, out _, out var rawMessage, out _, out _, out _, out _))
            return;

        var message = SeString.Parse(rawMessage.AsSpan()).TextValue;
        
        bool matched;
        try { matched = Regex.IsMatch(message, config.ArchivistRegex, RegexOptions.IgnoreCase); }
        catch { return; }

        if (!matched) return;

        var prediction = GetPredictedMinutes();
        if (prediction == null) return;

        lastArchivistReplyAt = now;
        ChatManager.Instance().SendMessage($"/sh 北{prediction.Value.NorthMinute}南{prediction.Value.SouthMinute}");
    }

    private (int NorthMinute, int SouthMinute)? GetPredictedMinutes()
    {
        var north = pots[0];
        var south = pots[1];
        
        Pot? lastSpawned = null;
        if (north.SpawnTime > 0)
            lastSpawned = north;
        if (south.SpawnTime > 0 && (lastSpawned == null || south.SpawnTime > lastSpawned.SpawnTime))
            lastSpawned = south;

        if (lastSpawned == null) return null;

        var lastMinute = DateTimeOffset.FromUnixTimeSeconds(lastSpawned.SpawnTime).ToLocalTime().Minute;
        var otherMinute = (lastMinute + 30) % 60;

        if (ReferenceEquals(lastSpawned, north))
            return (lastMinute, otherMinute);
        else
            return (otherMinute, lastMinute);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "倒计时显示方式");
        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton("服务器信息栏", config.DisplayMode == PotDisplayMode.DtrBar))
            {
                config.DisplayMode = PotDisplayMode.DtrBar;
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("悬浮窗", config.DisplayMode == PotDisplayMode.Overlay))
            {
                config.DisplayMode = PotDisplayMode.Overlay;
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("不显示", config.DisplayMode == PotDisplayMode.None))
            {
                config.DisplayMode = PotDisplayMode.None;
                config.Save(this);
            }
        }

        ImGui.NewLine();

        using (ImRaii.Disabled())
        {
            config.UseOnlineTracker = true;
            ImGui.Checkbox("从在线追踪器同步并上报数据", ref config.UseOnlineTracker);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "提醒方式");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("语音播报 (TTS)", ref config.SendTTS))
                config.Save(this);

            if (ImGui.Checkbox("游戏内通知", ref config.SendNotification))
                config.Save(this);

            if (ImGui.Checkbox("转发到聊天频道 (附带 <flag> 坐标)", ref config.SendChat))
                config.Save(this);

            if (config.SendChat)
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.TextUnformatted("频道 (可多选):");
                    for (var i = 0; i < ChatChannels.Length; i++)
                    {
                        var (cmd, label) = ChatChannels[i];

                        var on = config.ChatCommands.Contains(cmd);
                        if (ImGui.Checkbox($"{label}###Chat{i}", ref on))
                        {
                            if (on) config.ChatCommands.Add(cmd);
                            else    config.ChatCommands.Remove(cmd);
                            config.Save(this);
                        }

                        if (i % 4 != 3 && i != ChatChannels.Length - 1)
                            ImGui.SameLine();
                    }
                }
            }
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "提前提醒时间");
        using (ImRaii.PushIndent())
        {
            var minutes = Math.Clamp(config.LeadSeconds / 60, 1, 15);
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            if (ImGui.SliderInt("分钟###LeadMinutes", ref minutes, 1, 15))
                config.LeadSeconds = minutes * 60;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "史官功能 (自动回复罐子时间)");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("启用自动回复喊话频道 (需开启在线追踪器)", ref config.EnableArchivist))
                config.Save(this);

            if (!config.UseOnlineTracker && config.EnableArchivist)
            {
                ImGui.SameLine();
                ImGui.TextColored(KnownColor.Orange.ToVector4(), "警告: 请先开启“从在线追踪器同步”功能");
            }

            using (ImRaii.Disabled(!config.EnableArchivist || !config.UseOnlineTracker))
            {
                ImGui.SetNextItemWidth(250f * GlobalUIScale);
                if (ImGui.InputText("触发正则###ArchivistRegex", ref config.ArchivistRegex, 128))
                    config.Save(this);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("匹配喊话频道的关键词，使用正则表达式。例如: lw罐|史官");

                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                if (ImGui.SliderInt("回复间隔 (秒)###ArchivistCooldown", ref config.ArchivistCooldownSeconds, 10, 300))
                    config.Save(this);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }
        }

        ImGui.NewLine();

        ConfigUIMarkers();
    }

    private void ConfigUIMarkers()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "地图标记 (仅限新月岛)");
        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted("默认显示的标记 (可多选):");
            using (ImRaii.Disabled(savedMarkers != null))
            {
                var set = config.DefaultMarkers;
                for (var i = 0; i < SwitchButtons.Length; i++)
                {
                    var (label, flag) = SwitchButtons[i];
                    var on = set.HasFlag(flag);
                    if (ImGui.Checkbox($"{label}###CfgMarker{flag}", ref on))
                        SetUserMarkers(on ? set | flag : set & ~flag);

                    if (i % 3 != 2 && i != SwitchButtons.Length - 1)
                        ImGui.SameLine();
                }
            }
            if (savedMarkers != null)
                ImGui.TextDisabled("（携带撒娇罐中，标记由自动切换接管）");

            ImGui.Spacing();

            if (ImGui.Checkbox("显示快速切换悬浮窗 (打开地图时贴附在地图旁)", ref config.ShowFastSwitcher))
                config.Save(this);
            if (config.ShowFastSwitcher)
            {
                using (ImRaii.PushIndent())
                {
                    if (ImGui.Checkbox("悬浮窗显示在地图下方", ref config.SwitcherBelowMap))
                        config.Save(this);
                }
            }

            ImGui.Spacing();

            if (ImGui.Checkbox("携带「撒娇罐」时自动切换标记", ref config.AutoSwitchOnLure))
                config.Save(this);
            if (config.AutoSwitchOnLure)
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.TextUnformatted("自动切换为:");
                    var auto = config.AutoSwitchFlags;
                    (string Label, MarkerSet Flag)[] autoButtons =
                    [
                        ("北罐", MarkerSet.NorthPot),
                        ("南罐", MarkerSet.SouthPot),
                        ("续罐", MarkerSet.Reroll)
                    ];
                    for (var i = 0; i < autoButtons.Length; i++)
                    {
                        var (label, flag) = autoButtons[i];
                        var on = auto.HasFlag(flag);
                        if (ImGui.Checkbox($"{label}###CfgAuto{flag}", ref on))
                        {
                            config.AutoSwitchFlags = on ? auto | flag : auto & ~flag;
                            config.Save(this);
                        }

                        if (i != autoButtons.Length - 1)
                            ImGui.SameLine();
                    }
                }
            }

            ImGui.Spacing();

            if (ImGui.Checkbox("携带「撒娇罐」时在最近的埋藏宝箱处绘制圆圈", ref config.DrawCofferCircle))
                config.Save(this);
            if (config.DrawCofferCircle)
            {
                using (ImRaii.PushIndent())
                {
                    var circleColor = config.CircleColor;
                    if (ImGui.ColorEdit4("圆圈颜色###CircleColor", ref circleColor, ImGuiColorEditFlags.NoInputs))
                    {
                        config.CircleColor = circleColor with { W = 1f };
                        config.Save(this);
                    }
                }
            }
        }
    }

    protected override void OverlayUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "新月岛 魔法罐");
        ImGui.Separator();

        var text = string.IsNullOrEmpty(displayText) ? "等待刷新数据…" : displayText;
        if (ImGui.Selectable(text) && displayPot != null)
            OpenPotMap(displayPot);

        if (displayPot != null && ImGui.IsItemHovered())
            ImGui.SetTooltip("点击在地图上标记魔法罐位置 (<flag>)");
    }

    private void OnZoneChanged(uint zone)
    {
        FrameworkManager.Instance().Unreg(OnUpdate);

        foreach (var pot in pots)
            pot.Reset();
        displayPot         = null;
        displayText        = string.Empty;
        notifiedSpawnTime  = -1;
        lastFingerprint    = string.Empty;
        createdFingerprint = string.Empty;
        lastSyncAt         = 0;
        hasOnlineData      = false;
        lock (syncLock)
            pendingSync = null;
        HideDisplay();

        // 地图标记复位
        lureActive   = false;
        cofferPos    = Vector3.Zero;
        savedMarkers = null;
        currentMarkers = config.DefaultMarkers;
        ClearMapMarkers();

        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    private void OnUpdate(IFramework _)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
        {
            FrameworkManager.Instance().Unreg(OnUpdate);
            HideDisplay();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var fate in DService.Instance().Fate)
        {
            var pot = GetPot(fate.FateId);
            if (pot == null) continue;

            pot.LastSeenAlive   = now;
            pot.SpawnTime       = fate.StartTimeEpoch;
            pot.LocallyObserved = true;

            if (!pot.Alive)
                pot.Alive = true;
        }

        foreach (var pot in pots)
        {
            if (pot.Alive && pot.LastSeenAlive != now)
            {
                pot.Alive     = false;
                pot.DeathTime = pot.LastSeenAlive;
            }
        }

        if (config.UseOnlineTracker)
            TrySyncOnline(now);
        ApplyPendingSync();

        UpdatePrediction(now);
        ApplyDisplay();

        UpdateLure();
        UpdateMapMarkers();
    }

    private void Notify(Pot pot, int minutes)
    {
        var message = $"魔法罐约{minutes}分钟后在{pot.DirName}处刷新";

        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(message);

        if (config.SendTTS)
            NotifyHelperExtension.Speak(message);

        if (config.SendChat && config.ChatCommands.Count > 0)
        {
            SetPotFlag(pot);
            foreach (var cmd in config.ChatCommands)
                ChatManager.Instance().SendMessage($"{cmd} 魔法罐约{minutes}分钟后在{pot.DirName}<flag>处刷新");
        }
    }

    private void UpdatePrediction(long now)
    {
        var north = pots[0];
        var south = pots[1];

        var alive = north.Alive ? north : south.Alive ? south : null;
        if (alive != null)
        {
            displayPot        = alive;
            displayText       = $"魔法罐: 进行中 ({alive.DirName})";
            notifiedSpawnTime = -1;
            return;
        }

        Pot? lastSpawned = null;
        if (north.SpawnTime > 0)
            lastSpawned = north;
        if (south.SpawnTime > 0 && (lastSpawned == null || south.SpawnTime > lastSpawned.SpawnTime))
            lastSpawned = south;

        if (lastSpawned == null)
        {
            displayPot  = null;
            displayText = "魔法罐: 等待刷新";
            return;
        }

        var other    = ReferenceEquals(lastSpawned, north) ? south : north;
        var s        = lastSpawned.SpawnTime;
        var k        = now < s ? 0 : ((now - s) / Respawn) + 1;
        var nextTime = s + (k * Respawn);
        var nextPot  = k % 2 == 0 ? lastSpawned : other;

        ShowCountdown(now, nextTime, nextPot);
    }

    private void ShowCountdown(long now, long nextTime, Pot pot)
    {
        displayPot = pot;

        var remaining = nextTime - now;
        if (remaining <= 0)
        {
            displayText = $"魔法罐: 即将刷新 ({pot.DirName})";
            return;
        }

        var span = TimeSpan.FromSeconds(remaining);
        displayText = $"下个魔法罐 {span:mm\\:ss} ({pot.DirName})";

        if (notifiedSpawnTime != nextTime && remaining <= config.LeadSeconds)
        {
            Notify(pot, (int)Math.Ceiling(remaining / 60.0));
            notifiedSpawnTime = nextTime;
        }
    }

    private void ApplyDisplay()
    {
        if (entry != null)
        {
            if (config.DisplayMode == PotDisplayMode.DtrBar)
            {
                entry.Text  = displayText;
                entry.Shown = true;
            }
            else
                entry.Shown = false;
        }

        if (Overlay != null)
            Overlay.IsOpen = config.DisplayMode == PotDisplayMode.Overlay;
    }

    private void HideDisplay()
    {
        if (entry != null)
            entry.Shown = false;
        if (Overlay != null)
            Overlay.IsOpen = false;
    }

    private void OnDtrClick(DtrInteractionEvent _)
    {
        if (displayPot == null) return;
        OpenPotMap(displayPot);
    }

    private unsafe void OpenPotMap(Pot pot)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        var mapID = agent->CurrentMapId;
        agent->SelectedMapId = mapID;
        if (!agent->IsAgentActive())
            agent->Show();

        agent->SetFlagMapMarker(GameState.TerritoryType, mapID, pot.World);
        agent->OpenMap(mapID, GameState.TerritoryType, "魔法罐");
    }

    private unsafe void SetPotFlag(Pot pot)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->SetFlagMapMarker(GameState.TerritoryType, agent->CurrentMapId, pot.World);
    }

    private Pot? GetPot(ushort fateID)
    {
        foreach (var pot in pots)
        {
            if (pot.FateID == fateID)
                return pot;
        }

        return null;
    }

    #region 地图标记 - 逻辑

    private void OnAreaMapRefresh(AddonEvent type, AddonArgs args) => markersDirty = true;

    // 检测「撒娇罐」状态，更新最近埋藏宝箱坐标与自动切换
    private void UpdateLure()
    {
        if (!InOccultMapZone)
        {
            ClearLure();
            return;
        }

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var has = false;
        if (localPlayer != null)
        {
            foreach (var status in localPlayer.StatusList)
            {
                if (status.StatusID == LureStatusID)
                {
                    has = true;
                    break;
                }
            }
        }

        if (!has)
        {
            ClearLure();
            return;
        }

        lureActive = true;
        cofferPos  = OccultData.NearestCoffer(localPlayer!.Position);

        if (config.AutoSwitchOnLure && savedMarkers == null)
        {
            savedMarkers   = currentMarkers;
            currentMarkers = config.AutoSwitchFlags;
            markersDirty   = true;
        }
    }

    private void ClearLure()
    {
        lureActive = false;
        cofferPos  = Vector3.Zero;

        if (savedMarkers != null)
        {
            currentMarkers = savedMarkers.Value;
            savedMarkers   = null;
            markersDirty   = true;
        }
    }

    private unsafe void UpdateMapMarkers()
    {
        if (!InOccultMapZone) return;

        var agent = AgentMap.Instance();
        if (agent == null) return;

        if (markersDirty || placedMarkers != currentMarkers || placedMapID != agent->CurrentMapId)
            PlaceMapMarkers();
    }

    private unsafe void PlaceMapMarkers()
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->ResetMapMarkers();
        agent->ResetMiniMapMarkers();

        markersDirty  = false;
        placedMarkers = currentMarkers;
        placedMapID   = agent->CurrentMapId;

        if (!InOccultMapZone || currentMarkers == MarkerSet.None) return;

        if (currentMarkers.HasFlag(MarkerSet.BronzeTreasure))
            foreach (var (pos, tag) in OccultData.Treasures)
                if (tag == 1596) AddMarker(agent, pos, IconBronze);

        if (currentMarkers.HasFlag(MarkerSet.SilverTreasure))
            foreach (var (pos, tag) in OccultData.Treasures)
                if (tag == 1597) AddMarker(agent, pos, IconSilver);

        if (currentMarkers.HasFlag(MarkerSet.NorthPot))
            foreach (var pos in OccultData.NorthPots)
                AddMarker(agent, pos, IconGoldChest);

        if (currentMarkers.HasFlag(MarkerSet.SouthPot))
            foreach (var pos in OccultData.SouthPots)
                AddMarker(agent, pos, IconGoldChest);

        if (currentMarkers.HasFlag(MarkerSet.Reroll))
            foreach (var pos in OccultData.Rerolls)
                AddMarker(agent, pos, IconReroll);

        if (currentMarkers.HasFlag(MarkerSet.Bunny))
            foreach (var pos in OccultData.Bunnies)
                AddMarker(agent, pos, IconCarrot);
    }

    private static unsafe void AddMarker(AgentMap* agent, Vector3 pos, uint icon)
    {
        agent->AddMapMarker(pos, icon);
        agent->AddMiniMapMarker(pos, icon);
    }

    private unsafe void ClearMapMarkers()
    {
        // 仅在确实放置过标记时才复位，避免无谓地清掉其他来源的地图标记
        if (placedMarkers != MarkerSet.None)
        {
            var agent = AgentMap.Instance();
            if (agent != null)
            {
                agent->ResetMapMarkers();
                agent->ResetMiniMapMarkers();
            }
        }

        placedMarkers = MarkerSet.None;
        placedMapID   = 0;
        markersDirty  = false;
    }

    // 设置用户标记集合（携带撒娇罐时改保存的基准集合）
    private void SetUserMarkers(MarkerSet set)
    {
        config.DefaultMarkers = set;
        if (savedMarkers == null)
            currentMarkers = set;
        else
            savedMarkers = set;
        markersDirty = true;
        config.Save(this);
    }

    private void ToggleMarker(MarkerSet flag)
    {
        var set = savedMarkers ?? currentMarkers;
        SetUserMarkers(set.HasFlag(flag) ? set & ~flag : set | flag);
    }

    // 每帧绘制：埋藏宝箱圆圈 + 快速切换悬浮窗
    private void OnPostDraw()
    {
        if (!InOccultMapZone) return;

        DrawCofferCircle();
        DrawFastSwitcher();
    }

    private void DrawCofferCircle()
    {
        if (!config.DrawCofferCircle || !lureActive || cofferPos == Vector3.Zero) return;

        if (GameViewHelper.WorldToScreen(cofferPos, out var screen, out var inView) && inView)
            ImGui.GetForegroundDrawList()
                 .AddCircleFilled(screen, 8f * GlobalUIScale, ImGui.ColorConvertFloat4ToU32(config.CircleColor));
    }

    private unsafe void DrawFastSwitcher()
    {
        if (!config.ShowFastSwitcher) return;

        var agent = AgentMap.Instance();
        if (agent == null || agent->SelectedMapId != OccultMapID) return;

        var addon = (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName("AreaMap");
        if (addon == null || !addon->IsVisible || addon->RootNode == null) return;

        var scale  = addon->Scale;
        var height = addon->RootNode->Height * scale;
        var posX   = addon->X + (5f * scale);
        var posY   = config.SwitcherBelowMap
                         ? addon->Y + height
                         : addon->Y - (ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().WindowPadding.Y * 2f));

        ImGui.SetNextWindowPos(new Vector2(posX, posY), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.8f);

        if (ImGui.Begin("###OccultPotFastSwitcher", SwitcherFlags))
        {
            var locked = savedMarkers != null;
            using (ImRaii.Disabled(locked))
            {
                var active = savedMarkers ?? currentMarkers;
                for (var i = 0; i < SwitchButtons.Length; i++)
                {
                    var (label, flag) = SwitchButtons[i];
                    var on = active.HasFlag(flag);

                    if (on)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button,        SwitchActiveColor);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, SwitchActiveColor);
                    }

                    if (ImGui.Button($"{label}###Switch{flag}"))
                        ToggleMarker(flag);

                    if (on)
                        ImGui.PopStyleColor(2);

                    if (i != SwitchButtons.Length - 1)
                        ImGui.SameLine();
                }
            }

            if (locked && ImGui.IsWindowHovered())
                ImGui.SetTooltip("携带撒娇罐时自动标记进行中，暂不可手动切换");
        }

        ImGui.End();
    }

    #endregion

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TrackerAnonKey}");
        client.DefaultRequestHeaders.Add("Prefer",        "return=representation");
        client.DefaultRequestHeaders.Add("User-Agent",    "DailyRoutines-OccultPotNotifier");
        return client;
    }

    private void TrySyncOnline(long now)
    {
        if (syncInFlight) return;
        if (!TryBuildContext(out var context)) return;

        var refresh = hasOnlineData ? SyncRefreshSeconds : FastRetrySeconds;
        var due     = context.Fingerprint != lastFingerprint || now - lastSyncAt >= refresh;
        if (!due) return;

        lastFingerprint = context.Fingerprint;
        lastSyncAt      = now;
        syncInFlight    = true;
        _ = SyncAsync(context, now);
    }

    private bool TryBuildContext(out SyncContext context)
    {
        context = default;

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var dcID = localPlayer.CurrentWorld.Value.DataCenter.RowId;
        if (dcID == 0) return false;

        uint fateID    = 0;
        long bestEpoch = 0;
        foreach (var fate in DService.Instance().Fate)
        {
            if (!OccultFateIds.Contains(fate.FateId)) continue;
            if (fate.StartTimeEpoch <= 0)             continue;
            if (fate.StartTimeEpoch > bestEpoch)
            {
                bestEpoch = fate.StartTimeEpoch;
                fateID    = fate.FateId;
            }
        }

        if (fateID == 0) return false;

        context = new SyncContext
        {
            Fingerprint = ComputeHash(dcID, fateID, (int)bestEpoch),
            Datacenter  = (ushort)dcID,
            North       = PotObs.From(pots[0]),
            South       = PotObs.From(pots[1])
        };
        return true;
    }

    private static string ComputeHash(uint dcID, uint fateID, int timestamp)
    {
        Span<byte> buffer = stackalloc byte[12];
        BitConverter.TryWriteBytes(buffer[..4],  dcID);
        BitConverter.TryWriteBytes(buffer[4..8], fateID);
        BitConverter.TryWriteBytes(buffer[8..],  timestamp);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        var sb = new StringBuilder(64);
        foreach (var b in hash)
            sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private async Task SyncAsync(SyncContext context, long now)
    {
        try
        {
            var json = await Client.GetStringAsync($"{TrackerBaseURL}{TrackerTable}?last_fate=eq.{context.Fingerprint}");
            var rows = JsonConvert.DeserializeObject<TrackerRow[]>(json);

            if (rows is { Length: > 0 })
            {
                var row = rows[0];
                hasOnlineData = true;

                var shared = string.IsNullOrEmpty(row.PotHistory)
                                 ? null
                                 : JsonConvert.DeserializeObject<SharedPot[]>(row.PotHistory);
                if (shared != null)
                {
                    long ns = -1, nl = -1, ss = -1, sl = -1;
                    foreach (var sp in shared)
                    {
                        if (sp.FateID      == 1976) { ns = sp.SpawnTime; nl = sp.LastSeen; }
                        else if (sp.FateID == 1977) { ss = sp.SpawnTime; sl = sp.LastSeen; }
                    }

                    lock (syncLock)
                        pendingSync = (ns, nl, ss, sl);
                }

                await PatchPotHistoryAsync(row, context, now, shared);
            }
            else if (context.HasObservation && createdFingerprint != context.Fingerprint)
            {
                await CreateRowAsync(context);
                createdFingerprint = context.Fingerprint;
            }
        }
        catch
        {
        }
        finally
        {
            syncInFlight = false;
        }
    }

    private async Task PatchPotHistoryAsync(TrackerRow row, SyncContext context, long now, SharedPot[]? shared)
    {
        if (row.RowID <= 0) return;

        var changed = false;
        var north   = MergePot(1976, context.North, shared, ref changed);
        var south   = MergePot(1977, context.South, shared, ref changed);
        if (!changed) return;

        var body = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["pot_history"] = JsonConvert.SerializeObject(new[] { north, south }),
            ["last_update"] = now
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await Client.PatchAsync($"{TrackerBaseURL}{TrackerTable}?id=eq.{row.RowID}", content);
    }

    private async Task CreateRowAsync(SyncContext context)
    {
        var potHistory = JsonConvert.SerializeObject(new[]
        {
            UploadPot.From(1976, context.North),
            UploadPot.From(1977, context.South)
        });

        var body = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["version"]           = TrackerVersion,
            ["last_fate"]         = context.Fingerprint,
            ["tracker_type"]      = 1,
            ["datacenter"]        = context.Datacenter,
            ["encounter_history"] = "[]",
            ["fate_history"]      = "[]",
            ["pot_history"]       = potHistory
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await Client.PostAsync($"{TrackerBaseURL}{TrackerTable}", content);
    }

    private static UploadPot MergePot(uint fateID, PotObs local, SharedPot[]? shared, ref bool changed)
    {
        long spawn = -1, death = 0, lastSeen = -1;
        if (shared != null)
        {
            foreach (var sp in shared)
            {
                if (sp.FateID != fateID) continue;
                spawn    = sp.SpawnTime;
                death    = sp.DeathTime;
                lastSeen = sp.LastSeen;
                break;
            }
        }

        if (local.Observed && local.LastSeen > lastSeen)
        {
            spawn    = local.Spawn;
            death    = local.Death;
            lastSeen = local.LastSeen;
            changed  = true;
        }

        return new UploadPot { FateID = fateID, SpawnTime = spawn, DeathTime = death, LastSeen = lastSeen };
    }

    private void ApplyPendingSync()
    {
        (long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? data;
        lock (syncLock)
        {
            data        = pendingSync;
            pendingSync = null;
        }

        if (data == null) return;

        MergeSynced(pots[0], data.Value.NorthSpawn, data.Value.NorthSeen);
        MergeSynced(pots[1], data.Value.SouthSpawn, data.Value.SouthSeen);
    }

    private static void MergeSynced(Pot pot, long spawn, long lastSeen)
    {
        if (pot.Alive) return;
        if (lastSeen > pot.LastSeenAlive) pot.LastSeenAlive = lastSeen;
        if (spawn    > pot.SpawnTime)     pot.SpawnTime     = spawn;
    }

    private struct SyncContext
    {
        public string Fingerprint;
        public ushort Datacenter;
        public PotObs North;
        public PotObs South;

        public readonly bool HasObservation =>
            (North.Observed && North.Spawn > 0) || (South.Observed && South.Spawn > 0);
    }

    private readonly struct PotObs
    {
        public bool Observed { get; init; }
        public long Spawn    { get; init; }
        public long Death    { get; init; }
        public long LastSeen { get; init; }

        public static PotObs From(Pot pot) => new()
        {
            Observed = pot.LocallyObserved,
            Spawn    = pot.SpawnTime,
            Death    = pot.DeathTime,
            LastSeen = pot.LastSeenAlive
        };
    }

    private class TrackerRow
    {
        [JsonProperty("id")]
        public long RowID;

        [JsonProperty("pot_history")]
        public string PotHistory = string.Empty;
    }

    private struct SharedPot
    {
        [JsonProperty("fate_id")]
        public uint FateID;

        [JsonProperty("spawn_time")]
        public long SpawnTime;

        [JsonProperty("death_time")]
        public long DeathTime;

        [JsonProperty("last_seen")]
        public long LastSeen;
    }

    private class UploadPot
    {
        [JsonProperty("fate_id")]
        public uint FateID;

        [JsonProperty("spawn_time")]
        public long SpawnTime;

        [JsonProperty("death_time")]
        public long DeathTime;

        [JsonProperty("last_seen")]
        public long LastSeen;

        [JsonProperty("respawn_times")]
        public long[] RespawnTimes = [];

        public static UploadPot From(uint fateID, PotObs obs) => new()
        {
            FateID    = fateID,
            SpawnTime = obs.Observed ? obs.Spawn    : -1,
            DeathTime = obs.Observed ? obs.Death    : 0,
            LastSeen  = obs.Observed ? obs.LastSeen : -1
        };
    }

    private enum PotDisplayMode
    {
        None,
        DtrBar,
        Overlay
    }

    [Flags]
    private enum MarkerSet : uint
    {
        None           = 0,
        BronzeTreasure = 1 << 0,
        SilverTreasure = 1 << 1,
        NorthPot       = 1 << 2,
        SouthPot       = 1 << 3,
        Reroll         = 1 << 4,
        Bunny          = 1 << 5
    }

    private sealed class Pot
    {
        public ushort  FateID;
        public Vector3 World;
        public string  DirName = string.Empty;

        public bool Alive;
        public long SpawnTime       = -1;
        public long DeathTime       = -1;
        public long LastSeenAlive   = -1;
        public bool LocallyObserved;

        public void Reset()
        {
            Alive           = false;
            SpawnTime       = -1;
            DeathTime       = -1;
            LastSeenAlive   = -1;
            LocallyObserved = false;
        }
    }

    private class Config : ModuleConfig
    {
        public PotDisplayMode DisplayMode      = PotDisplayMode.DtrBar;
        public bool           UseOnlineTracker = true;

        public bool            SendTTS          = true;
        public bool            SendNotification = true;
        public bool            SendChat;
        public HashSet<string> ChatCommands     = ["/p"];
        public int             LeadSeconds      = 300;

        // 史官功能
        public bool   EnableArchivist          = false;
        public string ArchivistRegex           = "lw罐|史官";
        public int    ArchivistCooldownSeconds = 60;

        // 地图标记
        public MarkerSet DefaultMarkers   = MarkerSet.None;
        public bool      ShowFastSwitcher = true;
        public bool      SwitcherBelowMap;
        public bool      AutoSwitchOnLure = true;
        public MarkerSet AutoSwitchFlags  = MarkerSet.NorthPot | MarkerSet.SouthPot | MarkerSet.Reroll;
        public bool      DrawCofferCircle = true;
        public Vector4   CircleColor      = new(1f, 0.85f, 0.2f, 1f);
    }

    // 新月岛（南方海角 territory 1252）点位数据，移植自 Infiziert90/EurekaTrackerAutoPopper
    private static class OccultData
    {
        private const float CofferRange = 80f;

        // 宝箱：tag 1596 = 青铜, 1597 = 白银
        public static readonly (Vector3 Pos, uint Tag)[] Treasures =
        [
            (new(-283.98572f, 115.983765f, 377.03516f), 1597u),
            (new(277.7904f, 103.77649f, 241.90125f), 1596u),
            (new(-401.66327f, 85.03845f, 332.5398f), 1596u),
            (new(-372.67108f, 74.99805f, 527.4281f), 1596u),
            (new(609.61304f, 107.98804f, 117.2655f), 1596u),
            (new(256.1532f, 73.16687f, 492.3628f), 1596u),
            (new(870.6644f, 95.68933f, -388.35742f), 1596u),
            (new(-825.1621f, 2.9754639f, -832.2728f), 1597u),
            (new(697.322f, 69.99304f, 597.9247f), 1597u),
            (new(666.5292f, 79.11792f, -480.36932f), 1596u),
            (new(-444.11383f, 90.684326f, 26.230225f), 1596u),
            (new(642.96936f, 69.99304f, 407.79736f), 1596u),
            (new(-645.68555f, 202.99072f, 710.17017f), 1597u),
            (new(779.0187f, 96.08594f, -256.2448f), 1596u),
            (new(-118.97461f, 4.989685f, -708.4612f), 1596u),
            (new(726.28357f, 108.140625f, -67.91791f), 1596u),
            (new(596.45984f, 70.29822f, 622.76636f), 1596u),
            (new(294.8805f, 56.076904f, 640.2228f), 1596u),
            (new(-491.02008f, 2.9754639f, -529.59485f), 1596u),
            (new(770.7484f, 107.98804f, -143.5722f), 1597u),
            (new(471.18323f, 70.29822f, 530.022f), 1596u),
            (new(788.8761f, 120.378296f, 109.391846f), 1596u),
            (new(-648.0049f, 74.99805f, 403.95203f), 1596u),
            (new(55.283447f, 111.31445f, -289.0822f), 1596u),
            (new(-487.11377f, 98.527466f, -205.46277f), 1596u),
            (new(354.1161f, 95.65869f, -288.92963f), 1596u),
            (new(35.721313f, 65.11023f, 648.9509f), 1596u),
            (new(-197.19238f, 74.906494f, 618.3412f), 1596u),
            (new(-729.427f, 4.989685f, -724.81885f), 1596u),
            (new(433.70715f, 70.29822f, 683.52783f), 1596u),
            (new(517.7539f, 67.88733f, 236.1333f), 1597u),
            (new(-756.8322f, 76.55444f, 97.3678f), 1596u),
            (new(475.73047f, 95.994385f, -87.08331f), 1596u),
            (new(-661.7075f, 2.9754639f, -579.4919f), 1596u),
            (new(-884.123f, 3.7994385f, -682.0325f), 1596u),
            (new(-343.16016f, 52.32312f, -382.1317f), 1596u),
            (new(-550.13354f, 106.98096f, 627.74084f), 1596u),
            (new(-158.64807f, 98.61902f, -132.73828f), 1596u),
            (new(-729.9153f, 116.53308f, -79.05707f), 1596u),
            (new(142.1073f, 16.403442f, -574.0597f), 1596u),
            (new(-451.6823f, 2.9754639f, -775.5703f), 1596u),
            (new(-225.02484f, 74.99805f, 804.9896f), 1596u),
            (new(-856.9619f, 68.833374f, -93.15637f), 1596u),
            (new(-682.7955f, 135.60681f, -195.26971f), 1597u),
            (new(835.08044f, 69.99304f, 699.09204f), 1596u),
            (new(-140.45929f, 22.354431f, -414.2672f), 1596u),
            (new(140.97803f, 55.98523f, 770.99243f), 1596u),
            (new(8.987488f, 103.196655f, 426.96265f), 1596u),
            (new(386.92297f, 96.787964f, -451.37714f), 1596u),
            (new(-676.41724f, 170.9773f, 640.37524f), 1596u),
            (new(245.59387f, 109.11719f, -18.173523f), 1596u),
            (new(826.688f, 121.99585f, 434.9889f), 1596u),
            (new(-713.80176f, 62.05847f, 192.61462f), 1596u),
            (new(-25.68097f, 102.22009f, 150.16394f), 1596u),
            (new(-798.24524f, 105.57703f, -310.5669f), 1597u),
            (new(490.40967f, 62.45508f, -590.56995f), 1596u),
            (new(-256.88562f, 120.98877f, 125.078125f), 1596u),
            (new(-585.2903f, 4.989685f, -864.8356f), 1596u),
            (new(-716.1517f, 170.9773f, 794.4304f), 1596u),
            (new(-767.4525f, 115.61755f, -235.00421f), 1596u),
            (new(-600.27466f, 138.99438f, 802.6398f), 1596u),
            (new(617.08997f, 66.300415f, -703.8834f), 1596u),
            (new(-729.5491f, 106.98096f, 561.1504f), 1596u),
            (new(869.29126f, 109.97168f, 581.2008f), 1596u),
            (new(-394.88824f, 106.73682f, 175.43298f), 1596u),
            (new(-784.7562f, 138.99438f, 699.7634f), 1596u),
            (new(381.73486f, 22.171326f, -743.64844f), 1596u),
            (new(-680.5371f, 104.844604f, -354.78754f), 1596u)
        ];

        public static readonly Vector3[] NorthPots =
        [
            new(571.5841f, 51.451305f, -813.1642f),
            new(662.4388f, 120f, 161.1339f),
            new(606.4641f, 108.07402f, 184.8517f),
            new(-312.2778f, 103.19944f, -35.25348f),
            new(587.7039f, 78.8956f, -545.8168f),
            new(891.2597f, 120f, -20.672f),
            new(878.1131f, 108.28959f, -91.1057f),
            new(803.6609f, 95.99998f, -354.1809f),
            new(341.4413f, 95.99999f, 194.7507f),
            new(570.2421f, 64.66201f, 272.1734f),
            new(-216.372f, 5.4469404f, -510.1361f),
            new(684.4223f, 96.10129f, -165.4811f),
            new(-188.1745f, 2.999999f, -717.2005f),
            new(-476.3011f, 101.44228f, -86.69939f),
            new(80.19762f, 101.27949f, 391.2263f),
            new(-534.6993f, 2.999998f, -651.6244f),
            new(-165.2374f, 95.33837f, 437.4505f),
            new(330.8659f, 6.7168036f, -654.5339f),
            new(-333.3444f, 2.9999998f, -861.1722f),
            new(-313.2906f, 108.10962f, 70.76207f),
            new(-459.1735f, 93.57443f, 5.054043f),
            new(-54.69518f, 99.40573f, 405.0261f),
            new(-382.4396f, 109.30187f, -378.3482f),
            new(263.2559f, 100.38499f, 326.6834f),
            new(224.7233f, 68.7328f, 518.668f),
            new(19.73968f, 26.045855f, -420.977f),
            new(705.2716f, 68.143616f, 358.6714f),
            new(-660.5336f, 98f, -216.7666f),
            new(-324.2736f, 121f, 203.2017f),
            new(-386.5904f, -0.13994062f, -461.0976f)
        ];

        public static readonly Vector3[] SouthPots =
        [
            new(-195.4419f, 110.15342f, -287.8911f),
            new(74.73397f, 110.494316f, -394.1289f),
            new(-386.437f, 98.60658f, -221.7847f),
            new(-554.6146f, 99.01769f, -309.1231f),
            new(107.0611f, 105.699875f, 146.7059f),
            new(825.9521f, 70f, 772.4054f),
            new(-836.7586f, 106.999985f, 597.2944f),
            new(67.45271f, 69.477974f, 745.8658f),
            new(69.70596f, 111.56108f, -239.064f),
            new(301.8741f, 103.784424f, 70.59854f),
            new(-38.97946f, 102.073296f, -175.4589f),
            new(-60.72729f, 69.687035f, 828.4997f),
            new(17.60418f, 65.93209f, 674.6207f),
            new(393.2685f, 57.545956f, 844.6924f),
            new(393.0191f, 104f, -124.1651f),
            new(-798.7886f, 84.22545f, -4.822005f),
            new(440.8355f, 70.3f, 876.4097f),
            new(-734.1434f, 170.99998f, 683.7238f),
            new(423.3505f, 70.3f, 578.9013f),
            new(200.1241f, 56f, 624.2285f),
            new(-603.3457f, 139f, 858.6771f),
            new(-829.598f, 62.66814f, 66.82948f),
            new(-645.3027f, 135.69208f, -73.54771f),
            new(-836.1612f, 107f, 770.2822f),
            new(-676.6202f, 128.57442f, 1.531581f),
            new(-713.6796f, 203f, 710.08f),
            new(781.2514f, 70f, 560.0701f),
            new(-746.1318f, 172.00023f, 828.8809f),
            new(-730.5441f, 107.694275f, -371.4776f),
            new(-810.8279f, 114.053925f, -226.8324f)
        ];

        public static readonly Vector3[] Rerolls =
        [
            new(-676.4631f, 5f, -769.7955f),
            new(-823.9183f, 140.00032f, 677.6934f),
            new(-886.4718f, 107f, 712.4964f),
            new(-625.7809f, 171f, 810.8691f),
            new(-813.9943f, 5f, -663.3634f),
            new(-842.8967f, 75.76903f, -125.0559f),
            new(-680.0345f, 201f, 739.9117f),
            new(-793.0552f, 5f, -777.3126f),
            new(-708.6777f, 171f, 669.5714f),
            new(-718.0424f, 5f, -633.8791f),
            new(-868.8489f, 67.5054f, -59.44909f),
            new(-803.5182f, 3f, -602.7497f),
            new(-732.2048f, 139f, 828.8491f),
            new(-659.1158f, 12.198493f, -508.7968f),
            new(-785.997f, 162.39513f, 790.5948f),
            new(-840.8771f, 107.26465f, -250.273f),
            new(-708.687f, 141.16982f, -139.3283f),
            new(-796.66f, 114.15647f, -228.9318f),
            new(-776.6315f, 5f, -486.978f),
            new(-758.8058f, 127.66496f, -183.164f)
        ];

        public static readonly Vector3[] Bunnies =
        [
            new(283.6546f, 55.999996f, 587.3107f),
            new(-439.0463f, 115.82392f, 184.4665f),
            new(477.4074f, 96.10128f, 138.6543f),
            new(-743.601f, 96.39003f, 84.43998f),
            new(-575.6361f, 162.39511f, 668.7043f),
            new(865.0009f, 95.99958f, -214.6744f),
            new(248.9159f, 55.999996f, 791.1138f),
            new(-490.3187f, 3f, -741.0153f),
            new(720.4133f, 120f, 271.05f),
            new(466.2025f, 70.3f, 563.2519f),
            new(-701.8768f, 201f, 718.7181f),
            new(-273.0878f, 75f, 850.0336f),
            new(650.2321f, 108f, 141.1927f),
            new(827.2007f, 108f, -156.4444f),
            new(845.5334f, 98f, 777.4331f),
            new(772.3591f, 70.3f, 531.1259f),
            new(-84.73673f, 2.999999f, -796.0166f),
            new(-843.8602f, 83.657074f, -36.78173f),
            new(-727.8528f, 81.47683f, 328.9311f),
            new(-400.528f, 2.999999f, -518.3032f),
            new(-806.5123f, 107f, 887.6146f),
            new(-174.0473f, 121.00001f, 107.6488f),
            new(-771.6308f, 5f, -694.0016f),
            new(-710.266f, 3f, -451.5128f),
            new(-554.0244f, 110.698654f, -365.897f)
        ];

        // 携带撒娇罐时，返回 CofferRange 内最近的「罐 / 重抽」点位（即埋藏宝箱处）
        public static Vector3 NearestCoffer(Vector3 player)
        {
            var bestDist = CofferRange;
            var bestPos  = Vector3.Zero;

            foreach (var pos in NorthPots)
            {
                var dist = Vector3.Distance(player, pos);
                if (dist < bestDist) { bestDist = dist; bestPos = pos; }
            }

            foreach (var pos in SouthPots)
            {
                var dist = Vector3.Distance(player, pos);
                if (dist < bestDist) { bestDist = dist; bestPos = pos; }
            }

            foreach (var pos in Rerolls)
            {
                var dist = Vector3.Distance(player, pos);
                if (dist < bestDist) { bestDist = dist; bestPos = pos; }
            }

            return bestPos;
        }
    }
}
