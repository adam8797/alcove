using System.Windows.Controls;

namespace Fixture.Wpf.RollLib;

public class RollMarker : TextBlock
{
#if V1
    public RollMarker() => Text = "RollLib 1";
#else
    public RollMarker() => Text = "RollLib 2";
#endif
}
