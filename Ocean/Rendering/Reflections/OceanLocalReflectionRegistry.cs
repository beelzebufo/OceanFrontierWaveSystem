using System.Collections.Generic;
using Godot;

namespace OceanFrontier.Water.Rendering.Reflections;

/// <summary>Selects nearby local-reflection participants without rendering them.</summary>
public partial class OceanLocalReflectionRegistry : Node
{
	public const int MaxActiveReflectors = 4;
	public const float DefaultSelectionDistance = 80.0f;

	[Export] public NodePath MainCameraPath { get; set; }

	private readonly List<Registration> _registered = new();
	private readonly Selection[] _active = new Selection[MaxActiveReflectors];
	private Camera3D _mainCamera;
	private long _nextSequence;
	private int _activeCount;
	private Transform3D _lastCameraTransform;
	private bool _hasCameraTransform;
	private float _selectionDistance = DefaultSelectionDistance;

	[Export(PropertyHint.Range, "1,1000,1,or_greater")]
	public float SelectionDistance
	{
		get => _selectionDistance;
		set
		{
			if (!float.IsFinite(value) || value <= 0.0f)
				return;
			_selectionDistance = value;
			ParticipantChanged();
		}
	}

	public int RegisteredCount => _registered.Count;
	public int ActiveCount => _activeCount;

	private readonly struct Registration
	{
		public readonly OceanLocalReflectionParticipant Participant;
		public readonly long Sequence;

		public Registration(OceanLocalReflectionParticipant participant, long sequence)
		{
			Participant = participant;
			Sequence = sequence;
		}
	}

	private readonly struct Selection
	{
		public readonly OceanLocalReflectionParticipant Participant;
		public readonly int Priority;
		public readonly float DistanceSquared;
		public readonly long Sequence;

		public Selection(OceanLocalReflectionParticipant participant,
			int priority, float distanceSquared, long sequence)
		{
			Participant = participant;
			Priority = priority;
			DistanceSquared = distanceSquared;
			Sequence = sequence;
		}
	}

	public override void _Ready()
	{
		_mainCamera = MainCameraPath != null && !MainCameraPath.IsEmpty
			? GetNodeOrNull<Camera3D>(MainCameraPath) : null;
		if (_mainCamera == null)
		{
			GD.PushError("Local reflection registry main camera was not found.");
			return;
		}
		RenderingServer.FramePreDraw += RefreshForDraw;
	}

	public bool Register(OceanLocalReflectionParticipant participant)
	{
		if (!GodotObject.IsInstanceValid(participant) || !participant.IsInsideTree())
			return false;
		for (int i = 0; i < _registered.Count; i++)
			if (_registered[i].Participant == participant)
				return false;
		_registered.Add(new Registration(participant, _nextSequence++));
		ParticipantChanged();
		return true;
	}

	public bool Unregister(OceanLocalReflectionParticipant participant)
	{
		for (int i = 0; i < _registered.Count; i++)
		{
			if (_registered[i].Participant != participant)
				continue;
			_registered.RemoveAt(i);
			ParticipantChanged();
			return true;
		}
		return false;
	}

	public OceanLocalReflectionParticipant GetActiveParticipant(int index) =>
		index >= 0 && index < _activeCount ? _active[index].Participant : null;

	internal void ParticipantChanged()
	{
		if (_hasCameraTransform)
			RefreshSelection(_lastCameraTransform);
	}

	private void RefreshForDraw()
	{
		if (IsInsideTree() && GodotObject.IsInstanceValid(_mainCamera))
			RefreshSelection(_mainCamera.GetCameraTransform());
	}

	public void RefreshSelection(Transform3D effectiveCameraTransform)
	{
		_lastCameraTransform = effectiveCameraTransform;
		_hasCameraTransform = true;
		for (int i = 0; i < _activeCount; i++)
			_active[i] = default;
		_activeCount = 0;

		Vector3 cameraPosition = effectiveCameraTransform.Origin;
		Vector3 cameraForward = -effectiveCameraTransform.Basis.Z.Normalized();
		if (!IsFinite(cameraPosition) || !IsFinite(cameraForward))
			return;

		for (int i = 0; i < _registered.Count;)
		{
			Registration registration = _registered[i];
			OceanLocalReflectionParticipant participant = registration.Participant;
			if (!GodotObject.IsInstanceValid(participant) || !participant.IsInsideTree())
			{
				_registered.RemoveAt(i);
				continue;
			}
			i++;
			if (!participant.Enabled || participant.SourceRoot == null ||
				!participant.TryGetCenterWorld(out Vector3 center) ||
				!IsFinite(center))
				continue;
			float radius = participant.InfluenceRadius;
			if (!float.IsFinite(radius) || radius <= 0.0f)
				continue;
			Vector3 offset = center - cameraPosition;
			float distanceSquared = offset.LengthSquared();
			float limit = _selectionDistance + radius;
			if (!float.IsFinite(distanceSquared) ||
				distanceSquared > limit * limit ||
				cameraForward.Dot(offset) < -radius)
				continue;

			var candidate = new Selection(participant, participant.Priority,
				distanceSquared, registration.Sequence);
			int insertAt = 0;
			while (insertAt < _activeCount &&
				!Outranks(candidate, _active[insertAt]))
				insertAt++;
			if (insertAt >= MaxActiveReflectors)
				continue;
			if (_activeCount < MaxActiveReflectors)
				_activeCount++;
			for (int j = _activeCount - 1; j > insertAt; j--)
				_active[j] = _active[j - 1];
			_active[insertAt] = candidate;
		}
	}

	private static bool Outranks(Selection left, Selection right)
	{
		if (left.Priority != right.Priority)
			return left.Priority > right.Priority;
		if (left.DistanceSquared != right.DistanceSquared)
			return left.DistanceSquared < right.DistanceSquared;
		return left.Sequence < right.Sequence;
	}

	private static bool IsFinite(Vector3 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) &&
		float.IsFinite(value.Z);

	public override void _ExitTree()
	{
		RenderingServer.FramePreDraw -= RefreshForDraw;
		_registered.Clear();
		for (int i = 0; i < _activeCount; i++)
			_active[i] = default;
		_activeCount = 0;
		_mainCamera = null;
	}
}
