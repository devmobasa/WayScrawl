using global::Wayland;
using global::Wayland.SourceGenerator;
using WayScrawl.Wayland.Interop;
using WlrVirtualPointerUnstable;

namespace WayScrawl.Wayland;

// Test driver: injects pointer input through zwlr_virtual_pointer_v1 on one output.
// Use it only against an isolated compositor, never the owner's desktop.
public sealed class VirtualPointer : IDisposable, WlRegistry.IEvents
{
    private const uint BtnLeft = 0x110, BtnRight = 0x111;
    private readonly Display display;
    private readonly WlRegistry registry;
    private readonly Dictionary<uint, (WlOutput Output, Names Names)> outputs = [];
    private WlSeat? seat;
    private ZwlrVirtualPointerManagerV1? manager;
    private ZwlrVirtualPointerV1? pointer;
    private uint time;
    private readonly int width, height;

    // width/height: the output's logical size, the extent absolute positions are given in.
    public VirtualPointer(string displayPath, string outputName, int width, int height)
    {
        this.width = width;
        this.height = height;
        display = Display.Connect(displayPath);
        registry = display.GetRegistry();
        registry.Events = this;
        display.Roundtrip();
        display.Roundtrip();
        if (seat is null || manager is null) throw new WaylandException("The compositor lacks wl_seat or virtual pointers.");
        var output = outputs.Values.FirstOrDefault(o => o.Names.Name == outputName).Output
            ?? throw new WaylandException($"Output {outputName} was not found.");
        pointer = manager.CreateVirtualPointerWithOutput(seat, output);
        display.Roundtrip();
    }

    public void MoveTo(double x, double y)
    {
        pointer!.MotionAbsolute(++time, (uint)Math.Clamp(Math.Round(x), 0, width - 1),
            (uint)Math.Clamp(Math.Round(y), 0, height - 1), (uint)width, (uint)height);
        pointer.Frame();
        display.Roundtrip();
    }

    public void Press(bool right = false) => Button(right ? BtnRight : BtnLeft, WlPointer.ButtonStateEnum.Pressed);
    public void Release(bool right = false) => Button(right ? BtnRight : BtnLeft, WlPointer.ButtonStateEnum.Released);

    public void Drag(IReadOnlyList<(double X, double Y)> path)
    {
        MoveTo(path[0].X, path[0].Y);
        Press();
        foreach (var (x, y) in path.Skip(1)) MoveTo(x, y);
        Release();
    }

    private void Button(uint button, WlPointer.ButtonStateEnum state)
    {
        pointer!.Button(++time, button, state);
        pointer.Frame();
        display.Roundtrip();
    }

    public void OnGlobal(uint name, string @interface, uint version)
    {
        switch (@interface)
        {
            case "wl_seat" when seat is null: seat = registry.Bind(name, version, WlSeat.BindFactory); break;
            case "zwlr_virtual_pointer_manager_v1": manager = registry.Bind(name, version, ZwlrVirtualPointerManagerV1.BindFactory); break;
            case "wl_output":
                var output = registry.Bind(name, version, WlOutput.BindFactory);
                var names = new Names();
                output.Events = names;
                outputs[name] = (output, names);
                break;
        }
    }

    public void OnGlobalRemove(uint name) { }

    public void Dispose()
    {
        try
        {
            pointer?.Destroy();
            display.Roundtrip();
        }
        catch (WaylandException) { }
        display.Dispose();
    }

    private sealed class Names : WlOutput.IEvents
    {
        public string? Name { get; private set; }
        public void OnGeometry(int x, int y, int physicalWidth, int physicalHeight, WlOutput.SubpixelEnum subpixel,
            string make, string model, WlOutput.TransformEnum transform) { }
        public void OnMode(WlOutput.ModeEnum flags, int width, int height, int refresh) { }
        public void OnDone() { }
        public void OnScale(int factor) { }
        public void OnName(string name) => Name = name;
        public void OnDescription(string description) { }
    }
}
