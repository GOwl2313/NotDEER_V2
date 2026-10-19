using UnityEngine;

/// <summary>
/// The flashlight "detector": checks whether THIS enemy is currently being hit
/// by the player's flashlight (the Spot Light tagged "Flashlight").
///
/// An enemy counts as "lit" only when ALL of these are true:
///   1. The flashlight exists and is switched ON (its Light component enabled).
///   2. The enemy's body is inside the light's cone (Spot Angle + tolerance).
///   3. The enemy is within the light's Range (+ tolerance).
///   4. Nothing solid blocks the straight line between the light and the
///      enemy's body — enemies can never be stunned through walls, so an
///      enemy will never vanish somewhere the player cannot see it happen.
///
/// Attach it to the enemy prefab root (next to EnemyController, which reads
/// the IsLit property and runs the stun / disappear stages).
///
/// This component is fully standalone: it never references any other script —
/// it finds the light by tag and exposes the result as a property, so any
/// future gameplay (traps, statues, other light-sensitive creatures) can reuse
/// the same detector unchanged.
/// </summary>
[DisallowMultipleComponent]
public class FlashlightDetector : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Flashlight Lookup")]

    [Tooltip("Tag of the player's flashlight Spot Light. The detector finds it by this tag.")]
    [SerializeField] private string flashlightTag = "Flashlight";

    [Header("Cone & Range Tolerance")]

    [Tooltip("Extra degrees added to the spotlight's half angle. Lets an enemy count as lit " +
             "when only its edge is inside the cone — raise it for bigger enemy models.")]
    [SerializeField] private float angleToleranceDegrees = 5f;

    [Tooltip("Extra distance allowed beyond the spotlight's Range, so enemies right at the " +
             "edge of the beam still count as lit.")]
    [SerializeField] private float rangeTolerance = 1.5f;

    [Header("Line Of Sight (no stunning through walls)")]

    [Tooltip("Require an unblocked line between the flashlight and the enemy's body. Keep this ON " +
             "so enemies can never disappear behind a wall where the player wouldn't notice.")]
    [SerializeField] private bool requireLineOfSight = true;

    [Tooltip("Layers that can block the light (walls, props…). The enemy itself and the player " +
             "holding the light are always ignored automatically.")]
    [SerializeField] private LayerMask blockingLayers = ~0;

    [Tooltip("Points on the enemy body that get tested (local-space offsets from this transform). " +
             "If ANY single point is lit AND unblocked, the enemy counts as lit. " +
             "Spread them over the body height — defaults fit a 1-unit-tall placeholder cube; " +
             "adjust for your model (e.g. 0 / 0.75 / -0.75 for a 2-unit capsule).")]
    [SerializeField]
    private Vector3[] bodySamplePoints = new Vector3[]
    {
        new Vector3(0f,  0f,  0f), // body centre
        new Vector3(0f,  0.4f, 0f), // upper body
        new Vector3(0f, -0.4f, 0f)  // lower body
    };

    [Header("Performance")]

    [Tooltip("Seconds between light checks. 0 = every frame (most precise). " +
             "Raise it (e.g. 0.1) if you ever have many detectors running at once.")]
    [SerializeField] private float checkInterval = 0f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Transform flashlightTransform; // the light's own transform (cone position/axis)
    private Light flashlightLight;
    private float nextCheckTime;
    private bool loggedTagWarning;
    private bool loggedNoLightWarning;
    private bool loggedMissingWarning;
    private int failedSearchCount;

    // Reused buffer so the line-of-sight raycasts don't allocate memory every frame.
    private readonly RaycastHit[] hitBuffer = new RaycastHit[16];

    /// <summary>
    /// True while the flashlight is ON, this enemy is inside its cone/range,
    /// and nothing blocks the light. Updated every Check Interval.
    /// </summary>
    public bool IsLit { get; private set; }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Update()
    {
        if (Time.time < nextCheckTime) return;

        nextCheckTime = Time.time + Mathf.Max(0f, checkInterval);
        IsLit = ComputeIsLit();
    }

    /// <summary>Clamps Inspector values so the detector can never receive nonsense input.</summary>
    private void OnValidate()
    {
        angleToleranceDegrees = Mathf.Max(0f, angleToleranceDegrees);
        rangeTolerance = Mathf.Max(0f, rangeTolerance);
        checkInterval = Mathf.Max(0f, checkInterval);
    }

    // ------------------------------------------------------------------ //
    // Detection
    // ------------------------------------------------------------------ //

    private bool ComputeIsLit()
    {
        // Nothing to test without sample points (empty array in the Inspector).
        if (bodySamplePoints == null || bodySamplePoints.Length == 0) return false;

        FindFlashlightIfNeeded();

        // The light must exist and be switched on (FlashlightController's toggle).
        if (flashlightLight == null || !flashlightLight.enabled) return false;

        Vector3 lightPosition = flashlightTransform.position;
        Vector3 lightDirection = flashlightTransform.forward; // spotlight cone axis
        float halfAngle = flashlightLight.spotAngle * 0.5f + angleToleranceDegrees;
        float maxRange = flashlightLight.range + rangeTolerance;

        for (int i = 0; i < bodySamplePoints.Length; i++)
        {
            Vector3 sample = transform.TransformPoint(bodySamplePoints[i]);
            Vector3 toSample = sample - lightPosition;
            float distance = toSample.magnitude;

            if (distance > maxRange) continue;   // outside the beam's reach
            if (distance < 0.05f) return true;   // light is basically touching the body

            if (Vector3.Angle(lightDirection, toSample) > halfAngle) continue; // outside the cone

            if (!requireLineOfSight || HasClearLineOfSight(lightPosition, sample))
            {
                return true; // this body point is genuinely lit
            }
        }

        return false;
    }

    /// <summary>
    /// True when no solid object sits between the light and the given body point.
    /// The enemy's own colliders and the light holder's colliders never block
    /// (the ray ends inside the enemy, and a light can't be blocked by its owner).
    /// </summary>
    private bool HasClearLineOfSight(Vector3 from, Vector3 to)
    {
        Vector3 direction = to - from;
        float distance = direction.magnitude;
        if (distance < 0.0001f) return true;
        direction /= distance;

        int hitCount = Physics.RaycastNonAlloc(from, direction, hitBuffer, distance,
                                               blockingLayers, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = hitBuffer[i].collider;
            if (hitCollider == null) continue;

            // Never let the enemy block its own light.
            if (hitCollider.transform.IsChildOf(transform)) continue;

            // Never let the player holding the flashlight block their own light.
            if (flashlightTransform != null &&
                hitCollider.transform.IsChildOf(flashlightTransform.root)) continue;

            // Something solid stands between the light and the enemy's body.
            return false;
        }

        return true;
    }

    private void FindFlashlightIfNeeded()
    {
        if (flashlightTransform != null) return;

        GameObject found = null;

        if (!string.IsNullOrEmpty(flashlightTag))
        {
            try
            {
                found = GameObject.FindWithTag(flashlightTag);
            }
            catch (UnityException)
            {
                // The tag itself is not defined in the project yet — warn once,
                // then keep retrying silently in case it gets added later.
                if (!loggedTagWarning)
                {
                    loggedTagWarning = true;
                    Debug.LogWarning("[FlashlightDetector] Tag '" + flashlightTag + "' is not defined in this project. " +
                                     "Create it under Edit > Project Settings > Tags and Layers, then assign it to the " +
                                     "flashlight Spot Light GameObject.", this);
                }
            }
        }

        if (found == null)
        {
            // Keep retrying silently (the scene may still be loading), but after
            // ~2 seconds of searching, hint once at the likely setup mistake.
            failedSearchCount++;
            if (!loggedMissingWarning && failedSearchCount > 120)
            {
                loggedMissingWarning = true;
                Debug.LogWarning("[FlashlightDetector] No active object tagged '" + flashlightTag + "' found in the scene. " +
                                 "Make sure the flashlight Spot Light exists, is active, and carries the '" +
                                 flashlightTag + "' tag.", this);
            }
            return;
        }

        // The Light is expected on the tagged object itself; fall back to its
        // children so slightly different setups still work.
        Light light = found.GetComponent<Light>();
        if (light == null) light = found.GetComponentInChildren<Light>();

        if (light == null)
        {
            if (!loggedNoLightWarning)
            {
                loggedNoLightWarning = true;
                Debug.LogWarning("[FlashlightDetector] The object tagged '" + flashlightTag + "' has no Light component. " +
                                 "The detector needs the flashlight's Spot Light (for angle, range and on/off state).", this);
            }
            return;
        }

        flashlightLight = light;
        flashlightTransform = light.transform; // cone position/axis come from the Light itself
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    private void OnDrawGizmosSelected()
    {
        if (bodySamplePoints == null) return;

        // Visualise the body points that get tested against the light.
        Gizmos.color = Color.yellow;
        for (int i = 0; i < bodySamplePoints.Length; i++)
        {
            Gizmos.DrawWireSphere(transform.TransformPoint(bodySamplePoints[i]), 0.08f);
        }
    }
}
