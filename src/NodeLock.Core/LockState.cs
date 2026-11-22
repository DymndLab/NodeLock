namespace NodeLock.Core;

public enum Mode { Idle, Locked, Recovery }
public sealed class LockState
{
    public Mode Mode { get; private set; }
    public long? Target { get; private set; }
    public int Generation { get; private set; }
    public int PresentedGeneration { get; private set; } = -1;
    private int gesture = -1;
    private bool tabOwned;
    public bool Tab(bool pressed, bool released, bool repeat, bool eligible, bool plain, long? candidate)
    {
        if (released && tabOwned) { tabOwned = false; return true; }
        if (tabOwned) return true;
        if (!pressed || repeat || !eligible || !plain) return false;
        tabOwned = true;
        if (Mode != Mode.Idle) Change(Mode.Idle, null);
        else if (candidate.HasValue) Change(Mode.Locked, candidate);
        return true;
    }
    public void Invalidate(bool carried)
    {
        if (Mode != Mode.Idle) Change(carried ? Mode.Recovery : Mode.Idle, null);
    }
    public void FocusLost(bool carried) { tabOwned = false; Invalidate(carried); gesture = -1; }
    public void PreviewChanged() { Generation++; PresentedGeneration = -1; gesture = -1; }
    public void Presented() => PresentedGeneration = Generation;
    public void MouseDown() => gesture = PresentedGeneration == Generation && Mode != Mode.Recovery ? Generation : -1;
    public bool MouseUp()
    {
        bool valid = gesture == Generation && PresentedGeneration == Generation && Mode != Mode.Recovery;
        gesture = -1;
        return valid;
    }
    public void Placed() => Change(Mode.Idle, null);
    private void Change(Mode mode, long? target) { Mode = mode; Target = target; PreviewChanged(); }
}
