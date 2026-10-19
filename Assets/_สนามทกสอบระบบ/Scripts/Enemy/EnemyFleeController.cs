using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Run away" behaviour for the Disappear stage (v1.1).
///
/// The enemy does not vanish on the spot any more: it turns away from the
/// player and RUNS, then despawns at the end of the run (shrinking to zero
/// while still running instead of popping out of existence).
///
/// v1.1 CHANGES (user feedback: the turning circle was too wide — the enemy
/// sometimes ran TOWARD the player just to turn itself around):
/// 1. Snap Turn At Start (ON by default): the enemy instantly faces the
///    run-away direction the moment the flee begins — no turning circle, and
///    it can never run toward the player while turning. The initial direction
///    is already steered clear of walls, so it never snaps into an obstacle.
/// 2. The run now follows the PATH direction (away from the player from the
///    very first frame) instead of the body's old facing, so even with the
///    snap turned OFF the enemy never moves toward the player while it
///    rotates — it just looks like a short backpedal.
/// 3. New default end condition — OUT OF PLAYER VIEW: the enemy keeps
///    running until the player can no longer see it (off-screen, behind the
///    camera, beyond the View Distance, or hidden behind a wall / prop),
///    and only then does it despawn. The old fixed timer is still available
///    (End Condition = Fixed Duration). A Max Flee Duration safety cap ends
///    the run even if the player keeps watching or chasing the enemy.
///
/// v1.2 FIX (user console report: "Setting linear velocity of a kinematic
/// body is not supported"): the run-away used to re-enable the enemy
/// controller BEFORE handing the Rigidbody back its dynamic state, so the
/// controller's very first velocity write (the OnEnable hard-stop) landed on
/// a body that was still kinematic from the flee takeover — one console
/// warning per flee. The restore order is now: Rigidbody FIRST, controller
/// SECOND, both in FinishFlee() and in the emergency OnDisable() cleanup.
/// Nothing else changed.
///
/// v1.3 FIX (user report: "when enemy enter disappear stage, it runs straight
/// through terrain — it looks like the enemy ignores collision during this
/// stage"). Root cause: while fleeing, the Rigidbody is deliberately KINEMATIC
/// (so the run movement and physics can never fight each other), and the run
/// direction is flattened to the horizontal plane — but a kinematic body gets
/// NO gravity and NO collision pushback from the ground, so on any rising
/// terrain the enemy kept its old height while the hill rose around it and
/// simply tunneled into the slope. THE FIX: GROUND FOLLOWING — every frame
/// of the run, the script now raycasts straight down (from above the pivot,
/// so up-slopes are caught too) and glues the enemy's FEET onto whatever
/// surface is found: uphill it climbs, downhill it descends, ledges keep the
/// height. The feet offset is auto-measured from the enemy's colliders when
/// the flee starts. Everything is Inspector-adjustable (Follow Ground is ON
/// by default; turn it OFF only on perfectly flat levels). No other stage is
/// affected: waiting / chasing / stunned / disappearing still use the normal
/// dynamic Rigidbody with gravity, exactly as before.
///
/// HOW TO HOOK IT UP (unchanged from v1.0 — 2 small edits, do NOT replace
/// your whole enemy controller script):
///
///   1. Add this component to the enemy prefab (same GameObject as your
///      enemy controller).
///
///   2. In your enemy controller, where the Disappear (blue) stage ends and
///      the enemy used to despawn, call:
///
///          EnemyFleeController flee = GetComponent<EnemyFleeController>();
///          if (flee != null)
///              flee.BeginFlee(FinishDespawn, this);  // your original despawn method
///          else
///              FinishDespawn();
///
///      Passing "this" pauses your controller while the enemy runs away so
///      the two scripts can never fight over the enemy; it is re-enabled
///      automatically right before your despawn code runs.
///
/// Everything else (spawner, respawn cooldown, flashlight stun, Esc pause,
/// colors) is untouched. All values are adjustable in the Inspector.
/// </summary>
[DisallowMultipleComponent]
public class EnemyFleeController : MonoBehaviour
{
    public enum FleeEndCondition { OutOfView, FixedDuration }

    [Header("Flee Movement")]
    [Tooltip("Run speed while fleeing. Set this HIGHER than your chase speed so it looks like an escape.")]
    public float fleeSpeed = 6f;

    [Tooltip("How fast the run path can curve while steering around obstacles (degrees per second).")]
    public float turnSpeed = 240f;

    [Tooltip("Keeps updating the run-away direction while the player moves. OFF = runs in one straight line.")]
    public bool trackPlayerWhileFleeing = true;

    [Header("Turning Away (v1.1 - fixes the wide turning circle)")]
    [Tooltip("ON (default): the enemy instantly faces the run-away direction the moment the flee starts - no turning circle, it can never run toward the player just to turn around. OFF: the body rotates smoothly at Turn Speed while the enemy already runs away (looks like a short backpedal).")]
    public bool snapTurnAtStart = true;

    [Header("When Does The Run End?")]
    [Tooltip("OutOfView (default): the enemy runs until the player can no longer see it (off-screen / behind the camera / beyond View Distance / hidden behind a wall), then despawns. FixedDuration: the old behaviour - runs for Flee Duration seconds, then despawns.")]
    public FleeEndCondition endCondition = FleeEndCondition.OutOfView;

    [Tooltip("End Condition = Fixed Duration only: how many seconds the enemy runs before it despawns.")]
    public float fleeDuration = 3f;

    [Tooltip("End Condition = OutOfView only: farther than this from the camera (or from the player when no camera exists) counts as out of view.")]
    public float viewDistance = 25f;

    [Tooltip("End Condition = OutOfView only: extra margin beyond the screen edges that still counts as 'visible'. 0 = it may despawn the moment it touches the screen edge.")]
    public float viewportEdgeMargin = 0.1f;

    [Tooltip("End Condition = OutOfView only: a wall or prop between the camera and the enemy counts as out of view.")]
    public bool occlusionCountsAsOutOfView = true;

    [Tooltip("End Condition = OutOfView only: the enemy must STAY out of view this long before it despawns (prevents vanishing during one-frame flickers at the screen edge).")]
    public float outOfViewConfirmTime = 0.2f;

    [Tooltip("End Condition = OutOfView only: minimum run time before the out-of-view despawn may trigger (even if the enemy is already unseen).")]
    public float minFleeTimeBeforeDespawn = 0.5f;

    [Tooltip("Safety cap in seconds: the run ALWAYS ends after this long, even if the player keeps watching or chasing the enemy.")]
    public float maxFleeDuration = 8f;

    [Header("Player Reference")]
    [Tooltip("Optional: drag your Player here. If empty, the script finds the object tagged 'Player'.")]
    public Transform playerOverride;

    [Header("Simple Wall Avoidance (no NavMesh needed)")]
    [Tooltip("Steers around walls / trees while running (checks a small fan of directions and takes the clearest one).")]
    public bool avoidObstacles = true;

    [Tooltip("How far ahead the enemy checks for obstacles.")]
    public float obstacleCheckDistance = 2.5f;

    [Tooltip("Height of the obstacle check ray (about half the enemy height).")]
    public float obstacleRayHeight = 0.5f;

    [Tooltip("Which layers count as obstacles (everything by default; the enemy itself is always ignored).")]
    public LayerMask obstacleMask = ~0;

    [Header("Ground Following (v1.3 - the terrain fix)")]
    [Tooltip("ON (default): while the enemy runs away it raycasts straight down every frame and glues its feet onto the ground surface - uphill AND downhill. This is the fix for the enemy sinking / running straight through terrain during the run-away (the flee body is kinematic, so physics alone cannot rest it on the ground). OFF: the old flat-height run - use OFF only on perfectly flat levels.")]
    public bool followGround = true;

    [Tooltip("Layers that count as ground for the terrain fix. Everything by default; the enemy's own colliders are always skipped automatically.")]
    public LayerMask groundMask = ~0;

    [Tooltip("The ground probe raycast starts this far ABOVE the enemy's pivot and points straight down, so RISING terrain (uphill slopes) is caught as well as drops. Roughly half the enemy's height is a good value.")]
    public float groundProbeUp = 1f;

    [Tooltip("How far BELOW its feet the enemy still snaps DOWN onto the ground. Running past a ledge that is deeper than this keeps the current height instead of teleporting down the cliff.")]
    public float maxGroundDrop = 3f;

    [Tooltip("ON (default): the distance from the enemy's pivot down to the bottom of its feet is measured automatically from its colliders each time the flee starts (disabled or trigger colliders are ignored). Turn OFF to use Manual Foot Offset instead.")]
    public bool autoDetectFootOffset = true;

    [Tooltip("Used when Auto Detect Foot Offset is OFF (or the enemy has no colliders): distance from the enemy's pivot (its transform position) down to the bottom of its feet. A 2-unit-tall capsule with its pivot in the middle uses 1.")]
    public float manualFootOffset = 1f;

    [Header("Out-Of-View Check Point")]
    [Tooltip("Height on the enemy body that is checked for visibility (about half the enemy height).")]
    public float enemyAimHeight = 1f;

    [Header("Despawn Visual")]
    [Tooltip("Shrink the enemy to zero right before despawning (instead of popping out of existence). It keeps running while shrinking.")]
    public bool shrinkBeforeDespawn = true;

    [Tooltip("How long the shrink takes.")]
    public float shrinkDuration = 0.35f;

    [Header("Controller Pause Options")]
    [Tooltip("Also stops all coroutines on your enemy controller while it flees. Enable only if your enemy controller uses coroutines.")]
    public bool stopCoroutinesOnPause = false;

    // ---------------- runtime state ----------------
    Action onDespawned;
    MonoBehaviour pausedController;
    readonly List<MonoBehaviour> autoPaused = new List<MonoBehaviour>();
    Rigidbody enemyBody;
    bool tookOverBody;
    bool fleeing;
    bool shrinking;
    float fleeTimer;
    float shrinkTimer;
    float outOfViewTimer;
    float cameraSearchTimer;
    Vector3 fleeDirection;
    Vector3 originalScale;
    Transform playerTransform;
    Camera cachedCamera;
    float footOffset; // v1.3 — pivot -> feet distance, measured when the flee starts

    static readonly RaycastHit[] hitBuffer = new RaycastHit[8];
    static readonly RaycastHit[] groundHitBuffer = new RaycastHit[16];
    static readonly float[] steerAngles = { -35f, 35f, -70f, 70f, -105f, 105f, -140f, 140f, 180f };

    /// <summary>True while the enemy is running away.</summary>
    public bool IsFleeing { get { return fleeing; } }

    void Awake()
    {
        originalScale = transform.localScale;
    }

    void OnEnable()
    {
        // fresh state every time the spawner reuses this enemy
        fleeing = false;
        shrinking = false;
        outOfViewTimer = 0f;
        cameraSearchTimer = 0f;
        if (playerTransform == null)
            playerTransform = ResolvePlayer();
        transform.localScale = originalScale;
    }

    void OnDisable()
    {
        // if the enemy is deactivated mid-flee (spawner force-despawn),
        // clean everything up so the next reuse starts fresh
        if (fleeing)
        {
            fleeing = false;
            shrinking = false;
            // v1.2: Rigidbody back to dynamic FIRST, controller re-enabled
            // SECOND — the controller writes a velocity the moment it wakes
            // up, and a still-kinematic body would make Unity warn.
            RestoreBody();
            RestoreController();
            onDespawned = null;
        }
        transform.localScale = originalScale;
    }

    /// <summary>Clamps the v1.3 terrain-fix values so the ground following can
    /// never receive nonsense input from the Inspector. (Deliberately only
    /// the new v1.3 fields — everything else keeps its exact v1.2 behaviour.)</summary>
    void OnValidate()
    {
        groundProbeUp = Mathf.Max(0.1f, groundProbeUp);
        maxGroundDrop = Mathf.Max(0f, maxGroundDrop);
        manualFootOffset = Mathf.Max(0f, manualFootOffset);
    }

    /// <summary>
    /// Simple version: pauses every other script on the enemy so nothing fights the flee.
    /// </summary>
    public bool BeginFlee(Action despawnCallback)
    {
        return BeginFleeInternal(despawnCallback, null, true);
    }

    /// <summary>
    /// Recommended version: pass your original despawn method and "this" (your controller).
    /// The enemy runs away, then your despawn code runs.
    /// </summary>
    public bool BeginFlee(Action despawnCallback, MonoBehaviour controllerToPause)
    {
        return BeginFleeInternal(despawnCallback, controllerToPause, false);
    }

    bool BeginFleeInternal(Action despawnCallback, MonoBehaviour controllerToPause, bool autoPause)
    {
        if (fleeing || !enabled)
            return false;

        fleeing = true;
        fleeTimer = 0f;
        shrinkTimer = 0f;
        outOfViewTimer = 0f;
        shrinking = false;
        onDespawned = despawnCallback;
        originalScale = transform.localScale;
        playerTransform = ResolvePlayer();
        cachedCamera = Camera.main;

        // v1.1: the initial run direction — straight away from the player, and
        // already steered clear of anything right in front, so the snap below
        // can never face the enemy straight into a wall.
        fleeDirection = AwayFromPlayer();
        if (avoidObstacles)
            fleeDirection = SteerAroundObstacles(fleeDirection);

        TakeOverBody();
        PauseController(controllerToPause, autoPause);

        // v1.1 — THE TURNING-CIRCLE FIX: face the run direction INSTANTLY
        // (yaw only, the enemy stays level). No wide circle, and the enemy can
        // never run toward the player just to turn itself around.
        if (snapTurnAtStart)
            transform.rotation = Quaternion.LookRotation(fleeDirection, Vector3.up);

        // v1.3 — the terrain fix starts here: know where the feet are, and
        // start the run already standing ON the ground (also corrects any
        // tiny ground overlap the stun freeze left behind).
        ComputeFootOffset();
        SnapToGround();

        return true;
    }

    void Update()
    {
        if (!fleeing)
            return;

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return; // Esc menu (timeScale 0) freezes the run-away too, like everything else

        if (shrinking)
        {
            // keep running while shrinking, then despawn
            MoveStep(dt);
            shrinkTimer += dt;
            float t = Mathf.Clamp01(shrinkTimer / Mathf.Max(0.01f, shrinkDuration));
            transform.localScale = Vector3.Lerp(originalScale, Vector3.zero, t);
            if (t >= 1f)
                FinishFlee();
            return;
        }

        MoveStep(dt);
        fleeTimer += dt;

        // re-find the camera at most twice a second while we still have none
        // (cheap: Camera.main is only polled when it is actually missing)
        if (cachedCamera == null)
        {
            cameraSearchTimer += dt;
            if (cameraSearchTimer >= 0.5f)
            {
                cameraSearchTimer = 0f;
                cachedCamera = Camera.main;
            }
        }

        if (endCondition == FleeEndCondition.FixedDuration)
        {
            if (fleeTimer >= fleeDuration)
                StartShrink();
            return;
        }

        // ---------------- OutOfView end condition ----------------
        if (IsOutOfView())
            outOfViewTimer += dt;
        else
            outOfViewTimer = 0f; // visible again -> the confirm timer restarts

        // gone from the player's view (and it stayed gone long enough)
        if (outOfViewTimer >= outOfViewConfirmTime && fleeTimer >= minFleeTimeBeforeDespawn)
        {
            StartShrink();
            return;
        }

        // safety net: the player keeps watching / chasing the enemy
        if (fleeTimer >= maxFleeDuration)
            StartShrink();
    }

    void MoveStep(float dt)
    {
        Vector3 desired = trackPlayerWhileFleeing ? AwayFromPlayer() : fleeDirection;
        if (avoidObstacles)
            desired = SteerAroundObstacles(desired);

        // rate-limit how fast the run PATH can curve (steering stays smooth)
        float maxRadians = turnSpeed * Mathf.Deg2Rad * dt;
        Vector3 rotated = Vector3.RotateTowards(fleeDirection, desired, maxRadians, 0f);
        fleeDirection = rotated.sqrMagnitude > 0.000001f ? rotated.normalized : FlatForward();

        // the body faces the run path (yaw only, the enemy stays level - no sinking).
        // With Snap Turn At Start ON, body and path are aligned from frame one.
        Quaternion face = Quaternion.LookRotation(fleeDirection, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, face, turnSpeed * dt);

        // v1.1 - THE MOVEMENT FIX: move along the PATH direction, never along
        // the body's (possibly lagging) facing. The enemy moves away from the
        // player from the very first frame, even while the body is still turning.
        transform.position += fleeDirection * (fleeSpeed * dt);

        // v1.3 - THE TERRAIN FIX: the horizontal step above just moved the
        // pivot on a flat plane; this keeps the enemy glued to the ground
        // under that new position (uphill climbs, downhill descends), so the
        // run can never tunnel through terrain.
        SnapToGround();
    }

    // ------------------------------------------------------------------ //
    // v1.3 - ground following (the terrain fix)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Measures how far the enemy's pivot sits above the bottom of its feet
    /// (the lowest non-trigger collider found under this object). While
    /// fleeing the body is KINEMATIC — physics no longer rests it on the
    /// ground — so the script needs this offset to place the feet exactly ON
    /// the surface. Disabled colliders report empty bounds (Unity docs) and
    /// are skipped, so a disabled collider can never corrupt the measurement.
    /// </summary>
    void ComputeFootOffset()
    {
        footOffset = Mathf.Max(0f, manualFootOffset);
        if (!autoDetectFootOffset) return;

        Collider[] cols = GetComponentsInChildren<Collider>();
        float lowest = float.MaxValue;
        bool found = false;
        for (int i = 0; i < cols.Length; i++)
        {
            Collider c = cols[i];
            if (c == null || c.isTrigger) continue;

            Bounds b = c.bounds;
            if (b.size.y <= 0f || float.IsInfinity(b.min.y)) continue; // empty bounds = disabled collider

            if (b.min.y < lowest)
            {
                lowest = b.min.y;
                found = true;
            }
        }

        // No usable colliders (a collider-less rig) -> keep the manual offset.
        if (found)
            footOffset = Mathf.Max(0f, transform.position.y - lowest);
    }

    /// <summary>
    /// v1.3 — THE TERRAIN FIX. Raycast straight down from above the pivot and
    /// place the feet on whatever surface is found (the highest hit below the
    /// probe wins — RaycastNonAlloc results are not sorted). Rising ground
    /// lifts the enemy up the slope, dropping ground lets it follow the
    /// terrain down, and running past a ledge deeper than Max Ground Drop
    /// simply keeps the current height. The enemy's own colliders never count
    /// as ground. Works with AND without a Rigidbody — the flat horizontal
    /// step in MoveStep() tunnels into terrain either way; this puts the
    /// enemy back on top of it.
    /// </summary>
    void SnapToGround()
    {
        if (!followGround) return;

        Vector3 origin = transform.position + Vector3.up * groundProbeUp;
        float maxDistance = groundProbeUp + footOffset + maxGroundDrop;

        int count = Physics.RaycastNonAlloc(origin, Vector3.down, groundHitBuffer,
                                            maxDistance, groundMask, QueryTriggerInteraction.Ignore);
        int n = Mathf.Min(count, groundHitBuffer.Length);

        RaycastHit best = default(RaycastHit);
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            Collider col = groundHitBuffer[i].collider;
            if (col == null) continue;                        // empty slot
            if (col.transform.IsChildOf(transform)) continue; // never ground on ourselves

            if (!found || groundHitBuffer[i].point.y > best.point.y)
            {
                best = groundHitBuffer[i];
                found = true;
            }
        }

        if (!found) return; // ledge / no ground in range: keep the current height

        Vector3 p = transform.position;
        p.y = best.point.y + footOffset;
        transform.position = p;
    }

    // ------------------------------------------------------------------ //
    // v1.1 - "is the enemy still inside the player's view?" checks

    bool IsOutOfView()
    {
        Vector3 point = transform.position + Vector3.up * enemyAimHeight;
        Camera cam = cachedCamera;
        Transform player = playerOverride != null ? playerOverride : playerTransform;

        if (cam == null)
        {
            // no camera in the scene: fall back to a distance check from the player
            if (player == null)
                return true;
            Vector3 flat = transform.position - player.position;
            flat.y = 0f;
            return flat.sqrMagnitude > viewDistance * viewDistance;
        }

        // hidden behind a wall / prop from the camera's position?
        if (occlusionCountsAsOutOfView && IsOccludedFromCamera(cam, point))
            return true;

        Vector3 vp = cam.WorldToViewportPoint(point);

        // behind the camera, or farther than the view distance?
        if (vp.z <= 0f || vp.z > viewDistance)
            return true;

        // off-screen (with the small edge margin)?
        float m = viewportEdgeMargin;
        return vp.x < -m || vp.x > 1f + m || vp.y < -m || vp.y > 1f + m;
    }

    bool IsOccludedFromCamera(Camera cam, Vector3 point)
    {
        if (Physics.Linecast(cam.transform.position, point, out RaycastHit hit, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider != null && hit.collider.transform.IsChildOf(transform))
                return false; // we only hit ourselves = the enemy is still visible
            return true;      // something is between the camera and the enemy
        }
        return false;
    }

    // ------------------------------------------------------------------ //

    Vector3 SteerAroundObstacles(Vector3 desired)
    {
        Vector3 origin = transform.position + Vector3.up * obstacleRayHeight;

        if (!IsBlocked(origin, desired))
            return desired; // straight ahead is clear

        for (int i = 0; i < steerAngles.Length; i++)
        {
            Vector3 candidate = Quaternion.Euler(0f, steerAngles[i], 0f) * desired;
            if (!IsBlocked(origin, candidate))
                return candidate; // smallest clear deviation wins
        }

        return -desired; // boxed in: run back, the flee timer still despawns the enemy
    }

    bool IsBlocked(Vector3 origin, Vector3 direction)
    {
        int count = Physics.RaycastNonAlloc(origin, direction, hitBuffer, obstacleCheckDistance, obstacleMask, QueryTriggerInteraction.Ignore);
        int n = Mathf.Min(count, hitBuffer.Length);
        for (int i = 0; i < n; i++)
        {
            Collider col = hitBuffer[i].collider;
            if (col == null)
                continue;
            if (col.transform.IsChildOf(transform))
                continue; // never hit ourselves
            return true;
        }
        return false;
    }

    void StartShrink()
    {
        if (!shrinkBeforeDespawn)
        {
            FinishFlee();
            return;
        }
        shrinking = true;
        shrinkTimer = 0f;
    }

    void FinishFlee()
    {
        fleeing = false;
        shrinking = false;
        transform.localScale = originalScale;

        // v1.2 — RESTORE ORDER FIX: the Rigidbody must be dynamic again BEFORE
        // the enemy controller wakes up. RestoreController() re-enabling the
        // controller fires its OnEnable() immediately, which writes a stop
        // velocity; on a still-kinematic body Unity answers with
        // "Setting linear velocity of a kinematic body is not supported".
        RestoreBody();
        RestoreController();

        Action callback = onDespawned;
        onDespawned = null;
        if (callback != null)
            callback.Invoke();
    }

    void PauseController(MonoBehaviour controllerToPause, bool autoPause)
    {
        if (controllerToPause != null)
        {
            if (controllerToPause.enabled)
            {
                pausedController = controllerToPause;
                if (stopCoroutinesOnPause)
                    controllerToPause.StopAllCoroutines();
                controllerToPause.enabled = false;
            }
            return;
        }

        if (!autoPause)
            return;

        // simple mode: pause every other script on the enemy root so nothing
        // fights the flee (they are all restored right before the despawn)
        MonoBehaviour[] scripts = GetComponents<MonoBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] == this || !scripts[i].enabled)
                continue;
            if (stopCoroutinesOnPause)
                scripts[i].StopAllCoroutines();
            scripts[i].enabled = false;
            autoPaused.Add(scripts[i]);
        }
    }

    void RestoreController()
    {
        if (pausedController != null)
        {
            pausedController.enabled = true;
            pausedController = null;
        }
        for (int i = 0; i < autoPaused.Count; i++)
        {
            if (autoPaused[i] != null && !autoPaused[i].enabled)
                autoPaused[i].enabled = true;
        }
        autoPaused.Clear();
    }

    void TakeOverBody()
    {
        // if the enemy has a dynamic Rigidbody, make it kinematic while fleeing
        // so physics and the flee movement can never fight (and no sinking)
        enemyBody = GetComponent<Rigidbody>();
        tookOverBody = enemyBody != null && !enemyBody.isKinematic;
        if (tookOverBody)
            enemyBody.isKinematic = true;
    }

    void RestoreBody()
    {
        if (tookOverBody && enemyBody != null)
            enemyBody.isKinematic = false;
        tookOverBody = false;
        enemyBody = null;
    }

    Vector3 AwayFromPlayer()
    {
        Transform player = playerOverride != null ? playerOverride : playerTransform;
        if (player == null)
            return FlatForward();

        Vector3 away = transform.position - player.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
            return FlatForward();
        return away.normalized;
    }

    Vector3 FlatForward()
    {
        Vector3 f = transform.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.0001f)
            return Vector3.forward;
        return f.normalized;
    }

    Transform ResolvePlayer()
    {
        if (playerOverride != null)
            return playerOverride;

        GameObject go = GameObject.FindGameObjectWithTag("Player");
        if (go != null)
            return go.transform;

        return null;
    }
}