namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void HandleBossCircleCommand(string[] args)
    {
        ThrowIfDisposed();
        _module.HandleHohBossCircleCommand(args);
    }
}
