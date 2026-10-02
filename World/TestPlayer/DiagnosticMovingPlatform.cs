using Godot;

// Physics-tick motion for the FPC diagnostic scene only.
public partial class DiagnosticMovingPlatform : AnimatableBody3D
{
	public enum MotionMode { Translate, Rotate, Elevate }

	[Export] public MotionMode Mode { get; set; }
	[Export] public float Amplitude { get; set; } = 1f;
	[Export] public float Rate { get; set; } = 0.8f;

	private Transform3D _start;
	private float _time;

	public override void _Ready() => _start = GlobalTransform;

	public override void _PhysicsProcess(double delta)
	{
		_time += (float)delta;
		Transform3D next = _start;
		switch (Mode)
		{
			case MotionMode.Translate:
				next.Origin += Vector3.Right * (Amplitude * Mathf.Sin(_time * Rate));
				break;
			case MotionMode.Rotate:
				next.Basis = new Basis(Vector3.Up, _time * Rate) * _start.Basis;
				break;
			case MotionMode.Elevate:
				next.Origin += Vector3.Up * (Amplitude * Mathf.Sin(_time * Rate));
				break;
		}
		GlobalTransform = next;
	}
}
