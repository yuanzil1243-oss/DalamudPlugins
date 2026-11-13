using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class Pt99Arena
{
    public static readonly Vector2 Center = new(-600, -300);
    // Interior samples verified against the pinned reference encounter's light/dark collision polygons.
    private static readonly Vector2[] Light = [new(-604.3f,-287.6f), new(-588,-309.5f), new(-581.5f,-292.5f), new(-596.5f,-303.5f), new(-610.5f,-303.5f)];
    private static readonly Vector2[] Dark = [new(-612,-290.5f), new(-596,-312.5f), new(-618.5f,-307.5f), new(-589.5f,-296.5f), new(-603.5f,-296.5f)];
    public static IReadOnlyList<Vector2> Pools(Pt99Color color) => color switch
    {
        Pt99Color.Light => Light,
        Pt99Color.Dark => Dark,
        _ => Array.Empty<Vector2>()
    };
    public static bool Contains(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) &&
        Math.Abs(p.X - Center.X) <= 20 && Math.Abs(p.Y - Center.Y) <= 15;
}
