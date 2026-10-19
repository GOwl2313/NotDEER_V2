using UnityEngine;

/// <summary>
/// RITUAL SPOT FLOATING TEXT (v1.0) — shows your floating text at the exact
/// moment the event spot it belongs to appears.
///
/// THE PROBLEM THIS SOLVES
///   Your ritual spot stays perfectly hidden until all 4 totems are
///   collected (RitualEndingSpot disables its InteractionPoint and its
///   glowing marker while gated) — but a floating text placed under the spot
///   is NOT part of that hiding: "Place totems here" floats over the empty
///   ground from frame one, before the spot has even appeared.
///
/// WHAT THIS PLUG-IN DOES
///   Attach it to the TEXT ROOT (the object with your FloatText script on
///   it — the same place FloatTextVisibility goes). It follows the event
///   spot's InteractionPoint and keeps the text:
///     • HIDDEN while the spot is gated away (the ritual is not complete
///       yet — exactly like the spot's marker),
///     • VISIBLE from the SAME FRAME the spot is revealed (the 4th totem,
///       RevealRitualSpot(), the HUD's 'On All Totems Collected' event —
///       every reveal path re-enables the point, and this script reacts to
///       the point directly, so it can never drift out of sync with the
///       spot),
///     • HIDDEN again once the spot is used up (the ritual triggered — the
///       ending disables the point, the text goes with it).
///
/// WORKS FOR ANY GATED SPOT, NOT JUST THE RITUAL
///   The signal it follows is the event spot's InteractionPoint being
///   enabled/active. Any system that turns a point off to hide its spot
///   (gates, story locks, phase changes) now hides its floating text too —
///   with zero wiring.
///
/// FLOATTEXT-SAFE HIDING (the same technique as FloatTextVisibility):
///   • world-space Canvas on the text root → the Canvas component is
///     toggled. The GameObject stays active, so your FloatText script keeps
///     billboarding the whole time — no Start() re-runs, no lost offsets,
///     and the text snaps back exactly as it was the moment the spot
///     appears;
///   • TextMesh-style text (no Canvas) → its renderers are toggled;
///   • the hidden state is RE-ASSERTED every frame, so no other script can
///     accidentally leave the text visible while the spot is gated.
///
/// COOPERATES WITH THE OTHER TEXT SCRIPTS (never fights over the Canvas)
///   FloatTextVisibility and ProximityText may sit on the same text. All
///   these scripts read each other's RAW state (this one exposes
///   IsSpotActive, a live read of the point) and agree on a single rule:
///   the text is visible only while EVERY manager allows it — the spot is
///   active AND the owner still exists AND (when a ProximityText manages
///   the same text) the player is in range. Disable any manager component
///   to opt it out.
///
/// SETUP (one step)
///   1. Add this component to the text root (the object with FloatText.cs
///      — "Place totems here" — sitting under your ritual spot).
///   Done. The Target field can stay empty: the spot's InteractionPoint is
///   found on the text's parents automatically.
///   Only if the text is NOT a child of the spot: drag the spot object
///   into the 'Target' field.
///
/// UNITY 6 / URP / BUILT-IN / BOTH INPUT BACKENDS: uses no deprecated API
/// and no rendering API — works everywhere. Needs InteractionPoint.cs in
/// the project (any version v3.0+).
///
/// v1.0 — initial release.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(10)]   // after the points & RitualEndingSpot (order 0) —
                              // the point's enabled state is final for this
                              // frame by the time this script reads it
[AddComponentMenu("UI/Ritual Spot Floating Text (appears with the spot)")]
public class RitualSpotFloatingText : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Target (the event spot)")]

    [Tooltip("The event spot this text belongs to. Auto: the first InteractionPoint found on this text's parents (the usual setup — the text sits under the spot). Assign it manually ONLY if the text is NOT a child of the spot: drag the spot object here and its InteractionPoint is used.")]
    [SerializeField] private Transform target;

    [Header("Maintenance")]

    [Tooltip("Re-scan for the spot's InteractionPoint and the other text managers every N seconds (catches components created or removed at runtime — e.g. the point's glowing marker builds itself when Play starts). 0 = scan only once at Start.")]
    [SerializeField, Min(0f)] private float rescanInterval = 1f;

    [Tooltip("Write visibility changes to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private InteractionPoint point;             // the spot this text follows
    private Canvas ownCanvas;                  // how a Canvas-based text is toggled
    private Renderer[] ownRenderers;           // fallback: TextMesh-style texts
    private FloatTextVisibility sharedVisibility;   // other manager on this text (optional)
    private ProximityText proximity;                // other manager near this text (optional)
    private float rescanTimer;
    private bool textVisible;
    private bool warnedNoPoint;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// RAW state, read LIVE on every access: true while the spot's
    /// InteractionPoint is enabled and its object active — i.e. while the
    /// spot exists and is not gated away. FloatTextVisibility and
    /// ProximityText read this to stay in sync with this gate.
    /// </summary>
    public bool IsSpotActive
    {
        get { return point != null && point.isActiveAndEnabled; }
    }

    /// <summary>True while this gate currently shows the text.</summary>
    public bool IsTextVisible
    {
        get { return textVisible; }
    }

    /// <summary>
    /// The event spot this text follows (the object whose InteractionPoint
    /// gates the text). Assigning a new one re-resolves immediately.
    /// </summary>
    public Transform Target
    {
        get { return target; }
        set
        {
            if (target == value) return;
            target = value;
            point = null;          // re-resolved on the next Update / rescan
            ResolvePoint();
            ApplyCurrentState();
        }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        FindOwnVisuals();

        if (ownCanvas == null && (ownRenderers == null || ownRenderers.Length == 0))
        {
            Debug.LogWarning("[RitualSpotFloatingText] '" + name + "': no Canvas and no Renderer found on this " +
                             "object — there is nothing to show or hide. Attach this component to the TEXT ROOT " +
                             "(the same object FloatText.cs sits on). Disabling itself.", this);
            enabled = false;
            return;
        }

        ResolvePoint();
        ResolveCoManagers();

        // Never flash on spawn: a text under an already-revealed spot is
        // picked up by the first Update, one frame later at most.
        ApplyVisibility(false);

        if (debugLogs)
            Debug.Log("[RitualSpotFloatingText] '" + name + "' now follows '" +
                      (point != null ? point.name : "nothing (rescanning)") + "'." +
                      (point != null ? " Spot active: " + IsSpotActive + "." : ""), this);
    }

    private void Update()
    {
        ApplyCurrentState();

        // ---- periodic re-scan: the point may be created/removed at runtime,
        //      and so may the other text managers ----
        if (rescanInterval > 0f)
        {
            rescanTimer += Time.unscaledDeltaTime;
            if (rescanTimer >= rescanInterval)
            {
                rescanTimer = 0f;
                ResolvePoint();
                ResolveCoManagers();
            }
        }
    }

    /// <summary>Scene-view gizmo: a line from the text to the spot it follows.</summary>
    private void OnDrawGizmosSelected()
    {
        if (target == null) return;
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);   // amber, like the ritual fire
        Gizmos.DrawLine(transform.position, target.position);
        Gizmos.DrawWireSphere(target.position, 0.25f);
    }

    // ------------------------------------------------------------------ //
    // The rule
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Evaluates and applies the frame's truth. The hidden state is
    /// re-applied every frame (no other script can leave the text visible
    /// over a gated spot); the writes themselves are change-guarded, so a
    /// steady state costs nothing.
    /// </summary>
    private void ApplyCurrentState()
    {
        ApplyVisibility(ComputeVisible());
    }

    /// <summary>True while the text should be shown: the spot is active AND the other managers on this text allow it.</summary>
    private bool ComputeVisible()
    {
        // 1) the spot must exist and be active (the ritual revealed it /
        //    any gating system enabled the point)
        if (!IsSpotActive) return false;

        // 2) FloatTextVisibility on this text: its layers say the owner is
        //    gone (destroyed / deactivated / a Depleted point). Reads its
        //    evaluated state — never this component's own toggling — so the
        //    two scripts always agree. A disabled component opts out.
        if (sharedVisibility != null && sharedVisibility.isActiveAndEnabled && !sharedVisibility.IsTextVisible)
            return false;

        // 3) a ProximityText managing this same text: the player must be in
        //    ITS range too. Reads the RAW range state, never the toggling.
        if (proximity != null && proximity.isActiveAndEnabled && !proximity.IsPlayerInRange)
            return false;

        return true;
    }

    // ------------------------------------------------------------------ //
    // Show / hide (never SetActive — FloatText keeps running the whole time)
    // ------------------------------------------------------------------ //

    private void ApplyVisibility(bool show)
    {
        if (ownCanvas != null)
        {
            if (ownCanvas.enabled != show) ownCanvas.enabled = show;
        }
        else if (ownRenderers != null)
        {
            for (int i = 0; i < ownRenderers.Length; i++)
            {
                Renderer r = ownRenderers[i];
                if (r != null && r.enabled != show) r.enabled = show;
            }
        }

        if (textVisible != show)
        {
            textVisible = show;
            if (debugLogs)
                Debug.Log("[RitualSpotFloatingText] '" + name + "': text " +
                          (show ? "shown" : "hidden") + " (spot '" +
                          (point != null ? point.name : "-") + "' " +
                          (IsSpotActive ? "active" : "inactive") + ").", this);
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
    // Resolution
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Finds the InteractionPoint to follow: on the assigned Target (or its
    /// parents) when set, otherwise anywhere above this text. Re-scanned
    /// periodically — a point built or re-parented at runtime is picked up
    /// automatically.
    /// </summary>
    private void ResolvePoint()
    {
        if (target != null)
        {
            point = target.GetComponentInParent<InteractionPoint>(true);
        }
        else
        {
            point = GetComponentInParent<InteractionPoint>(true);
            if (point != null) target = point.transform;   // keep the gizmo line meaningful
        }

        if (point == null && !warnedNoPoint)
        {
            warnedNoPoint = true;
            Debug.LogWarning("[RitualSpotFloatingText] '" + name + "': no InteractionPoint found on this text's " +
                             "parents and no Target assigned — there is no spot to appear with. Put the text UNDER " +
                             "the event spot (or drag the spot object into 'Target'). Rescanning every " +
                             rescanInterval + "s; the text stays hidden until a spot is found.", this);
        }
    }

    /// <summary>
    /// Re-reads the other text managers this script cooperates with:
    /// FloatTextVisibility on this same text root, and a ProximityText on
    /// the spot (or its children) that toggles this same text.
    /// </summary>
    private void ResolveCoManagers()
    {
        sharedVisibility = GetComponent<FloatTextVisibility>();
        proximity = FindRelatedProximity();
    }

    /// <summary>
    /// A ProximityText on the spot (or under it) whose Text Object is this
    /// text root or anything inside it — the "in range too" manager.
    /// </summary>
    private ProximityText FindRelatedProximity()
    {
        if (point == null) return null;

        ProximityText[] candidates = point.GetComponentsInChildren<ProximityText>(true);
        for (int i = 0; i < candidates.Length; i++)
        {
            ProximityText candidate = candidates[i];
            if (candidate != null && TextBelongsToUs(candidate.TextObject)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// True when the object a ProximityText toggles is this text root or a
    /// child of it. Destroyed / never-assigned references never match.
    /// </summary>
    private bool TextBelongsToUs(GameObject managed)
    {
        if (managed == null) return false;   // Unity fake-null: destroyed or never assigned
        Transform t = managed.transform;
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
        ResolvePoint();
        ResolveCoManagers();
        ApplyCurrentState();
        Debug.Log("[RitualSpotFloatingText] '" + name + "' re-scanned: spot '" +
                  (point != null ? point.name : "-") + "' " + (IsSpotActive ? "active" : "inactive") +
                  ", text " + (textVisible ? "visible" : "hidden") + ".", this);
    }
}