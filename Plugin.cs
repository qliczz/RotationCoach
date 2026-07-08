namespace RotationCoach;

using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;

/// <summary>
/// 插件入口。注入 Dalamud 服务，装配 CastTracker 与主窗口。
/// </summary>
public class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly CastTracker tracker;
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IObjectTable objects,
        IDataManager dataManager,
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        ICondition condition)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.framework = framework;
        this.log = log;

        DalamudApi.ObjectTable = objects;
        DalamudApi.DataManager = dataManager;
        DalamudApi.ClientState = clientState;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var configDir = pluginInterface.GetPluginConfigDirectory();
        this.tracker = new CastTracker(interop, sigScanner, objects, dataManager, log, condition, configDir);
        this.tracker.IsTrackingEnabled = this.config.EnableTracking;
        this.tracker.Enable();

        this.mainWindow = new MainWindow(this.tracker, this.config, dataManager);
        this.windowSystem = new WindowSystem("旋转教练");
        this.windowSystem.AddWindow(this.mainWindow);
        this.mainWindow.IsOpen = this.config.MainWindowOpen;

        this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += ToggleMain;

        this.framework.Update += this.OnFrameworkUpdate;

        commandManager.AddHandler("/rc", new CommandInfo(OnCommand)
        {
            HelpMessage = "旋转教练 —— 打开旋转分析窗口；/rc reset 清空历史出手记录",
        });

        this.log.Info("[旋转教练] 插件已加载。");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        this.tracker.Tick();
    }

    private void OnCommand(string command, string arguments)
    {
        var arg = arguments.Trim().ToLowerInvariant();
        if (arg == "reset")
        {
            this.tracker.Reset();
            return;
        }

        this.mainWindow.Toggle();
        SaveConfig();
    }

    private void ToggleMain()
    {
        this.mainWindow.Toggle();
        SaveConfig();
    }

    private void SaveConfig()
    {
        this.config.MainWindowOpen = this.mainWindow.IsOpen;
        this.pluginInterface.SavePluginConfig(this.config);
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.commandManager.RemoveHandler("/rc");
        this.pluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.windowSystem.RemoveAllWindows();
        this.tracker.Dispose();
    }
}
