namespace DeepDungeon.Fsd.Core;

/// <summary>One ordering for movement and combat output; deferred optional targets cannot be reselected by assist.</summary>
public static class HohPetrifiedTargetPolicy
{
    public static T? Select<T>(IEnumerable<T> candidates, Func<T, bool> engaged, Func<T, bool> suppressed,
        Func<T, float> distanceSquared, Func<T, bool> preferred, Func<T, bool>? priority = null) where T : class => candidates
        .Where(e => engaged(e) || !suppressed(e))
        .OrderByDescending(e => priority?.Invoke(e) == true).ThenByDescending(engaged)
        .ThenByDescending(preferred).ThenBy(distanceSquared).FirstOrDefault();
}
