using Godot;

// Presentation heading only. The initial local basis is the rig's fixed forward correction.
public partial class CharacterVisualFacing : Node
{
	[Export] public PlayerViewController View { get; set; }
	[Export] public KinematicCharacterMotor Motor { get; set; }
	[Export] public Node3D CharacterVisual { get; set; }

	private Basis _rigForwardCorrection;

	public override void _Ready()
	{
		View ??= GetNode<PlayerViewController>("../PlayerViewController");
		Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
		CharacterVisual ??= GetNode<Node3D>("../CharacterVisual");
		_rigForwardCorrection = CharacterVisual.Basis;
		ApplyHeading();
	}

	public override void _Process(double delta) => ApplyHeading();

	private void ApplyHeading()
	{
		CharacterVisual.GlobalBasis = new Basis(Motor.Up.Normalized(), View.Yaw) *
			_rigForwardCorrection;
	}
}
