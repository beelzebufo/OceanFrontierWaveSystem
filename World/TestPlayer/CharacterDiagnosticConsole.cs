using Godot;
using System.Text;

// Diagnostic history only; the motor never reads this node.
public partial class CharacterDiagnosticConsole : Node
{
    private const int Capacity = 12;
    private readonly string[] _events = new string[Capacity];
    private int _next;
    private int _count;
    private Label _output;
    private bool _carryWasBlocked;

    public override void _Ready()
    {
        _output = GetNode<Label>("../DebugHud/EventLabel");
        _output.Text = "Events (newest first)";
    }

    public void Observe(CharacterContactState contact, bool wasStable,
        CharacterGroundMotionTracker groundMotion, ulong frame)
    {
        string prefix = $"#{frame} ";
        if (contact.StepStatus != "not tried")
        {
            if (contact.SteppedUp)
                Add($"{prefix}STEP accepted rise={contact.StepRise:F3} | G={contact.SlopeAngle:F1}° | B{contact.BounceCount} R{contact.RecoveryCount}");
            else
                Add($"{prefix}STEP rejected: {contact.StepStatus} | B{contact.BounceCount} R{contact.RecoveryCount}");
        }

        if (contact.SnappedDown)
            Add($"{prefix}SNAP accepted drop={contact.SnapDistance:F3} | G={contact.SlopeAngle:F1}°");
        else if (wasStable && !contact.IsStable && contact.SnapStatus != "not tried")
            Add($"{prefix}SNAP rejected: {contact.SnapStatus}");

        if (!wasStable && contact.IsStable)
            Add($"{prefix}GROUND acquired G={contact.SlopeAngle:F1}°");
        else if (wasStable && !contact.IsStable)
            Add($"{prefix}GROUND lost");

        if (groundMotion.Transition != null)
            Add($"{prefix}PLATFORM {groundMotion.Transition}");
        if (groundMotion.CarryBlocked && !_carryWasBlocked)
            Add($"{prefix}PLATFORM carry blocked={groundMotion.CarryTravelled:F3}/{groundMotion.CarryRequested:F3}");
        _carryWasBlocked = groundMotion.CarryBlocked;
    }

    private void Add(string message)
    {
        _events[_next] = message;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
            _count++;

        var lines = new StringBuilder("Events (newest first)");
        for (int i = 0; i < _count; i++)
        {
            int index = (_next - 1 - i + Capacity) % Capacity;
            lines.Append('\n').Append(_events[index]);
        }
        _output.Text = lines.ToString();
    }
}
