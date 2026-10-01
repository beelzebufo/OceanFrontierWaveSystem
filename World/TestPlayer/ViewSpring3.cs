using Godot;

// Small presentation-only spring. State and integration use value types only.
public sealed class ViewSpring3
{
    private const int MaxSubsteps = 32;

    public Vector3 Current { get; private set; }
    public Vector3 Velocity { get; private set; }
    public Vector3 Target { get; private set; }
    public float SpringStrength { get; set; } = 80f;
    public float Damping { get; set; } = 18f;
    // 120 Hz bounds the semi-implicit Euler step at 30, 60 and 120 FPS.
    public float MaxStep { get; set; } = 1f / 120f;

    public void SetTarget(Vector3 target) => Target = target;

    public void AddImpulse(Vector3 impulse) => Velocity += impulse;

    public Vector3 Evaluate(float delta)
    {
        if (delta <= 0f || !float.IsFinite(delta))
            return Current;

        float stepLimit = Mathf.Max(MaxStep, 0.0001f);
        // A stalled render frame advances at most 32 bounded steps. Discarding
        // excess elapsed time prevents a large catch-up jump in presentation.
        float elapsed = Mathf.Min(delta, stepLimit * MaxSubsteps);
        int steps = Mathf.CeilToInt(elapsed / stepLimit);
        float step = elapsed / steps;
        for (int i = 0; i < steps; i++)
        {
            Vector3 acceleration = (Target - Current) * SpringStrength - Velocity * Damping;
            Velocity += acceleration * step;
            Current += Velocity * step;
        }
        return Current;
    }

    public void Reset()
    {
        Current = Vector3.Zero;
        Velocity = Vector3.Zero;
        Target = Vector3.Zero;
    }
}
