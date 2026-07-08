namespace RotationCoach;

using Dalamud.Configuration;

/// <summary>
/// 插件配置，随 Dalamud 插件配置持久化（Newtonsoft JSON）。
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>配置版本，IPluginConfiguration 要求。</summary>
    public int Version { get; set; } = 1;

    /// <summary>是否启用出手序列采集。</summary>
    public bool EnableTracking { get; set; } = true;

    /// <summary>主窗口是否打开。</summary>
    public bool MainWindowOpen { get; set; } = true;
}
