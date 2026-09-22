using Avalonia;
using FfxMap1;

namespace FfxLab;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // headless render harness: --render-test <geom> <tex> <out.png> [W]
        if (args.Length >= 3 && args[0] == "--render-test")
            return RenderTest.RunMap(args[1], args[2], args[3],
                args.Length > 4 ? int.Parse(args[4]) : 800);
        if (args.Length >= 2 && args[0] == "--render-actor")
            return RenderTest.RunActor(args[1], args[2],
                args.Length > 3 ? int.Parse(args[3]) : 800);
        // --render-anim <actor.bin> <anim.erl> <animId hex|auto> <t> <out.png>
        if (args.Length >= 5 && args[0] == "--render-anim")
            return RenderTest.RunActorAnim(args[1], args[2],
                args[3] == "auto" ? -1 : Convert.ToInt32(args[3], 16),
                float.Parse(args[4]), args[5], args.Length > 6 ? int.Parse(args[6]) : 800);
        // --render-field <geom 13> <tex 13> <ev 0c> <out.png> [W]
        if (args.Length >= 5 && args[0] == "--render-field")
            return RenderTest.RunField(args[1], args[2], args[3],
                args[4], args.Length > 5 ? int.Parse(args[5]) : 800);
        // --render-enc <lists 0d> <enc 0e> <arenaGeom> <arenaTex> <out.png> [W]
        if (args.Length >= 6 && args[0] == "--render-enc")
            return RenderTest.RunEncounter(args[1], args[2], args[3], args[4],
                args[5], args.Length > 6 ? int.Parse(args[6]) : 800);
        // --render-magic <11-bin> <index> <warm> <out.png> [W]
        if (args.Length >= 5 && args[0] == "--render-magic")
            return RenderTest.RunMagic(args[1], int.Parse(args[2]),
                int.Parse(args[3]), args[4], args.Length > 5 ? int.Parse(args[5]) : 800);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
