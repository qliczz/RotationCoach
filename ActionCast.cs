namespace RotationCoach;

using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>一次出手记录（实际按下的技能）。</summary>
public sealed class ActionCast
{
    public uint TimeMs;
    public uint ActionId;
    public float AnimLock;   // 引擎下发的动画锁（秒，约 = 复唱时间；GCD≈2.5，能力≈0.6）
    public bool IsGcd;
}

/// <summary>一场已结束（或正在进行）的出手记录，可序列化落盘。</summary>
public sealed class CastSession
{
    public string Id = "";
    public string LocalName = "";
    public string JobName = "";
    public string ZoneName = "";
    public double? DurationSec;
    public Dictionary<uint, List<ActionCast>> ActionLog = new();

    [JsonIgnore]
    public int CastCount
    {
        get
        {
            int n = 0;
            foreach (var l in ActionLog.Values) n += l.Count;
            return n;
        }
    }
}

/// <summary>给 UI 用的只读视图（当前战斗或某场历史）。</summary>
public sealed class BattleView
{
    public string Id = "";
    public string Label = "";
    public string LocalName = "";
    public double? DurationSec;
    public Dictionary<uint, List<ActionCast>>? ActionLog;
}
