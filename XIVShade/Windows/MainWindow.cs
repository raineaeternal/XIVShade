using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XIVShade.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly PostProcessor? postProcessor;

    public MainWindow(PostProcessor? postProcessor)
        : base("XIVShade###XIVShadeMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 100),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

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
    }
}
