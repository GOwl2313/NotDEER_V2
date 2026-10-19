
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Which direction the character faces while moving.
/// </summary>
public enum PlayerRotationMode
{
    FaceCameraDirection,
    FaceMovementDirection
}

/// <summary>
/// Third-person player movement with 8-direction animation support.
/// Movement is locked while the pickup animation is playing.
/// </summary>
[DisallowMultipleComponent]
public class PlayerMovementController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]

    [Tooltip("Camera that defines the movement direction.")]
    [SerializeField] private Camera movementCamera;

    [Header("Speeds")]

    [Tooltip("Normal movement speed.")]
    [SerializeField] private float walkSpeed = 3f;

    [Tooltip("Movement speed while sprinting.")]
    [SerializeField] private float runSpeed = 6f;

    [Header("Acceleration")]

    [SerializeField] private float accelerationTime = 0.12f;
    [SerializeField] private float decelerationTime = 0.2f;

    [Header("Rotation")]

    [SerializeField] private PlayerRotationMode rotationMode =
        PlayerRotationMode.FaceCameraDirection;

    [SerializeField] private bool instantTurning = true;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float turnDeadzone = 3f;

    [Header("Sprint")]

    [SerializeField] private KeyCode sprintKey = KeyCode.LeftShift;

    [Header("Rigidbody (optional)")]

    [SerializeField] private bool useRigidbodyWhenAvailable = true;

    [Header("Animation")]

    [Tooltip("Animator that controls the movement Blend Tree.")]
    [SerializeField] private Animator animator;

    [Tooltip("Bool parameter for walking.")]
    [SerializeField] private string movingParameter = "IsMoving";

    [Tooltip("Bool parameter for running.")]
    [SerializeField] private string runningParameter = "IsRunning";

    [Tooltip("Float parameter for horizontal movement.")]
    [SerializeField] private string moveXParameter = "MoveX";

    [Tooltip("Float parameter for vertical movement.")]
    [SerializeField] private string moveYParameter = "MoveY";

    [Header("Pickup")]

    [Tooltip("Pickup animation controller that locks movement.")]
    [SerializeField] private PickupAnimationController pickupAnimationController;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    public bool IsRunning { get; private set; }
    public bool IsMoving { get; private set; }
    public Vector2 MoveInput { get; private set; }

    public float CurrentPlanarSpeed
    {
        get { return planarVelocity.magnitude; }
    }

    private Rigidbody rb;
    private bool usesPhysics;
    private Vector3 planarVelocity;
    private Vector3 cachedCamForward = Vector3.forward;
    private Vector3 cachedCamRight = Vector3.right;
    private bool rotateToFaceTarget;
    private Quaternion faceTargetRotation;
    private bool warnedNoCamera;

    private int movingHash;
    private int runningHash;
    private int moveXHash;
    private int moveYHash;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        usesPhysics =
            useRigidbodyWhenAvailable &&
            rb != null &&
            !rb.isKinematic;

        if (movementCamera == null)
            movementCamera = Camera.main;

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        // Automatically find the pickup controller on this object
        // or its children if it was not assigned in the Inspector.
        if (pickupAnimationController == null)
        {
            pickupAnimationController =
                GetComponentInChildren<PickupAnimationController>();
        }

        movingHash = Animator.StringToHash(movingParameter);
        runningHash = Animator.StringToHash(runningParameter);
        moveXHash = Animator.StringToHash(moveXParameter);
        moveYHash = Animator.StringToHash(moveYParameter);
    }

    private void Update()
    {
        if (GamePauseManager.IsPaused)
            return;

        RefreshCamera();

        if (IsPickupLocked())
        {
            StopMovementImmediately();
            UpdateMovementAnimation();
            return;
        }

        ReadInput();
        ComputeMovement(Time.deltaTime);

        if (!usesPhysics)
        {
            ApplyTransformMovement(Time.deltaTime);
        }
    }

    private void FixedUpdate()
    {
        if (GamePauseManager.IsPaused)
            return;

        if (!usesPhysics)
            return;

        if (IsPickupLocked())
        {
            StopMovementImmediately();
            return;
        }

        ApplyRigidbodyMovement(Time.fixedDeltaTime);
    }

    private void OnValidate()
    {
        walkSpeed = Mathf.Max(0f, walkSpeed);

        if (runSpeed < walkSpeed)
            runSpeed = walkSpeed;

        accelerationTime = Mathf.Max(0f, accelerationTime);
        decelerationTime = Mathf.Max(0f, decelerationTime);
        turnSpeed = Mathf.Max(1f, turnSpeed);
        turnDeadzone = Mathf.Max(0f, turnDeadzone);
    }

    // ------------------------------------------------------------------ //
    // Pickup lock
    // ------------------------------------------------------------------ //

    private bool IsPickupLocked()
    {
        return pickupAnimationController != null &&
               pickupAnimationController.IsPickingUp;
    }

    private void StopMovementImmediately()
    {
        MoveInput = Vector2.zero;
        IsMoving = false;
        IsRunning = false;

        // Remove horizontal movement immediately.
        planarVelocity = Vector3.zero;

        // Keep the character's current facing direction during pickup.
        rotateToFaceTarget = false;

        // Stop Rigidbody movement without changing vertical velocity.
        if (usesPhysics && rb != null)
        {
            SetPlanarVelocity(rb, Vector3.zero);
        }
    }

    // ------------------------------------------------------------------ //
    // Input
    // ------------------------------------------------------------------ //

    private void ReadInput()
    {
        if (IsPickupLocked())
        {
            StopMovementImmediately();
            UpdateMovementAnimation();
            return;
        }

        MoveInput = ReadMoveInput();

        IsMoving = MoveInput.sqrMagnitude > 0.01f;
        IsRunning = IsMoving && IsSprintHeld();

        UpdateMovementAnimation();
    }

    private void UpdateMovementAnimation()
    {
        if (animator == null)
            return;

        animator.SetBool(movingHash, IsMoving);
        animator.SetBool(runningHash, IsRunning);

        animator.SetFloat(moveXHash, MoveInput.x);
        animator.SetFloat(moveYHash, MoveInput.y);
    }

    private static Vector2 ReadMoveInput()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
            return Vector2.zero;

        float x =
            (keyboard.dKey.isPressed ? 1f : 0f) -
            (keyboard.aKey.isPressed ? 1f : 0f);

        float y =
            (keyboard.wKey.isPressed ? 1f : 0f) -
            (keyboard.sKey.isPressed ? 1f : 0f);

        return new Vector2(x, y);
#else
        return new Vector2(
            Input.GetAxisRaw("Horizontal"),
            Input.GetAxisRaw("Vertical")
        );
#endif
    }

    private bool IsSprintHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return KeyCodeBridge.IsKeyPressed(
            Keyboard.current,
            sprintKey
        );
#else
        return Input.GetKey(sprintKey);
#endif
    }

    // ------------------------------------------------------------------ //
    // Movement math
    // ------------------------------------------------------------------ //

    private void RefreshCamera()
    {
        if (movementCamera == null)
            movementCamera = Camera.main;

        if (movementCamera != null)
        {
            cachedCamForward =
                Flatten(movementCamera.transform.forward).normalized;

            cachedCamRight =
                Flatten(movementCamera.transform.right).normalized;

            warnedNoCamera = false;
        }
        else
        {
            if (!warnedNoCamera)
            {
                Debug.LogWarning(
                    "[PlayerMovementController] No camera found. " +
                    "Movement uses the character's orientation.",
                    this
                );

                warnedNoCamera = true;
            }

            cachedCamForward = Flatten(transform.forward).normalized;
            cachedCamRight = Flatten(transform.right).normalized;
        }
    }

    private void ComputeMovement(float dt)
    {
        if (IsPickupLocked())
        {
            StopMovementImmediately();
            return;
        }

        Vector3 wishDir =
            cachedCamForward * MoveInput.y +
            cachedCamRight * MoveInput.x;

        if (wishDir.sqrMagnitude > 1f)
            wishDir.Normalize();

        float maxSpeed = IsRunning ? runSpeed : walkSpeed;

        Vector3 targetVelocity = wishDir * maxSpeed;

        float smoothTime =
            wishDir.sqrMagnitude > 0.0001f
                ? accelerationTime
                : decelerationTime;

        float maxDelta =
            maxSpeed / Mathf.Max(smoothTime, 0.0001f) * dt;

        planarVelocity = Vector3.MoveTowards(
            planarVelocity,
            targetVelocity,
            maxDelta
        );

        if (rotationMode == PlayerRotationMode.FaceCameraDirection)
        {
            faceTargetRotation =
                Quaternion.LookRotation(cachedCamForward);

            rotateToFaceTarget = true;
        }
        else if (wishDir.sqrMagnitude > 0.0001f)
        {
            faceTargetRotation = Quaternion.LookRotation(wishDir);
            rotateToFaceTarget = true;
        }
        else
        {
            rotateToFaceTarget = false;
        }
    }

    // ------------------------------------------------------------------ //
    // Applying movement
    // ------------------------------------------------------------------ //

    private void ApplyTransformMovement(float dt)
    {
        if (IsPickupLocked())
        {
            StopMovementImmediately();
            return;
        }

        transform.position += planarVelocity * dt;

        if (!rotateToFaceTarget)
            return;

        if (instantTurning)
        {
            transform.rotation = faceTargetRotation;
            return;
        }

        if (Quaternion.Angle(
            transform.rotation,
            faceTargetRotation
        ) > turnDeadzone)
        {
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                faceTargetRotation,
                turnSpeed * dt
            );
        }
    }

    private void ApplyRigidbodyMovement(float fixedDt)
    {
        if (IsPickupLocked())
        {
            StopMovementImmediately();
            return;
        }

        SetPlanarVelocity(rb, planarVelocity);

        if (!rotateToFaceTarget)
            return;

        if (instantTurning)
        {
            rb.MoveRotation(faceTargetRotation);
            return;
        }

        if (Quaternion.Angle(
            rb.rotation,
            faceTargetRotation
        ) > turnDeadzone)
        {
            rb.MoveRotation(
                Quaternion.RotateTowards(
                    rb.rotation,
                    faceTargetRotation,
                    turnSpeed * fixedDt
                )
            );
        }
    }

    private static void SetPlanarVelocity(
        Rigidbody body,
        Vector3 planar
    )
    {
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

    private static Vector3 Flatten(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying)
            return;

        if (planarVelocity.sqrMagnitude > 0.001f)
        {
            Gizmos.color = Color.cyan;

            Gizmos.DrawRay(
                transform.position + Vector3.up * 0.1f,
                planarVelocity.normalized * 2f
            );
        }
    }
}
