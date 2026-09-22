using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FfxLab;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            int scene = -1, magic = -1, actor = -1; string dados = "";
            string collapse = "";
            var a = desktop.Args ?? [];
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] == "--scene") int.TryParse(a[i + 1], out scene);
                if (a[i] == "--collapse") collapse = a[i + 1];
                if (a[i] == "--magic") int.TryParse(a[i + 1], out magic);
                if (a[i] == "--actor") int.TryParse(a[i + 1], out actor);
                if (a[i] == "--dados") dados = a[i + 1].StartsWith("--") ? "command.bin" : a[i + 1];
            }
            desktop.MainWindow = new MainWindow
            { InitialScene = scene, InitialCollapse = collapse, InitialMagic = magic, InitialDados = dados,
              InitialActor = actor };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
