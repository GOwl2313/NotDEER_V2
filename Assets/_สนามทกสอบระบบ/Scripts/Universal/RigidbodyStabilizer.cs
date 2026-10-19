using UnityEngine;

/// <summary>
/// Keeps characters with a Rigidbody from falling over after touching anything
/// (players, enemies, props pushing into each other, etc.).
///
///   - Freezes pitch/roll rotation (tilt) while keeping yaw free, so the
///     character can still turn but can never tip onto its face/back.
///   - Optionally freezes yaw too for fully-locked orientation.
///   - Optionally enables interpolation (smoother visuals) and continuous
///     collision detection (no tunneling through thin walls at high speed).
///   - Safety net: if the character somehow ends up tilted (teleport, spawn,
///     constraint override), it slowly rotates itself back upright while
///     preserving its facing direction.
///
/// Add this component to EVERY character that has a Rigidbody – the player,
/// the enemy prefab, and anything else you spawn later. It is fully
/// independent from the movement/AI scripts.
/// </summary>
[DisallowMultipleComponent]
public class RigidbodyStabilizer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Rotation Lock (anti-tipping)")]

    [Tooltip("Freeze pitch/roll rotation so the character can never fall over. " +
             "Yaw (turning left/right) stays free.")]
    [SerializeField] private bool freezeTilt = true;

    [Tooltip("Also freeze yaw rotation for a fully-locked orientation (rarely needed).")]
    [SerializeField] private bool freezeYaw = false;

    [Header("Rigidbody Auto-Setup")]

    [Tooltip("Apply the settings below to the Rigidbody automatically on Awake.")]
    [SerializeField] private bool autoConfigureRigidbody = true;

    [Tooltip("Interpolate between physics steps – makes camera follow smooth instead of jittery.")]
    [SerializeField] private bool useInterpolation = true;

    [Tooltip("Continuous collision detection – prevents tunneling through thin geometry at speed.")]
    [SerializeField] private bool useContinuousCollision = true;

    [Header("Self-Righting Safety Net")]

    [Tooltip("If the character somehow ends up tilted, rotate it back upright over time.")]
    [SerializeField] private bool selfRighting = true;

    [Tooltip("Tilt angle (degrees from upright) above which self-righting kicks in.")]
    [SerializeField] private float selfRightThreshold = 15f;

    [Tooltip("Degrees per second used when rotating back upright.")]
    [SerializeField] private float selfRightSpeed = 360f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Rigidbody rb;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (rb == null)
        {
            Debug.LogWarning("[RigidbodyStabilizer] No Rigidbody on '" + name + "' – stabilizer disabled. " +
                             "Add this component to objects that actually have a Rigidbody.", this);
            enabled = false;
            return;
        }

        if (autoConfigureRigidbody)
        {
            ApplyConfiguration();
        }
    }

    private void FixedUpdate()
    {
        if (!selfRighting || rb == null) return;

        // Safety net for cases where the body ends up tilted anyway
        // (spawn placement, external code, constraints changed at runtime).
        float tilt = Vector3.Angle(transform.up, Vector3.up);
        if (tilt > selfRightThreshold)
        {
            float yaw = transform.eulerAngles.y;
            Quaternion upright = Quaternion.Euler(0f, yaw, 0f);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, upright,
                selfRightSpeed * Time.fixedDeltaTime));
        }
    }

    /// <summary>Clamps Inspector values.</summary>
    private void OnValidate()
    {
        selfRightThreshold = Mathf.Clamp(selfRightThreshold, 1f, 89f);
        selfRightSpeed = Mathf.Max(1f, selfRightSpeed);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>(Re)applies the rotation lock and physics quality settings.</summary>
    public void ApplyConfiguration()
    {
        if (rb == null) return;

        RigidbodyConstraints constraints = RigidbodyConstraints.None;
        if (freezeTilt) constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        if (freezeYaw) constraints |= RigidbodyConstraints.FreezeRotationY;
        rb.constraints = constraints;

        if (useInterpolation) rb.interpolation = RigidbodyInterpolation.Interpolate;
        if (useContinuousCollision) rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
    }
}
