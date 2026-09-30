using Godot;

public partial class PlayerIntent : Node
{
    [Export] public Camera3D Camera { get; set; }

    public override void _Ready()
    {
        Camera ??= GetNode<Camera3D>("../CameraManager/Arm/Camera3D");
    }

    public Vector3 ReadWorldDirection()
    {
        Vector2 axes = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W)) axes.Y -= 1f;
        if (Input.IsKeyPressed(Key.S)) axes.Y += 1f;
        if (Input.IsKeyPressed(Key.A)) axes.X -= 1f;
        if (Input.IsKeyPressed(Key.D)) axes.X += 1f;
        axes = axes.Normalized();

        if (Camera == null)
            return Vector3.Zero;
        Vector3 forward = -Camera.GlobalBasis.Z;
        forward.Y = 0f;
        Vector3 right = Camera.GlobalBasis.X;
        right.Y = 0f;
        return (right.Normalized() * axes.X - forward.Normalized() * axes.Y).Normalized();
    }
}
