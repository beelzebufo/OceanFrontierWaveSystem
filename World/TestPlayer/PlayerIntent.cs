using Godot;

public partial class PlayerIntent : Node
{
    [Export] public PlayerViewController View { get; set; }
    private bool _jumpPressed;

    public override void _Ready()
    {
        View ??= GetNode<PlayerViewController>("../PlayerViewController");
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo &&
            (key.Keycode == Key.Space || key.PhysicalKeycode == Key.Space))
            _jumpPressed = true;
    }

    public bool ConsumeJumpPressed()
    {
        bool pressed = _jumpPressed;
        _jumpPressed = false;
        return pressed;
    }

    public Vector3 ReadWorldDirection()
    {
        Vector2 axes = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W)) axes.Y -= 1f;
        if (Input.IsKeyPressed(Key.S)) axes.Y += 1f;
        if (Input.IsKeyPressed(Key.A)) axes.X -= 1f;
        if (Input.IsKeyPressed(Key.D)) axes.X += 1f;
        axes = axes.Normalized();

        if (View == null)
            return Vector3.Zero;
        Vector3 up = View.Motor.Up.Normalized();
        Vector3 forward = View.GetPlanarForward(up);
        Vector3 right = View.GetPlanarRight(up);
        return (right * axes.X - forward * axes.Y).Normalized();
    }
}
