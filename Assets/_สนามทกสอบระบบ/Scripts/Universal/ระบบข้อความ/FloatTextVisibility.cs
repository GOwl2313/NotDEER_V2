using UnityEngine;

/// <summary>
/// FLOAT TEXT VISIBILITY — makes a floating text (or any billboard object)
/// disappear together with the object it belongs to, and come back exactly
/// as it was when that object returns.
///
/// Attach it to the SAME GameObject as FloatText.cs (the text root — the
/// world-space Canvas that is a CHILD of the interaction point / item).
/// FloatText.cs keeps handling the camera-facing; this script only decides
/// WHEN the text is visible. The two never touch each other.
///
/// WHAT COUNTS AS "THE PARENT DISAPPEARED" (all layers checked every frame,
/// so every pickup system you build later works without touching this file):
///   1. DESTROYED   — the parent was Destroy()ed. If the text is a child of
///                    it, Unity removes the text automatically; if it is not
///                    (text placed elsewhere, target assigned by hand), this
///                    script hides it instead.
///   2. DEACTIVATED — the parent (or anything above it) was SetActive(false).
///                    The classic "item picked up" implementation — works
///                    with zero configuration.
///   3. USED UP     — the parent is (or sits anywhere under) an
///                    InteractionPoint that just got Depleted: a single-use
///                    event point triggered, a respawnable point waiting for
///                    its timer. The text hides the INSTANT the point is
///                    used and returns the INSTANT the point respawns —
///                    InteractionPoint never deactivates its GameObject, so
///                    this layer is exactly what makes points remove their
///                    text. (Permanent points never "disappear" — their text
///                    simply stays visible, matching the marker.)
///   4. HIDDEN MESH — fallback for custom items: every renderer under the
///                    parent is disabled (the text's own renderers are
///                    never counted) → e.g. an item "picked up" by hiding
///                    its mesh. Colliders can be counted the same way
///                    (optional, off by default).
///   5. OUT OF RANGE — a ProximityText on the target (or its children) that
///                    toggles THIS SAME text reports the player out of its
///                    range → the "press [E]" window is closed, so the
///                    text is hidden for it too. This layer is what keeps
///                    the two scripts perfectly in sync: without it, this
///                    script's Start() would re-enable the Canvas that
///                    ProximityText just hid (it runs later in the frame)
///                    and the text would stay visible until the player
///                    entered and left the range once. With it, both
///                    scripts always agree — from frame one.
///   6. GATED SPOT — a RitualSpotFloatingText on this same text root gates
///                    it to its event spot's active state → hidden while
///                    the spot is still gated away (e.g. the ritual spot
///                    waiting for all totems). The two scripts stay in
///                    sync the same way as layer 5.
///
/// HOW IT HIDES: it toggles the Canvas component on this object (or, when
/// there is no Canvas — TextMesh-style texts — the renderers on this
/// object). It NEVER deactivates the GameObject and NEVER disables your
/// other components, so FloatText.cs keeps running while hidden and the
/// text snaps back to the exact right position the moment it reappears.
/// Nothing is ever destroyed or reset: after a respawn the text looks
/// exactly like before — same offset, same size, same text — until the
/// object disappears again.
///
/// SETUP (one step):
///   1. Add this component to the text root (the object that has
///      FloatText.cs). Done. The Target field can stay empty: it
///      auto-assigns to the parent.
///   Only if the text is NOT a child of the object it belongs to: drag that
///   object into the Target field.
///
/// UNITY 6 / URP / BUILT-IN / BOTH INPUT BACKENDS: uses no deprecated API
/// and no rendering API — works everywhere. No other script needs any edit
/// (InteractionPoint v3.0 or later is detected automatically if present).
///
/// v1.0 — initial release.
/// v1.1 — cooperation layers 5 & 6 (above): FloatTextVisibility and the
///        scripts that toggle the same text's Canvas (ProximityText,
///        RitualSpotFloatingText) used to fight over it when combined on
///        one text — the text could start the game VISIBLE even though the
///        player was nowhere near it, and only an enter/leave cycle fixed
///        it. Both sides now read each other's state and always agree.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("UI/Float Text Visibility (disappears with the parent)")]
public class FloatTextVisibility : MonoBehaviour
{
    [Header("Target")]

    [Tooltip("The object this text belongs to. Auto: the parent at Start. Assign it manually ONLY if the text is not a child of that object.")]
    [SerializeField] private Transform target;

    [Header("Disappearance Detection")]

    [Tooltip("Hide the text while an InteractionPoint on the target (or anywhere above the text) is Depleted — a single-use point that was triggered, a respawnable point waiting for its timer. Recommended ON: this is what makes event points remove their text.")]
    [SerializeField] private bool hideWhenPointDepleted = true;

    [Tooltip("Hide the text when ALL renderers under the target are disabled (the text's own renderers never count). Catches items that are 'picked up' by hiding their mesh instead of deactivating themselves.")]
    [SerializeField] private bool hideWhenAllMeshesHidden = true;

    [Tooltip("Hide the text while a ProximityText that toggles this same text reports the player out of range (the 'press [E]' window is closed). Recommended ON: this is what keeps this script and ProximityText perfectly in sync instead of fighting over the same Canvas — including on the very first frame.")]
    [SerializeField] private bool hideWhenProximityOutOfRange = true;

    [Tooltip("Hide the text while a RitualSpotFloatingText on this text gates it to an event spot that is currently hidden (e.g. the ritual spot waiting for its totems). The gate component can simply be disabled to opt out.")]
    [SerializeField] private bool hideWhenSpotGated = true;

    [Tooltip("Also count colliders like the meshes above: all colliders under the target disabled = disappeared. Off by default — some gameplay code disables colliders without meaning to hide the object.")]
    [SerializeField] private bool hideWhenAllCollidersDisabled = false;

    [Header("Maintenance")]

    [Tooltip("Re-scan the target every N seconds for renderers, colliders and an InteractionPoint (catches components created or removed at runtime — e.g. the point's glowing marker builds itself when Play starts). 0 = scan only once at Start.")]
    [SerializeField, Min(0f)] private float rescanInterval = 1f;

    [Tooltip("Write visibility changes to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Canvas ownCanvas;            // how a Canvas-based text is hidden
    private Renderer[] ownRenderers;     // fallback: TextMesh-style texts
    private InteractionPoint point;      // null = no InteractionPoint above

    private ProximityText[] managedProximities;  // ProximityTexts toggling THIS text
    private RitualSpotFloatingText managedGate;  // a gate on this text root (null = none)

    private Renderer[] trackedRenderers; // under the TARGET (this text excluded)
    private Collider[] trackedColliders; // under the TARGET (this text excluded)
    private float rescanTimer;
    private bool visible = true;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>True right now (last evaluated state). Read-only.</summary>
    public bool IsTextVisible
    {
        get { return visible; }
    }

    /// <summary>
    /// The object whose disappearance hides this text. Assigning a new one
    /// re-scans it immediately.
    /// </summary>
    public Transform Target
    {
        get { return target; }
        set
        {
            if (target == value) return;
            target = value;
            if (target != null) ScanTarget();
        }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        if (target == null)
            target = transform.parent;

        if (target == null)
        {
            Debug.LogWarning("[FloatTextVisibility] '" + name + "' has no parent and no Target assigned — " +
                             "there is nothing to disappear with. Put the text UNDER the interaction point / item " +
                             "(or assign the Target field). Disabling itself.", this);
            enabled = false;
            return;
        }

        FindOwnVisuals();

        if (ownCanvas == null && (ownRenderers == null || ownRenderers.Length == 0))
        {
            Debug.LogWarning("[FloatTextVisibility] '" + name + "': no Canvas and no Renderer found on this " +
                             "object — there is nothing to hide. Attach this component to the TEXT ROOT (the same " +
                             "object FloatText.cs sits on). Disabling itself.", this);
            enabled = false;
            return;
        }

        ScanTarget();

        // Apply the current state immediately: a text placed under an already
        // depleted point starts hidden.
        visible = ComputeVisible();
        SetVisible(visible);

        if (debugLogs)
            Debug.Log("[FloatTextVisibility] '" + name + "' now follows '" + target.name + "'" +
                      (point != null ? " (InteractionPoint found)" : "") + ". Visible: " + visible + ".", this);
    }

    private void Update()
    {
        // ---- the one unified rule — cheap enough for every frame ----
        bool now = ComputeVisible();
        if (now != visible)
        {
            visible = now;
            SetVisible(visible);
            if (debugLogs)
                Debug.Log("[FloatTextVisibility] '" + name + "' is now " +
                          (visible ? "VISIBLE" : "HIDDEN") + ".", this);
        }

        // ---- periodic re-scan: renderers / colliders / a point added or
        //      removed under the target at runtime ----
        if (rescanInterval > 0f)
        {
            rescanTimer += Time.unscaledDeltaTime;
            if (rescanTimer >= rescanInterval)
            {
                rescanTimer = 0f;
                ScanTarget();
            }
        }
    }

    // ------------------------------------------------------------------ //
    // The rule
    // ------------------------------------------------------------------ //

    /// <summary>True while the text should be shown. Every layer must agree.</summary>
    private bool ComputeVisible()
    {
        // 1) the target is gone (destroyed) or turned off
        if (target == null) return false;
        if (!target.gameObject.activeInHierarchy) return false;

        // 2) an interaction point was used up — its GameObject stays ACTIVE,
        //    which is exactly why this script exists
        if (hideWhenPointDepleted && point != null && !point.IsReady) return false;

        // 2b) a ProximityText toggling this same text says the player is out
        //     of range — the "press [E]" window is closed for it. Reads its
        //     RAW range state (IsPlayerInRange), never its canvas toggling,
        //     so the two scripts can never lock each other out.
        if (hideWhenProximityOutOfRange && AnyProximityOutOfRange()) return false;

        // 2c) a gate script on this text root keeps it hidden while its event
        //     spot is gated away (e.g. the ritual spot before all totems).
        //     A disabled gate component opts out and is ignored here.
        if (hideWhenSpotGated && managedGate != null && managedGate.isActiveAndEnabled && !managedGate.IsSpotActive) return false;

        // 3) every mesh under the target is hidden (generic pickup fallback)
        if (hideWhenAllMeshesHidden &&
            trackedRenderers != null && trackedRenderers.Length > 0 &&
            !AnyRendererEnabled(trackedRenderers)) return false;

        // 4) optional: every collider under the target is disabled
        if (hideWhenAllCollidersDisabled &&
            trackedColliders != null && trackedColliders.Length > 0 &&
            !AnyColliderEnabled(trackedColliders)) return false;

        return true;
    }

    private static bool AnyRendererEnabled(Renderer[] list)
    {
        for (int i = 0; i < list.Length; i++)
        {
            Renderer r = list[i];
            if (r != null && r.enabled) return true;
        }
        return false;
    }

    /// <summary>
    /// True while any ProximityText that toggles THIS text reports the player
    /// out of range. Reads the RAW range state (IsPlayerInRange) — never the
    /// canvas toggling — so this script and ProximityText can never lock each
    /// other out (raw states are independent inputs, no circular reads).
    /// </summary>
    private bool AnyProximityOutOfRange()
    {
        if (managedProximities == null) return false;
        for (int i = 0; i < managedProximities.Length; i++)
        {
            ProximityText proximity = managedProximities[i];
            if (proximity != null && proximity.isActiveAndEnabled && !proximity.IsPlayerInRange) return true;
        }
        return false;
    }

    private static bool AnyColliderEnabled(Collider[] list)
    {
        for (int i = 0; i < list.Length; i++)
        {
            Collider c = list[i];
            if (c != null && c.enabled) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Hiding / showing (never touches the GameObject's active state, so
    // FloatText.cs keeps running while the text is hidden)
    // ------------------------------------------------------------------ //

    private void SetVisible(bool value)
    {
        if (ownCanvas != null)
        {
            if (ownCanvas.enabled != value) ownCanvas.enabled = value;
            return;
        }

        if (ownRenderers != null)
        {
            for (int i = 0; i < ownRenderers.Length; i++)
            {
                Renderer r = ownRenderers[i];
                if (r != null && r.enabled != value) r.enabled = value;
            }
        }
    }

    /// <summary>Finds what to toggle: the Canvas on this object, or (fallback) this object's own renderers.</summary>
    private void FindOwnVisuals()
    {
        ownCanvas = GetComponent<Canvas>();
        if (ownCanvas != null) return;   // Canvas-based text (the normal setup)

        ownRenderers = GetComponentsInChildren<Renderer>(true);   // TextMesh-style text
    }

    // ------------------------------------------------------------------ //
    // Scanning the target
    // ------------------------------------------------------------------ //

    /// <summary>
    /// (Re)collects everything the rule needs: the InteractionPoint above the
    /// text, the renderers / colliders under the target (this text's own
    /// subtree is always excluded), the ProximityTexts that toggle this same
    /// text, and a RitualSpotFloatingText on this text root.
    /// </summary>
    private void ScanTarget()
    {
        // the point can sit on the target itself or anywhere above the text
        // (typical: the text root is a child of the interaction point object)
        point = hideWhenPointDepleted ? GetComponentInParent<InteractionPoint>(true) : null;
        if (point == null && target != null && target != transform.parent)
            point = target.GetComponentInParent<InteractionPoint>(true);

        trackedRenderers = CollectRenderersUnder(target);
        trackedColliders = CollectCollidersUnder(target);

        // v1.1 cooperation scans — see layers 2b/2c in ComputeVisible()
        managedGate = GetComponent<RitualSpotFloatingText>();
        managedProximities = CollectRelatedProximities();
    }

    /// <summary>
    /// Every ProximityText under the target that toggles THIS text (its Text
    /// Object is this text root or anything inside/above it). The typical
    /// layouts: the text is a CHILD of the item (ProximityText sits on the
    /// item = the target), or the text is a SIBLING of the item under the
    /// same parent (target = that shared parent).
    /// </summary>
    private ProximityText[] CollectRelatedProximities()
    {
        if (target == null) return null;

        ProximityText[] candidates = target.GetComponentsInChildren<ProximityText>(true);
        int related = 0;
        for (int i = 0; i < candidates.Length; i++)
            if (candidates[i] != null && TextBelongsToUs(candidates[i].TextObject)) related++;

        if (related == 0) return null;

        ProximityText[] result = new ProximityText[related];
        int n = 0;
        for (int i = 0; i < candidates.Length; i++)
            if (candidates[i] != null && TextBelongsToUs(candidates[i].TextObject))
                result[n++] = candidates[i];
        return result;
    }

    /// <summary>
    /// True when the object a ProximityText toggles is this text root, a
    /// child of it (the user dragged the TEXT child of the Canvas), or a
    /// parent of it (the user dragged a wrapper above the text root).
    /// Destroyed / never-assigned references never match.
    /// </summary>
    private bool TextBelongsToUs(GameObject managed)
    {
        if (managed == null) return false;   // Unity fake-null: destroyed or never assigned
        Transform t = managed.transform;
        return t == transform || IsOwnSubtree(t) || IsSubtreeOf(t);
    }

    /// <summary>True when this text root lies anywhere below root.</summary>
    private bool IsSubtreeOf(Transform root)
    {
        Transform t = transform;
        while (t != null)
        {
            if (t == root) return true;
            t = t.parent;
        }
        return false;
    }

    private Renderer[] CollectRenderersUnder(Transform root)
    {
        if (root == null) return new Renderer[0];
        return FilterOwnSubtree(root.GetComponentsInChildren<Renderer>(true));
    }

    private Collider[] CollectCollidersUnder(Transform root)
    {
        if (root == null) return new Collider[0];
        return FilterOwnSubtree(root.GetComponentsInChildren<Collider>(true));
    }

    /// <summary>Removes everything that belongs to this text itself (it must never hide itself by its own renderers).</summary>
    private T[] FilterOwnSubtree<T>(T[] components) where T : Component
    {
        int own = 0;
        for (int i = 0; i < components.Length; i++)
            if (components[i] != null && IsOwnSubtree(components[i].transform)) own++;

        if (own == 0) return components;   // the text is not under the target — nothing to filter

        T[] filtered = new T[components.Length - own];
        int n = 0;
        for (int i = 0; i < components.Length; i++)
            if (components[i] != null && !IsOwnSubtree(components[i].transform))
                filtered[n++] = components[i];
        return filtered;
    }

    /// <summary>True when t is this object or anything below it.</summary>
    private bool IsOwnSubtree(Transform t)
    {
        while (t != null)
        {
            if (t == transform) return true;
            t = t.parent;
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    [ContextMenu("Rescan Now")]
    private void RescanNow()
    {
        ScanTarget();
        visible = ComputeVisible();
        SetVisible(visible);
        Debug.Log("[FloatTextVisibility] '" + name + "' re-scanned: " +
                  (visible ? "visible" : "hidden") +
                  (point != null ? " (InteractionPoint: " + (point.IsReady ? "Ready" : "Depleted") + ")" : "") + ".", this);
    }
}