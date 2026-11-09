using Godot;

namespace PvpDuel.Core.SplitMode;

/// <summary>保活泵：离开对局之后，本体不再有人泵联机传输，这个常驻 Node 接着泵。</summary>
/// <remarks>挂在场景树根上（不随对局销毁），关掉保活后也留着，成本只有一次空判断。</remarks>
internal sealed partial class SplitLinkPump : Node
{
    public override void _Process(double delta)
    {
        SplitLink.Pump(delta);
    }
}
