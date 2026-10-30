using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XIVShade.Windows;

namespace XIVShade;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    private const string CommandName = "/xivshade";

    public Configuration Configuration { get; init; }

    private readonly WindowSystem windowSystem = new("XIVShade");
    private readonly PostProcessor? postProcessor;
    private readonly Hotkeys? hotkeys;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        try
        {
            postProcessor = new PostProcessor(SigScanner, GameInterop, TextureProvider, Log, Configuration, PluginInterface.GetPluginConfigDirectory()) { Enabled = Configuration.Enabled, IncludeUi = Configuration.IncludeUi };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set up post-processing");
        }

        if (postProcessor != null)
            hotkeys = new Hotkeys(Configuration, postProcessor, Framework, KeyState, ToastGui);

        mainWindow = new MainWindow(Configuration, postProcessor, hotkeys);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the XIVShade window"
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        hotkeys?.Dispose();
        postProcessor?.Dispose();
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    private void ToggleMainUi() => mainWindow.Toggle();
}
