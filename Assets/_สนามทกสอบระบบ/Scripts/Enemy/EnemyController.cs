using System;
using UnityEngine;

/// <summary>
/// The lifecycle states of the enemy.
/// </summary>
public enum EnemyState
{
    /// <summary>Just spawned. Stands completely still for the configured wait time (default 2s).</summary>
    Waiting,
    /// <summary>Chasing the player. Placeholder model is tinted green.</summary>
    Moving,
    /// <summary>Inside the stop radius around the player's center. Placeholder model is tinted white again.</summary>
    Stopped,
    /// <summary>STUN STAGE — the flashlight is on the enemy (or it stands inside a SafeZone). Frozen in place and tinted red (placeholder for the enemy covering its face). The input must stay active continuously for Stun Duration to advance.</summary>
    Stunned,
    /// <summary>DISAPPEAR STAGE — fully stunned. Tinted blue (placeholder for the thrashing animation) for Disappear Duration, then the enemy despawns and the spawner rolls its random respawn cooldown.</summary>
    Disappearing
}

/// <summary>
/// Basic enemy behaviour for a single reusable enemy prefab.
///
/// Lifecycle:
///   1. Spawn (handled invisibly by <see cref="EnemySpawner"/>).
///   2. Wait  – stands perfectly still for Wait Time seconds (default 2).
///   3. Move  – walks toward the player at Move Speed (turns green).
///   4. Stop  – freezes once inside the Stop Distance radius around the
///              player's center (turns back to white).
///   If the player later moves away beyond Resume Distance, the enemy
///   starts chasing again (turns green).
///
/// Flashlight combat (v2.0):
///   Stun stage      – while the player's flashlight hits the enemy (see
///                     <see cref="FlashlightDetector"/>: inside the cone, in
///                     range, and nothing blocking the light), the enemy
///                     freezes in place and turns RED (placeholder for the
///                     enemy covering its face). The light must stay on it
///                     CONTINUOUSLY for Stun Duration (default 0.5s) — the
///                     anti-exploit timer. If the light breaks off early,
///                     the enemy resumes its previous state and chases again.
///   Disappear stage – after a full stun, the enemy turns BLUE (placeholder
///                     for the thrashing animation) for Disappear Duration
///                     (default 0.5s), then despawns. The spawner hears the
///                     OnDespawned event, waits a random 12–18s (adjustable)
///                     and respawns the enemy out of sight, restarting the
///                     whole lifecycle.
///
/// v1.1 – Rigidbody support: when the prefab has a (non-kinematic) Rigidbody,
/// movement and rotation are applied through the physics engine in
/// FixedUpdate (velocity + MoveRotation) instead of teleporting the
/// transform. Without a Rigidbody the enemy behaves exactly like v1.0
/// (direct transform movement).
///
/// v2.0 – Flashlight stun / disappear stages, the OnDespawned event and the
/// freeze-while-stunned physics handling (this version).
///
/// v2.1 – RUN-AWAY DESPAWN: the end of the disappear stage now hands over to
/// EnemyFleeController (if present on the prefab) so the enemy runs away from
/// the player before it despawns. Only Despawn() changed from v2.0; everything
/// else is byte-identical.
///
/// v2.2 – kinematic safety guard in SetPlanarVelocity(): velocity writes on a
/// kinematic Rigidbody are now skipped instead of letting Unity print
/// "Setting linear velocity of a kinematic body is not supported" (such
/// writes are ignored by physics anyway). This makes the wake-up ordering
/// with EnemyFleeController a hard guarantee. Only that guard was added;
/// v2.1 behaviour is otherwise unchanged.
///
/// v2.3 – CONTACT SPIN FIX (user report: "enemy spinned when touching any
/// object, like the player did"): physics contact torque could rotate the
/// enemy whenever it touched the player, walls or props. With a dynamic
/// Rigidbody the enemy now gets the same proven recipe as the player fix —
/// tilt frozen (X/Z rotation), maxAngularVelocity = 0 and a per-step angular
/// velocity wipe — so contacts can NEVER rotate the body. All turning stays
/// owned by this script (rb.MoveRotation), which keeps working unchanged.
///
/// v2.4 – SAFE ZONE support (for the SafeZone plug-in script): a SafeZone
/// calls <see cref="SetSafeZoneStun"/> every frame while this enemy is
/// inside its area. That input acts EXACTLY like the flashlight being held
/// on the enemy — the enemy freezes in the stun stage (red) no matter where
/// the player's light is pointing, then runs the normal disappear stage
/// (blue) and runs away, exactly like a flashlight kill. The stun input is
/// now "flashlight light OR inside a safe zone"; with no SafeZone in the
/// scene the behaviour is byte-identical to v2.3.
///
/// Designed for a simple placeholder model (e.g. a white cube/sphere).
/// Every tuning value is exposed in the Inspector.
/// </summary>
[DisallowMultipleComponent]
public class EnemyController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]

    [Tooltip("The transform this enemy chases. Auto-found via the 'Player' tag if left empty.")]
    [SerializeField] private Transform player;

    [Tooltip("The FlashlightDetector that tells this enemy when the player's light is on it. " +
             "Auto-found on this GameObject when left empty.")]
    [SerializeField] private FlashlightDetector flashlightDetector;

    [Header("Movement")]

    [Tooltip("Speed (units per second) while chasing the player.")]
    [SerializeField] private float moveSpeed = 3f;

    [Tooltip("Enemy stops once it is this close to the player's center (radius = 1 by default).")]
    [SerializeField] private float stopDistance = 1f;

    [Tooltip("A stopped enemy only resumes chasing once the player is farther away than this. " +
             "Prevents the stop/resume transition from flickering. Must be >= Stop Distance.")]
    [SerializeField] private float resumeDistance = 1.5f;

    [Header("Timing")]

    [Tooltip("Seconds the enemy stays perfectly still after spawning before it starts moving.")]
    [SerializeField] private float waitTime = 2f;

    [Header("Flashlight Combat")]

    [Tooltip("STUN STAGE duration: seconds the flashlight must continuously hit the enemy before " +
             "it advances to the disappear stage. This is the anti-exploit timer (default 0.5s).")]
    [SerializeField] private float stunDuration = 0.5f;

    [Tooltip("DISAPPEAR STAGE duration: seconds the enemy stays in the disappear stage " +
             "(blue, placeholder for the thrashing animation) before it actually disappears.")]
    [SerializeField] private float disappearDuration = 0.5f;

    [Tooltip("When the light breaks off the enemy before the full stun duration, its stun progress " +
             "resets — the light must be held continuously. Turn OFF to let progress carry over " +
             "between separate flashes (easier for the player).")]
    [SerializeField] private bool resetStunProgressOnLightBreak = true;

    [Header("Facing (optional)")]

    [Tooltip("Smoothly rotate to face the player while moving.")]
    [SerializeField] private bool facePlayerWhileMoving = true;

    [Tooltip("Turning speed in degrees per second.")]
    [SerializeField] private float turnSpeed = 270f;

    [Header("Placeholder Colors")]

    [Tooltip("Color while waiting / stopped (default white).")]
    [SerializeField] private Color idleColor = Color.white;

    [Tooltip("Color while moving toward the player (green).")]
    [SerializeField] private Color movingColor = Color.green;

    [Tooltip("Color during the stun stage (red) — placeholder for the enemy covering its face.")]
    [SerializeField] private Color stunnedColor = Color.red;

    [Tooltip("Color during the disappear stage (blue) — placeholder for the thrashing animation.")]
    [SerializeField] private Color disappearingColor = Color.blue;

    [Header("Physics (optional)")]

    [Tooltip("Move through the Rigidbody when one is attached (recommended for characters " +
             "with colliders – pairs with RigidbodyStabilizer). Falls back to direct " +
             "transform movement when no Rigidbody exists, so the prefab keeps working " +
             "either way.")]
    [SerializeField] private bool useRigidbodyWhenAvailable = true;

    [Tooltip("v2.3 – STOPS THE ENEMY SPINNING when it touches the player, walls or props: " +
             "contact torque can no longer rotate the body (the same recipe as the player fix: " +
             "tilt frozen + physics spin killed). The enemy's own turning still works — it is " +
             "driven by this script, never by physics.")]
    [SerializeField] private bool preventContactSpin = true;

    // ------------------------------------------------------------------ //
    // Runtime state (read-only from outside)
    // ------------------------------------------------------------------ //

    /// <summary>Current behaviour state: Waiting, Moving, Stopped, Stunned or Disappearing.</summary>
    public EnemyState CurrentState { get; private set; } = EnemyState.Waiting;

    /// <summary>Raised whenever the state actually changes. Hook VFX/SFX/UI here later.</summary>
    public event Action<EnemyState> OnStateChanged;

    /// <summary>
    /// Raised right before the enemy despawns at the end of the disappear stage.
    /// <see cref="EnemySpawner"/> uses this to start the random respawn cooldown.
    /// </summary>
    public event Action<EnemyController> OnDespawned;

    /// <summary>Stun progress from 0 to 1 (handy for a future HUD stun bar).</summary>
    public float StunProgress
    {
        get { return stunDuration > 0f ? Mathf.Clamp01(stunTimer / stunDuration) : 0f; }
    }

    private float waitTimer;
    private float stunTimer;
    private float disappearTimer;
    private EnemyState stateBeforeStun = EnemyState.Waiting;
    private float waitTimerAtStunStart;

    // v2.4: set by a SafeZone while this enemy stands inside its area.
    // Combined with the flashlight input in IsStunInputActive.
    private bool safeZoneStun;

    private Renderer[] renderers;
    private MaterialPropertyBlock propBlock;
    private Rigidbody rb;
    private bool usesPhysics;

    // Setting both IDs covers the Built-in RP ("_Color") and URP ("_BaseColor") shaders.
    private static readonly int BuiltinColorId = Shader.PropertyToID("_Color");
    private static readonly int UrpBaseColorId = Shader.PropertyToID("_BaseColor");

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>();
        propBlock = new MaterialPropertyBlock();

        rb = GetComponent<Rigidbody>();
        usesPhysics = rb != null && useRigidbodyWhenAvailable && !rb.isKinematic;

        // v2.3 — THE SPIN FIX (same proven recipe as the player fix):
        // freezing tilt stops the solver from tipping the enemy over, and
        // maxAngularVelocity = 0 plus the per-step wipe in FixedUpdate means
        // contacts can NEVER rotate the body. Turning stays owned by this
        // script (rb.MoveRotation), so the physics-vs-script tug-of-war that
        // looked like spinning is gone.
        if (usesPhysics && preventContactSpin)
        {
            rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            rb.maxAngularVelocity = 0f;
        }

        if (flashlightDetector == null)
        {
            flashlightDetector = GetComponent<FlashlightDetector>();
        }

        if (player == null)
        {
            GameObject found = GameObject.FindGameObjectWithTag("Player");
            if (found != null) player = found.transform;
        }
    }

    private void Start()
    {
        if (player == null)
        {
            Debug.LogError("[EnemyController] No player assigned on '" + name +
                           "' and no GameObject tagged 'Player' exists in the scene. Disabling enemy.", this);
            enabled = false;
            return;
        }

        if (flashlightDetector == null)
        {
            Debug.LogWarning("[EnemyController] No FlashlightDetector on '" + name +
                             "' — the enemy will ignore the player's flashlight. " +
                             "Add the FlashlightDetector component to the prefab to enable stunning.", this);
        }

        EnterState(EnemyState.Waiting);
    }

    private void OnEnable()
    {
        // Covers both the very first spawn and pooled reuse (SetActive toggling):
        // a freshly activated enemy always begins in the Waiting state.
        safeZoneStun = false;   // v2.4: a fresh spawn never inherits a stale zone flag
        EnterState(EnemyState.Waiting);
    }

    private void Update()
    {
        // The flashlight or a SafeZone can freeze the enemy out of any of its
        // normal states.
        if (CurrentState == EnemyState.Waiting ||
            CurrentState == EnemyState.Moving ||
            CurrentState == EnemyState.Stopped)
        {
            CheckStunInputs();
        }

        switch (CurrentState)
        {
            case EnemyState.Waiting:
                UpdateWaiting();
                break;
            case EnemyState.Moving:
                UpdateMoving();
                break;
            case EnemyState.Stopped:
                UpdateStopped();
                break;
            case EnemyState.Stunned:
                UpdateStunned();
                break;
            case EnemyState.Disappearing:
                UpdateDisappearing();
                break;
        }
    }

    private void FixedUpdate()
    {
        if (!usesPhysics || player == null) return;

        // v2.3: wipe any angular velocity a contact may have injected this step.
        // With maxAngularVelocity = 0 the solver already clamps it — this makes
        // it a hard guarantee (identical to the player stabilizer's belt-and-braces).
        if (preventContactSpin && rb.angularVelocity.sqrMagnitude > 0f)
            rb.angularVelocity = Vector3.zero;

        // Truly frozen while the flashlight holds the enemy (stun + disappear
        // stages): horizontal velocity is pinned to zero every physics step.
        if (CurrentState == EnemyState.Stunned || CurrentState == EnemyState.Disappearing)
        {
            SetPlanarVelocity(rb, Vector3.zero);
            return;
        }

        // Rigidbody movement must be applied in the physics step.
        if (CurrentState != EnemyState.Moving) return;

        Vector3 toPlayer = Flatten(player.position - transform.position);
        if (toPlayer.magnitude <= stopDistance) return; // transition handled in Update

        SetPlanarVelocity(rb, toPlayer.normalized * moveSpeed);

        if (facePlayerWhileMoving)
        {
            Quaternion targetRotation = Quaternion.LookRotation(toPlayer.normalized);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, targetRotation,
                turnSpeed * Time.fixedDeltaTime));
        }
    }

    /// <summary>Clamps Inspector values so the state machine can never receive nonsense input.</summary>
    private void OnValidate()
    {
        moveSpeed = Mathf.Max(0f, moveSpeed);
        stopDistance = Mathf.Max(0.05f, stopDistance);
        if (resumeDistance < stopDistance) resumeDistance = stopDistance;
        waitTime = Mathf.Max(0f, waitTime);
        stunDuration = Mathf.Max(0f, stunDuration);
        disappearDuration = Mathf.Max(0f, disappearDuration);
        turnSpeed = Mathf.Max(1f, turnSpeed);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Injects the player reference. Used by <see cref="EnemySpawner"/> so the prefab
    /// needs zero manual setup, but you can also call this from your own code.
    /// </summary>
    public void Initialize(Transform playerTransform)
    {
        if (playerTransform != null) player = playerTransform;
    }

    /// <summary>Resets the enemy to the freshly-spawned Waiting state (useful for pooling).</summary>
    public void ResetEnemy()
    {
        safeZoneStun = false;   // v2.4: pooled reuse starts outside any zone influence
        EnterState(EnemyState.Waiting);
    }

    /// <summary>True while a SafeZone currently holds this enemy (read-only).</summary>
    public bool IsInsideSafeZone
    {
        get { return safeZoneStun; }
    }

    /// <summary>
    /// v2.4 – SAFE ZONE support. Called by a SafeZone every frame with
    /// active = true while this enemy is inside its area. While active, this
    /// is the same as the flashlight being held on the enemy: it enters the
    /// stun stage immediately (frozen, red) and then continues into the
    /// disappear stage and the run-away, exactly like a flashlight kill.
    /// Called with active = false when the enemy leaves the zone (or the zone
    /// is switched off) — if the flashlight is not on the enemy at that
    /// moment, it simply resumes whatever it was doing before.
    ///
    /// Repeated calls with the same value are free (idempotent).
    /// </summary>
    public void SetSafeZoneStun(bool active)
    {
        if (active)
        {
            if (safeZoneStun) return;   // already applied this frame's input
            safeZoneStun = true;

            if (CurrentState == EnemyState.Waiting ||
                CurrentState == EnemyState.Moving ||
                CurrentState == EnemyState.Stopped)
            {
                // The same interrupt bookkeeping as a flashlight hit, so an
                // interrupted enemy resumes naturally if the zone releases it
                // before the full stun duration (and keeps its remaining
                // post-spawn wait time).
                stateBeforeStun = CurrentState;
                waitTimerAtStunStart = waitTimer;
                if (resetStunProgressOnLightBreak) stunTimer = 0f;

                EnterState(EnemyState.Stunned);
            }
            // Already Stunned (flashlight or zone) or Disappearing — the
            // combined stun input keeps the timers running; nothing to do.
        }
        else
        {
            safeZoneStun = false;
            // Nothing else needed: UpdateStunned re-evaluates the combined
            // stun input (flashlight OR safe zone) and resumes the previous
            // state naturally when neither one is active.
        }
    }

    // ------------------------------------------------------------------ //
    // State machine — normal lifecycle
    // ------------------------------------------------------------------ //

    private void UpdateWaiting()
    {
        waitTimer -= Time.deltaTime;
        if (waitTimer <= 0f)
        {
            EnterState(EnemyState.Moving);
        }
    }

    private void UpdateMoving()
    {
        if (player == null) return;

        Vector3 toPlayer = Flatten(player.position - transform.position);

        // Requirement: stop once inside the radius around the player's center.
        if (toPlayer.magnitude <= stopDistance)
        {
            EnterState(EnemyState.Stopped);
            return;
        }

        // Direct transform movement (no usable Rigidbody on this prefab).
        // Rigidbody movement runs in FixedUpdate instead.
        if (!usesPhysics)
        {
            transform.position += toPlayer.normalized * (moveSpeed * Time.deltaTime);

            if (facePlayerWhileMoving)
            {
                FaceDirection(toPlayer);
            }
        }
    }

    private void UpdateStopped()
    {
        if (player == null) return;

        // Resume chasing once the player moves out of the resume radius.
        Vector3 toPlayer = Flatten(player.position - transform.position);
        if (toPlayer.magnitude > resumeDistance)
        {
            EnterState(EnemyState.Moving);
        }
    }

    // ------------------------------------------------------------------ //
    // State machine — flashlight stages
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Combined stun input (v2.4): the player's flashlight is on the enemy OR
    /// the enemy is inside a SafeZone's area. Either one freezes it.
    /// </summary>
    private bool IsStunInputActive
    {
        get { return safeZoneStun || (flashlightDetector != null && flashlightDetector.IsLit); }
    }

    /// <summary>
    /// The moment the flashlight touches the enemy (any normal state) — or a
    /// SafeZone holds it — it is frozen in place and enters the stun stage.
    /// </summary>
    private void CheckStunInputs()
    {
        if (!IsStunInputActive) return;

        // Remember where the enemy was interrupted so it can resume naturally
        // when the light breaks — including how much spawn-wait time it had left.
        stateBeforeStun = CurrentState;
        waitTimerAtStunStart = waitTimer;

        if (resetStunProgressOnLightBreak) stunTimer = 0f;

        EnterState(EnemyState.Stunned);
    }

    private void UpdateStunned()
    {
        if (IsStunInputActive)
        {
            // Accumulate the continuous-input time. Once the light has been on
            // the enemy (or the enemy has been inside a safe zone) for the full
            // Stun Duration, advance to the disappear stage.
            stunTimer += Time.deltaTime;
            if (stunTimer >= stunDuration)
            {
                EnterState(EnemyState.Disappearing);
            }
            return;
        }

        // The input broke off before the full duration — the enemy is NOT
        // fully stunned: it resumes normal behaviour and chases the player again.
        ResumeAfterInterruptedStun();
    }

    private void ResumeAfterInterruptedStun()
    {
        EnemyState resumeState = stateBeforeStun;

        // Safety net — the stored state can never be one of the flashlight
        // stages, but never trust runtime state blindly.
        if (resumeState == EnemyState.Stunned || resumeState == EnemyState.Disappearing)
        {
            resumeState = EnemyState.Moving;
        }

        EnterState(resumeState);

        // If the enemy was interrupted during its post-spawn wait, give it
        // back only the REMAINING wait time instead of the full wait again.
        if (resumeState == EnemyState.Waiting)
        {
            waitTimer = waitTimerAtStunStart;
        }
    }

    private void UpdateDisappearing()
    {
        disappearTimer -= Time.deltaTime;
        if (disappearTimer <= 0f)
        {
            Despawn();
        }
    }

    /// <summary>
    /// v2.1 – RUN-AWAY DESPAWN: instead of vanishing on the spot at the end of
    /// the disappear stage, the enemy now turns away from the player and RUNS
    /// (EnemyFleeController on the same prefab), then despawns when the run
    /// ends – it shrinks to zero while still running, so it never just pops
    /// out of existence. If no EnemyFleeController is on the prefab, this
    /// falls back to the v2.0 instant despawn automatically.
    /// </summary>
    private void Despawn()
    {
        EnemyFleeController flee = GetComponent<EnemyFleeController>();
        if (flee != null && flee.BeginFlee(FinishDespawn, this))
        {
            return; // the enemy runs away; FinishDespawn() is called when it ends
        }

        FinishDespawn();
    }

    /// <summary>
    /// The actual despawn: notifies the spawner (random respawn cooldown) and
    /// destroys the enemy. Called directly, or by EnemyFleeController after
    /// the run-away finishes.
    /// </summary>
    private void FinishDespawn()
    {
        OnDespawned?.Invoke(this);
        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ //
    // State transitions
    // ------------------------------------------------------------------ //

    private void EnterState(EnemyState next)
    {
        bool changed = CurrentState != next;
        CurrentState = next;

        // Re-entering the same state also refreshes the timer/colors, which keeps
        // ResetEnemy()/pooled reuse correct.
        switch (next)
        {
            case EnemyState.Waiting:
                waitTimer = waitTime;
                SetColor(idleColor);   // default white
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero); // no sliding while waiting
                break;

            case EnemyState.Moving:
                SetColor(movingColor); // green while moving
                break;

            case EnemyState.Stopped:
                SetColor(idleColor);   // back to default white
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero); // hard stop
                break;

            case EnemyState.Stunned:
                SetColor(stunnedColor);          // red — covering its face (placeholder)
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero); // frozen in place
                break;

            case EnemyState.Disappearing:
                disappearTimer = disappearDuration;
                SetColor(disappearingColor);     // blue — thrashing (placeholder)
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero); // still frozen
                break;
        }

        if (changed) OnStateChanged?.Invoke(next);
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>Replaces the horizontal part of the Rigidbody velocity, keeping gravity.</summary>
    private static void SetPlanarVelocity(Rigidbody body, Vector3 planar)
    {
        // v2.2 guard: kinematic bodies ignore velocity writes and Unity prints
        // "Setting linear velocity of a kinematic body is not supported" for
        // each one. Skipping the write is behaviour-identical and keeps the
        // console clean no matter who re-enabled this controller while a
        // flee takeover still held the body kinematic.
        if (body == null || body.isKinematic) return;

#if UNITY_6000_0_OR_NEWER
        Vector3 v = body.linearVelocity;
        v.x = planar.x;
        v.z = planar.z;
        body.linearVelocity = v;
#else
        Vector3 v = body.velocity;
        v.x = planar.x;
        v.z = planar.z;
        body.velocity = v;
#endif
    }

    /// <summary>Keeps gameplay math on the horizontal plane (works on flat ground).</summary>
    private static Vector3 Flatten(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    private void FaceDirection(Vector3 flatDirection)
    {
        if (flatDirection.sqrMagnitude < 0.0001f) return;

        Quaternion targetRotation = Quaternion.LookRotation(flatDirection.normalized);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);
    }

    /// <summary>
    /// Tints every renderer under this enemy. Uses a MaterialPropertyBlock so no
    /// material instances are created and prefab sharing stays intact.
    /// </summary>
    private void SetColor(Color color)
    {
        if (renderers == null) return;

        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) continue;

            renderers[i].GetPropertyBlock(propBlock);
            propBlock.SetColor(BuiltinColorId, color);
            propBlock.SetColor(UrpBaseColorId, color);
            renderers[i].SetPropertyBlock(propBlock);
        }
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    private void OnDrawGizmosSelected()
    {
        // Visualise the stop radius around this enemy's chase target.
        Transform target = player;
        if (target == null)
        {
            GameObject found = GameObject.FindGameObjectWithTag("Player");
            if (found != null) target = found.transform;
        }

        if (target != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(target.position, stopDistance);
        }
    }
}