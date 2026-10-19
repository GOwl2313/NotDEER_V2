using UnityEngine;

/// <summary>
/// Fixes the player shaking problem: the player's Y position and rotation
/// trembling while touching walls / props / ground, even when standing still.
///
/// WHAT WAS CAUSING IT (matches your v1.2 player setup):
/// 1. Your RigidbodyStabilizer freezes only TILT (X/Z rotation) — the physics
///    engine can still spin the player around the Y axis. Every time the
///    capsule touches a wall or prop, the contact applies a small yaw torque.
///    PlayerMovementController then snaps the rotation back with
///    Rigidbody.MoveRotation every FixedUpdate -> physics vs. MoveRotation
///    tug-of-war -> the visible rotation shake, even while standing still.
/// 2. Gravity keeps pushing the capsule into the ground and the contact
///    solver keeps pushing back -> a tiny vertical "micro bounce" velocity
///    that survives forever -> the Y position shake.
/// 3. The capsule's default friction lets walls "grab" it, feeding both of
///    the fights above.
///
/// WHAT THIS SCRIPT DOES (does NOT change PlayerMovementController at all):
/// 1. killPhysicsSpin – sets maxAngularVelocity to 0 AND zeroes angular
///    velocity every physics step -> the solver can never rotate the player,
///    so MoveRotation is the ONLY writer of rotation. No fight = no shake.
///    (Turning still works exactly like before.)
/// 2. Freezes tilt (X/Z rotation) – the capsule can never tip over.
/// 3. AutoGround – cancels the tiny vertical bounce while resting on the
///    ground (slopes and stairs keep working; there is no jumping in this game).
/// 4. Zero-friction material – walls and the ground can no longer grab the capsule.
/// 5. Interpolation + continuous collision – smoother visuals, no tunneling.
///
/// SETUP: add this component to the player GameObject (the one with the
/// Rigidbody + CapsuleCollider). You can keep RigidbodyStabilizer on the
/// enemy, but remove it from the PLAYER — this script replaces it there.
/// All values are adjustable in the Inspector.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)] // runs after the movement script's FixedUpdate, so our cleanup is applied last
public class PlayerPhysicsStabilizer : MonoBehaviour
{
    public enum RotationStabilization { None, FreezeTiltOnly, FreezeAll }
    public enum VerticalStabilization { Off, AutoGround, FreezeY }

    [Header("Spin Kill (rotation shake fix)")]
    [Tooltip("Kills ALL physics-driven rotation (maxAngularVelocity = 0 + zeroes angular velocity every step). Rigidbody.MoveRotation (your instant turning) still works and becomes the only writer of rotation, so the tug-of-war with physics stops. Recommended ON.")]
    public bool killPhysicsSpin = true;

    [Header("Rotation Lock (anti-tipping)")]
    [Tooltip("FreezeTiltOnly = blocks falling over but keeps yaw free (correct for PlayerMovementController's MoveRotation turning - DEFAULT). FreezeAll = also locks physics yaw (only for setups that never use MoveRotation).")]
    public RotationStabilization rotationStabilization = RotationStabilization.FreezeTiltOnly;

    [Header("Vertical (Y) Fix")]
    [Tooltip("AutoGround = cancels the tiny vertical bounce while resting on the ground (safe with slopes and stairs). FreezeY = hard-locks the Y axis (only for completely flat maps). Off = do nothing.")]
    public VerticalStabilization verticalStabilization = VerticalStabilization.AutoGround;

    [Tooltip("A contact counts as 'ground' when its normal points up at least this much (1 = perfectly flat, 0.65 ~ about 49 degrees slope).")]
    [Range(0f, 1f)] public float groundNormalThreshold = 0.65f;

    [Tooltip("While resting on the ground, vertical speed smaller than this is zeroed out (this removes the micro-bouncing).")]
    public float verticalVelocityCutoff = 0.35f;

    [Tooltip("How many seconds the script remembers the last ground contact. Keep it small (0.05 - 0.2).")]
    public float groundContactMemory = 0.15f;

    [Header("Physics Quality")]
    [Tooltip("Smooths the visual position between physics steps (removes micro jitter).")]
    public bool useInterpolation = true;

    [Tooltip("Prevents the capsule from being pushed through thin walls or props by fast hits.")]
    public bool useContinuousCollision = true;

    [Tooltip("Gives the player colliders a zero-friction physics material so walls and the ground cannot grab the capsule. Turn OFF if the player starts sliding when idle and your movement is force-based.")]
    public bool useZeroFrictionMaterial = true;

    Rigidbody body;
    float lastGroundContactTime = -999f;
    readonly ContactPoint[] contactBuffer = new ContactPoint[16];

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        if (body == null)
        {
            Debug.LogError("PlayerPhysicsStabilizer: no Rigidbody found on '" + gameObject.name +
                "'. This fix is for Rigidbody-based players. If your player uses a CharacterController, " +
                "remove this component and send me your PlayerMovementController.cs - I will make a CharacterController version.", this);
            enabled = false;
            return;
        }

        if (rotationStabilization == RotationStabilization.FreezeAll)
            body.constraints |= RigidbodyConstraints.FreezeRotation;
        else if (rotationStabilization == RotationStabilization.FreezeTiltOnly)
            body.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        if (verticalStabilization == VerticalStabilization.FreezeY)
            body.constraints |= RigidbodyConstraints.FreezePositionY;

        if (killPhysicsSpin)
            body.maxAngularVelocity = 0f;

        if (useInterpolation)
            body.interpolation = RigidbodyInterpolation.Interpolate;

        if (useContinuousCollision)
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;

        if (useZeroFrictionMaterial)
            ApplyZeroFrictionMaterial();
    }

    void FixedUpdate()
    {
        // Runs AFTER the movement script (DefaultExecutionOrder 100), so this is
        // the final word on the body state for this physics step.

        if (killPhysicsSpin)
        {
            body.maxAngularVelocity = 0f;          // solver can never spin the player
            if (body.angularVelocity.sqrMagnitude > 0f)
                body.angularVelocity = Vector3.zero;
        }

        if (verticalStabilization != VerticalStabilization.AutoGround)
            return;

        if (Time.fixedTime - lastGroundContactTime > groundContactMemory)
            return; // not resting on the ground right now

        if (Mathf.Abs(GetVerticalVelocity()) < verticalVelocityCutoff)
            SetVerticalVelocity(0f);
    }

    void OnCollisionStay(Collision collision)
    {
        // wall contacts (normal pointing sideways) do not count as ground
        int count = collision.GetContacts(contactBuffer);
        for (int i = 0; i < count; i++)
        {
            if (contactBuffer[i].normal.y >= groundNormalThreshold)
            {
                lastGroundContactTime = Time.fixedTime;
                return;
            }
        }
    }

    void ApplyZeroFrictionMaterial()
    {
#if UNITY_6000_0_OR_NEWER
        PhysicsMaterial mat = new PhysicsMaterial("PlayerZeroFriction");
        mat.frictionCombine = PhysicsMaterialCombine.Minimum;
        mat.bounceCombine = PhysicsMaterialCombine.Minimum;
#else
        PhysicMaterial mat = new PhysicMaterial("PlayerZeroFriction");
        mat.frictionCombine = PhysicMaterialCombine.Minimum;
        mat.bounceCombine = PhysicMaterialCombine.Minimum;
#endif
        mat.dynamicFriction = 0f;
        mat.staticFriction = 0f;
        mat.bounciness = 0f;

        Collider[] colliders = GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
            colliders[i].material = mat;
    }

    float GetVerticalVelocity()
    {
#if UNITY_6000_0_OR_NEWER
        return body.linearVelocity.y;
#else
        return body.velocity.y;
#endif
    }

    void SetVerticalVelocity(float value)
    {
#if UNITY_6000_0_OR_NEWER
        Vector3 v = body.linearVelocity;
        if (v.y != value)
        {
            v.y = value;
            body.linearVelocity = v;
        }
#else
        Vector3 v = body.velocity;
        if (v.y != value)
        {
            v.y = value;
            body.velocity = v;
        }
#endif
    }
}