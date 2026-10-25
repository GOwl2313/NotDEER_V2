using System;
using UnityEngine;

/// <summary>
/// The lifecycle states of the enemy.
/// </summary>
public enum EnemyState
{
    /// <summary>Just spawned. Stands completely still for the configured wait time (default 2s).</summary>
    Waiting,
    /// <summary>Chasing the player (attack animation).</summary>
    Moving,
    /// <summary>Inside the stop radius around the player's center (idle animation).</summary>
    Stopped,
    /// <summary>STUN STAGE — the flashlight is on the enemy (or it stands inside a SafeZone). Frozen in place (stun animation — the enemy covering its face). The input must stay active continuously for Stun Duration to advance.</summary>
    Stunned,
    /// <summary>DISAPPEAR STAGE — fully stunned for Disappear Duration, then the enemy despawns and the spawner rolls its random respawn cooldown.</summary>
    Disappearing
}

/// <summary>
/// Basic enemy behaviour for a single reusable enemy prefab.
///
/// Lifecycle:
///   1. Spawn (handled invisibly by <see cref="EnemySpawner"/>).
///   2. Wait  – stands perfectly still for Wait Time seconds (default 2).
///   3. Move  – walks toward the player at Move Speed (attack animation).
///   4. Stop  – freezes once inside the Stop Distance radius around the
///              player's center (idle animation).
///   If the player later moves away beyond Resume Distance, the enemy
///   starts chasing again.
///
/// Flashlight combat (v2.0):
///   Stun stage      – while the player's flashlight hits the enemy, the enemy
///                     freezes in place (stun animation). The light must stay on it
///                     CONTINUOUSLY for Stun Duration (default 0.5s).
///   Disappear stage – after a full stun, the enemy stays frozen for Disappear
///                     Duration (default 0.5s), then despawns.
///
/// v1.1 – Rigidbody support.
/// v2.0 – Flashlight stun / disappear stages, OnDespawned event.
/// v2.1 – RUN-AWAY DESPAWN via EnemyFleeController.
/// v2.2 – kinematic safety guard in SetPlanarVelocity().
/// v2.3 – CONTACT SPIN FIX.
/// v2.4 – SAFE ZONE support (SetSafeZoneStun).
///
/// v2.5 – ANIMATION BOOLS: read-only IsAttack / IsStun / IsFlee (+ IsDisappearing)
/// and automatic sync into an Animator (Bool parameters "IsAttack", "IsStun",
/// "IsFlee" — names configurable). Parameters missing from the Animator
/// Controller are skipped silently. No state-machine behaviour was changed.
///
/// v2.6 – PLACEHOLDER COLOR TINT REMOVED: the four state colors and the
/// MaterialPropertyBlock tinting existed only for the old primitive model.
/// The real Wendo model + its Animator now communicate state, so the model
/// keeps its natural material colors in every state (and its renderers stay
/// eligible for SRP batching). NO state-machine, timing, physics or
/// animation-sync change of any kind.
///
/// v2.7 – VR TARGET SUPPORT (XR Origin first): when no player is assigned in
/// the Inspector, the chase target is auto-resolved VR-first — an XR Origin
/// (XR Rig) in the scene is preferred (the rig that actually moves in VR),
/// then the classic 'Player' tag. Non-VR scenes behave exactly as before.
/// </summary>
[DisallowMultipleComponent]
public class EnemyController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]

    [Tooltip("The transform this enemy chases. Auto-found when left empty: prefers the " +
             "XR Origin (XR Rig) in VR scenes, then falls back to the 'Player' tag.")]
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

    [Header("Animation (optional)")]

    [Tooltip("Animator ของโมเดลศัตรู ถ้าเว้นว่างจะหาจากตัวเอง/ลูกให้อัตโนมัติ")]
    [SerializeField] private Animator animator;

    [Tooltip("ชื่อ Bool parameter: กำลังพุ่งโจมตี (ไม่มีใน Animator = ข้ามเงียบๆ)")]
    [SerializeField] private string isAttackParam = "IsAttack";

    [Tooltip("ชื่อ Bool parameter: กลัวแสง (ไม่มีใน Animator = ข้ามเงียบๆ)")]
    [SerializeField] private string isStunParam = "IsStun";

    [Tooltip("ชื่อ Bool parameter: วิ่งหนี (ไม่มีใน Animator = ข้ามเงียบๆ)")]
    [SerializeField] private string isFleeParam = "IsFlee";

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

    // ---- v2.5 animation bools ---------------------------------------- //

    /// <summary>กำลังพุ่งโจมตี: ไล่พุ่งเข้าหาผู้เล่น (state Moving) และไม่ได้หนีอยู่</summary>
    public bool IsAttack
    {
        get { return CurrentState == EnemyState.Moving && !isFleeing; }
    }

    /// <summary>กลัวแสง: โดนไฟฉาย/อยู่ใน SafeZone (state Stunned) — ตอนใช้มือปิดหน้า</summary>
    public bool IsStun
    {
        get { return CurrentState == EnemyState.Stunned && !isFleeing; }
    }

    /// <summary>หนี: วิ่งหนีหลังผ่าน Disappear stage (EnemyFleeController ทำงานอยู่)</summary>
    public bool IsFlee
    {
        get { return isFleeing; }
    }

    /// <summary>(แถม) ช่วงดิ้นทุรนทุราย — state Disappearing ก่อนเริ่มหนี</summary>
    public bool IsDisappearing
    {
        get { return CurrentState == EnemyState.Disappearing && !isFleeing; }
    }

    private float waitTimer;
    private float stunTimer;
    private float disappearTimer;
    private EnemyState stateBeforeStun = EnemyState.Waiting;
    private float waitTimerAtStunStart;

    // v2.4: set by a SafeZone while this enemy stands inside its area.
    private bool safeZoneStun;

    // v2.5: true from the moment the run-away starts until the enemy is destroyed.
    private bool isFleeing;
    private int attackHash, stunHash, fleeHash;
    private bool hasAttackParam, hasStunParam, hasFleeParam;

    private Rigidbody rb;
    private bool usesPhysics;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        usesPhysics = rb != null && useRigidbodyWhenAvailable && !rb.isKinematic;

        // v2.3 — THE SPIN FIX.
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
            player = ResolveChaseTarget();
        }

        // v2.5: Animator lookup + parameter cache
        if (animator == null) animator = GetComponentInChildren<Animator>();
        CacheAnimatorParams();
    }

    private void Start()
    {
        if (player == null)
        {
            // Second chance — the XR rig may have become visible to
            // FindAnyObjectByType after the first Awake pass.
            player = ResolveChaseTarget();
        }

        if (player == null)
        {
            Debug.LogError("[EnemyController] No player assigned on '" + name +
                           "' and no XR Origin or GameObject tagged 'Player' exists in the scene. Disabling enemy.", this);
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
        // Covers both the very first spawn and pooled reuse (SetActive toggling).
        // NOTE: isFleeing is intentionally NOT reset here — EnemyFleeController may
        // toggle this component during the run-away and IsFlee must survive that.
        safeZoneStun = false;
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

        SyncAnimator(); // v2.5
    }

    private void FixedUpdate()
    {
        if (!usesPhysics || player == null) return;

        // v2.3: wipe any angular velocity a contact may have injected this step.
        if (preventContactSpin && rb.angularVelocity.sqrMagnitude > 0f)
            rb.angularVelocity = Vector3.zero;

        // Truly frozen while the flashlight holds the enemy (stun + disappear stages).
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
        safeZoneStun = false;
        isFleeing = false;      // v2.5
        EnterState(EnemyState.Waiting);
        SyncAnimator();
    }

    /// <summary>True while a SafeZone currently holds this enemy (read-only).</summary>
    public bool IsInsideSafeZone
    {
        get { return safeZoneStun; }
    }

    /// <summary>
    /// v2.4 – SAFE ZONE support. Called by a SafeZone every frame with
    /// active = true while this enemy is inside its area. Acts exactly like the
    /// flashlight being held on the enemy. Repeated calls with the same value are free.
    /// </summary>
    public void SetSafeZoneStun(bool active)
    {
        if (active)
        {
            if (safeZoneStun) return;
            safeZoneStun = true;

            if (CurrentState == EnemyState.Waiting ||
                CurrentState == EnemyState.Moving ||
                CurrentState == EnemyState.Stopped)
            {
                stateBeforeStun = CurrentState;
                waitTimerAtStunStart = waitTimer;
                if (resetStunProgressOnLightBreak) stunTimer = 0f;

                EnterState(EnemyState.Stunned);
            }
        }
        else
        {
            safeZoneStun = false;
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

        if (toPlayer.magnitude <= stopDistance)
        {
            EnterState(EnemyState.Stopped);
            return;
        }

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

        Vector3 toPlayer = Flatten(player.position - transform.position);
        if (toPlayer.magnitude > resumeDistance)
        {
            EnterState(EnemyState.Moving);
        }
    }

    // ------------------------------------------------------------------ //
    // State machine — flashlight stages
    // ------------------------------------------------------------------ //

    /// <summary>Combined stun input (v2.4): flashlight on the enemy OR inside a SafeZone.</summary>
    private bool IsStunInputActive
    {
        get { return safeZoneStun || (flashlightDetector != null && flashlightDetector.IsLit); }
    }

    private void CheckStunInputs()
    {
        if (!IsStunInputActive) return;

        stateBeforeStun = CurrentState;
        waitTimerAtStunStart = waitTimer;

        if (resetStunProgressOnLightBreak) stunTimer = 0f;

        EnterState(EnemyState.Stunned);
    }

    private void UpdateStunned()
    {
        if (IsStunInputActive)
        {
            stunTimer += Time.deltaTime;
            if (stunTimer >= stunDuration)
            {
                EnterState(EnemyState.Disappearing);
            }
            return;
        }

        ResumeAfterInterruptedStun();
    }

    private void ResumeAfterInterruptedStun()
    {
        EnemyState resumeState = stateBeforeStun;

        if (resumeState == EnemyState.Stunned || resumeState == EnemyState.Disappearing)
        {
            resumeState = EnemyState.Moving;
        }

        EnterState(resumeState);

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
    /// v2.1 – RUN-AWAY DESPAWN via EnemyFleeController (falls back to instant despawn
    /// when none is on the prefab).
    /// v2.5 – sets isFleeing and pushes IsFlee to the Animator immediately, because
    /// the flee controller may disable this component (Update would no longer run).
    /// </summary>
    private void Despawn()
    {
        EnemyFleeController flee = GetComponent<EnemyFleeController>();
        if (flee != null && flee.BeginFlee(FinishDespawn, this))
        {
            isFleeing = true;
            SyncAnimator();
            return; // the enemy runs away; FinishDespawn() is called when it ends
        }

        FinishDespawn();
    }

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

        switch (next)
        {
            case EnemyState.Waiting:
                waitTimer = waitTime;
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero);
                break;

            case EnemyState.Moving:
                break;

            case EnemyState.Stopped:
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero);
                break;

            case EnemyState.Stunned:
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero);
                break;

            case EnemyState.Disappearing:
                disappearTimer = disappearDuration;
                if (usesPhysics) SetPlanarVelocity(rb, Vector3.zero);
                break;
        }

        if (changed) OnStateChanged?.Invoke(next);
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// v2.7 – resolves the chase target, VR-first:
    ///   1. The explicit Inspector reference (always wins over everything).
    ///   2. An XR Origin (XR Rig) in the scene — in VR this is the rig that
    ///      actually moves with the player (VR scenes often still contain an
    ///      old non-VR player, so the 'Player' tag alone is ambiguous there).
    ///   3. The classic 'Player' tag (non-VR scenes keep working unchanged).
    /// </summary>
    private Transform ResolveChaseTarget()
    {
        if (player != null) return player;

        Unity.XR.CoreUtils.XROrigin xrOrigin = FindXROriginInScene();
        if (xrOrigin != null) return xrOrigin.transform;

        GameObject found = GameObject.FindGameObjectWithTag("Player");
        return found != null ? found.transform : null;
    }

    /// <summary>
    /// Version-aware XR Origin lookup — the same pattern the stuck detector's
    /// spawner lookup uses: Unity 2023+ (including Unity 6) gets
    /// FindAnyObjectByType, older Unity gets FindObjectOfType.
    /// </summary>
    private static Unity.XR.CoreUtils.XROrigin FindXROriginInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
#else
        return FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
#endif
    }

    /// <summary>Caches parameter hashes and checks which Bool parameters exist in the Animator.</summary>
    private void CacheAnimatorParams()
    {
        attackHash = Animator.StringToHash(isAttackParam);
        stunHash = Animator.StringToHash(isStunParam);
        fleeHash = Animator.StringToHash(isFleeParam);

        hasAttackParam = hasStunParam = hasFleeParam = false;
        if (animator == null || animator.runtimeAnimatorController == null) return;

        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.type != AnimatorControllerParameterType.Bool) continue;

            if (p.nameHash == attackHash) hasAttackParam = true;
            else if (p.nameHash == stunHash) hasStunParam = true;
            else if (p.nameHash == fleeHash) hasFleeParam = true;
        }
    }

    /// <summary>Pushes IsAttack / IsStun / IsFlee into the Animator (missing parameters are skipped).</summary>
    private void SyncAnimator()
    {
        if (animator == null) return;

        if (hasAttackParam) animator.SetBool(attackHash, IsAttack);
        if (hasStunParam) animator.SetBool(stunHash, IsStun);
        if (hasFleeParam) animator.SetBool(fleeHash, IsFlee);
    }

    /// <summary>Replaces the horizontal part of the Rigidbody velocity, keeping gravity.</summary>
    private static void SetPlanarVelocity(Rigidbody body, Vector3 planar)
    {
        // v2.2 guard: kinematic bodies ignore velocity writes.
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

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    private void OnDrawGizmosSelected()
    {
        Transform target = player;
        if (target == null)
        {
            // v2.7 – same XR-first resolution as at runtime (editor gizmo).
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

        if (target != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(target.position, stopDistance);
        }
    }
}