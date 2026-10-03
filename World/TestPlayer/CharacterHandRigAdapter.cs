using Godot;

// Presentation-only bridge from generic world-space brace poses to an optional arm rig.
// The Skeleton3D/TwoBoneIK3D assets and bone chains are supplied by a future rig.
public partial class CharacterHandRigAdapter : Node
{
    [Export] public CharacterHandBraceTargets BraceTargets { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public PlayerViewController View { get; set; }

    [Export] public TwoBoneIK3D LeftIK { get; set; }
    [Export] public Node3D LeftTarget { get; set; }
    [Export] public Node3D LeftPole { get; set; }
    [Export] public int LeftIKSettingIndex { get; set; }
    [Export] public Vector3 LeftTargetLocalOffset { get; set; }
    [Export] public Vector3 LeftTargetEulerDegrees { get; set; }
    // Components are yaw-local right, character Up, and yaw-local forward.
    [Export] public Vector3 LeftPoleYawOffset { get; set; } = new(-0.35f, -0.15f, -0.25f);

    [Export] public TwoBoneIK3D RightIK { get; set; }
    [Export] public Node3D RightTarget { get; set; }
    [Export] public Node3D RightPole { get; set; }
    [Export] public int RightIKSettingIndex { get; set; }
    [Export] public Vector3 RightTargetLocalOffset { get; set; }
    [Export] public Vector3 RightTargetEulerDegrees { get; set; }
    [Export] public Vector3 RightPoleYawOffset { get; set; } = new(0.35f, -0.15f, -0.25f);

    public float LeftSourceWeight => _left.SourceWeight;
    public float RightSourceWeight => _right.SourceWeight;
    public float LeftAppliedIKInfluence => _left.AppliedInfluence;
    public float RightAppliedIKInfluence => _right.AppliedInfluence;
    public bool LeftTargetTransformValid => _left.TargetValid;
    public bool RightTargetTransformValid => _right.TargetValid;
    public Vector3 LeftPolePosition => _left.PolePosition;
    public Vector3 RightPolePosition => _right.PolePosition;
    public bool LeftFullyWired => _leftWired;
    public bool RightFullyWired => _rightWired;
    public bool LeftActive => _leftWired && LeftIK.Active && _left.AppliedInfluence > 0f;
    public bool RightActive => _rightWired && RightIK.Active && _right.AppliedInfluence > 0f;

    private struct SideState
    {
        public float SourceWeight;
        public float AppliedInfluence;
        public bool TargetValid;
        public Vector3 PolePosition;
    }

    private SideState _left;
    private SideState _right;
    private bool _leftWired;
    private bool _rightWired;

    public override void _Ready()
    {
        BraceTargets ??= GetNodeOrNull<CharacterHandBraceTargets>("../CharacterHandBraceTargets");
        Motor ??= GetNodeOrNull<KinematicCharacterMotor>("../KinematicCharacterMotor");
        View ??= GetNodeOrNull<PlayerViewController>("../PlayerViewController");
        ConfigureRig();
    }

    // Call once after wiring a rig at runtime. Paths and IK settings are not rebuilt per frame.
    public void ConfigureRig()
    {
        _leftWired = ConfigureSide(LeftIK, LeftTarget, LeftPole, LeftIKSettingIndex);
        _rightWired = ConfigureSide(RightIK, RightTarget, RightPole, RightIKSettingIndex);
        _left = _right = default;
    }

    private static bool ConfigureSide(TwoBoneIK3D ik, Node3D target, Node3D pole,
        int settingIndex)
    {
        if (ik == null)
            return false;
        ik.Influence = 0f;
        if (target == null || pole == null || target == pole ||
            settingIndex < 0 || settingIndex >= ik.SettingCount ||
            !ik.IsInsideTree() || !target.IsInsideTree() || !pole.IsInsideTree() ||
            ik.GetTree() != target.GetTree() || ik.GetTree() != pole.GetTree() ||
            ik.GetParent() is not Skeleton3D skeleton)
            return false;
        int boneCount = skeleton.GetBoneCount();
        int root = ik.GetRootBone(settingIndex);
        int middle = ik.GetMiddleBone(settingIndex);
        int end = ik.GetEndBone(settingIndex);
        if (root < 0 || root >= boneCount || middle < 0 || middle >= boneCount ||
            end < 0 || end >= boneCount || root == middle || middle == end || root == end)
            return false;
        ik.SetTargetNode(settingIndex, ik.GetPathTo(target));
        ik.SetPoleNode(settingIndex, ik.GetPathTo(pole));
        return true;
    }

    public override void _Process(double delta)
    {
        HandBraceTarget left = BraceTargets?.LeftBrace ?? default;
        HandBraceTarget right = BraceTargets?.RightBrace ?? default;
        _left = ApplySide(left, LeftTarget, LeftPole, LeftIK, _leftWired,
            LeftTargetLocalOffset, LeftTargetEulerDegrees, LeftPoleYawOffset);
        _right = ApplySide(right, RightTarget, RightPole, RightIK, _rightWired,
            RightTargetLocalOffset, RightTargetEulerDegrees, RightPoleYawOffset);
    }

    private SideState ApplySide(HandBraceTarget brace, Node3D target, Node3D pole,
        TwoBoneIK3D ik, bool wired, Vector3 localOffset, Vector3 eulerDegrees,
        Vector3 poleYawOffset)
    {
        SideState state = default;
        state.SourceWeight = float.IsFinite(brace.Weight)
            ? Mathf.Clamp(brace.Weight, 0f, 1f) : 0f;
        if (!brace.IsPresented || state.SourceWeight <= 0f ||
            !brace.SmoothedTargetPosition.IsFinite() ||
            !brace.SmoothedTargetBasis.IsFinite() ||
            !localOffset.IsFinite() || !eulerDegrees.IsFinite() ||
            !poleYawOffset.IsFinite())
        {
            if (ik != null)
                ik.Influence = 0f;
            return state;
        }

        Basis surface = brace.SmoothedTargetBasis;
        float determinant = surface.Determinant();
        if (!float.IsFinite(determinant) || determinant < 0.000001f)
        {
            if (ik != null)
                ik.Influence = 0f;
            return state;
        }
        Vector3 radians = new(Mathf.DegToRad(eulerDegrees.X),
            Mathf.DegToRad(eulerDegrees.Y), Mathf.DegToRad(eulerDegrees.Z));
        Basis calibratedBasis = surface.Orthonormalized() * Basis.FromEuler(radians);
        Vector3 calibratedPosition = brace.SmoothedTargetPosition + surface * localOffset;
        if (!calibratedBasis.IsFinite() || !calibratedPosition.IsFinite())
        {
            if (ik != null)
                ik.Influence = 0f;
            return state;
        }
        state.TargetValid = true;
        if (target != null)
            target.GlobalTransform = new Transform3D(calibratedBasis, calibratedPosition);

        Vector3 up = Motor?.Up ?? Vector3.Zero;
        if (up.IsFinite() && up.LengthSquared() > 0.000001f && View != null)
        {
            up = up.Normalized();
            Vector3 right = View.GetPlanarRight(up);
            Vector3 forward = View.GetPlanarForward(up);
            Vector3 polePosition = calibratedPosition + right * poleYawOffset.X +
                up * poleYawOffset.Y + forward * poleYawOffset.Z;
            if (right.IsFinite() && forward.IsFinite() && polePosition.IsFinite())
            {
                state.PolePosition = polePosition;
                if (pole != null)
                    pole.GlobalPosition = polePosition;
                state.AppliedInfluence = wired ? state.SourceWeight : 0f;
            }
        }
        if (ik != null)
            ik.Influence = state.AppliedInfluence;
        return state;
    }
}
