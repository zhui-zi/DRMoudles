using System;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using OmenTools;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoBmraiMaxDistance : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = "自动调整 bmrai 距离",
        Description = "根据当前职业自动调整 BossmodRebornCN 的 maxdistancetarget 设定。\n切换职业为坦克或近战时调整为 3，治疗或远程时调整为 15。",
        Category = ModuleCategory.Combat,
        Author = ["黑川启太"]
    };

    private class Config : ModuleConfig
    {
        public float MeleeDistance = 3.0f;
        public float RangedDistance = 15.0f;
        public string CommandFormat = "/bmrai maxdistancetarget {0}";
    }

    private Config config = null!;
    private uint lastJobId = 0;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        DService.Instance().Framework.Update += OnUpdate;
    }

    protected override void Uninit()
    {
        DService.Instance().Framework.Update -= OnUpdate;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("坦克/近战距离", ref config.MeleeDistance, 0.1f, 1.0f, "%.1f"))
            config.Save(this);

        ImGui.SetNextItemWidth(100f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("治疗/远程距离", ref config.RangedDistance, 0.1f, 1.0f, "%.1f"))
            config.Save(this);

        ImGui.SetNextItemWidth(300f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("指令格式", ref config.CommandFormat, 128))
            config.Save(this);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("使用 {0} 代表距离数值。默认: /bmrai maxdistancetarget {0}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        var classJob = localPlayer.ClassJob.Value;
        if (classJob.RowId == 0) return;

        if (classJob.RowId != lastJobId)
        {
            lastJobId = classJob.RowId;
            
            var role = classJob.Role; 
            
            if (role == 1 || role == 2) // Tank or Melee
            {
                ChatManager.Instance().SendMessage(string.Format(config.CommandFormat, config.MeleeDistance));
            }
            else if (role == 3 || role == 4) // Ranged or Healer
            {
                ChatManager.Instance().SendMessage(string.Format(config.CommandFormat, config.RangedDistance));
            }
        }
    }
}
