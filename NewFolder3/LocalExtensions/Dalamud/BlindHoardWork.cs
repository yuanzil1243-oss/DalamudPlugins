using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

internal readonly partial record struct ObjectiveSite
{
    internal BlindHoardProbeGroup? BlindProbe { get; init; }
}

internal static class BlindHoardWork
{
    internal static void Merge(List<ObjectiveSite> sites, FloorKnowledge floor,
        Dictionary<string, double> probabilities, Func<int, Vector3, bool> canStand)
    {
        var sources = sites.Where(s => s.Kind == ObjectiveSiteKind.Candidate &&
                s.Status == ObjectiveSiteStatus.Open && !floor.IsProbeCompleted(ObjectiveSiteDriver.SiteKey(s)))
            .GroupBy(s => ObjectiveSiteDriver.SiteKey(s), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (sources.Count < 2) return;
        var groups = BlindHoardProbePlanner.Build(floor.Floor, sources.Select(kv =>
            new BlindHoardPoint(kv.Key, kv.Value.RoomIndex, kv.Value.Position)), canStand);
        foreach (var group in groups)
        {
            if (group.Points.Count < 2) continue;
            var keys = group.Points.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var first = sources[group.Points[0].Key];
            sites.RemoveAll(s => s.Kind == ObjectiveSiteKind.Candidate && keys.Contains(ObjectiveSiteDriver.SiteKey(s)));
            sites.Add(first with { Key = group.Key, Position = group.Position, BlindProbe = group,
                Value = group.Points.Sum(p => sources[p.Key].Value), Reason = $"blind-merged:{group.Points.Count}" });
            probabilities[group.Key] = group.Points.Sum(p => probabilities.GetValueOrDefault(p.Key));
        }
    }

    internal static void Complete(FloorKnowledge floor, SiteWorkToken token, ObjectiveSite site)
    {
        if (floor.Generation != token.FloorGeneration || site.Kind != ObjectiveSiteKind.Candidate ||
            ObjectiveSiteDriver.SiteKey(site) != token.Key) return;
        if (site.BlindProbe is not { } group)
            floor.MarkProbeCompleted(token.Key);
        else if (group.Floor == floor.Floor && group.Room == site.RoomIndex && group.Key == token.Key)
            foreach (var point in group.Points)
                floor.MarkProbeCompleted(point.Key);
    }
}
