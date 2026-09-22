// Standalone viewport window — hosts a ViewportControl for a detached
// 3D view. The same control is embedded in the main shell's workspace.
using Avalonia.Controls;
using FfxMap1;

namespace FfxLab;

public class MapWindow : Window
{
    readonly ViewportControl _vp = new();

    public MapWindow(MapRenderer r, string title)
    {
        Title = title;
        Width = 1100; Height = 760;
        Background = (Avalonia.Media.IBrush?)Avalonia.Application.Current!
            .FindResource("LabBgPage") ?? Avalonia.Media.Brushes.Black;
        Content = _vp;
        _vp.SetContent(r);
        Closed += (s, e) => _vp.StopAnimation();
    }

    public void AttachAnimation(ActorBin actor, ActorAnim anim, int animId)
        => _vp.AttachAnimation(actor, anim, animId);
}
