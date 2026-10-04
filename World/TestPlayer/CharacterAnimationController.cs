using Godot;

// Presentation only: gameplay motion and contact state select the base pose.
public partial class CharacterAnimationController : Node
{
	private enum BaseState { Idle, Walk, JumpStart, JumpLoop, JumpLand }

	[Export] public PlayerLocomotion Locomotion { get; set; }
	[Export] public KinematicCharacterMotor Motor { get; set; }
	[Export] public AnimationTree Tree { get; set; }
	[Export(PropertyHint.Range, "0.1,10,0.1")] public float WalkReferenceSpeed { get; set; } = 2.8f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float MinimumWalkSpeed { get; set; } = 0.1f;
	[Export(PropertyHint.Range, "0.1,2,0.05")] public float MinimumWalkRate { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "0.1,3,0.05")] public float MaximumWalkRate { get; set; } = 1.5f;
	[Export(PropertyHint.Range, "0,0.5,0.01")] public float GroundBlendSeconds { get; set; } = 0.15f;
	[Export(PropertyHint.Range, "0,0.5,0.01")] public float JumpBlendSeconds { get; set; } = 0.08f;
	[Export(PropertyHint.Range, "0.05,0.5,0.01")] public float JumpStartHoldSeconds { get; set; } = 0.18f;
	[Export(PropertyHint.Range, "0.05,0.5,0.01")] public float JumpLandHoldSeconds { get; set; } = 0.18f;

	public string RequestedState => _state.ToString();
	public float PlanarSpeed { get; private set; }
	public float WalkPlaybackRate { get; private set; } = 1f;
	public ulong StateChangedPhysicsTick { get; private set; }

	private AnimationNodeStateMachinePlayback _playback;
	private BaseState _state = BaseState.Idle;
	private float _stateRemaining;
	private bool _airbornePresentation;
	private Vector3 _previousWorldPosition;

	public override void _Ready()
	{
		Locomotion ??= GetParent<PlayerLocomotion>();
		Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
		Tree ??= GetNode<AnimationTree>("../CharacterVisual/Quaternius/BaseAnimationTree");
		if (Locomotion == null || Motor == null || Tree?.TreeRoot is not AnimationNodeStateMachine graph)
		{
			GD.PushError("CharacterAnimationController requires locomotion, motor and base animation state machine.");
			SetPhysicsProcess(false);
			return;
		}
		// Transition tuning belongs to this player instance, not the shared graph resource.
		graph = (AnimationNodeStateMachine)graph.Duplicate(true);
		Tree.TreeRoot = graph;
		for (int i = 0; i < graph.GetTransitionCount(); i++)
		{
			string from = graph.GetTransitionFrom(i).ToString();
			string to = graph.GetTransitionTo(i).ToString();
			graph.GetTransition(i).XfadeTime =
				(from == "Idle" && to == "Walk") || (from == "Walk" && to == "Idle") ||
				(from == "JumpLand" && (to == "Idle" || to == "Walk"))
					? GroundBlendSeconds : JumpBlendSeconds;
		}
		Tree.Active = true;
		_playback = Tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
		_playback.Start("Idle");
		_previousWorldPosition = Locomotion.GlobalPosition;
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_playback == null)
			return;
		float dt = (float)delta;
		Vector3 up = Motor.Up.Normalized();
		Vector3 worldDelta = Locomotion.GlobalPosition - _previousWorldPosition;
		_previousWorldPosition = Locomotion.GlobalPosition;
		Vector3 supportDelta = Motor.Contacts.IsStable ?
			Motor.Contacts.GroundColliderVelocity * dt : Vector3.Zero;
		Vector3 relativeVelocity = (worldDelta - supportDelta) / Mathf.Max(dt, 0.000001f);
		PlanarSpeed = (relativeVelocity - up * relativeVelocity.Dot(up)).Length();
		bool moving = PlanarSpeed >= MinimumWalkSpeed;
		WalkPlaybackRate = moving ? Mathf.Clamp(PlanarSpeed / Mathf.Max(WalkReferenceSpeed, 0.01f),
			MinimumWalkRate, MaximumWalkRate) : 1f;
		Tree.Set("parameters/Walk/WalkRate/scale", WalkPlaybackRate);
		bool stable = Motor.Contacts.IsStable;

		if (Locomotion.JumpAcceptedThisTick)
		{
			_airbornePresentation = true;
			ChangeState(BaseState.JumpStart);
			_stateRemaining = JumpStartHoldSeconds;
			return;
		}
		if (Locomotion.LandedThisTick && _airbornePresentation)
		{
			_airbornePresentation = false;
			ChangeState(BaseState.JumpLand);
			_stateRemaining = JumpLandHoldSeconds;
			return;
		}
		if (!stable && (_state == BaseState.Idle || _state == BaseState.Walk))
		{
			_airbornePresentation = true;
			ChangeState(BaseState.JumpLoop);
		}
		else if (_state == BaseState.JumpStart)
		{
			_stateRemaining -= dt;
			if (_stateRemaining <= 0f)
				ChangeState(stable ? BaseState.JumpLand : BaseState.JumpLoop);
		}
		else if (_state == BaseState.JumpLoop && stable)
		{
			_airbornePresentation = false;
			ChangeState(BaseState.JumpLand);
			_stateRemaining = JumpLandHoldSeconds;
		}
		else if (_state == BaseState.JumpLand)
		{
			_stateRemaining -= dt;
			if (_stateRemaining <= 0f && stable)
				ChangeState(moving ? BaseState.Walk : BaseState.Idle);
			else if (!stable)
			{
				_airbornePresentation = true;
				ChangeState(BaseState.JumpLoop);
			}
		}
		else if (stable)
			ChangeState(moving ? BaseState.Walk : BaseState.Idle);
	}

	private void ChangeState(BaseState next)
	{
		if (next == _state)
			return;
		_state = next;
		StateChangedPhysicsTick = Engine.GetPhysicsFrames();
		_playback.Travel(next.ToString());
	}
}
