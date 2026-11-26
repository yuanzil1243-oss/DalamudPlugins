using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void AttachTriggerCounters(FeatureTriggerCounters counters) => _module.AttachTriggerCounters(counters);
}
