using System;
using Godot;

public readonly struct ViewMotionPhysicsSnapshot
{
	public readonly ulong Tick;
	public readonly bool IsStable;
	public readonly Vector3 Velocity;
	public readonly Vector3 GroundNormal;
	public readonly bool Jumped;
	public readonly float JumpSpeed;
	public readonly bool Landed;
	public readonly float LandingSpeed;

	public ViewMotionPhysicsSnapshot(ulong tick, bool isStable, Vector3 velocity,
		Vector3 groundNormal, bool jumped, float jumpSpeed, bool landed, float landingSpeed)
	{
		Tick = tick;
		IsStable = isStable;
		Velocity = velocity;
		GroundNormal = groundNormal;
		Jumped = jumped;
		JumpSpeed = jumpSpeed;
		Landed = landed;
		LandingSpeed = landingSpeed;
	}
}

public struct ViewMotionFrame
{
	public Vector3 Position;
	public Basis Rotation;

	public static ViewMotionFrame Identity => new() { Rotation = Basis.Identity };
}

// One compositor owns both procedural transforms.
public partial class PlayerViewMotionController : Node
{
	public enum Channel
	{
		Bob,
		Strafe,
		LookSway,
		JumpFallLanding,
		Lean,
		External,
		RecoilShake,
		Count
	}

	private struct Contribution
	{
		public Vector3 Position;
		public Vector3 RotationRadians;
	}

	[Export] public PlayerLocomotion Locomotion { get; set; }
	[Export] public KinematicCharacterMotor Motor { get; set; }
	[Export] public PlayerViewController View { get; set; }
	[Export] public Node3D ViewMotionPosition { get; set; }
	[Export] public Node3D ViewMotionRotation { get; set; }
	// One step advances half a bob cycle; two steps cover 2 * BobStepLength.
	[Export(PropertyHint.Range, "0.1,2,0.01")] public float BobStepLength { get; set; } = 0.75f;
	[Export] public Vector3 BobPositionAmplitude { get; set; } = new(0.004f, 0.014f, 0.001f);
	[Export] public Vector3 BobRotationAmplitudeDegrees { get; set; } = new(0.15f, 0.08f, 0.18f);
	[Export(PropertyHint.Range, "0,3.14,0.01")] public float BobRotationPhaseOffset { get; set; } = 0.25f;
	[Export(PropertyHint.Range, "0.001,0.5,0.001")] public float BobFadeTime { get; set; } = 0.08f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float BobMinimumSpeed { get; set; } = 0.05f;

	public ViewMotionPhysicsSnapshot LatestPhysicsSnapshot { get; private set; }
	public ViewMotionFrame CurrentFrame => _frame;
	public int PendingJumpCount => _pendingJumpCount;
	public float PendingJumpSpeed => _pendingJumpSpeed;
	public int PendingLandingCount => _pendingLandingCount;
	public float PendingLandingSpeed => _pendingLandingSpeed;
	public int LastRenderJumpCount { get; private set; }
	public float LastRenderJumpSpeed { get; private set; }
	public int LastRenderLandingCount { get; private set; }
	public float LastRenderLandingSpeed { get; private set; }
	public float BobPhase { get; private set; }
	public float BobWeight { get; private set; }
	public float BobPlanarSpeed { get; private set; }
	public Vector3 BobPosition { get; private set; }
	// Euler radians, in ViewMotionRotation's local space.
	public Vector3 BobRotation { get; private set; }

	private readonly Contribution[] _channels = new Contribution[(int)Channel.Count];
	private readonly ViewSpring3 _positionSpring = new();
	private readonly ViewSpring3 _rotationSpring = new();
	private ViewMotionFrame _frame = ViewMotionFrame.Identity;
	private int _pendingJumpCount;
	private float _pendingJumpSpeed;
	private int _pendingLandingCount;
	private float _pendingLandingSpeed;
	private double _previousBobDistance;
	private double _currentBobDistance;
	private bool _bobActive;

	public override void _Ready()
	{
		Locomotion ??= GetParent<PlayerLocomotion>();
		Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
		View ??= GetNode<PlayerViewController>("../PlayerViewController");
		ViewMotionPosition ??= GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition");
		ViewMotionRotation ??= GetNode<Node3D>(
			"../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot/ViewMotionRotation");
		ResetMotion();
	}

	public override void _PhysicsProcess(double delta)
	{
		CharacterContactState contact = Motor.Contacts;
		bool jumped = Locomotion.JumpAcceptedThisTick;
		bool landed = Locomotion.LandedThisTick;
		Vector3 up = Motor.Up.Normalized();
		float jumpSpeed = jumped ? Locomotion.JumpLaunchVelocity.Dot(up) : 0f;
		float landingSpeed = landed
			? Mathf.Max(0f, -Locomotion.LandingPreImpactVerticalSpeed) : 0f;
		LatestPhysicsSnapshot = new ViewMotionPhysicsSnapshot(Engine.GetPhysicsFrames(),
			contact.IsStable, Locomotion.LocomotionVelocity, contact.GroundNormal,
			jumped, jumpSpeed, landed, landingSpeed);
		// Integrate only player locomotion, never the platform carry or root delta.
		Vector3 velocity = Locomotion.LocomotionVelocity;
		Vector3 planarVelocity = velocity - up * velocity.Dot(up);
		BobPlanarSpeed = contact.IsStable ? planarVelocity.Length() : 0f;
		_bobActive = contact.IsStable && BobPlanarSpeed > BobMinimumSpeed;
		_previousBobDistance = _currentBobDistance;
		if (_bobActive)
			_currentBobDistance += BobPlanarSpeed * delta;

		// Counts retain multiple events when several physics ticks precede a render.
		if (jumped)
		{
			if (_pendingJumpCount < int.MaxValue)
				_pendingJumpCount++;
			_pendingJumpSpeed = jumpSpeed;
		}
		if (landed)
		{
			if (_pendingLandingCount < int.MaxValue)
				_pendingLandingCount++;
			_pendingLandingSpeed = Mathf.Max(_pendingLandingSpeed, landingSpeed);
		}
	}

	public override void _Process(double delta)
	{
		LastRenderJumpCount = _pendingJumpCount;
		LastRenderJumpSpeed = _pendingJumpSpeed;
		LastRenderLandingCount = _pendingLandingCount;
		LastRenderLandingSpeed = _pendingLandingSpeed;
		_pendingJumpCount = _pendingLandingCount = 0;
		_pendingJumpSpeed = 0f;
		_pendingLandingSpeed = 0f;

		_frame = ViewMotionFrame.Identity;
		UpdateBob((float)delta);
		// Enum order is the composition order. Each source submits only its
		// channel; rotation bases multiply in that fixed order after mouse pitch.
		for (int i = 0; i < _channels.Length; i++)
		{
			Contribution channel = _channels[i];
			_frame.Position += channel.Position;
			if (!channel.RotationRadians.IsZeroApprox())
				_frame.Rotation *= Basis.FromEuler(channel.RotationRadians);
			_channels[i] = default;
		}
		ViewMotionPosition.Position = _frame.Position;
		ViewMotionRotation.Basis = _frame.Rotation;
	}

	public void AddPosition(Channel channel, Vector3 offset)
		=> _channels[(int)channel].Position += offset;

	public void AddRotation(Channel channel, Vector3 radians)
		=> _channels[(int)channel].RotationRadians += radians;

	// Authoritative world velocity expressed in player yaw space; pitch and
	// camera motion never enter this conversion.
	public Vector3 GetYawLocalVelocity()
		=> new Basis(Motor.Up.Normalized(), View.Yaw).Inverse() * LatestPhysicsSnapshot.Velocity;

	private void UpdateBob(float delta)
	{
		// Physics owns distance. Interpolation keeps constant-speed phase smooth
		// when several render frames occur between physics ticks.
		double alpha = Mathf.Clamp((float)Engine.GetPhysicsInterpolationFraction(), 0f, 1f);
		double distance = _previousBobDistance + (_currentBobDistance - _previousBobDistance) * alpha;
		double radiansPerMeter = Math.Tau / (2.0 * Math.Max(BobStepLength, 0.001f));
		BobPhase = (float)((distance * radiansPerMeter) % Math.Tau);
		if (delta > 0f && float.IsFinite(delta))
		{
			float fadeTime = Mathf.Max(BobFadeTime, 0.001f);
			float blend = 1f - (float)Math.Exp(-delta / fadeTime);
			BobWeight += ((_bobActive ? 1f : 0f) - BobWeight) * blend;
		}

		float fundamental = Mathf.Cos(BobPhase);
		float secondHarmonic = Mathf.Cos(BobPhase * 2f);
		BobPosition = new Vector3(fundamental * BobPositionAmplitude.X,
			secondHarmonic * BobPositionAmplitude.Y,
			fundamental * BobPositionAmplitude.Z) * BobWeight;
		float rotationPhase = BobPhase + BobRotationPhaseOffset;
		BobRotation = new Vector3(
			Mathf.Cos(BobPhase * 2f + BobRotationPhaseOffset) * Mathf.DegToRad(BobRotationAmplitudeDegrees.X),
			Mathf.Cos(rotationPhase) * Mathf.DegToRad(BobRotationAmplitudeDegrees.Y),
			Mathf.Cos(rotationPhase) * Mathf.DegToRad(BobRotationAmplitudeDegrees.Z)) * BobWeight;
		AddPosition(Channel.Bob, BobPosition);
		AddRotation(Channel.Bob, BobRotation);
	}

	public void ResetMotion()
	{
		Array.Clear(_channels, 0, _channels.Length);
		_frame = ViewMotionFrame.Identity;
		_positionSpring.Reset();
		_rotationSpring.Reset();
		LatestPhysicsSnapshot = default;
		_pendingJumpCount = _pendingLandingCount = 0;
		_pendingJumpSpeed = 0f;
		_pendingLandingSpeed = 0f;
		LastRenderJumpCount = LastRenderLandingCount = 0;
		LastRenderJumpSpeed = 0f;
		LastRenderLandingSpeed = 0f;
		_previousBobDistance = _currentBobDistance = 0.0;
		_bobActive = false;
		BobPhase = BobWeight = BobPlanarSpeed = 0f;
		BobPosition = BobRotation = Vector3.Zero;
		ViewMotionPosition.Position = Vector3.Zero;
		ViewMotionRotation.Basis = Basis.Identity;
	}
}
