using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Navigation;

/// <summary>What the sealed-interior joins need besides the meshes: the gathered geometry (to keep a bridge out of standing walls) and the agent's size.</summary>
internal sealed record SealedJoinInputs(ZoneGeometry Exterior, IReadOnlyList<ZoneGeometry> Interiors, float MaxClimb, float AgentHeight);

internal static partial class NavInteriors
{
    /// <summary>
    /// Engine choice behind <see cref="NavBuildSettings.JoinSealedInteriors"/>: how far (units, on X/Z) from a sealed interior's doorway the interior and exterior
    /// edges bridged through it may lie (docs/game/pathfinding.md "Sealed interiors").
    /// </summary>
    public const float SealedDoorwayReach = 48;
    /// <summary>
    /// Engine choice behind the switch: how far in height from a doorway's middle (a door's centre stands ~10..20 over its floor) a bridged edge may lie: the
    /// floor and the ground at the door, not an upper storey or a roof.
    /// </summary>
    public const float SealedDoorwayHeight = 30;
    /// <summary>Engine choice behind the switch: the longest bridge (units, between the middles of its two ends).</summary>
    public const float SealedLength = 48;
    /// <summary>
    /// Engine choice behind the switch: a bridge rising more than <see cref="JoinHeight"/> (a walker steps up to 12) is at least this long per unit it rises, so a
    /// walker (up to ~4 units a tick) stands on it between its ends and no tick climbs more than 12.
    /// </summary>
    public const float SealedRunPerRise = 0.5f;
    /// <summary>Engine choice behind the switch: how far apart in height a bridge's two ends may be (a flight of steps up to a door).</summary>
    public const float SealedRise = 20;
    /// <summary>Engine choice behind the switch: a bridge's width at each end (units), less where an edge is shorter, never under <see cref="SealedMinWidth"/>.</summary>
    public const float SealedWidth = 12, SealedMinWidth = 8;

    /// <summary>
    /// Bridges every interior that no polygon of the street reaches after the door, hull and ruin joins (a "sealed" one) to the exterior through its doorways,
    /// Meitou's own rule behind <see cref="NavBuildSettings.JoinSealedInteriors"/>: at each doorway (a door painter's centre; for a ruin, where the intact
    /// building's doors stood) the interior's open edge and the kept exterior's open edge that face it, within <see cref="SealedDoorwayReach"/> of it and
    /// <see cref="SealedRise"/> of each other in height, whose bridge is clear of collision between the climb and the agent's height (a ruin's boundary ring
    /// aside) and nearest the doorway, get a bridge polygon across, linked to both. Returns how many bridge polygons were added (their heights are their corners').
    /// </summary>
    static int OpenSealed(List<Vector3> v, List<int[]> polygons, List<int[]> neighbours, List<byte> areas, List<bool> kept, List<List<int>?> links, List<int> doorOf,
        int exteriorCount, List<int> offsets, IReadOnlyList<InteriorMesh> interiors, SealedJoinInputs inputs, Action<string>? log)
    {
        var reached = new bool[polygons.Count];
        var queue = new Queue<int>();
        for (int p = 0; p < exteriorCount; p++) if (kept[p] && areas[p] != NavArea.Null) { reached[p] = true; queue.Enqueue(p); }
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (int n in neighbours[p]) if (n >= 0 && !reached[n] && areas[n] != NavArea.Null) { reached[n] = true; queue.Enqueue(n); }
            if (links[p] is { } l) for (int i = 1; i < l.Count; i += 2) if (!reached[l[i]] && areas[l[i]] != NavArea.Null) { reached[l[i]] = true; queue.Enqueue(l[i]); }
        }

        bool Free(int p, int e) => neighbours[p][e] < 0 && !NavGeometry.HasLink(CollectionsMarshal.AsSpan(links[p]), e);
        var outside = new List<(int, int)>();
        for (int q = 0; q < exteriorCount; q++)
            if (kept[q] && areas[q] != NavArea.Null)
                for (int f = 0; f < polygons[q].Length; f++) if (Free(q, f)) outside.Add((q, f));
        int added = 0;
        for (int k = 0; k < interiors.Count; k++)
        {
            int first = offsets[k], count = interiors[k].Mesh.PolygonCount;
            if (Enumerable.Range(first, count).Any(p => reached[p])) continue;
            string name = interiors[k].Key ?? $"interior {k}";
            var geometry = inputs.Interiors[k];
            var doorways = geometry.RuinDoorways ?? [.. geometry.Painters.Select(d => new Vector3(d.Polygon.Average(c => c.X), (d.YMin + d.YMax) / 2, d.Polygon.Average(c => c.Y)))];
            log?.Invoke($"    sealed {name}{(interiors[k].Ruin ? " (ruin)" : "")}: {doorways.Count} doorways");
            var inside = new List<(int, int)>();
            for (int p = first; p < first + count; p++)
                if (areas[p] != NavArea.Null)
                    for (int e = 0; e < polygons[p].Length; e++) if (Free(p, e)) inside.Add((p, e));
            foreach (var door in doorways)
            {
                var near = inside.Select(c => Toward(c, door)).Where(c => c is not null).Select(c => c!.Value).ToList();
                var far = outside.Select(c => Toward(c, door)).Where(c => c is not null).Select(c => c!.Value).ToList();
                Bridge? best = null;
                int tried = 0, blocked = 0;
                foreach (var a in near)
                    foreach (var b in far)
                    {
                        if (MathF.Abs(a.Y - b.Y) > SealedRise || best is { } current && a.Distance + b.Distance >= current.Distance) continue;
                        if (Quad(a, b) is not { } bridge) continue;
                        tried++;
                        if (Blocked(bridge, geometry)) { blocked++; continue; }
                        best = bridge;
                    }
                if (best is not { } c)
                {
                    log?.Invoke($"      doorway {door.X:0},{door.Y:0},{door.Z:0}: no bridge ({near.Count} interior and {far.Count} exterior edges face it, {tried} bridges tried, {blocked} blocked)");
                    continue;
                }
                int b0 = polygons.Count, vo = v.Count;
                v.AddRange([c.I1, c.I0, c.E1, c.E0]);
                polygons.Add([vo, vo + 1, vo + 2, vo + 3]);
                neighbours.Add([-1, -1, -1, -1]);
                areas.Add(NavArea.Ground);
                kept.Add(false);
                links.Add([0, c.P, 2, c.Q]);
                doorOf.Add(-1);
                (links[c.P] ??= []).AddRange([c.E, b0]);
                (links[c.Q] ??= []).AddRange([c.F, b0]);
                added++;
                log?.Invoke($"      doorway {door.X:0},{door.Y:0},{door.Z:0}: bridge {b0} from interior polygon {c.P - first} edge {c.E} ({c.I0.X:0},{c.I0.Y:0},{c.I0.Z:0}) to exterior {c.Q} edge {c.F} ({c.E0.X:0},{c.E0.Y:0},{c.E0.Z:0}), {c.Distance:0.0} through the doorway");
            }
        }
        return added;

        // An open edge facing the doorway (the doorway on its outer side) within reach: the part of it nearest the doorway, at most SealedWidth long.
        Side? Toward((int P, int E) c, Vector3 door)
        {
            var poly = polygons[c.P];
            var a0 = v[poly[c.E]]; var a1 = v[poly[(c.E + 1) % poly.Length]];
            var d = new Vector2(a1.X - a0.X, a1.Z - a0.Z);
            float len = d.Length();
            if (len < SealedMinWidth) return null;
            d /= len;
            float t = Math.Clamp((door.X - a0.X) * d.X + (door.Z - a0.Z) * d.Y, 0, len);
            var at = Vector3.Lerp(a0, a1, t / len);
            float distance = Vector2.Distance(new(at.X, at.Z), new(door.X, door.Z));
            if (distance > SealedDoorwayReach || MathF.Abs(at.Y - door.Y) > SealedDoorwayHeight) return null;
            // Outward of a counter-clockwise polygon's edge is its right: (d.Y, -d.X).
            if ((door.X - at.X) * d.Y - (door.Z - at.Z) * d.X < -JoinDistance) return null;
            float half = MathF.Min(SealedWidth, len) / 2;
            float lo = Math.Clamp(t - half, 0, len - 2 * half);
            return new Side(c.P, c.E, Vector3.Lerp(a0, a1, lo / len), Vector3.Lerp(a0, a1, (lo + 2 * half) / len), distance, at.Y);
        }

        // The bridge between two facing parts: interior part I0 I1 and exterior part E0 E1, each in its polygon's winding, joined as I1 I0 E1 E0; null unless convex.
        Bridge? Quad(Side a, Side b)
        {
            float length = Vector2.Distance(new((a.A0.X + a.A1.X) / 2, (a.A0.Z + a.A1.Z) / 2), new((b.A0.X + b.A1.X) / 2, (b.A0.Z + b.A1.Z) / 2));
            float rise = MathF.Abs(a.Y - b.Y);
            if (length > SealedLength || rise > JoinHeight && length < SealedRunPerRise * rise) return null;
            Span<Vector3> q = [a.A1, a.A0, b.A1, b.A0];
            for (int i = 0; i < 4; i++)
            {
                var p0 = q[i]; var p1 = q[(i + 1) % 4]; var p2 = q[(i + 2) % 4];
                if ((p1.X - p0.X) * (p2.Z - p1.Z) - (p1.Z - p0.Z) * (p2.X - p1.X) < 1e-3f) return null;
            }
            return new Bridge(a.A0, a.A1, b.A0, b.A1, a.Distance + b.Distance, a.P, a.E, b.P, b.E);
        }

        // Whether a wall stands across the bridge between the climb and the agent's height over it: rays along both sides and the middle,
        // at three heights, against the exterior's triangles and the interior's (not a ruin's boundary ring, which the bridge crosses by design).
        bool Blocked(Bridge c, ZoneGeometry interior)
        {
            var lo = Vector2.Min(Vector2.Min(new(c.I0.X, c.I0.Z), new(c.I1.X, c.I1.Z)), Vector2.Min(new(c.E0.X, c.E0.Z), new(c.E1.X, c.E1.Z)));
            var hi = Vector2.Max(Vector2.Max(new(c.I0.X, c.I0.Z), new(c.I1.X, c.I1.Z)), Vector2.Max(new(c.E0.X, c.E0.Z), new(c.E1.X, c.E1.Z)));
            // A face whose top stays within the climb over the higher end is the riser of the step the bridge takes, not a wall.
            float yLo = MathF.Max(MathF.Max(c.I0.Y, c.I1.Y), MathF.Max(c.E0.Y, c.E1.Y)) + inputs.MaxClimb, yHi = MathF.Max(MathF.Max(c.I0.Y, c.I1.Y), MathF.Max(c.E0.Y, c.E1.Y)) + inputs.AgentHeight;
            var rays = new List<(Vector3, Vector3)>();
            foreach (float s in (ReadOnlySpan<float>)[0.1f, 0.5f, 0.9f])
            {
                // I0 faces E1 and I1 faces E0 across the bridge.
                var a = Vector3.Lerp(c.I0, c.I1, s); var b = Vector3.Lerp(c.E1, c.E0, s);
                foreach (float rise in (ReadOnlySpan<float>)[inputs.MaxClimb + 1, inputs.AgentHeight / 2, inputs.AgentHeight - 1])
                    rays.Add((a + new Vector3(0, rise, 0), b + new Vector3(0, rise, 0)));
            }
            return Hits(inputs.Exterior, (0, 0)) || Hits(interior, interior.BoundaryTriangles);

            bool Hits(ZoneGeometry g, (int First, int Last) skip)
            {
                var vs = g.Vertices; var ix = g.Indices;
                for (int t = 0; t < g.TriangleCount; t++)
                {
                    if (t >= skip.First && t < skip.Last) continue;
                    int i0 = ix[t * 3] * 3, i1 = ix[t * 3 + 1] * 3, i2 = ix[t * 3 + 2] * 3;
                    var p0 = new Vector3(vs[i0], vs[i0 + 1], vs[i0 + 2]); var p1 = new Vector3(vs[i1], vs[i1 + 1], vs[i1 + 2]); var p2 = new Vector3(vs[i2], vs[i2 + 1], vs[i2 + 2]);
                    if (MathF.Max(p0.X, MathF.Max(p1.X, p2.X)) < lo.X || MathF.Min(p0.X, MathF.Min(p1.X, p2.X)) > hi.X) continue;
                    if (MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z)) < lo.Y || MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z)) > hi.Y) continue;
                    if (MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y)) < yLo || MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)) > yHi) continue;
                    // Only walls: a floor, the ground or the water a bridge rises or drops to is no obstacle.
                    var normal = Vector3.Cross(p1 - p0, p2 - p0);
                    if (MathF.Abs(normal.Y) >= WallNormalY * normal.Length()) continue;
                    foreach (var (a, b) in rays) if (SegmentHitsTriangle(a, b, p0, p1, p2)) return true;
                }
                return false;
            }
        }
    }

    /// <summary>A triangle steeper than 45° (its normal's Y under this) is a wall to the bridges (engine choice).</summary>
    const float WallNormalY = 0.7071f;

    /// <summary>The part A0 A1 (in its polygon's winding) of open edge (P, E) nearest a doorway, <paramref name="Distance"/> from it (on X/Z), at height <paramref name="Y"/> there.</summary>
    readonly record struct Side(int P, int E, Vector3 A0, Vector3 A1, float Distance, float Y);

    /// <summary>
    /// A bridge between interior edge (P, E), part I0 I1, and exterior edge (Q, F), part E0 E1 (each in its polygon's winding); its polygon is I1 I0 E1 E0
    /// (counter-clockwise), edge 0 on the interior's edge, edge 2 on the exterior's. <paramref name="Distance"/>: both parts' distances from the doorway.
    /// </summary>
    readonly record struct Bridge(Vector3 I0, Vector3 I1, Vector3 E0, Vector3 E1, float Distance, int P, int E, int Q, int F);

    /// <summary>Whether segment a b crosses triangle p0 p1 p2 (Möller–Trumbore).</summary>
    static bool SegmentHitsTriangle(Vector3 a, Vector3 b, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        var dir = b - a;
        var e1 = p1 - p0; var e2 = p2 - p0;
        var h = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-6f) return false;
        float f = 1 / det;
        var s = a - p0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float w = f * Vector3.Dot(dir, q);
        if (w < 0 || u + w > 1) return false;
        float t = f * Vector3.Dot(e2, q);
        return t >= 0 && t <= 1;
    }
}
