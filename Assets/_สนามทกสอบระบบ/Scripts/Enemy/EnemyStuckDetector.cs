using UnityEngine;

/// <summary>
/// AUTO-RESPAWN WATCHDOG for <see cref="EnemyController"/> (v1.0).
///
/// An enemy can end up permanently broken in two ways — wedged against a
/// wall/prop so it can never reach the player, or left so far behind that
/// it can never catch up. This component watches for both and recycles the
/// enemy through <see cref="EnemySpawner"/> so it can never stay broken:
///
///   1. STUCK (hunt stage): while the enemy is in the Moving state (tinted
///      GREEN — actively hunting the player) its REAL planar speed is
///      measured as displacement over a small window. If it barely moves
///      (below the Stuck Speed Threshold, default 1 unit/s — a healthy
///      chase runs at the controller's Move Speed, default 3) for Stuck
///      Time Limit seconds CONTINUOUSLY (default 10 s), it counts as
///      wedged on geometry.
///
///   2. OUT OF SPAWN RANGE: if the enemy stays farther from the player than
///      the spawner's outer spawn ring (Max Spawn Distance — auto-read from
///      the spawner so both always match) for Out Of Range Time Limit
///      seconds CONTINUOUSLY (default 10 s), it counts as lost — e.g. the
///      player outran it and it will never catch up, or it fell somewhere
///      far away.
///
/// RECOVERY: the moment either condition has persisted for its full time
/// limit, the broken enemy is removed and the spawner queues a replacement
/// that spawns OUT OF SIGHT after Respawn Delay seconds (default 5 s) —
/// exactly like a flashlight kill, just with the short delay. The respawn
/// still respects every spawner rule automatically: the Safe Zone pause,
/// the Spawn Delay and Max Alive Enemies are all handled by the spawner
/// itself, and the new enemy lands on tagged ground out of the player's
/// view.
///
/// States where the enemy is LEGITIMATELY stationary never trigger the
/// stuck check — Waiting (just spawned, stands still), Stopped (standing
/// next to the player), Stunned / Disappearing (flashlight / safe-zone
/// stages) and the <see cref="EnemyFleeController"/> run-away (this
/// component's controller is disabled during it) all pause and reset both
/// timers automatically. The whole watchdog also freezes while the pause
/// menu is open (GamePauseManager), just like the spawner.
///
/// SETUP: add this component to the enemy prefab root, next to
/// EnemyController. It finds the EnemyController (same GameObject), the
/// player (XR Origin first, then the tag 'Player') and the scene's
/// EnemySpawner automatically — or pin any of them manually in the
/// Inspector. Requires EnemySpawner v1.4+ (its QueueStuckRespawn API); with
/// an older spawner or none in the scene this watchdog logs a warning and
/// disables itself instead of misfiring.
///
/// v1.1 – VR TARGET SUPPORT (XR Origin first): the out-of-range check now
/// measures distance to the player's XR Origin (XR Rig) when one exists in
/// the scene — the rig that actually moves in VR — instead of whatever old
/// object happens to carry the 'Player' tag. Non-VR scenes are unchanged.
///
/// Every tuning value is exposed in the Inspector.
/// </summary>
[DisallowMultipleComponent]
public class EnemyStuckDetector : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]

    [Tooltip("The enemy brain this watchdog monitors. Auto-found on this GameObject when left empty.")]
    [SerializeField] private EnemyController controller;

    [Tooltip("The player the enemy chases — used for the out-of-range check. Auto-found when " +
             "left empty: prefers the XR Origin (XR Rig) in VR scenes, then the 'Player' tag.")]
    [SerializeField] private Transform player;

    [Tooltip("The spawner that owns respawning. Auto-found in the scene when left empty (pin it manually " +
             "if you ever run several spawners and want a specific one).")]
    [SerializeField] private EnemySpawner spawner;

    [Header("Stuck Detection (hunt stage – green)")]

    [Tooltip("While the enemy hunts (Moving state, green) its real planar speed is measured. Below this " +
             "value (units per second) it counts as barely moving. Default 1 — a healthy chase moves at " +
             "the controller's Move Speed (default 3), so anything under 1 means it is wedged.")]
    [SerializeField] private float stuckSpeedThreshold = 1f;

    [Tooltip("Seconds the hunt-stage speed must stay below the threshold CONTINUOUSLY before the enemy " +
             "counts as permanently stuck (default 10). Recovering above the threshold resets the timer.")]
    [SerializeField] private float stuckTimeLimit = 10f;

    [Tooltip("Speed is measured as real displacement over this window (seconds), so physics jitter and " +
             "single-frame hiccups can never fake 'moving'. 0.5 is a good default.")]
    [SerializeField] private float speedSampleWindow = 0.5f;

    [Header("Out-Of-Range Detection")]

    [Tooltip("Use the spawner's Max Spawn Distance as the out-of-range distance, so this check always " +
             "matches the ring the enemy spawns on (recommended — change the spawner's ring and the " +
             "watchdog follows automatically).")]
    [SerializeField] private bool useSpawnerRange = true;

    [Tooltip("Out-of-range distance used when 'Use Spawner Range' is off (planar distance from the " +
             "player, default 30 — the spawner's default outer ring).")]
    [SerializeField] private float outOfRangeDistance = 30f;

    [Tooltip("Seconds the enemy must stay out of range CONTINUOUSLY before it counts as lost " +
             "(default 10). Getting back in range resets the timer.")]
    [SerializeField] private float outOfRangeTimeLimit = 10f;

    [Header("Respawn")]

    [Tooltip("Seconds between removing the broken enemy and its replacement spawning (queued on the " +
             "spawner, default 5). The replacement still waits for any Safe Zone pause / Spawn Delay.")]
    [SerializeField] private float respawnDelay = 5f;

    [Tooltip("Log one Console line whenever the watchdog recycles an enemy (and why), so stuck events " +
             "are visible while testing.")]
    [SerializeField] private bool debugLog = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    // Condition timers (reset to 0 whenever the condition is not active).
    private float stuckTimer;
    private float outOfRangeTimer;

    // Windowed speed measurement.
    private Vector3 sampleStartPosition;
    private float sampleTimer;
    private float windowedPlanarSpeed;
    private bool wasHuntStage;

    // Last computed planar distance to the player (kept for read-outs).
    private float lastPlayerDistance;

    /// <summary>Real planar speed over the last completed sample window (units per second) — read-only.</summary>
    public float CurrentPlanarSpeed
    {
        get { return float.IsPositiveInfinity(windowedPlanarSpeed) ? 0f : windowedPlanarSpeed; }
    }

    /// <summary>Planar distance from the player right now — read-only.</summary>
    public float CurrentPlayerDistance
    {
        get { return lastPlayerDistance; }
    }

    /// <summary>Seconds of continuous stuck time accumulated so far (0 = healthy) — read-only.</summary>
    public float StuckTimeAccumulated
    {
        get { return stuckTimer; }
    }

    /// <summary>Seconds of continuous out-of-range time accumulated so far (0 = in range) — read-only.</summary>
    public float OutOfRangeTimeAccumulated
    {
        get { return outOfRangeTimer; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (controller == null) controller = GetComponent<EnemyController>();

        if (player == null)
        {
            player = ResolvePlayerReference();
        }

        if (spawner == null) spawner = FindSpawnerInScene();
    }

    private void Start()
    {
        // Second chance for late scene setups (the Awake scan may have run
        // before the spawner existed).
        if (spawner == null) spawner = FindSpawnerInScene();

        if (controller == null)
        {
            Debug.LogWarning("[EnemyStuckDetector] No EnemyController on '" + name +
                             "' — this watchdog disables itself (nothing to monitor).", this);
            enabled = false;
            return;
        }

        if (spawner == null)
        {
            Debug.LogWarning("[EnemyStuckDetector] No EnemySpawner found in the scene — a recycled enemy " +
                             "could never come back. This watchdog disables itself. Add an EnemySpawner " +
                             "(v1.4+) to the scene, or assign one manually.", this);
            enabled = false;
            return;
        }

        if (player == null)
        {
            // Second chance — the XR rig may have become visible to
            // FindAnyObjectByType after the first Awake pass.
            player = ResolvePlayerReference();
        }

        if (player == null)
        {
            Debug.LogWarning("[EnemyStuckDetector] No player found (no XR Origin, nothing tagged 'Player', " +
                             "none assigned) — the out-of-range check needs a player. This watchdog " +
                             "disables itself. (EnemyController refuses to run without a player too.)", this);
            enabled = false;
        }
    }

    private void OnEnable()
    {
        // Fresh start for both a brand-new spawn and pooled reuse — no
        // leftover timers or half-finished speed windows can survive.
        stuckTimer = 0f;
        outOfRangeTimer = 0f;
        sampleTimer = 0f;
        sampleStartPosition = transform.position;
        windowedPlanarSpeed = 0f;
        wasHuntStage = false;
        lastPlayerDistance = 0f;
    }

    private void Update()
    {
        // Fully frozen while the pause menu is open — the same rule the
        // spawner follows: no watchdog timer may tick while the game is
        // paused.
        if (GamePauseManager.IsPaused) return;

        // A missing or disabled controller means nobody is hunting (e.g. the
        // flee controller has taken over): both timers freeze and reset.
        bool controllerActive = controller != null && controller.isActiveAndEnabled;
        bool huntStage = controllerActive && controller.CurrentState == EnemyState.Moving;
        bool lifecycleState = controllerActive &&
            (controller.CurrentState == EnemyState.Waiting ||
             controller.CurrentState == EnemyState.Moving ||
             controller.CurrentState == EnemyState.Stopped);

        // Entering the hunt stage restarts the speed window, so the
        // measurement only ever covers hunt-stage movement (every other
        // stage stands still by design and would pollute the average).
        if (huntStage && !wasHuntStage)
        {
            sampleStartPosition = transform.position;
            sampleTimer = 0f;
            windowedPlanarSpeed = float.PositiveInfinity; // one full window of grace
        }
        wasHuntStage = huntStage;

        // ---- Measure the real planar speed over the sample window. ----
        sampleTimer += Time.deltaTime;
        if (sampleTimer >= speedSampleWindow)
        {
            Vector3 delta = transform.position - sampleStartPosition;
            delta.y = 0f;
            windowedPlanarSpeed = sampleTimer > 0f ? delta.magnitude / sampleTimer : 0f;
            sampleStartPosition = transform.position;
            sampleTimer = 0f;
        }

        // ---- Condition 1: STUCK — hunting (green) but barely moving. ----
        if (huntStage && windowedPlanarSpeed < stuckSpeedThreshold)
        {
            stuckTimer += Time.deltaTime;
        }
        else
        {
            stuckTimer = 0f;
        }

        // ---- Condition 2: OUT OF SPAWN RANGE. ----
        float range = ResolveOutOfRangeDistance();
        lastPlayerDistance = PlanarDistanceToPlayer();

        if (lifecycleState && lastPlayerDistance > range)
        {
            outOfRangeTimer += Time.deltaTime;
        }
        else
        {
            outOfRangeTimer = 0f;
        }

        // ---- Either condition persisting for its full limit recycles the enemy. ----
        if (stuckTimer >= stuckTimeLimit)
        {
            TriggerRespawn("stuck while hunting — planar speed " + CurrentPlanarSpeed.ToString("0.00") +
                           " stayed under " + stuckSpeedThreshold.ToString("0.00") + " for " +
                           stuckTimeLimit.ToString("0.#") + " s");
            return;
        }

        if (outOfRangeTimer >= outOfRangeTimeLimit)
        {
            TriggerRespawn("out of spawn range — distance " + lastPlayerDistance.ToString("0.0") +
                           " stayed beyond " + range.ToString("0.0") + " for " +
                           outOfRangeTimeLimit.ToString("0.#") + " s");
        }
    }

    /// <summary>Clamps Inspector values so the watchdog can never receive nonsense input.</summary>
    private void OnValidate()
    {
        stuckSpeedThreshold = Mathf.Max(0f, stuckSpeedThreshold);
        stuckTimeLimit = Mathf.Max(0.1f, stuckTimeLimit);
        speedSampleWindow = Mathf.Max(0.05f, speedSampleWindow);
        outOfRangeDistance = Mathf.Max(1f, outOfRangeDistance);
        outOfRangeTimeLimit = Mathf.Max(0.1f, outOfRangeTimeLimit);
        respawnDelay = Mathf.Max(0f, respawnDelay);
    }

    // ------------------------------------------------------------------ //
    // Detection helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The out-of-range distance: the spawner's outer ring when 'Use Spawner
    /// Range' is on (and a spawner exists), otherwise the manual value.
    /// </summary>
    private float ResolveOutOfRangeDistance()
    {
        if (useSpawnerRange && spawner != null)
        {
            return Mathf.Max(1f, spawner.MaxSpawnDistance);
        }
        return Mathf.Max(1f, outOfRangeDistance);
    }

    /// <summary>Planar (horizontal) distance to the player — the same measure the spawner's ring uses.</summary>
    private float PlanarDistanceToPlayer()
    {
        if (player == null) return 0f; // in range by definition — never a false trigger

        Vector3 delta = player.position - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    // ------------------------------------------------------------------ //
    // Recovery
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Queues the replacement on the spawner FIRST (with the respawn delay),
    /// then removes the broken enemy. The replacement itself spawns out of
    /// sight, on tagged ground, when the spawner's countdown expires — every
    /// spawner rule (Safe Zone pause, Spawn Delay, Max Alive Enemies) applies
    /// to it automatically.
    /// </summary>
    private void TriggerRespawn(string reason)
    {
        if (spawner != null)
        {
            float delay = Mathf.Max(0f, respawnDelay);
            spawner.QueueStuckRespawn(delay);

            if (debugLog)
            {
                Debug.Log("[EnemyStuckDetector] Recycled '" + name + "' — " + reason +
                          ". A replacement spawns in " + delay.ToString("0.#") + " s.", this);
            }
        }

        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ //
    // Scene scan
    // ------------------------------------------------------------------ //

    /// <summary>
    /// v1.1 – resolves the out-of-range measurement target, VR-first:
    ///   1. The explicit Inspector reference (always wins over everything).
    ///   2. An XR Origin (XR Rig) in the scene — the rig that actually moves
    ///      with the player in VR (the 'Player' tag alone is ambiguous there,
    ///      old non-VR players often keep the tag as well).
    ///   3. The classic 'Player' tag (non-VR scenes keep working unchanged).
    /// </summary>
    private Transform ResolvePlayerReference()
    {
        if (player != null) return player;

#if UNITY_2023_1_OR_NEWER
        Unity.XR.CoreUtils.XROrigin xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
#else
        Unity.XR.CoreUtils.XROrigin xrOrigin = FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
#endif
        if (xrOrigin != null) return xrOrigin.transform;

        GameObject found = GameObject.FindGameObjectWithTag("Player");
        return found != null ? found.transform : null;
    }

    /// <summary>
    /// Version-aware spawner lookup — the same pattern SafeZone v1.1 uses.
    /// Unity 2023+ (including Unity 6) gets the current FindAnyObjectByType,
    /// which has never been deprecated; older Unity gets FindObjectOfType,
    /// which only compiles there (pre-2023 never marks it obsolete). No
    /// Unity version prints a warning for the branch it actually compiles.
    /// </summary>
    private static EnemySpawner FindSpawnerInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return FindAnyObjectByType<EnemySpawner>();
#else
        return FindObjectOfType<EnemySpawner>();
#endif
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    private void OnDrawGizmosSelected()
    {
        Transform target = player;
        if (target == null)
        {
            // v1.1 – same XR-first resolution as at runtime (editor gizmo).
#if UNITY_2023_1_OR_NEWER
            Unity.XR.CoreUtils.XROrigin xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
#else
            Unity.XR.CoreUtils.XROrigin xrOrigin = FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
#endif
            if (xrOrigin != null) target = xrOrigin.transform;

            if (target == null)
            {
                GameObject found = GameObject.FindGameObjectWithTag("Player");
                if (found != null) target = found.transform;
            }
        }
        if (target == null) return;

        // The effective out-of-range ring (falls back to the manual value
        // when no spawner is pinned in the Inspector).
        float range = useSpawnerRange && spawner != null
            ? spawner.MaxSpawnDistance
            : outOfRangeDistance;
        range = Mathf.Max(1f, range);

        // Ring around the player — the enemy counts as lost beyond it.
        Gizmos.color = new Color(1f, 0.5f, 0f); // orange
        DrawRing(target.position, range);

        // Line to the player: red while currently out of range, gray otherwise.
        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        Gizmos.color = delta.magnitude > range ? Color.red : Color.gray;
        Gizmos.DrawLine(transform.position, target.position);
    }

    private static void DrawRing(Vector3 center, float radius)
    {
        const int segments = 64;
        Vector3 previous = center + new Vector3(radius, 0f, 0f);

        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
    }
}
