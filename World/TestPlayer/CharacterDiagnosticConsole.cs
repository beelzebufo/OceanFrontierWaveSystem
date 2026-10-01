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
    private string _lastEdgeOutcome;

    public override void _Ready()
    {
        _output = GetNode<Label>("../DebugHud/EventLabel");
        _output.Text = "Events (newest first)";
    }

    public void Observe(CharacterContactState contact, bool wasStable,
        bool wasSupportContinuity,
        CharacterGroundMotionTracker groundMotion, ulong frame, PlayerLocomotion locomotion)
    {
        string prefix = $"#{frame} ";
        if (contact.StepStatus != "not tried")
        {
            string probes = $"up={contact.StepRiseTravelled:F3}/{contact.StepRiseRequested:F3} " +
                $"clr={contact.StepClearanceTravelled:F3}/{contact.StepClearanceRequested:F3} " +
                $"fwd={contact.StepForwardTravelled:F3}/{contact.StepForwardRequested:F3} " +
                $"land={contact.StepLandingDelta:+0.000;-0.000;0.000}";
            if (contact.SteppedUp)
                Add($"{prefix}STEP accepted {probes} | G={contact.SlopeAngle:F1}° B{contact.BounceCount} R{contact.RecoveryCount}");
            else
                Add($"{prefix}STEP rejected: {contact.StepStatus} | {probes} B{contact.BounceCount} R{contact.RecoveryCount}");
        }

        if (contact.SnappedDown && contact.SnapSource == "primary")
            Add($"{prefix}SNAP accepted drop={contact.SnapDistance:F3} | G={contact.SlopeAngle:F1}°");
        else if (wasStable && !contact.IsStable && contact.SnapStatus != "not tried")
            Add($"{prefix}SNAP rejected: {contact.SnapStatus}");

        if (contact.SnapSecondaryAttempted)
        {
            string outcome = contact.SnapSecondaryValidation;
            if (outcome != _lastEdgeOutcome)
            {
                string candidate = contact.SnapSecondaryCandidateFound
                    ? $"candidate={contact.SnapSecondarySlope:F1}° drop={contact.SnapSecondaryDrop:F3}"
                    : "candidate=none";
                Add($"{prefix}SNAP edge-fallback {outcome} primary={contact.SnapPrimarySlope:F1}° " +
                    $"travel={contact.SnapPrimaryTravel:F3} {candidate} recovery={contact.SnapSecondaryRecovery}");
            }
            _lastEdgeOutcome = outcome;
        }
        else
            _lastEdgeOutcome = null;

        if (!wasSupportContinuity && contact.SupportContinuityActive)
            Add($"{prefix}SUPPORT continuity enter drop={contact.SupportContinuityCandidateDrop:F3} " +
                $"applied={contact.SupportContinuityAppliedDrop:F3} G={contact.SupportContinuitySlope:F1}°");
        else if (wasSupportContinuity && !contact.SupportContinuityActive)
            Add(contact.IsStable
                ? $"{prefix}SUPPORT continuity exit → capsule contact G={contact.SlopeAngle:F1}°"
                : locomotion.JumpAcceptedThisTick
                    ? $"{prefix}SUPPORT continuity exit → jump"
                    : $"{prefix}SUPPORT continuity lost → airborne ({contact.SnapStatus}; {contact.SnapSecondaryValidation})");

        if (!wasStable && contact.IsStable)
            Add($"{prefix}GROUND acquired G={contact.SlopeAngle:F1}°");
        else if (wasStable && !contact.IsStable)
            Add($"{prefix}GROUND lost");

        if (groundMotion.Transition != null)
            Add($"{prefix}PLATFORM {groundMotion.Transition}");
        if (groundMotion.CarryBlocked && !_carryWasBlocked)
            Add($"{prefix}PLATFORM carry blocked={groundMotion.CarryTravelled:F3}/{groundMotion.CarryRequested:F3}");
        _carryWasBlocked = groundMotion.CarryBlocked;

        if (locomotion.JumpBufferedThisTick)
            Add($"{prefix}JUMP buffered {locomotion.JumpBufferRemaining:F2}s");
        if (locomotion.JumpAcceptedThisTick)
        {
            Add($"{prefix}JUMP accepted v={locomotion.JumpLaunchVelocity}");
            if (locomotion.CoyoteJumpThisTick)
                Add($"{prefix}JUMP coyote from RID {locomotion.LastGroundRid}");
        }
        if (locomotion.GroundVelocityTransferredThisTick)
            Add($"{prefix}GROUND VELOCITY TRANSFER={locomotion.TransferredGroundVelocity} " +
                $"speed={locomotion.TransferredGroundVelocity.Length():F2}");
        if (locomotion.AirborneEnteredThisTick)
            Add($"{prefix}AIRBORNE entered");
        if (locomotion.LandedThisTick)
            Add($"{prefix}LAND speed={locomotion.LandingPreImpactVerticalSpeed:F2} " +
                $"G={contact.SlopeAngle:F1}° RID={contact.GroundColliderRid} ID={contact.GroundColliderObjectId}");
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
