using UnityEngine;

/// <summary>
/// Keeps characters GLUED TO THE GROUND and stops the "climb a tiny slope and
/// shoot into the air" launch — for the PLAYER and the ENEMY alike.
///
/// WHAT WAS CAUSING THE LAUNCH (matches this project's movement scripts):
///   PlayerMovementController and EnemyController both write ONLY the
///   horizontal part of the velocity every physics step (x/z), leaving y to
///   gravity. When the character's collider walks into a rising slope, the
///   physics solver pushes the body out of the geometry along the slope's
///   surface normal — that push is mostly UPWARD, it lands in the y velocity,
///   and the movement scripts never clean it. Every step in contact adds
///   more. Even a 5-degree angle flings the character sky-high.
///   WHY MASS COULD NOT FIX IT: the push is a POSITIONAL correction
///   (depenetration), not a force. It sets velocity directly, so its size is
///   completely independent of mass — a 10 kg and a 10,000 kg character get
///   flung exactly the same.
///
/// WHAT THIS SCRIPT DOES (four layers, all in the physics step):
///   1. DEPENETRATION CAP  — clamps how fast the solver is allowed to push a
///      body out of geometry (Rigidbody.maxDepenetrationVelocity, default
///      2 m/s — the standard value for characters). This alone turns the
///      "shoot into orbit" launch into at most a 2 m/s nudge.
///   2. SLOPE-FOLLOWING MOVEMENT — while the character is on the ground, its
///      walking velocity is re-aimed ALONG the slope surface (projected onto
///      the ground plane) instead of horizontally into it. The character
///      glides up and down slopes at full speed and never rams into the
///      terrain in the first place. This also wipes any leftover upward
///      velocity every step, so the launch cannot build up.
///   3. MAGNETIC PULL — the ground is detected up to 'Ground Snap Distance'
///      below the collider's bottom; whenever the character floats above the
///      surface (crest walk-offs, small ledges, stopping mid-hover), it is
///      pulled down to it smoothly instead of floating away. This is the
///      "always close to the ground" behaviour.
///   4. IDLE GUARD — standing still on the ground, any upward push the solver
///      injects is killed immediately (a resting character can never rise).
///
/// Slopes steeper than 'Max Slope Angle' are treated as walls: no sticking,
/// no re-aiming — the character simply cannot walk up them (physics takes
/// over, exactly like before).
///
/// SETUP (add this component, change NOTHING else):
///   - PLAYER:  add to the same GameObject as PlayerMovementController
///              (works alongside PlayerPhysicsStabilizer — this script runs
///              after it and owns the final vertical velocity).
///   - ENEMY:   add to the enemy PREFAB root (same object as
///              EnemyController). Spawner-created copies inherit it.
///   - Both only need a Rigidbody (non-kinematic) — the same requirement the
///     movement scripts already have. No Rigidbody = the component disables
///     itself with one clear warning.
///
/// Fully independent: works with any movement script that writes horizontal
/// velocity (this project's controllers, replacers, future enemies).
/// v1.0 — initial version.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)] // after movement scripts (order 0) and PlayerPhysicsStabilizer (100): our vertical write is the last word each physics step
public class GroundStick : MonoBehaviour
{
    /// <summary>Hover smaller than this (metres above the surface) counts as touching — no pull.</summary>
    private const float HoverDeadzone = 0.02f;

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Ground Detection")]

    [Tooltip("Which layers count as ground. Default = Everything. If the ray ever catches " +
             "part of the character itself, restrict this to your Ground/Terrain layer.")]
    [SerializeField] private LayerMask groundMask = ~0; // ~0 = "Everything"

    [Tooltip("How far below the collider's BOTTOM the ground may be and still count as " +
             "'on the ground' — the magnetic range. Bigger = the character sticks over " +
             "bigger crests/drops/ledges. 0.5-0.8 feels right for walking characters.")]
    [SerializeField] private float groundSnapDistance = 0.6f;

    [Tooltip("Slopes steeper than this (degrees) are treated as walls: no sticking, no " +
             "slope-following — the character cannot walk up them. 45-60 is typical.")]
    [SerializeField] private float maxSlopeAngle = 55f;

    [Header("Magnetic Pull (hug the ground)")]

    [Tooltip("How hard the character is pulled down to the surface when floating above it " +
             "(crest walk-offs, small ledges). 8 = the gap closes in about 1/8 of a second.")]
    [SerializeField] private float stickPullStrength = 8f;

    [Tooltip("Maximum extra downward speed the magnetic pull may add (m/s).")]
    [SerializeField] private float maxStickFallSpeed = 10f;

    [Header("Anti-Launch (depenetration cap)")]

    [Tooltip("Clamp how fast the physics solver may push the character out of geometry " +
             "(this is THE launch killer — the push is a velocity, so mass never mattered).")]
    [SerializeField] private bool applyDepenetrationCap = true;

    [Tooltip("Maximum push-out speed in units per second. 2 is the standard value for " +
             "characters: launches become impossible, spawns inside geometry still resolve.")]
    [SerializeField] private float maxDepenetrationVelocity = 2f;

    [Header("Debug")]

    [Tooltip("Log ground state changes to the Console (for tuning; leave off normally).")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state (read-only from outside)
    // ------------------------------------------------------------------ //

    /// <summary>True while the character is on (or within snap distance of) walkable ground.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Surface normal of the ground currently under the character (up = flat).</summary>
    public Vector3 GroundNormal { get; private set; }

    /// <summary>Distance from the measurement point (collider center, or the transform when no collider exists) down to the ground. -1 while not grounded.</summary>
    public float GroundDistance { get; private set; }

    private Rigidbody rb;
    private Collider groundCollider;      // the collider whose shape defines 'the character'
    private Collider[] ownColliders;      // every collider on this GameObject (self-hit guard)
    private bool warnedNoCollider;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (rb == null)
        {
            Debug.LogWarning("[GroundStick] No Rigidbody on '" + name + "' – ground stick disabled. " +
                             "Add this component to objects that have a (non-kinematic) Rigidbody.", this);
            enabled = false;
            return;
        }

        // The launch killer, applied at the source: without a cap the solver may
        // eject the body at ANY speed, which is what made characters fly.
        if (applyDepenetrationCap)
        {
            rb.maxDepenetrationVelocity = maxDepenetrationVelocity;
        }

        CacheColliders();
    }

    private void FixedUpdate()
    {
        // Kinematic bodies ignore velocity writes (e.g. during the enemy's flee
        // takeover) — do nothing, exactly like the movement scripts' guard.
        if (rb == null || rb.isKinematic) return;

        // Ray starts at the collider's center (a ray never hits a collider its
        // origin is inside, so the character's own body is skipped naturally)
        // and reaches 'snap distance' below the collider's bottom. Without a
        // collider the ray measures from the transform itself (warned once in
        // CacheColliders) — still functional, just less precise.
        Vector3 origin;
        float bottomOffset;
        if (groundCollider != null)
        {
            origin = groundCollider.bounds.center;
            bottomOffset = groundCollider.bounds.extents.y;
        }
        else
        {
            origin = transform.position;
            bottomOffset = 0f;
        }
        float rayLength = bottomOffset + groundSnapDistance;

        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLength, groundMask,
                             QueryTriggerInteraction.Ignore) || IsOwnCollider(hit.collider))
        {
            SetGroundedState(false, Vector3.up, -1f);
            return;
        }

        // Steeper than the walkable limit = a wall, not ground. No sticking.
        float minNormalY = Mathf.Cos(maxSlopeAngle * Mathf.Deg2Rad);
        if (hit.normal.y < minNormalY)
        {
            SetGroundedState(false, Vector3.up, -1f);
            return;
        }

        SetGroundedState(true, hit.normal, hit.distance);

        Vector3 v = GetVelocity();
        Vector3 planar = new Vector3(v.x, 0f, v.z);

        // Re-aim the horizontal movement along the slope surface. This is
        // what makes climbing smooth (no ramming the terrain) and wipes any
        // upward push the solver injected earlier this step — the launch can
        // never build up. Speed is preserved (same speed, new direction).
        Vector3 target = v;
        bool movingAlongSlope = false;

        if (planar.sqrMagnitude > 0.000001f)
        {
            Vector3 slopeDir = Vector3.ProjectOnPlane(planar, hit.normal);

            if (slopeDir.sqrMagnitude > 0.000001f)
            {
                target = slopeDir.normalized * planar.magnitude;
                movingAlongSlope = true;
            }
            // Degenerate safety: movement parallel to the normal (cannot happen
            // with a ground-like normal, but never launch if it ever did).
        }

        // Magnetic pull: whenever the collider floats above the surface
        // (walking over a crest, off a small ledge, stopping mid-hover),
        // descend toward it smoothly — "always pulled close to the ground".
        // The expected resting distance is normal-corrected (bottomOffset /
        // normal.y): a vertical ray naturally reads longer on a tilted slope,
        // and without the correction a character RESTING on a steep-ish slope
        // would look like it hovers and be wrongly pulled downhill.
        float expectedResting = bottomOffset / hit.normal.y;
        float hover = hit.distance - expectedResting - HoverDeadzone;
        if (hover > 0f)
        {
            float pull = Mathf.Min(hover * stickPullStrength, maxStickFallSpeed);
            target.y = -pull;
        }
        else if (v.y > 0f && !movingAlongSlope)
        {
            // Standing still: kill any upward push (depenetration). Slope
            // motion keeps its own y — the projection already owns it.
            target.y = 0f;
        }

        if (target != v) SetVelocity(target); // resting flat + idle = zero writes per step
    }

    /// <summary>Clamps Inspector values so the stick can never receive nonsense input.</summary>
    private void OnValidate()
    {
        groundSnapDistance = Mathf.Max(0.05f, groundSnapDistance);
        maxSlopeAngle = Mathf.Clamp(maxSlopeAngle, 5f, 85f);
        stickPullStrength = Mathf.Max(0.1f, stickPullStrength);
        maxStickFallSpeed = Mathf.Max(0.5f, maxStickFallSpeed);
        maxDepenetrationVelocity = Mathf.Clamp(maxDepenetrationVelocity, 0.5f, 10f);
    }

    // ------------------------------------------------------------------ //
    // Ground bookkeeping
    // ------------------------------------------------------------------ //

    private void SetGroundedState(bool grounded, Vector3 normal, float distance)
    {
        GroundDistance = distance;

        // Always fresh: the normal changes continuously on curved terrain even
        // while the grounded STATE stays true (flat -> slope must update it).
        GroundNormal = grounded ? normal : Vector3.up;

        if (grounded == IsGrounded) return; // no state change

        IsGrounded = grounded;

        if (debugLogs)
        {
            Debug.Log("[GroundStick] '" + name + "' " + (grounded ? "grounded" : "airborne") + ".", this);
        }
    }

    /// <summary>
    /// Finds the collider that defines the character's shape (the first
    /// NON-TRIGGER collider, so auto-created trigger zones like PlayerHitbox's
    /// dead zone never distort the measurement) and remembers all own colliders
    /// as a belt-and-braces self-hit guard for the ray.
    /// </summary>
    private void CacheColliders()
    {
        ownColliders = GetComponents<Collider>();

        for (int i = 0; i < ownColliders.Length; i++)
        {
            if (ownColliders[i] != null && !ownColliders[i].isTrigger)
            {
                groundCollider = ownColliders[i];
                return;
            }
        }

        // Only trigger colliders (or nothing at all): measure from the
        // transform instead — trigger volumes (like PlayerHitbox's dead zone)
        // must never define the character's physical shape. Say so once.
        if (!warnedNoCollider)
        {
            warnedNoCollider = true;
            Debug.LogWarning("[GroundStick] No non-trigger Collider on '" + name + "' – measuring the " +
                             "ground from the transform's position instead. Add a CapsuleCollider " +
                             "for accurate ground detection.", this);
        }
    }

    private bool IsOwnCollider(Collider hitCollider)
    {
        if (hitCollider == null || ownColliders == null) return false;

        for (int i = 0; i < ownColliders.Length; i++)
        {
            if (ownColliders[i] == hitCollider) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Velocity helpers (Unity 6 renamed Rigidbody.velocity -> linearVelocity)
    // ------------------------------------------------------------------ //

    private Vector3 GetVelocity()
    {
#if UNITY_6000_0_OR_NEWER
        return rb.linearVelocity;
#else
        return rb.velocity;
#endif
    }

    private void SetVelocity(Vector3 velocity)
    {
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = velocity;
#else
        rb.velocity = velocity;
#endif
    }
}
