using UnityEngine;

/// <summary>
/// THE PLAYER'S HITBOX ("dead zone") for the one-hit-kill jumpscare.
///
/// Attach this component to the PLAYER (root or any child object). It watches
/// for an enemy tagged "Enemy" entering the player's trigger collider:
///
///   1. When a LIVE enemy touches the trigger, the jumpscare starts
///      (JumpscareGameOverManager.TriggerJumpscare): both freeze at the
///      contact position, the video plays, then Game Over → Restart.
///   2. The trigger NEVER fires while the enemy is in the red stun stage or
///      the blue disappearing stage (or already running away after them) —
///      those enemies are harmless.
///   3. If no trigger collider exists on this GameObject yet, one is created
///      automatically (a BoxCollider "dead zone" you can resize freely in the
///      Inspector — its Center/Size ARE the dead zone).
///
/// HOW TRIGGER EVENTS WORK (Unity's built-in physics messages):
///   A collider with "Is Trigger" ticked generates OnTriggerEnter instead of
///   physical collisions. The entering object needs a Rigidbody for the
///   message to fire — the enemy prefab already has one, so there is nothing
///   extra to set up. OnTriggerEnter fires on first contact; OnTriggerStay
///   covers the edge case of the player standing INSIDE a stunned enemy that
///   then wakes up — the moment it becomes live again, the jumpscare triggers.
///
/// SETUP: Player (root, with its Rigidbody)
///          └─ PlayerHitbox  (+ auto-created BoxCollider trigger)
///
/// Everything is adjustable in the Inspector. Fully independent: works with
/// any enemy tagged "Enemy"; the stun/disappear state check simply doesn't
/// apply when no EnemyController is found (tag alone decides).
/// </summary>
[DisallowMultipleComponent]
public class PlayerHitbox : MonoBehaviour
{
    [Header("Enemy Detection")]

    [Tooltip("Only colliders whose GameObject carries this tag can trigger the jumpscare. " +
             "Tag your enemy prefab's ROOT with 'Enemy' (Tags & Layers).")]
    [SerializeField] private string enemyTag = "Enemy";

    [Tooltip("The enemy must be in a LIVE state to kill: NOT the red stun stage, NOT the blue " +
             "disappearing stage, and NOT already running away (its controller is paused then). " +
             "Turn OFF to trigger on any 'Enemy'-tagged overlap regardless of state.")]
    [SerializeField] private bool ignoreStunnedAndDisappearing = true;

    [Header("Auto-Created Dead Zone (only used when no trigger collider exists yet)")]

    [Tooltip("Center of the auto-created BoxCollider dead zone (local space).")]
    [SerializeField] private Vector3 defaultTriggerCenter = new Vector3(0f, 1f, 0f);

    [Tooltip("Size of the auto-created BoxCollider dead zone. Bigger = the enemy kills from " +
             "farther away. Adjust the BoxCollider itself after it exists — this only seeds it.")]
    [SerializeField] private Vector3 defaultTriggerSize = new Vector3(1.6f, 2.2f, 1.6f);

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private bool triggered;
    private bool warnedNoManager;
    private bool warnedTagMissing;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        EnsureTriggerCollider();
    }

    private void OnTriggerEnter(Collider other)
    {
        Evaluate(other);
    }

    // Covers the "player stands inside a stunned enemy that wakes up" case:
    // OnTriggerEnter only fires on FIRST contact, OnTriggerStay keeps checking.
    private void OnTriggerStay(Collider other)
    {
        Evaluate(other);
    }

    /// <summary>Clamps Inspector values so the hitbox can never receive nonsense input.</summary>
    private void OnValidate()
    {
        if (defaultTriggerSize.x < 0.05f || defaultTriggerSize.y < 0.05f || defaultTriggerSize.z < 0.05f)
            defaultTriggerSize = new Vector3(
                Mathf.Max(0.05f, defaultTriggerSize.x),
                Mathf.Max(0.05f, defaultTriggerSize.y),
                Mathf.Max(0.05f, defaultTriggerSize.z));
    }

    // ------------------------------------------------------------------ //
    // Detection
    // ------------------------------------------------------------------ //

    private void Evaluate(Collider other)
    {
        if (triggered || other == null || !enabled) return;
        if (!IsEnemy(other)) return;
        if (ignoreStunnedAndDisappearing && !IsLiveEnemy(other)) return;

        JumpscareGameOverManager manager = JumpscareGameOverManager.Instance;
        if (manager == null)
        {
            WarnNoManagerOnce();
            return;
        }

        if (manager.TriggerJumpscare(other))
        {
            // One hit kill: stop watching after the first confirmed hit.
            triggered = true;
            enabled = false;
        }
    }

    /// <summary>Tag check that survives a tag that does not exist yet in the project.</summary>
    private bool IsEnemy(Collider other)
    {
        if (string.IsNullOrEmpty(enemyTag)) return false;

        try
        {
            return other.CompareTag(enemyTag);
        }
        catch
        {
            // The tag has not been created in the project yet — warn once.
            if (!warnedTagMissing)
            {
                warnedTagMissing = true;
                Debug.LogWarning("[PlayerHitbox] Tag '" + enemyTag + "' is not defined in this project. " +
                                 "Create it under Edit > Project Settings > Tags and Layers, then assign it " +
                                 "to the enemy prefab's ROOT GameObject.", this);
            }
            return false;
        }
    }

    /// <summary>
    /// True when the enemy behind this collider is in a state that can actually kill:
    /// any normal state (Waiting / Moving / Stopped). False while stunned (red),
    /// disappearing (blue) or already running away (its controller is paused then).
    /// Enemies without an EnemyController fall back to tag-only detection.
    /// </summary>
    private bool IsLiveEnemy(Collider other)
    {
        EnemyController controller = other.GetComponentInParent<EnemyController>();
        if (controller == null) return true; // custom enemy — the tag alone decides

        if (!controller.enabled) return false; // flee takeover: this enemy is already "dead"

        EnemyState state = controller.CurrentState;
        return state != EnemyState.Stunned && state != EnemyState.Disappearing;
    }

    // ------------------------------------------------------------------ //
    // Setup helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Makes sure a trigger collider exists on this GameObject. A player-solid
    /// collider (capsule etc.) is NOT converted — the dead zone is a separate
    /// trigger collider so the player's physics body keeps working normally.
    /// </summary>
    private void EnsureTriggerCollider()
    {
        Collider[] colliders = GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null && colliders[i].isTrigger) return; // user already made one
        }

        BoxCollider deadZone = gameObject.AddComponent<BoxCollider>();
        deadZone.isTrigger = true;
        deadZone.center = defaultTriggerCenter;
        deadZone.size = defaultTriggerSize;

        Debug.Log("[PlayerHitbox] No trigger collider found on '" + name + "' — created a BoxCollider " +
                  "dead zone (Center " + defaultTriggerCenter + ", Size " + defaultTriggerSize +
                  "). Select the player and resize that BoxCollider to tune the kill distance.", this);
    }

    private void WarnNoManagerOnce()
    {
        if (warnedNoManager) return;
        warnedNoManager = true;

        Debug.LogWarning("[PlayerHitbox] No JumpscareGameOverManager exists in the scene, so the " +
                         "jumpscare cannot start. Add the JumpscareGameOverManager component to any " +
                         "GameObject (an empty 'GameManagers' object is a good home).", this);
    }
}
