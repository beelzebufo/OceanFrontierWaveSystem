using System;
using Godot;

namespace OceanFrontier.Water.Waves.SeaFloorDepth;

/// <summary>
/// Batched max-height composition following Crest 4 Sea Floor Depth at
/// wave-harmonic/crest db0658ff0b2e93e4a9e28cc2867509658b0ecc00 (MIT).
/// </summary>
internal sealed class SeaFloorDepthComposePass : IDisposable
{
	public const int DescriptorStrideBytes = 48;
	public const int DescriptorBufferBytes = SeaFloorDepthInputRegistry.Capacity * DescriptorStrideBytes;
	private const int PushConstantBytes = 16;
	private const int LocalSize = 8;

	private readonly RenderingDevice _rd;
	private readonly int _resolution;
	private readonly int _lodCount;
	private readonly byte[] _descriptorBytes = new byte[DescriptorBufferBytes];
	private readonly byte[] _pushBytes = new byte[PushConstantBytes];

	private Rid _shader;
	private Rid _pipeline;
	private Rid _descriptorBuffer;
	private Rid _uniformSet;

	public int ActiveInputCount { get; private set; }
	public long DispatchCount { get; private set; }

	public SeaFloorDepthComposePass(RenderingDevice rd, Rid lodBuffer, Rid depthField, int resolution, int lodCount)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));
		if (!lodBuffer.IsValid || !depthField.IsValid) throw new ArgumentException("Sea floor depth pass received an invalid GPU resource.");
		_resolution = resolution;
		_lodCount = lodCount;
		try { Create(lodBuffer, depthField); }
		catch { Dispose(); throw; }
	}

	private void Create(Rid lodBuffer, Rid depthField)
	{
		RDShaderFile shaderFile = GD.Load<RDShaderFile>("res://Ocean/Shaders/Waves/sea_floor_depth_compose.glsl")
			?? throw new InvalidOperationException("Failed to load sea_floor_depth_compose.glsl.");
		RDShaderSpirV spirv = shaderFile.GetSpirV();
		string compileError = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
		if (!string.IsNullOrEmpty(compileError)) throw new InvalidOperationException("Sea floor depth shader compilation failed:\n" + compileError);

		_shader = _rd.ShaderCreateFromSpirV(spirv);
		_pipeline = _rd.ComputePipelineCreate(_shader);
		_descriptorBuffer = _rd.StorageBufferCreate(DescriptorBufferBytes, _descriptorBytes);

		var lodUniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
		lodUniform.AddId(lodBuffer);
		var descriptorUniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
		descriptorUniform.AddId(_descriptorBuffer);
		var outputUniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 };
		outputUniform.AddId(depthField);
		_uniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { lodUniform, descriptorUniform, outputUniform }, _shader, 0);

		if (!_shader.IsValid || !_pipeline.IsValid || !_descriptorBuffer.IsValid || !_uniformSet.IsValid)
			throw new InvalidOperationException("Failed to create sea floor depth GPU resources.");
	}

	public void Upload(ReadOnlySpan<SeaFloorDepthInputSnapshot> inputs)
	{
		if (inputs.Length > SeaFloorDepthInputRegistry.Capacity) throw new ArgumentOutOfRangeException(nameof(inputs));
		ActiveInputCount = inputs.Length;
		if (inputs.IsEmpty) return;

		for (int i = 0; i < inputs.Length; i++)
		{
			SeaFloorDepthInputSnapshot input = inputs[i];
			int offset = i * DescriptorStrideBytes;
			WriteFloat(offset + 0, input.CenterXZ.X); WriteFloat(offset + 4, input.CenterXZ.Y);
			WriteFloat(offset + 8, input.AxisX.X); WriteFloat(offset + 12, input.AxisX.Y);
			WriteFloat(offset + 16, input.AxisZ.X); WriteFloat(offset + 20, input.AxisZ.Y);
			WriteFloat(offset + 24, input.SizeXZ.X); WriteFloat(offset + 28, input.SizeXZ.Y);
			WriteFloat(offset + 32, input.BottomHeightY);
		}

		uint byteCount = (uint)(inputs.Length * DescriptorStrideBytes);
		Error error = _rd.BufferUpdate(_descriptorBuffer, 0, byteCount, _descriptorBytes.AsSpan(0, (int)byteCount));
		if (error != Error.Ok) throw new InvalidOperationException($"Sea floor depth descriptor upload failed: {error}.");
	}

	public void Dispatch()
	{
		if (ActiveInputCount == 0) return;
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(0, 4), (uint)_resolution);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(4, 4), (uint)_lodCount);
		BitConverter.TryWriteBytes(_pushBytes.AsSpan(8, 4), (uint)ActiveInputCount);
		long list = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(list, _pipeline);
		_rd.ComputeListBindUniformSet(list, _uniformSet, 0);
		_rd.ComputeListSetPushConstant(list, _pushBytes, PushConstantBytes);
		_rd.ComputeListDispatch(list, (uint)((_resolution + LocalSize - 1) / LocalSize), (uint)((_resolution + LocalSize - 1) / LocalSize), (uint)_lodCount);
		_rd.ComputeListEnd();
		DispatchCount++;
	}

	private void WriteFloat(int offset, float value) => BitConverter.TryWriteBytes(_descriptorBytes.AsSpan(offset, 4), value);

	public void Dispose()
	{
		if (_uniformSet.IsValid) { _rd.FreeRid(_uniformSet); _uniformSet = default; }
		if (_descriptorBuffer.IsValid) { _rd.FreeRid(_descriptorBuffer); _descriptorBuffer = default; }
		if (_pipeline.IsValid) { _rd.FreeRid(_pipeline); _pipeline = default; }
		if (_shader.IsValid) { _rd.FreeRid(_shader); _shader = default; }
	}
}
