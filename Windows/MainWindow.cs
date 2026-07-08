namespace RotationCoach;

using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System.Numerics;

/// <summary>
/// 旋转教练主窗口：实时/历史切换，展示分析报告与完整出手序列。
/// </summary>
public class MainWindow : Window
{
    private readonly CastTracker tracker;
    private readonly Configuration config;
    private readonly IDataManager dataManager;
    private int selectedIndex = 0;

    public MainWindow(CastTracker tracker, Configuration config, IDataManager dataManager)
        : base("旋转教练##RotationCoach")
    {
        this.tracker = tracker;
        this.config = config;
        this.dataManager = dataManager;
        this.Size = new Vector2(560, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var list = this.tracker.GetViewList();
        if (list.Count == 0)
        {
            ImGui.TextWrapped("还没有出手记录。进入战斗后，「旋转教练」会实时分析你实际按下的技能序列，指出发呆、卡手等问题。\n指令：/rc 打开本窗口；/rc reset 清空历史。");
            return;
        }

        if (this.selectedIndex >= list.Count) this.selectedIndex = 0;
        if (ImGui.BeginCombo("##sess", list[this.selectedIndex].Label))
        {
            for (int i = 0; i < list.Count; i++)
            {
                bool sel = i == this.selectedIndex;
                if (ImGui.Selectable(list[i].Label, sel)) this.selectedIndex = i;
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DrawReport(list[this.selectedIndex]);
    }

    private void DrawReport(BattleView view)
    {
        if (view.ActionLog == null || view.ActionLog.Count == 0)
        {
            ImGui.TextWrapped("这场没有出手记录。");
            return;
        }

        uint focus = view.ActionLog.Keys.First();
        string focusName = view.LocalName;
        double dur = view.DurationSec ?? 1;
        var rep = RotationAnalysis.Analyze(view.ActionLog, focus, dur, this.dataManager);

        ImGui.TextUnformatted($"对象：{focusName}　总出手 {rep.TotalActions}　GCD {rep.GcdCount}　能力 {rep.OgcdCount}　APM {rep.Apm:F1}");
        ImGui.SameLine();
        if (rep.GcdCadenceSec > 0)
            ImGui.TextUnformatted($"GCD节奏 ≈ {rep.GcdCadenceSec:F2}s");
        ImGui.TextUnformatted($"发呆 {rep.IdleSec:F1}s（{rep.IdlePct:F0}%）　发呆段 {rep.IdleGaps}　最长停顿 {rep.LongestIdleSec:F1}s");

        if (rep.Issues.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("待改进（按时间）：");
            foreach (var iss in rep.Issues)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), $"  • {iss.Detail}");
        }
        else
        {
            ImGui.Separator();
            ImGui.TextUnformatted("没发现明显发呆/卡手，节奏不错。");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("出手序列（实际按下的技能）：");
        if (ImGui.BeginChild("##RotSeq", new Vector2(-1, -1), true))
        {
            foreach (var a in rep.Sequence)
            {
                ImGui.TextUnformatted($"{(a.TimeMs / 1000.0):F1}s");
                ImGui.SameLine();
                ImGui.TextUnformatted(a.IsGcd ? "[GCD]" : "[能力]");
                ImGui.SameLine();
                ImGui.TextUnformatted(RotationAnalysis.ActionName(a.ActionId, this.dataManager));
            }
        }
        ImGui.EndChild();
    }
}
