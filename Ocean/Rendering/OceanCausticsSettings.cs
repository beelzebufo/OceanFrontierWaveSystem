using System;
using Godot;

namespace OceanFrontier.Water.Rendering;

/// <summary>
/// Serialized authority for the projected-caustics rendering feature.
/// Physical water-medium values remain in OceanOpticsSettings.
/// </summary>
[GlobalClass]
public partial class OceanCausticsSettings : Resource
{
	public const float DefaultScale = 5.0f;
	public const float DefaultTextureAverage = 0.5f;
	public const float DefaultStrength = 1.0f;
	public const float DefaultFocalDepth = 2.0f;
	public const float DefaultDepthOfField = 0.33f;
	public const float DefaultDistortionScale = 10.0f;
	public const float DefaultDistortionStrength = 0.15f;


	[Export]
	public Texture2D Texture { get; set; }


	[Export(PropertyHint.Range, "0.1,50,0.1")]
	public float Scale { get; set; } =
		DefaultScale;


	[Export(PropertyHint.Range, "0,1,0.01")]
	public float TextureAverage { get; set; } =
		DefaultTextureAverage;


	[Export(PropertyHint.Range, "0,4,0.01")]
	public float Strength { get; set; } =
		DefaultStrength;


	[Export(PropertyHint.Range, "0,100,0.1")]
	public float FocalDepth { get; set; } =
		DefaultFocalDepth;


	[Export(PropertyHint.Range, "0.01,100,0.01")]
	public float DepthOfField { get; set; } =
		DefaultDepthOfField;


	[Export]
	public Texture2D DistortionTexture { get; set; }


	[Export(PropertyHint.Range, "0.1,50,0.1")]
	public float DistortionScale { get; set; } =
		DefaultDistortionScale;


	[Export(PropertyHint.Range, "0,2,0.01")]
	public float DistortionStrength { get; set; } =
		DefaultDistortionStrength;


	internal OceanCausticsState Snapshot() =>
		new(
			Texture,
			Sanitize(
				Scale,
				0.1f,
				50.0f,
				DefaultScale),
			Sanitize(
				TextureAverage,
				0.0f,
				1.0f,
				DefaultTextureAverage),
			Sanitize(
				Strength,
				0.0f,
				4.0f,
				DefaultStrength),
			Sanitize(
				FocalDepth,
				0.0f,
				100.0f,
				DefaultFocalDepth),
			Sanitize(
				DepthOfField,
				0.01f,
				100.0f,
				DefaultDepthOfField),
			DistortionTexture,
			Sanitize(
				DistortionScale,
				0.1f,
				50.0f,
				DefaultDistortionScale),
			Sanitize(
				DistortionStrength,
				0.0f,
				2.0f,
				DefaultDistortionStrength));


	internal static OceanCausticsState DefaultState =>
		new(
			null,
			DefaultScale,
			DefaultTextureAverage,
			DefaultStrength,
			DefaultFocalDepth,
			DefaultDepthOfField,
			null,
			DefaultDistortionScale,
			DefaultDistortionStrength);


	private static float Sanitize(
		float value,
		float minimum,
		float maximum,
		float fallback) =>
		Mathf.Clamp(
			float.IsFinite(value)
				? value
				: fallback,
			minimum,
			maximum);
}


/// <summary>Immutable main-thread snapshot shared by renderer consumers.</summary>
internal readonly struct OceanCausticsState : IEquatable<OceanCausticsState>
{
	internal readonly Texture2D Texture;
	internal readonly float Scale;
	internal readonly float TextureAverage;
	internal readonly float Strength;
	internal readonly float FocalDepth;
	internal readonly float DepthOfField;
	internal readonly Texture2D DistortionTexture;
	internal readonly float DistortionScale;
	internal readonly float DistortionStrength;


	internal OceanCausticsState(
		Texture2D texture,
		float scale,
		float textureAverage,
		float strength,
		float focalDepth,
		float depthOfField,
		Texture2D distortionTexture,
		float distortionScale,
		float distortionStrength)
	{
		Texture = texture;
		Scale = scale;
		TextureAverage = textureAverage;
		Strength = strength;
		FocalDepth = focalDepth;
		DepthOfField = depthOfField;
		DistortionTexture = distortionTexture;
		DistortionScale = distortionScale;
		DistortionStrength = distortionStrength;
	}


	internal OceanCausticsGpuState ToGpuState()
	{
		Rid texture =
			Texture != null &&
			GodotObject.IsInstanceValid(Texture)
				? Texture.GetRid()
				: default;


		Rid distortionTexture =
			DistortionTexture != null &&
			GodotObject.IsInstanceValid(DistortionTexture)
				? DistortionTexture.GetRid()
				: default;


		return
			new OceanCausticsGpuState(
				texture,
				Scale,
				TextureAverage,
				Strength,
				FocalDepth,
				DepthOfField,
				distortionTexture,
				DistortionScale,
				DistortionStrength);
	}


	public bool Equals(
		OceanCausticsState other) =>
		ReferenceEquals(
			Texture,
			other.Texture) &&
		Scale == other.Scale &&
		TextureAverage == other.TextureAverage &&
		Strength == other.Strength &&
		FocalDepth == other.FocalDepth &&
		DepthOfField == other.DepthOfField &&
		ReferenceEquals(
			DistortionTexture,
			other.DistortionTexture) &&
		DistortionScale == other.DistortionScale &&
		DistortionStrength == other.DistortionStrength;


	public override bool Equals(
		object obj) =>
		obj is OceanCausticsState other &&
		Equals(other);


	public override int GetHashCode() =>
		HashCode.Combine(
			Texture,
			Scale,
			TextureAverage,
			Strength,
			FocalDepth,
			DepthOfField,
			DistortionTexture,
			HashCode.Combine(
				DistortionScale,
				DistortionStrength));
}


/// <summary>
/// Resource-free value snapshot queued to the render thread. Texture RIDs are
/// RenderingServer handles and must be resolved there to borrowed RD RIDs.
/// </summary>
internal readonly struct OceanCausticsGpuState
{
	internal readonly Rid Texture;
	internal readonly float Scale;
	internal readonly float TextureAverage;
	internal readonly float Strength;
	internal readonly float FocalDepth;
	internal readonly float DepthOfField;
	internal readonly Rid DistortionTexture;
	internal readonly float DistortionScale;
	internal readonly float DistortionStrength;


	internal OceanCausticsGpuState(
		Rid texture,
		float scale,
		float textureAverage,
		float strength,
		float focalDepth,
		float depthOfField,
		Rid distortionTexture,
		float distortionScale,
		float distortionStrength)
	{
		Texture = texture;
		Scale = scale;
		TextureAverage = textureAverage;
		Strength = strength;
		FocalDepth = focalDepth;
		DepthOfField = depthOfField;
		DistortionTexture = distortionTexture;
		DistortionScale = distortionScale;
		DistortionStrength = distortionStrength;
	}
}
