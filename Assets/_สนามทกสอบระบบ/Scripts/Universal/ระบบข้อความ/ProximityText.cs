using UnityEngine;

/// <summary>
/// PROXIMITY TEXT (v1.0) — shows YOUR floating text only while the player is nearby.
///
/// WHAT IT DOES
///   This is the "press [E]" prompt the way YOU want it: it does not build
///   any UI of its own — it toggles YOUR text object (the one your FloatText
///   script billboards toward the camera):
///     • player enters the range → your text becomes visible,
///     • player leaves the range → your text hides,
///     • that is ALL it does. No keys, no pickup logic, no screen-space UI.
///
/// HOW IT HIDES/SHOWS (FloatText-safe):
///   • your text is a world-space Canvas (the normal FloatText setup) → the
///     Canvas component is toggled ON/OFF. The GameObject stays active, so
///     your FloatText script keeps running the whole time — no Start()
///     re-runs, no offset re-capture, no dead script.
///   • you dragged the TEXT child instead of the Canvas → the nearest
///     world-space parent Canvas is found and toggled.
///   • your text is a TextMesh (no Canvas at all) → its renderers are toggled.
///   • SAFETY: a Screen-Space canvas is never touched — dragging your HUD
///     text here can never switch your whole HUD off.
///
/// LIFECYCLE
///   • Put this component on the ITEM (e.g. the Totem), NOT on the text.
///     Drag your text object into 'Text Object'.
///   • When the item is picked up (ItemPickup destroys the item), this
///     component dies with it — and takes the text object with it by default
///     ('Destroy Text With Item'). Your text can sit anywhere: on the item
///     itself, or NEXT TO it (a sibling under the same parent) — without
///     this, a sibling text would keep floating over the empty spot forever.
///   • The item being deactivated hides the text instantly; activating it
///     again re-evaluates on the next frame.
///   • Freezes while GamePauseManager's pause menu is open (keeps its last
///     visibility, exactly like the InteractionPoints' prompts).
///
/// WORKS WITH OR WITHOUT ItemPickup:
///   • With ItemPickup on the same object (recommended for items):
///     'Auto Match Pickup Radius' (ON by default) mirrors the item's real
///     pickup radius every frame — the text can never disagree with the
///     actual E range. Change the radius on either component: both follow.
///   • Without it (a door, a note, a lever…): the own 'Range' field applies.
///
/// v1.2 — NEVER STUCK VISIBLE AGAIN + full cooperation with the other text
///   scripts (FloatTextVisibility, RitualSpotFloatingText):
///   • The HIDDEN state is re-applied every frame. Any script that re-enables
///     the text's Canvas behind this component's back — the classic case was
///     FloatTextVisibility's Start() re-enabling it because the parent is
///     alive — is corrected within one frame. (The show direction still
///     happens on a transition only, so two ProximityTexts sharing one text
///     never fight over it.)
///   • Before SHOWING, this component now defers to the other managers on the
///     same text: a FloatTextVisibility whose parent disappeared (or whose
///     InteractionPoint is Depleted) keeps the text hidden, and a
///     RitualSpotFloatingText whose event spot is still gated keeps it hidden
///     too. All the scripts read each other's RAW state — no one can lock the
///     others out, and they always agree, from frame one.
///
/// v1.0 — initial release.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-40)]   // just after ItemPickup (-50), before the points
public class ProximityText : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Text (your FloatText object)")]

    [Tooltip("YOUR floating text object — the one with FloatText.cs billboarding it (usually the world-space Canvas). Shown while the player is in range, hidden otherwise. Its style, text and position are never touched.")]
    [SerializeField] private GameObject textObject;

    [Header("Range")]

    [Tooltip("How close the player must be for the text to show (meters, measured HORIZONTALLY from the player's tagged object to this object).")]
    [SerializeField, Min(0.1f)] private float range = 2.5f;

    [Tooltip("When an ItemPickup sits on the same object, always use ITS pickup radius — the text then never disagrees with the actual E range. The 'Range' field above becomes the fallback for standalone use (doors, notes…).")]
    [SerializeField] private bool autoMatchItemPickupRadius = true;

    [Header("Player")]

    [Tooltip("The player the range is measured to. Auto-found by tag when left empty.")]
    [SerializeField] private Transform playerTransform;

    [Tooltip("Tag used to find the player automatically. Tag the player's ROOT 'Player' (Tags & Layers).")]
    [SerializeField] private string playerTag = "Player";

    [Header("Behaviour")]

    [Tooltip("Destroy the text object together with this component (picked-up item → no 'press [E]' text left floating over the empty spot). Turn OFF only when the text object is SHARED with something else that outlives this item.")]
    [SerializeField] private bool destroyTextWithItem = true;

    [Tooltip("Freeze while GamePauseManager's pause menu is open (keeps the last visibility).")]
    [SerializeField] private bool respectPause = true;

    [Tooltip("Write visibility changes to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Canvas textCanvas;        // the world-space canvas being toggled
    private Renderer[] textRenderers; // TextMesh-style fallback (no canvas found)
    private bool resolvedText;        // components resolved once (re-resolved after a warning)
    private bool textVisible;
    private ItemPickup linkedPickup;  // optional — only read when auto-matching
    private float searchTimer;
    private bool warnedNoPlayer;
    private bool warnedNoText;

    // v1.2 — the other scripts allowed to toggle the same text (cooperation)
    private FloatTextVisibility sharedVisibility;   // on the text object (optional)
    private RitualSpotFloatingText spotGate;        // on the text object (optional)
    private float managerRescanTimer;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>True while the player is within the (effective) range — updated every frame.</summary>
    public bool IsPlayerInRange { get; private set; }

    /// <summary>True while the text is currently shown.</summary>
    public bool IsTextVisible
    {
        get { return textVisible; }
    }

    /// <summary>The standalone range in meters (used when no ItemPickup auto-matching applies).</summary>
    public float Range
    {
        get { return range; }
        set { range = Mathf.Max(0.1f, value); }
    }

    /// <summary>The text object being toggled (assignable from code too).</summary>
    public GameObject TextObject
    {
        get { return textObject; }
        set
        {
            textObject = value;
            resolvedText = false;   // re-resolve the toggle targets with the new object
            textCanvas = null;
            textRenderers = null;
        }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        linkedPickup = GetComponent<ItemPickup>();   // optional link — enables live radius mirroring
        TryFindPlayer();                             // immediate first search, then every 0.5 s
        ApplyVisibility(false);                      // never flash on spawn — first Update re-evaluates
    }

    private void Update()
    {
        // Frozen with the rest of the game while the pause menu is open —
        // the text keeps its last state, exactly like the points' prompts.
        if (respectPause && GamePauseManager.IsPaused) return;

        bool want = false;

        if (ResolvePlayer())
        {
            Vector3 playerPos = playerTransform.position;
            Vector3 itemPos = transform.position;
            float dx = playerPos.x - itemPos.x;
            float dz = playerPos.z - itemPos.z;
            float effective = EffectiveRange();
            IsPlayerInRange = (dx * dx + dz * dz) <= effective * effective;
            want = IsPlayerInRange;
        }
        else
        {
            IsPlayerInRange = false;
        }

        // v1.2: while out of range the hidden state is RE-ASSERTED every
        // frame — if any other script re-enabled the text's Canvas behind
        // this component's back (the classic case: FloatTextVisibility's
        // Start() re-enabling it because the parent is alive), the text is
        // hidden again within one frame, no enter/leave cycle needed. The
        // writes are change-guarded, so this costs nothing while hidden.
        // While in range, showing still happens only on a transition so two
        // ProximityTexts sharing one text can never fight over it.
        if (want)
        {
            if (want != textVisible && OtherManagersAllowVisible()) ApplyVisibility(true);
        }
        else
        {
            ApplyVisibility(false);
        }

        // v1.2: the other managers on the text are re-checked periodically
        // (they may be added or removed at runtime).
        managerRescanTimer += Time.unscaledDeltaTime;
        if (managerRescanTimer >= 1f)
        {
            managerRescanTimer = 0f;
            if (textObject != null) ResolveCoManagers();
        }
    }

    /// <summary>Clamps Inspector values so the component can never receive nonsense input.</summary>
    private void OnValidate()
    {
        range = Mathf.Max(0.1f, range);
        if (string.IsNullOrEmpty(playerTag)) playerTag = "Player";
    }

    /// <summary>Component off or GameObject deactivated — never keep the text visible.</summary>
    private void OnDisable()
    {
        ApplyVisibility(false);
    }

    private void OnDestroy()
    {
        // The picked-up item is being destroyed — the floating text belongs
        // to it and must never keep floating over the empty spot. (A text
        // that is a CHILD of this object dies with it anyway — destroying
        // it again is harmless.)
        if (destroyTextWithItem && textObject != null)
        {
            Destroy(textObject);
            textObject = null;
        }
    }

    /// <summary>Scene-view gizmo: the effective range as a cyan disc on the ground plane through the object.</summary>
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
        DrawWireDisc(transform.position, EffectiveRange(), 40);
    }

    private static void DrawWireDisc(Vector3 center, float radius, int segments)
    {
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    // ------------------------------------------------------------------ //
    // Show / hide (never SetActive — FloatText keeps running the whole time)
    // ------------------------------------------------------------------ //

    private void ApplyVisibility(bool show)
    {
        if (textObject == null)
        {
            // Unity's '==' treats a DESTROYED object as null too: when the text
            // object died (e.g. during teardown of the item's parent) there is
            // nothing to show/hide and nothing to warn about — stay hidden
            // silently. Only a field that was genuinely never ASSIGNED warns.
            if (ReferenceEquals(textObject, null) && !warnedNoText)
            {
                warnedNoText = true;
                Debug.LogWarning("[ProximityText] '" + name + "': no Text Object assigned — drag your floating " +
                                 "text object (the one with FloatText.cs on it) into the 'Text Object' field.", this);
            }
            textVisible = false;
            return;
        }

        if (!resolvedText) ResolveTextComponents();

        if (textCanvas != null)
        {
            if (textCanvas.enabled != show) textCanvas.enabled = show;
            if (textVisible != show)
            {
                textVisible = show;
                if (debugLogs) Debug.Log("[ProximityText] '" + name + "': text " + (show ? "shown" : "hidden") + ".", this);
            }
        }
        else if (textRenderers != null && textRenderers.Length > 0)
        {
            for (int i = 0; i < textRenderers.Length; i++)
            {
                Renderer r = textRenderers[i];
                if (r != null && r.enabled != show) r.enabled = show;
            }
            if (textVisible != show)
            {
                textVisible = show;
                if (debugLogs) Debug.Log("[ProximityText] '" + name + "': text " + (show ? "shown" : "hidden") + ".", this);
            }
        }
        // nothing toggleable on the text object → ResolveTextComponents already
        // warned once; IsTextVisible stays false (nothing was actually shown)
    }

    /// <summary>
    /// Finds what to toggle on the text object, in order:
    ///   1. a WORLD-SPACE Canvas on the object or in its children (the normal
    ///      FloatText setup);
    ///   2. a WORLD-SPACE Canvas on the parents (the user dragged the TEXT
    ///      child of a Canvas — climb up to the Canvas that renders it);
    ///   3. renderers in the children (TextMesh-style 3D text).
    /// Screen-space canvases are deliberately NEVER touched (safety: an
    /// accidentally-dragged HUD text can never switch the HUD off).
    /// </summary>
    private void ResolveTextComponents()
    {
        resolvedText = true;
        textCanvas = null;
        textRenderers = null;

        // v1.2: the other text managers on the same object (cooperation —
        // see OtherManagersAllowVisible). Re-read periodically in Update.
        ResolveCoManagers();

        // 1) Canvas on the object itself or in its children
        Canvas canvas = textObject.GetComponent<Canvas>();
        if (canvas == null) canvas = textObject.GetComponentInChildren<Canvas>(true);
        if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
        {
            textCanvas = canvas;
            return;
        }
        if (canvas != null && canvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogWarning("[ProximityText] '" + name + "': the text object '" + textObject.name +
                             "' uses a SCREEN-SPACE Canvas — ProximityText only manages world-space floating text " +
                             "(toggling it could switch your HUD off). Assign the world-space Canvas your FloatText " +
                             "script billboards instead.", this);
            return;
        }

        // 2) climb up: the user may have dragged the TEXT child of a Canvas
        Transform t = textObject.transform.parent;
        int climbed = 0;
        while (t != null && climbed < 4)
        {
            Canvas parentCanvas = t.GetComponent<Canvas>();
            if (parentCanvas != null)
            {
                if (parentCanvas.renderMode == RenderMode.WorldSpace)
                {
                    textCanvas = parentCanvas;
                    return;
                }
                Debug.LogWarning("[ProximityText] '" + name + "': the parent Canvas of '" + textObject.name +
                                 "' is SCREEN-SPACE — ProximityText only manages world-space floating text. " +
                                 "Assign the world-space Canvas your FloatText script billboards instead.", this);
                return;
            }
            t = t.parent;
            climbed++;
        }

        // 3) no Canvas anywhere — TextMesh-style 3D text: toggle its renderers
        textRenderers = textObject.GetComponentsInChildren<Renderer>(true);
        if (textRenderers.Length == 0)
        {
            Debug.LogWarning("[ProximityText] '" + name + "': the text object '" + textObject.name +
                             "' has neither a Canvas nor a Renderer — nothing to show or hide. " +
                             "Is it the right object?", this);
        }
    }

    /// <summary>Re-reads which other text managers sit on the text object (v1.2).</summary>
    private void ResolveCoManagers()
    {
        if (textObject == null) return;
        sharedVisibility = textObject.GetComponent<FloatTextVisibility>();
        spotGate = textObject.GetComponent<RitualSpotFloatingText>();
    }

    /// <summary>
    /// v1.2 — true when no other manager on this text is currently keeping it
    /// hidden. Reads their evaluated states only — never this component's own
    /// canvas toggling — and those states are built from RAW inputs (range,
    /// parent liveness, spot activity) that no script can feed back into
    /// itself, so the scripts always agree and can never lock each other out.
    /// Disabled components are ignored: the user turned them off.
    /// </summary>
    private bool OtherManagersAllowVisible()
    {
        // FloatTextVisibility: the text's owner is gone (destroyed /
        // deactivated / a Depleted InteractionPoint / hidden meshes).
        if (sharedVisibility != null && sharedVisibility.isActiveAndEnabled && !sharedVisibility.IsTextVisible)
            return false;

        // RitualSpotFloatingText: the event spot this text belongs to is
        // still gated away (e.g. the ritual spot waiting for its totems).
        if (spotGate != null && spotGate.isActiveAndEnabled && !spotGate.IsSpotActive)
            return false;

        return true;
    }

    // ------------------------------------------------------------------ //
    // Range
    // ------------------------------------------------------------------ //

    /// <summary>The range that actually applies: the linked item's pickup radius when auto-matching, otherwise the own range.</summary>
    private float EffectiveRange()
    {
        if (autoMatchItemPickupRadius && linkedPickup != null)
            return linkedPickup.PickupRadius;
        return range;
    }

    // ------------------------------------------------------------------ //
    // Player finding (retry quietly until the player exists)
    // ------------------------------------------------------------------ //

    private bool ResolvePlayer()
    {
        if (playerTransform != null) return true;   // also survives destroy: Unity fake-null

        searchTimer += Time.unscaledDeltaTime;
        if (searchTimer < 0.5f) return false;       // retry every 0.5 seconds only
        searchTimer = 0f;

        TryFindPlayer();
        return playerTransform != null;
    }

    private void TryFindPlayer()
    {
        if (playerTransform != null) return;

        GameObject player = FindPlayerByTag();
        if (player != null)
        {
            playerTransform = player.transform;
            return;
        }

        if (!warnedNoPlayer)
        {
            warnedNoPlayer = true;
            Debug.LogWarning("[ProximityText] '" + name + "': no GameObject tagged '" + playerTag +
                             "' found yet. Tag the player's ROOT '" + playerTag +
                             "' (or drag it into 'Player Transform'). Retrying every 0.5 s.", this);
        }
    }

    /// <summary>Tag lookup that survives a tag that does not exist in the project yet.</summary>
    private GameObject FindPlayerByTag()
    {
        if (string.IsNullOrEmpty(playerTag)) return null;

        try
        {
            return GameObject.FindGameObjectWithTag(playerTag);
        }
        catch
        {
            return null;   // the tag is not defined — the warning above already said it
        }
    }
}
