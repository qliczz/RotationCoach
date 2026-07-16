namespace RotationCoach;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using Lumina.Excel.Sheets;

/// <summary>
/// 出手序列采集器（旋转教练核心）。
/// 钩取 ActionEffectHandler.Receive，只记录「本地玩家实际按下的技能」序列，
/// 自动按战斗分段。不采集伤害/运气/团队，保持轻量。
/// </summary>
public unsafe class CastTracker : IDisposable
{
    private const string ReceiveSig =
        "E8 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ?? 48 33 CC E8 ?? ?? ?? ?? 48 81 C4 00 05 00 00";

    private const int CombatGraceSeconds = 8;
    private const int MaxHistory = 50;
    private const int MaxEventsPerSession = 20000;

    private readonly IGameInteropProvider interop;
    private readonly ISigScanner sigScanner;
    private readonly IObjectTable objects;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly ICondition condition;
    private readonly string configDir;

    private Hook<ReceiveDelegate>? hook;
    private readonly object lockObj = new();

    private List<CastSession> history = new();
    private CastSession? current;
    private DateTime battleStart = DateTime.MinValue;
    private DateTime lastCombatTime = DateTime.MinValue;
    private Dictionary<uint, List<ActionCast>>? actionLog;
    private int diagCount;

    private delegate void ReceiveDelegate(
        uint casterEntityId,
        Character* casterPtr,
        System.Numerics.Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    public bool IsTrackingEnabled { get; set; } = true;
    public bool IsInBattle => this.current != null;

    public CastTracker(IGameInteropProvider interop, ISigScanner sigScanner,
        IObjectTable objects, IDataManager dataManager, IPluginLog log, ICondition condition, string configDir)
    {
        this.interop = interop;
        this.sigScanner = sigScanner;
        this.objects = objects;
        this.dataManager = dataManager;
        this.log = log;
        this.condition = condition;
        this.configDir = configDir;
        LoadAll();
    }

    public void Enable()
    {
        try
        {
            var addr = this.sigScanner.ScanText(ReceiveSig);
            this.hook = this.interop.HookFromAddress<ReceiveDelegate>(addr, Detour);
            this.hook.Enable();
            this.log.Info("[旋转教练] ActionEffectHandler.Receive 钩子已启用。");
        }
        catch (Exception ex)
        {
            this.hook = null;
            this.log.Error(ex, "[旋转教练] 无法安装 Receive 钩子，请检查 ReceiveSig（可能游戏版本更新导致签名失效）。");
        }
    }

    public void Dispose()
    {
        EndSession();
        this.hook?.Disable();
        this.hook?.Dispose();
        this.hook = null;
    }

    public void Tick()
    {
        bool inCombat;
        try { inCombat = this.condition[ConditionFlag.InCombat]; }
        catch { inCombat = false; }

        if (inCombat)
            this.lastCombatTime = DateTime.Now;

        if (!this.IsTrackingEnabled)
        {
            if (this.current != null)
                EndSession();
            return;
        }

        if (inCombat && this.current == null)
            StartSession();
        else if (!inCombat && this.current != null &&
                 (DateTime.Now - this.lastCombatTime).TotalSeconds > CombatGraceSeconds)
            EndSession();
    }

    private void StartSession()
    {
        string localName = ""; string jobName = ""; string zoneName = "";
        try
        {
            var lp = this.objects.LocalPlayer;
            if (lp != null)
            {
                localName = lp.Name.ToString();
                var ch = lp as ICharacter;
                if (ch != null)
                {
                    var sheet = this.dataManager.GetExcelSheet<ClassJob>();
                    if (sheet != null)
                    {
                        var row = sheet.GetRow((byte)ch.ClassJob.RowId);
                        jobName = row.Name.ToString();
                    }
                }
            }
            uint tid = DalamudApi.ClientState?.TerritoryType ?? 0;
            var z = this.dataManager.GetExcelSheet<TerritoryType>()?.GetRow(tid);
            if (z != null)
            {
                var r = z.Value;
                var zn = r.PlaceNameZone.Value.Name.ToString();
                var nm = r.Name.ToString();
                zoneName = !string.IsNullOrWhiteSpace(zn) ? zn
                           : (!string.IsNullOrWhiteSpace(nm) ? nm : $"区域 {tid}");
            }
        }
        catch { }

        this.current = new CastSession
        {
            Id = Guid.NewGuid().ToString("N"),
            LocalName = localName,
            JobName = jobName,
            ZoneName = zoneName,
        };
        this.battleStart = DateTime.Now;
        this.actionLog = this.current.ActionLog;
        this.log.Info($"[旋转教练] 开始记录：{jobName} @ {zoneName}");
    }

    private void EndSession()
    {
        var rec = this.current;
        this.current = null;
        if (rec == null)
            return;

        rec.DurationSec = Math.Max(1, (DateTime.Now - this.battleStart).TotalSeconds);
        lock (this.lockObj)
        {
            this.history.Insert(0, rec);
            while (this.history.Count > MaxHistory)
                this.history.RemoveAt(this.history.Count - 1);
        }
        SaveAll();
        this.log.Info($"[旋转教练] 战斗结束：{rec.JobName}，出手 {rec.CastCount} 次。");
    }

    private void Detour(uint casterEntityId, Character* casterPtr, System.Numerics.Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            if (header != null && this.diagCount < 5)
            {
                this.diagCount++;
                this.log.Info($"[旋转教练][诊断] Receive #{this.diagCount} caster={casterEntityId} action={header->ActionId}");
            }
            if (this.IsTrackingEnabled && header != null && this.current != null)
            {
                var local = this.objects.LocalPlayer;
                if (local != null && casterPtr != null && (nint)casterPtr == local.Address)
                    RecordCast(header);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[旋转教练] Receive 回调异常。");
        }
        finally
        {
            this.hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        }
    }

    /// <summary>
    /// 记录一次「出手」（无论是否造成伤害）。GCD 使用 Action 表的公共复唱组判定，
    /// 不再把动画锁长度误当成 GCD 复唱时间。
    /// </summary>
    private void RecordCast(ActionEffectHandler.Header* header)
    {
        if (header->ActionId == 0)
            return;
        var castMs = (uint)(DateTime.Now - this.battleStart).TotalMilliseconds;
        float animLock = header->AnimationLock;
        bool isGcd = false;
        try
        {
            var action = this.dataManager.GetExcelSheet<Action>()?.GetRow(header->ActionId);
            isGcd = action != null && action.Value.CooldownGroup == 58;
        }
        catch
        {
            // 表查询失败时保留该次出手，但不猜测它属于 GCD。
        }
        lock (this.lockObj)
        {
            if (!this.actionLog!.ContainsKey(0))
                this.actionLog[0] = new List<ActionCast>();
            var lst = this.actionLog[0];
            if (lst.Count < MaxEventsPerSession)
                lst.Add(new ActionCast { TimeMs = castMs, ActionId = header->ActionId, AnimLock = animLock, IsGcd = isGcd });
        }
    }

    // ---------- 查询 ----------

    public BattleView? GetCurrentView()
    {
        lock (this.lockObj)
            return this.current == null ? null : ToView(this.current, "（实时）", true);
    }

    public List<BattleView> GetViewList()
    {
        var list = new List<BattleView>();
        lock (this.lockObj)
        {
            if (this.current != null) list.Add(ToView(this.current, "（实时）", true));
            foreach (var h in this.history) list.Add(ToView(h, "", false));
        }
        return list;
    }

    public BattleView? GetSession(string id)
    {
        lock (this.lockObj)
        {
            var h = this.history.FirstOrDefault(x => x.Id == id);
            return h == null ? null : ToView(h, "", false);
        }
    }

    private BattleView ToView(CastSession s, string prefix, bool isCurrent)
    {
        string label = $"{prefix}{(string.IsNullOrEmpty(s.JobName) ? "未知职业" : s.JobName)} @ " +
                       $"{(string.IsNullOrEmpty(s.ZoneName) ? "未知区域" : s.ZoneName)} · {s.CastCount}出手";
        return new BattleView
        {
            Id = s.Id,
            Label = label,
            LocalName = s.LocalName,
            DurationSec = isCurrent ? Math.Max(1, (DateTime.Now - this.battleStart).TotalSeconds) : s.DurationSec,
            ActionLog = CloneActionLog(s.ActionLog),
        };
    }

    public void Reset()
    {
        EndSession();
        lock (this.lockObj) this.history.Clear();
        SaveAll();
    }

    // ---------- 持久化 ----------

    private void LoadAll()
    {
        try
        {
            var path = Path.Combine(this.configDir, "sessions.json");
            if (File.Exists(path))
            {
                var list = JsonConvert.DeserializeObject<List<CastSession>>(File.ReadAllText(path));
                if (list != null)
                {
                    foreach (var s in list)
                        if (string.IsNullOrEmpty(s.Id))
                            s.Id = Guid.NewGuid().ToString("N");
                    lock (this.lockObj) this.history = list;
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[旋转教练] 读取历史失败，将从空开始。");
        }
    }

    private void SaveAll()
    {
        try
        {
            Directory.CreateDirectory(this.configDir);
            List<CastSession> copy;
            lock (this.lockObj) copy = this.history.Select(CloneSession).ToList();
            var path = Path.Combine(this.configDir, "sessions.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(copy, Formatting.Indented));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[旋转教练] 保存历史失败。");
        }
    }

    private static CastSession CloneSession(CastSession source) => new()
    {
        Id = source.Id,
        LocalName = source.LocalName,
        JobName = source.JobName,
        ZoneName = source.ZoneName,
        DurationSec = source.DurationSec,
        ActionLog = CloneActionLog(source.ActionLog),
    };

    private static Dictionary<uint, List<ActionCast>> CloneActionLog(Dictionary<uint, List<ActionCast>> source)
        => source.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(cast => new ActionCast
            {
                TimeMs = cast.TimeMs,
                ActionId = cast.ActionId,
                AnimLock = cast.AnimLock,
                IsGcd = cast.IsGcd,
            }).ToList());
}
