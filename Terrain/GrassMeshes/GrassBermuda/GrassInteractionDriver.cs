using Godot;

public partial class GrassInteractionDriver : Node
{
	[Export] public Node3D Player { get; set; }
	[Export] public ShaderMaterial GrassMaterial { get; set; }

	public override void _Ready()
	{
		if (Player == null || GrassMaterial == null)
		{
			GD.PushError("GrassInteractionDriver requires Player and GrassMaterial references.");
			SetProcess(false);
			return;
		}

		GrassMaterial.SetShaderParameter("player_world_position", Player.GlobalPosition);
		GrassMaterial.SetShaderParameter("interaction_enabled", true);
	}

	public override void _Process(double delta)
	{
		GrassMaterial.SetShaderParameter("player_world_position", Player.GlobalPosition);
	}

	public override void _ExitTree()
	{
		GrassMaterial?.SetShaderParameter("interaction_enabled", false);
	}
}
