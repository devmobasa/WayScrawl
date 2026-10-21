namespace WayScrawl.Core;

public static class InkMath
{
    public const int MaxPointsPerStroke = 4000;

    // Bounds point volume: drops sub-pixel moves, then subsamples evenly, always keeping
    // the first and last points so a stroke's extent and timing are preserved.
    public static Stroke Simplify(Stroke stroke, double minDistance = 0.75)
    {
        if (stroke.Points.Length <= 2) return stroke;
        var kept = new List<InkPoint> { stroke.Points[0] };
        for (int i = 1; i < stroke.Points.Length - 1; i++)
        {
            var last = kept[^1];
            double dx = stroke.Points[i].X - last.X, dy = stroke.Points[i].Y - last.Y;
            if (dx * dx + dy * dy >= minDistance * minDistance) kept.Add(stroke.Points[i]);
        }
        kept.Add(stroke.Points[^1]);
        if (kept.Count > MaxPointsPerStroke)
        {
            double step = (kept.Count - 1) / (double)(MaxPointsPerStroke - 1);
            kept = Enumerable.Range(0, MaxPointsPerStroke).Select(i => kept[(int)Math.Round(i * step)]).ToList();
        }
        return stroke with { Points = kept.ToArray() };
    }
}
