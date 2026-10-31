namespace VivantValley.Services;

/// <summary>Release-only activation, with irreversible drag/multi-touch cancellation.</summary>
public sealed class TouchTapGesture
{
    public bool IsDown { get; private set; }
    public bool Dragged { get; private set; }
    private float startX, startY, previousY;
    public void Press(float x, float y)
    {
        if (IsDown) return;
        IsDown = true; Dragged = false;
        startX = x; startY = previousY = y;
    }
    public float Move(float x, float y, float slop = 14)
    {
        if (!IsDown) return 0;
        if ((x - startX) * (x - startX) + (y - startY) * (y - startY) > slop * slop) Dragged = true;
        float amount = Dragged ? previousY - y : 0;
        previousY = y;
        return amount;
    }
    public bool Release(float x, float y, float slop = 14)
    {
        if (!IsDown) return false;
        Move(x, y, slop);
        IsDown = false;
        return !Dragged;
    }
    public void Cancel() { IsDown = false; Dragged = true; }
}
