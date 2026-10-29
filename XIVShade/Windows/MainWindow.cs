using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XIVShade.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly PostProcessor? postProcessor;

    public MainWindow(Configuration configuration, PostProcessor? postProcessor)
        : base("XIVShade###XIVShadeMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 100),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.configuration = configuration;
        this.postProcessor = postProcessor;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (postProcessor == null)
        {
            ImGui.Text("Post-processing unavailable, see /xllog.");
            return;
        }

        var enabled = postProcessor.Enabled;
        if (ImGui.Checkbox("Test effect (greyscale)", ref enabled))
            postProcessor.Enabled = enabled;

        var includeUi = configuration.IncludeUi;
        if (ImGui.Checkbox("Include UI", ref includeUi))
        {
            configuration.IncludeUi = includeUi;
            postProcessor.IncludeUi = includeUi;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Apply effects after the UI is drawn. Off: only the 3D scene is affected.");
    }
}
