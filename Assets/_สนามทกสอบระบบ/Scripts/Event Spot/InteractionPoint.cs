using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// UNIVERSAL INTERACTION POINT — a glowing GTA-San-Andreas-style marker: a
/// VERTICAL HOLLOW CYLINDER of translucent light (see InteractionRing.shader
/// v3.0) that only the PLAYER can interact with.
///
/// HOW TO CREATE ONE (zero setup):
///   1. Create an empty GameObject where the marker should be.
///   2. Add this component. Everything else builds itself:
///      • the "Interaction Point" tag is created in Tags &amp; Layers (editor,
///        silently — see the v3.0 tag fix below) and assigned automatically,
///      • the glowing cylinder becomes REAL IN THE EDITOR immediately (a
///        persistent child object with the shared marker material),
///      • the shared material asset Assets/Materials/ZAI_InteractionRing.mat
///        is created once (referencing the shader → the shader can never be
///        stripped from builds),
///      • a trigger capsule collider is added (invisible, TRIGGER = nothing
///        physical ever blocks movement — the player, enemies and the camera
///        all move through it freely; only the player TAG can interact).
///
/// THE MARKER LOOK (GTA "hollowed cylinder"):
///   the marker grows upward from the object's pivot — Radius x Radius
///   footprint, Height meters tall. The tube walls glow softly, the
///   silhouette edges burn brighter (the hollow-tube trick), and the top and
///   bottom have hot rim bands — all live-editable in the Inspector:
///   • Color / Opacity / Radius / Height — the four basics,
///   • Wall Glow (body translucency), Edge Glow + Edge Power (silhouette),
///     Rim Band Height + Rim Strength (top/bottom rings), Top Fade (the
///     glow dissolving toward the top), Pulse (breathing).
///
/// USAGE MODES (toggle freely):
///   • SingleUse   — disappears forever after one use.
///   • Respawnable — fades out, then fades back in after "Respawn Time"
///                   seconds (the timer is an Inspector value).
///   • Permanent   — never disappears; usable again after
///                   "Permanent Repeat Delay" seconds (it dims while
///                   recharging).
///
/// GIVING IT BEHAVIOUR (the "universal" part — plug anything in later):
///   • NO CODE:   the UnityEvents On Interact / On Depleted / On Respawned
///                (On Interact even receives the player GameObject — wire
///                e.g. FlashlightController.RechargeBattery to it).
///   • WITH CODE: extend InteractionPointListener and attach it next to the
///                point (auto-discovered) — see InteractionPointListener.cs
///                and the ready-made example BatteryRechargePoint.cs.
///   • DIRECT:    call TryActivate(player) / Deplete() / Respawn() yourself.
///
/// ACTIVATION:
///   • Trigger On Enter — the classic GTA way: walking into the marker
///     activates it.
///   • Press Key — a "Press E" prompt (code-built, no assets) follows the
///     point while the player stands inside; pressing the key activates it.
///
/// UPGRADING FROM v2.0 (the flat ring): just overwrite this file and
/// InteractionRing.shader — existing points upgrade automatically:
///   • your saved Color / Opacity / Radius migrate (FormerlySerializedAs);
///     the old "ring height" (center height) becomes the cylinder HEIGHT,
///     so raise it if the marker looks too short (GTA default = 2 m),
///   • the old "Interaction Ring Visual" child is renamed and its flat quad
///     mesh is swapped for the cylinder tube,
///   • the shader NAME is unchanged, so the shared material asset and saved
///     scenes keep working untouched.
///
/// REQUIREMENTS
///   • Keep InteractionRing.shader, InteractionPointListener.cs,
///     KeyCodeBridge.cs and GamePauseManager.cs in the project (the first is
///     the visual, the listener is the extension base, the last two are the
///     project's existing input / pause utilities this script reuses).
///   • The player root must be tagged "Player" and carry a Rigidbody or a
///     CharacterController (Unity only sends trigger events to moving
///     colliders that have one — the existing player setup already does).
///
/// PERFORMANCE: one open tube (96 triangles), one unlit transparent material
/// per point, no lights/shadows/particles; while depleted the renderer is
/// disabled entirely (zero draw calls). The prompt UI is only built if
/// "Press Key" mode is actually used.
///
/// v1.0 — initial release.
/// v2.0 — Unity 6 / URP hardening: URP-native SRP-Batcher-compatible shader
///        path (with a Built-in RP fallback SubShader), a real EDIT-MODE
///        ring, an auto-created shared material asset that guarantees the
///        shader survives builds.
/// v3.0 — GTA hollow-cylinder visual: the flat ring became a vertical
///        translucent tube (procedural mesh + fresnel/rim shader). Unity 6
///        fix: FindAll() no longer uses the deprecated
///        FindObjectsByType overload (the two CS0618 warnings are gone).
///        TAG FIX: creating the "Interaction Point" tag no longer produces
///        "Tag: Interaction Point is not defined" console errors — the tag
///        is written to Tags &amp; Layers silently and auto-assigned after the
///        next script reload (OnValidate). v2.0 scenes upgrade in place.
/// v3.1 — universal interaction key support (item system): new read-only
///        property WantsInteractKey — true when this point would consume
///        the interaction key RIGHT NOW ('Press Key' mode + Ready + not a
///        recharging Permanent point). ItemPickup.cs reads it to give a
///        ready point priority over item pickups on the shared E key.
///        Purely additive — zero behavior change otherwise.
/// </summary>
[DisallowMultipleComponent]
public class InteractionPoint : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Public constants & types
    // ------------------------------------------------------------------ //

    /// <summary>Tag this component puts on its GameObject (auto-created in the editor).</summary>
    public const string PointTag = "Interaction Point";

    /// <summary>Name of the shader the marker visual is built from (InteractionRing.shader).</summary>
    public const string RingShaderName = "ZAI/Unlit/InteractionRing";

    public enum ActivationMode
    {
        [InspectorName("Trigger On Enter (GTA style)")] TriggerOnEnter,
        [InspectorName("Press Key")] PressKey
    }

    public enum UsageMode
    {
        [InspectorName("Single Use (disappears forever)")] SingleUse,
        [InspectorName("Respawnable (comes back after timer)")] Respawnable,
        [InspectorName("Permanent (stays, repeats)")] Permanent
    }

    public enum PointState
    {
        Ready,
        Depleted
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Interaction")]

    [Tooltip("How the player activates the point. 'Trigger On Enter' = the classic GTA way (walking into the marker). 'Press Key' = a 'Press E' prompt appears while standing inside.")]
    [SerializeField] private ActivationMode activationMode = ActivationMode.TriggerOnEnter;

    [Tooltip("Key that activates the point while the player stands inside it (only used by 'Press Key' mode). Works on BOTH input backends — keep KeyCodeBridge.cs in the project when using the new Input System.")]
    [SerializeField] private KeyCode interactKey = KeyCode.E;

    [Tooltip("Tag that identifies the player. Checked on the entering collider AND on all of its parents, so the player's collider may sit anywhere in the player hierarchy.")]
    [SerializeField] private string playerTag = "Player";

    [Tooltip("What happens after the point is used. SingleUse disappears forever; Respawnable fades back in after 'Respawn Time' seconds; Permanent stays and can be used again after 'Permanent Repeat Delay' seconds (it dims while recharging).")]
    [SerializeField] private UsageMode usageMode = UsageMode.SingleUse;

    [Tooltip("Seconds the point stays gone before it fades back in (Respawnable mode only).")]
    [SerializeField, Min(0.1f)] private float respawnTime = 15f;

    [Tooltip("Minimum seconds between two uses in Permanent mode.")]
    [SerializeField, Min(0f)] private float permanentRepeatDelay = 1f;

    [Header("Marker Visual — GTA hollow cylinder (live-editable, even while playing)")]

    [Tooltip("Color of the glowing cylinder.")]
    [FormerlySerializedAs("ringColor")]
    [SerializeField] private Color markerColor = new Color(1f, 0.35f, 0.06f, 1f);

    [Tooltip("Overall opacity of the whole marker (0 = invisible, 1 = fully solid).")]
    [SerializeField, Range(0f, 1f)] private float opacity = 0.85f;

    [Tooltip("Radius of the cylinder in meters (the 'size').")]
    [FormerlySerializedAs("ringRadius")]
    [SerializeField, Min(0.05f)] private float markerRadius = 0.6f;

    [Tooltip("Height of the cylinder in meters. The marker grows UPWARD from this object's pivot (GTA-style: about 2 for a full-height marker).")]
    [FormerlySerializedAs("ringHeight")]
    [SerializeField, Min(0.05f)] private float markerHeight = 2f;

    [Tooltip("Translucency of the tube's WALLS — the body of the glowing column. 0 = only the edges glow, 1 = solid-looking walls.")]
    [SerializeField, Range(0f, 1f)] private float wallOpacity = 0.30f;

    [Tooltip("Brightness of the silhouette edges. Looking at the tube from a grazing angle sees through much more glowing volume, so the OUTLINE burns bright — this is what makes the marker read as a HOLLOWED CYLINDER.")]
    [SerializeField, Range(0f, 4f)] private float edgeGlow = 1.75f;

    [Tooltip("How sharply the edge glow concentrates on the silhouette. Low = broad soft glow, high = thin bright outline.")]
    [SerializeField, Range(0.25f, 8f)] private float edgePower = 2.5f;

    [Tooltip("Height of the hot rim bands at the very top and bottom of the cylinder, as a fraction of the total height.")]
    [SerializeField, Range(0f, 0.5f)] private float rimWidth = 0.12f;

    [Tooltip("Brightness of the hot rim bands at the top and bottom.")]
    [SerializeField, Range(0f, 4f)] private float rimIntensity = 2.4f;

    [Tooltip("How much the glow fades toward the top of the cylinder. 0 = even column, 1 = strong at the ground, dissolving at the top (the reference look).")]
    [SerializeField, Range(0f, 1f)] private float topFade = 0.45f;

    [Header("Animation (script-driven — freezes with the pause menu)")]

    [Tooltip("Breathing speed, breaths per second. 0 = off.")]
    [SerializeField, Min(0f)] private float pulseSpeed = 1.4f;

    [Tooltip("How strongly the brightness breathes (0 = none, 1 = full).")]
    [SerializeField, Range(0f, 1f)] private float pulseAmount = 0.15f;

    [Header("Interaction Prompt ('Press Key' mode only)")]

    [Tooltip("Show the 'Press E' style prompt while the player stands inside the point (built entirely from code — no assets).")]
    [SerializeField] private bool showPrompt = true;

    [Tooltip("Text of the prompt shown while the player stands inside the point.")]
    [SerializeField] private string promptText = "Press E";

    [Tooltip("Prompt height above the cylinder's TOP, in meters.")]
    [SerializeField] private float promptOffsetY = 0.4f;

    [Header("Trigger Volume")]

    [Tooltip("Size of the invisible trigger capsule relative to the cylinder (1 = same radius as the marker). The collider is a TRIGGER: nothing ever physically blocks on it — the player, enemies and the camera all pass through freely; only the player TAG can interact.")]
    [SerializeField, Range(0.1f, 2f)] private float triggerSize = 0.85f;

    [Tooltip("Keep the trigger capsule's radius/height/center synced to Radius and Height. Turn OFF to size the capsule by hand in the Inspector.")]
    [SerializeField] private bool autoSizeTrigger = true;

    [Header("Events (behaviour without code)")]

    [Tooltip("Fires when the player activates the point. The player's root GameObject is passed as the dynamic argument (wire any method that takes a GameObject — e.g. FlashlightController.RechargeBattery).")]
    public UnityEvent<GameObject> onInteract;

    [Tooltip("Fires when the point is used up (Single Use) or starts its respawn wait (Respawnable).")]
    public UnityEvent onDepleted;

    [Tooltip("Fires when a Respawnable point fades back in after its timer.")]
    public UnityEvent onRespawned;

    [Header("Advanced")]

    [Tooltip("Seconds the fade-out / fade-in animation takes when the point is used or respawns.")]
    [SerializeField, Min(0.05f)] private float fadeDuration = 0.45f;

    [Tooltip("Draw the marker preview in the Scene view (a real-time preview in edit mode, a thin outline while playing).")]
    [SerializeField] private bool drawSceneGizmos = true;

    [Tooltip("Write debug messages to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private PointState state = PointState.Ready;
    private float respawnTimer;
    private float permanentCooldown;
    private float fadeCurrent = 1f;
    private float fadeTarget = 1f;
    private float pulsePhase;

    private Collider triggerCollider;          // the interaction volume (trigger)
    private CapsuleCollider ownCapsule;        // the capsule when it is ours to auto-size
    private Transform visual;                  // the marker visual child
    private MeshRenderer visualRenderer;
    private Material ringMaterial;
    private InteractionPointListener[] listeners;

    private readonly HashSet<Collider> insidePlayerColliders = new HashSet<Collider>();
    private GameObject playerInside;

    // prompt UI (built lazily, only for 'Press Key' mode)
    private Canvas promptCanvas;
    private Text promptLabel;

    // cached shader property ids (no per-frame string allocations)
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int WallAlphaId = Shader.PropertyToID("_WallAlpha");
    private static readonly int EdgePowerId = Shader.PropertyToID("_EdgePower");
    private static readonly int EdgeBoostId = Shader.PropertyToID("_EdgeBoost");
    private static readonly int RimWidthId = Shader.PropertyToID("_RimWidth");
    private static readonly int RimIntensityId = Shader.PropertyToID("_RimIntensity");
    private static readonly int TopFadeId = Shader.PropertyToID("_TopFade");
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int PulseAmountId = Shader.PropertyToID("_PulseAmount");
    private static readonly int PulsePhaseId = Shader.PropertyToID("_PulsePhase");

    // shared mesh (one for all points — never destroyed)
    private static Mesh s_tubeMesh;

    private const string VisualChildName = "Interaction Marker Visual";
    private const string LegacyVisualChildName = "Interaction Ring Visual";   // v1/v2 scenes

    /// <summary>Radial segments of the marker tube (48 → a perfectly smooth silhouette).</summary>
    private const int TubeSegments = 48;

#if UNITY_EDITOR
    private static bool s_shaderInclusionDone;
    private static bool s_warnedShaderMissing;
    private static bool s_tagCreatedThisSession;   // statics are wiped by every script reload —
                                                   // exactly when Unity refreshes the tag database
    private static Material s_ringMaterialAsset;   // cached shared ring material asset

    /// <summary>Folder that holds the auto-created shared marker material asset.</summary>
    private const string RingMaterialDir = "Assets/Materials";

    /// <summary>
    /// The shared material asset every marker visual references in the editor.
    /// A saved scene referencing this asset pulls the marker shader into every
    /// BUILD automatically — the most robust anti-stripping guarantee there
    /// is (no Shader.Find needed at runtime at all).
    /// </summary>
    private const string RingMaterialAssetPath = "Assets/Materials/ZAI_InteractionRing.mat";
#endif

    // ------------------------------------------------------------------ //
    // Public API (properties)
    // ------------------------------------------------------------------ //

    /// <summary>Current lifecycle state: Ready (usable) or Depleted (gone / waiting to respawn).</summary>
    public PointState CurrentState
    {
        get { return state; }
    }

    /// <summary>True while the point can be activated.</summary>
    public bool IsReady
    {
        get { return state == PointState.Ready; }
    }

    /// <summary>The player's root GameObject while they stand inside the point, otherwise null.</summary>
    public GameObject PlayerInside
    {
        get { return playerInside; }
    }

    /// <summary>
    /// True when this point would consume the interaction key RIGHT NOW:
    /// 'Press Key' mode, Ready, and not a Permanent point waiting out its
    /// repeat delay. Other E-key systems (ItemPickup) read this to give the
    /// point priority on the game's universal interaction key.
    /// </summary>
    public bool WantsInteractKey
    {
        get
        {
            return activationMode == ActivationMode.PressKey &&
                   state == PointState.Ready &&
                   !(usageMode == UsageMode.Permanent && permanentCooldown > 0f);
        }
    }

    /// <summary>The marker's color (live in Play mode).</summary>
    public Color MarkerColor
    {
        get { return markerColor; }
        set
        {
            markerColor = value;
            if (Application.isPlaying) ApplyVisualProperties();
        }
    }

    /// <summary>The marker's opacity, 0-1 (live in Play mode).</summary>
    public float Opacity
    {
        get { return opacity; }
        set
        {
            opacity = Mathf.Clamp01(value);
            if (Application.isPlaying) ApplyVisualProperties();
        }
    }

    /// <summary>The marker's radius in meters (live in Play mode; also resizes the trigger).</summary>
    public float MarkerRadius
    {
        get { return markerRadius; }
        set
        {
            markerRadius = Mathf.Max(0.05f, value);
            if (Application.isPlaying)
            {
                ApplyTransform();
                SyncTriggerSize();
            }
        }
    }

    /// <summary>The marker's height in meters, growing up from the pivot (live in Play mode; also resizes the trigger).</summary>
    public float MarkerHeight
    {
        get { return markerHeight; }
        set
        {
            markerHeight = Mathf.Max(0.05f, value);
            if (Application.isPlaying)
            {
                ApplyTransform();
                SyncTriggerSize();
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Public API (methods)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// All interaction points in the scene, including inactive ones.
    /// Handy for managers, cutscenes or save systems.
    /// </summary>
    public static InteractionPoint[] FindAll()
    {
#if UNITY_6000_0_OR_NEWER
        // Unity 6: the FindObjectsSortMode overloads are deprecated (they
        // produced CS0618 warnings) — the sort-free overload is the current API.
        return FindObjectsByType<InteractionPoint>(FindObjectsInactive.Include);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<InteractionPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<InteractionPoint>();
#endif
    }

    /// <summary>
    /// Activates the point as if the given player had walked in / pressed the
    /// key. Fires the events and listeners, then applies the Usage Mode.
    /// Returns false (and does nothing) when the point is not ready.
    /// </summary>
    public bool TryActivate(GameObject player)
    {
        if (!isActiveAndEnabled) return false;
        if (state != PointState.Ready) return false;
        if (usageMode == UsageMode.Permanent && permanentCooldown > 0f) return false;
        if (player == null) return false;

        // 1) no-code hooks (the player GameObject is the dynamic argument)
        if (onInteract != null) onInteract.Invoke(player);

        // 2) code hooks — any listener returning true consumes the interaction.
        //    Inspector-wired events also count as consumption so a pure-event
        //    point (no listeners) still follows its Usage Mode.
        bool consumed = (onInteract != null && onInteract.GetPersistentEventCount() > 0) ||
                        listeners == null || listeners.Length == 0;

        if (listeners != null)
        {
            for (int i = 0; i < listeners.Length; i++)
            {
                InteractionPointListener listener = listeners[i];
                if (listener == null) continue;
                if (listener.OnPointActivated(this, player)) consumed = true;
            }
        }

        if (!consumed)
        {
            if (debugLogs)
                Debug.Log("[InteractionPoint] '" + name + "': no listener consumed the interaction — the point stays ready.", this);
            return false;
        }

        switch (usageMode)
        {
            case UsageMode.SingleUse:
                Deplete();
                break;

            case UsageMode.Respawnable:
                Deplete();
                respawnTimer = respawnTime;
                break;

            case UsageMode.Permanent:
                permanentCooldown = permanentRepeatDelay;
                break;
        }

        if (debugLogs)
            Debug.Log("[InteractionPoint] '" + name + "' activated by '" + player.name + "' (mode: " + usageMode + ").", this);

        return true;
    }

    /// <summary>
    /// Removes the point immediately (marker fades out, trigger disabled).
    /// SingleUse/Respawnable points stay gone until Respawn()/timer.
    /// Call this from cutscenes or scripts.
    /// </summary>
    public void Deplete()
    {
        if (state == PointState.Depleted) return;
        state = PointState.Depleted;

        // Disable the trigger so nothing can interact while gone. Unity does
        // NOT send OnTriggerExit when a collider is disabled, so the player
        // tracking is cleared manually here.
        if (triggerCollider != null) triggerCollider.enabled = false;
        insidePlayerColliders.Clear();
        playerInside = null;
        fadeTarget = 0f;

        if (onDepleted != null) onDepleted.Invoke();

        if (listeners != null)
        {
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null) listeners[i].OnPointDepleted(this);
            }
        }

        if (debugLogs) Debug.Log("[InteractionPoint] '" + name + "' depleted.", this);
    }

    /// <summary>
    /// Brings a depleted point back immediately (marker fades in, trigger
    /// re-enabled). A player standing inside it triggers it on their NEXT
    /// entry (step out and back in, GTA-style).
    /// </summary>
    public void Respawn()
    {
        if (state == PointState.Ready) return;
        state = PointState.Ready;
        respawnTimer = 0f;
        permanentCooldown = 0f;

        if (triggerCollider != null) triggerCollider.enabled = true;
        if (visualRenderer != null && !visualRenderer.enabled) visualRenderer.enabled = true;
        fadeTarget = 1f;

        if (onRespawned != null) onRespawned.Invoke();

        if (listeners != null)
        {
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null) listeners[i].OnPointRespawned(this);
            }
        }

        if (debugLogs) Debug.Log("[InteractionPoint] '" + name + "' respawned.", this);
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ApplyTagRuntime();
        EnsureTriggerCollider();
        listeners = GetComponentsInChildren<InteractionPointListener>(true);
        BuildVisual();
    }

    private void Reset()
    {
        // Reset runs in the EDITOR when the component is first added —
        // create the trigger collider AND the real marker visual right away,
        // so both are visible and saved with the scene.
        EnsureTriggerCollider();

#if UNITY_EDITOR
        EnsureTagEditor();
        EnsureShaderIncludedEditor();
        EnsureVisualEditor();
        if (gameObject.scene.IsValid())
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);

        Debug.Log("[InteractionPoint] '" + name + "' ready — the glowing cylinder is now visible in the editor. " +
                  "Set Radius / Height / Color in the Inspector, wire 'On Interact' or attach an " +
                  "InteractionPointListener for behaviour.", this);
#endif
    }

    private void Update()
    {
        // Frozen with the rest of the game while the pause menu is open
        // (Time.timeScale = 0 also zeroes deltaTime — double-guaranteed).
        if (GamePauseManager.IsPaused) return;

        // ---- 'Press Key' activation ----
        if (activationMode == ActivationMode.PressKey &&
            playerInside != null &&
            state == PointState.Ready &&
            ReadInteractKeyPressed() &&
            !KeyCodeBridge.WasKeyConsumedThisFrame(interactKey))   // an ItemPickup (or another point) already used this press
        {
            if (TryActivate(playerInside))
            {
                // one press = one point: claim the universal key for this
                // frame so no other E-key system fires on the same press
                KeyCodeBridge.ConsumeKey(interactKey);
            }
        }

        // ---- respawn countdown (Respawnable mode) ----
        if (state == PointState.Depleted && usageMode == UsageMode.Respawnable)
        {
            respawnTimer -= Time.deltaTime;
            if (respawnTimer <= 0f) Respawn();
        }

        // ---- repeat delay (Permanent mode) ----
        if (permanentCooldown > 0f)
            permanentCooldown = Mathf.Max(0f, permanentCooldown - Time.deltaTime);

        // ---- fade target from the current state ----
        float target = 1f;
        if (state == PointState.Depleted) target = 0f;
        else if (usageMode == UsageMode.Permanent && permanentCooldown > 0f) target = 0.3f;   // dimmed = recharging
        fadeTarget = target;

        if (!Mathf.Approximately(fadeCurrent, fadeTarget))
        {
            fadeCurrent = Mathf.MoveTowards(fadeCurrent, fadeTarget, Time.deltaTime / Mathf.Max(0.01f, fadeDuration));
            if (ringMaterial != null) ringMaterial.SetFloat(FadeId, fadeCurrent);

            // fully faded out: disable the renderer entirely — zero draw calls
            if (fadeCurrent <= 0.001f && fadeTarget <= 0.001f && visualRenderer != null && visualRenderer.enabled)
                visualRenderer.enabled = false;
        }

        // ---- animation phase (script-driven so the pause menu freezes it) ----
        bool animate = visualRenderer != null && visualRenderer.enabled;
        if (animate && pulseSpeed > 0f && pulseAmount > 0f)
        {
            pulsePhase = Mathf.Repeat(pulsePhase + pulseSpeed * Time.deltaTime, 1f);
            if (ringMaterial != null) ringMaterial.SetFloat(PulsePhaseId, pulsePhase);
        }
    }

    private void LateUpdate()
    {
        if (GamePauseManager.IsPaused) return;
        UpdatePrompt();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!enabled) return;

        GameObject playerRoot;
        if (!IsPlayerCollider(other, out playerRoot)) return;   // everything that is not the player passes through silently

        insidePlayerColliders.Add(other);
        playerInside = playerRoot;

        if (debugLogs)
            Debug.Log("[InteractionPoint] Player '" + playerRoot.name + "' entered '" + name + "'.", this);

        if (activationMode == ActivationMode.TriggerOnEnter)
            TryActivate(playerRoot);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!insidePlayerColliders.Remove(other)) return;
        if (insidePlayerColliders.Count == 0)
            playerInside = null;    // the player fully left (another collider may still be inside)
    }

    private void OnValidate()
    {
        // Clamp every Inspector value so the system can never receive nonsense.
        markerRadius = Mathf.Max(0.05f, markerRadius);
        markerHeight = Mathf.Max(0.05f, markerHeight);
        opacity = Mathf.Clamp01(opacity);
        wallOpacity = Mathf.Clamp01(wallOpacity);
        edgeGlow = Mathf.Clamp(edgeGlow, 0f, 4f);
        edgePower = Mathf.Clamp(edgePower, 0.25f, 8f);
        rimWidth = Mathf.Clamp(rimWidth, 0f, 0.5f);
        rimIntensity = Mathf.Clamp(rimIntensity, 0f, 4f);
        topFade = Mathf.Clamp01(topFade);
        pulseAmount = Mathf.Clamp01(pulseAmount);
        triggerSize = Mathf.Clamp(triggerSize, 0.1f, 2f);
        respawnTime = Mathf.Max(0.1f, respawnTime);
        permanentRepeatDelay = Mathf.Max(0f, permanentRepeatDelay);
        fadeDuration = Mathf.Max(0.05f, fadeDuration);
        if (string.IsNullOrEmpty(playerTag)) playerTag = "Player";
        if (string.IsNullOrEmpty(promptText)) promptText = "Press E";

        if (Application.isPlaying)
        {
            // live editing while the game runs
            ApplyVisualProperties();
            ApplyTransform();
            SyncTriggerSize();
            if (promptLabel != null && promptLabel.text != promptText) promptLabel.text = promptText;
            return;
        }

#if UNITY_EDITOR
        // Edit mode: zero-setup conveniences. UpdateEditorVisual only refreshes
        // an EXISTING child (component creation is not allowed during
        // validation — the child itself is created by Reset / the context
        // menu below / Play).
        EnsureTagEditor();
        EnsureShaderIncludedEditor();
        UpdateEditorVisual();
#endif
    }

    private void OnDestroy()
    {
        // clean up the things this script created at runtime
        if (ringMaterial != null)
        {
            Destroy(ringMaterial);
            ringMaterial = null;
        }
        if (visual != null && !Application.isPlaying)
        {
            // in the EDITOR the visual child was created by Reset (a persistent
            // scene object, not ours to delete); at Play OnDestroy only ever
            // runs during teardown, where Destroy is harmless either way.
            if (visual.gameObject.name == VisualChildName || visual.gameObject.name == LegacyVisualChildName)
                visual = null;   // keep the persistent editor child
        }
        if (visual != null)
        {
            Destroy(visual.gameObject);
            visual = null;
            visualRenderer = null;
        }
        if (promptCanvas != null)
        {
            Destroy(promptCanvas.gameObject);
            promptCanvas = null;
            promptLabel = null;
        }
        if (ownCapsule != null)
        {
            Destroy(ownCapsule);
            ownCapsule = null;
            triggerCollider = null;
        }
    }

    // ------------------------------------------------------------------ //
    // Tag handling
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Tags this GameObject "Interaction Point" at Play. The tag's presence is
    /// never assumed: scene objects are usually already tagged (the editor
    /// flow did it), so in the normal case this never even calls set_tag —
    /// which is what keeps the Console clean in Unity 6.
    /// </summary>
    private void ApplyTagRuntime()
    {
        if (gameObject.tag == PointTag) return;   // already tagged — nothing to do

        try
        {
            gameObject.tag = PointTag;
        }
        catch
        {
            Debug.LogWarning("[InteractionPoint] The tag '" + PointTag + "' is not defined in this build. " +
                             "Open the project in the editor and touch this component once in the Inspector (the tag " +
                             "is created automatically), then rebuild. The point still works without the tag — it is " +
                             "only used by other scripts to FIND points.", this);
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// EDIT-MODE tag setup — v3.0 CONSOLE-SLEAN FLOW. The old version tried to
    /// assign the tag immediately after creating it, and Unity 6 logs
    /// "Tag: Interaction Point is not defined." into the Console for EVERY
    /// failed assignment (even inside try/catch) because the runtime tag
    /// database only refreshes on the next script reload. The new flow:
    ///   • already tagged             → nothing happens;
    ///   • tag exists in Tags&amp;Layers → assign (always safe — the database
    ///                                  knows the tag);
    ///   • tag missing                → write it to Tags &amp; Layers SILENTLY and
    ///                                  do NOT assign yet; after the next
    ///                                  script reload (Play / recompile —
    ///                                  OnValidate runs on every reload) the
    ///                                  second branch assigns it. No console
    ///                                  errors at any point.
    /// </summary>
    private void EnsureTagEditor()
    {
        if (gameObject.tag == PointTag) return;                 // already tagged
        if (s_tagCreatedThisSession) return;                    // we just created it — the runtime
                                                                // database refreshes on the next reload

        if (!TagExistsInTagManager())
        {
            if (AddTagToTagManager())
            {
                s_tagCreatedThisSession = true;
                Debug.Log("[InteractionPoint] Tag '" + PointTag + "' created in Tags & Layers. " +
                          "It will be assigned automatically after the next script reload (just press Play — " +
                          "nothing else to do).", this);
            }
            return;                                             // never assign a tag the database
                                                                // does not know yet → no console errors
        }

        // the tag already existed (created earlier, by hand, or before this
        // editor session) → the database knows it, the assignment is safe
        try { gameObject.tag = PointTag; }
        catch { /* unreachable in practice: the tag list contained it */ }
    }

    /// <summary>Read-only scan of ProjectSettings/TagManager.asset — no side effects, no exceptions.</summary>
    private static bool TagExistsInTagManager()
    {
        Object[] assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0) return false;

        UnityEditor.SerializedObject tagManager = new UnityEditor.SerializedObject(assets[0]);
        UnityEditor.SerializedProperty tags = tagManager.FindProperty("tags");
        if (tags == null) return false;

        for (int i = 0; i < tags.arraySize; i++)
        {
            if (tags.GetArrayElementAtIndex(i).stringValue == PointTag) return true;
        }
        return false;
    }

    /// <summary>Writes the tag into Tags & Layers (SerializedObject — edit mode only).</summary>
    private static bool AddTagToTagManager()
    {
        try
        {
            Object[] assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return false;

            UnityEditor.SerializedObject tagManager = new UnityEditor.SerializedObject(assets[0]);
            UnityEditor.SerializedProperty tags = tagManager.FindProperty("tags");
            if (tags == null) return false;

            for (int i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == PointTag) return true;   // someone beat us to it
            }

            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = PointTag;
            tagManager.ApplyModifiedProperties();
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[InteractionPoint] Could not auto-create the '" + PointTag + "' tag: " +
                             e.Message + " Add it manually in Edit > Project Settings > Tags and Layers.", null);
            return false;
        }
    }
#endif

    // ------------------------------------------------------------------ //
    // Trigger volume
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Finds or creates the interaction volume. It is always a TRIGGER, so
    /// nothing ever physically blocks on it (player, enemies, camera — all
    /// pass through freely). Rules:
    ///   • trigger capsule on this object (usually the one this script added) → owned + auto-sized;
    ///   • any other trigger collider already on this object → adopted as the interaction volume;
    ///   • only solid colliders present (e.g. a pickup model) → they are left untouched, a trigger capsule is added alongside.
    /// </summary>
    private void EnsureTriggerCollider()
    {
        Collider[] colliders = GetComponents<Collider>();
        Collider firstTrigger = null;
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i].isTrigger)
            {
                firstTrigger = colliders[i];
                break;
            }
        }

        if (firstTrigger == null)
        {
            ownCapsule = gameObject.AddComponent<CapsuleCollider>();
            triggerCollider = ownCapsule;
        }
        else
        {
            triggerCollider = firstTrigger;
            ownCapsule = firstTrigger as CapsuleCollider;   // null for non-capsule triggers → no auto-sizing
        }

        if (ownCapsule != null)
        {
            if (!ownCapsule.isTrigger) ownCapsule.isTrigger = true;
            ownCapsule.direction = 1;   // vertical
            if (autoSizeTrigger) SyncTriggerSize();
        }
    }

    /// <summary>Sizes the trigger capsule from the marker values: it spans the cylinder's height and radius (equality-guarded writes — no Inspector dirty churn).</summary>
    private void SyncTriggerSize()
    {
        if (ownCapsule == null || !autoSizeTrigger) return;

        float radius = Mathf.Max(0.05f, markerRadius * triggerSize);
        float height = Mathf.Max(markerHeight, 2f * radius);   // must be >= 2 x radius for a valid capsule
        Vector3 center = new Vector3(0f, markerHeight * 0.5f, 0f);   // spans pivot → top of the cylinder

        if (ownCapsule.radius != radius) ownCapsule.radius = radius;
        if (ownCapsule.height != height) ownCapsule.height = height;
        if (ownCapsule.center != center) ownCapsule.center = center;
        if (ownCapsule.direction != 1) ownCapsule.direction = 1;
    }

    // ------------------------------------------------------------------ //
    // Marker visual
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Creates (or reuses — including the v2.0 "Interaction Ring Visual"
    /// child, which is upgraded in place) the marker visual child: one
    /// cylinder tube + the interaction marker material.
    /// </summary>
    private void BuildVisual()
    {
        visual = FindVisualChild();
        if (visual == null)
        {
            GameObject go = new GameObject(VisualChildName);
            visual = go.transform;
            visual.SetParent(transform, false);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = MarkerTube;
            visualRenderer = go.AddComponent<MeshRenderer>();
        }
        else
        {
            UpgradeLegacyVisual();
            visualRenderer = visual.GetComponent<MeshRenderer>();

            MeshFilter mf = visual.GetComponent<MeshFilter>();
            if (mf == null) mf = visual.gameObject.AddComponent<MeshFilter>();
            if (mf.sharedMesh == null || mf.sharedMesh.name == "InteractionRingQuad")
                mf.sharedMesh = MarkerTube;
        }

        if (visualRenderer == null) visualRenderer = visual.gameObject.AddComponent<MeshRenderer>();

        ApplyRendererPerfFlags(visualRenderer);

        // v2.0 material resolution — Shader.Find is only the LAST resort:
        //   1. the material the EDITOR already put on the visual child — the
        //      auto-created Assets/Materials/ZAI_InteractionRing.mat asset
        //      (or the user's own material, if they assigned one);
        //   2. Shader.Find + a Resources folder fallback (for points created
        //      purely from code at runtime).
        Material template = visualRenderer.sharedMaterial;
        Shader shader = template != null ? template.shader : ResolveRingShader();
        if (shader == null)
        {
            visualRenderer.enabled = false;
            Debug.LogError("[InteractionPoint] The shader '" + RingShaderName + "' was not found. Copy " +
                           "'InteractionRing.shader' (delivered together with InteractionPoint.cs) anywhere under " +
                           "Assets/. The interaction itself still works — only the glowing marker is missing.", this);
            return;
        }

        // ONE material instance per point — created once here, cached, and
        // destroyed in OnDestroy. Never renderer.material (which silently
        // clones the shared material on every access) and never a
        // MaterialPropertyBlock (which opts the renderer out of the SRP
        // Batcher). All points share ONE shader, so the SRP Batcher keeps
        // them in a single batch group and these per-material SetFloat calls
        // only re-upload the tiny UnityPerMaterial constant buffer.
        ringMaterial = new Material(shader);
        if (template != null) ringMaterial.CopyPropertiesFromMaterial(template);
        visualRenderer.sharedMaterial = ringMaterial;

        ApplyTransform();
        ApplyVisualProperties();
    }

    /// <summary>
    /// Upgrades a v2.0 scene in place: renames the old "Interaction Ring
    /// Visual" child and swaps its flat quad mesh for the cylinder tube.
    /// </summary>
    private void UpgradeLegacyVisual()
    {
        if (visual == null || visual.name != LegacyVisualChildName) return;

        visual.name = VisualChildName;

        MeshFilter mf = visual.GetComponent<MeshFilter>();
        if (mf != null)
        {
            Mesh oldMesh = mf.sharedMesh;
            mf.sharedMesh = MarkerTube;

            // best-effort cleanup of the old flat quad (safe: every point's
            // quad was its own instance; a duplicated point refills a null
            // mesh in its next OnValidate)
            if (oldMesh != null && oldMesh.name == "InteractionRingQuad")
                Destroy(oldMesh);
        }
    }

    /// <summary>Shader.Find first (editor + included builds), then a Resources folder fallback.</summary>
    private static Shader ResolveRingShader()
    {
        Shader shader = Shader.Find(RingShaderName);
        if (shader == null) shader = Resources.Load<Shader>("InteractionRing");
        return shader;
    }

    /// <summary>
    /// Sizes the visual child from the current Inspector values: the tube
    /// mesh is a unit cylinder (radius 0.5, height 1, centred), so the visual
    /// spans pivot → (0, Height, 0) with the given radius (equality-guarded
    /// writes).
    /// </summary>
    private void ApplyTransform()
    {
        if (visual == null) return;

        float radius = Mathf.Max(0.05f, markerRadius);
        float height = Mathf.Max(0.05f, markerHeight);

        Vector3 pos = new Vector3(0f, height * 0.5f, 0f);        // unit tube is centred → lift by h/2
        Vector3 scale = new Vector3(radius * 2f, height, radius * 2f);   // unit radius is 0.5 → 2r
        Quaternion rot = Quaternion.identity;                    // the cylinder is always vertical —
                                                                 // rotate the whole object to tilt it

        if (visual.localPosition != pos) visual.localPosition = pos;
        if (visual.localScale != scale) visual.localScale = scale;
        if (visual.localRotation != rot) visual.localRotation = rot;
    }

    /// <summary>Lights/probes must never touch the marker (perf hygiene, equality-guarded).</summary>
    private static void ApplyRendererPerfFlags(MeshRenderer meshRenderer)
    {
        if (meshRenderer == null) return;
        if (meshRenderer.shadowCastingMode != ShadowCastingMode.Off) meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        if (meshRenderer.receiveShadows) meshRenderer.receiveShadows = false;
        if (meshRenderer.lightProbeUsage != LightProbeUsage.Off) meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        if (meshRenderer.reflectionProbeUsage != ReflectionProbeUsage.Off) meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    /// <summary>Pushes all visual values into the material (cheap — a handful of SetFloat/SetColor calls).</summary>
    private void ApplyVisualProperties()
    {
        if (ringMaterial == null) return;

        ringMaterial.SetColor(ColorId, new Color(markerColor.r, markerColor.g, markerColor.b, opacity));
        ringMaterial.SetFloat(WallAlphaId, wallOpacity);
        ringMaterial.SetFloat(EdgePowerId, edgePower);
        ringMaterial.SetFloat(EdgeBoostId, edgeGlow);
        ringMaterial.SetFloat(RimWidthId, rimWidth);
        ringMaterial.SetFloat(RimIntensityId, rimIntensity);
        ringMaterial.SetFloat(TopFadeId, topFade);
        ringMaterial.SetFloat(PulseAmountId, pulseAmount);
        ringMaterial.SetFloat(PulsePhaseId, pulsePhase);
        ringMaterial.SetFloat(FadeId, fadeCurrent);
    }

    // ------------------------------------------------------------------ //
    // Input
    // ------------------------------------------------------------------ //

    private bool ReadInteractKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // KeyCodeBridge maps the Inspector KeyCode to the new Input System key,
        // so the Interact Key field genuinely works on both input backends.
        return KeyCodeBridge.WasKeyPressedThisFrame(Keyboard.current, interactKey);
#else
        return Input.GetKeyDown(interactKey);
#endif
    }

    /// <summary>
    /// True when the collider belongs to the player. The tag is checked on the
    /// collider itself and on every parent, so the player's collider may sit
    /// anywhere in the player hierarchy (root, camera child, hitbox child …).
    /// try/catch guards projects where a custom Player tag was never defined.
    /// </summary>
    private bool IsPlayerCollider(Collider other, out GameObject playerRoot)
    {
        playerRoot = null;
        if (other == null || string.IsNullOrEmpty(playerTag)) return false;

        Transform t = other.transform;
        while (t != null)
        {
            bool match = false;
            try { match = t.CompareTag(playerTag); } catch { }
            if (match)
            {
                playerRoot = t.gameObject;
                return true;
            }
            t = t.parent;
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // 'Press Key' prompt UI (built entirely from code — no assets)
    // ------------------------------------------------------------------ //

    private void UpdatePrompt()
    {
        bool want = showPrompt &&
                    activationMode == ActivationMode.PressKey &&
                    state == PointState.Ready &&
                    playerInside != null;

        if (!want)
        {
            if (promptCanvas != null && promptCanvas.enabled) promptCanvas.enabled = false;
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
        {
            if (promptCanvas != null && promptCanvas.enabled) promptCanvas.enabled = false;
            return;
        }

        if (promptCanvas == null) BuildPrompt();

        Vector3 world = transform.TransformPoint(0f, markerHeight + promptOffsetY, 0f);
        Vector3 screen = cam.WorldToScreenPoint(world);

        if (screen.z <= 0f)
        {
            // behind the camera
            if (promptCanvas.enabled) promptCanvas.enabled = false;
            return;
        }

        promptCanvas.enabled = true;

        RectTransform canvasRect = promptCanvas.transform as RectTransform;
        Vector2 localPoint;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, null, out localPoint))
            promptLabel.rectTransform.anchoredPosition = localPoint;
    }

    /// <summary>Builds the overlay canvas with one outlined text label (lazily, only when actually shown).</summary>
    private void BuildPrompt()
    {
        GameObject root = new GameObject("Interaction Prompt (auto)");
        promptCanvas = root.AddComponent<Canvas>();
        promptCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        promptCanvas.sortingOrder = 250;   // above the battery HUD (200), far below the pause menu (32767)
        promptCanvas.enabled = false;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject labelGO = new GameObject("Label");
        labelGO.transform.SetParent(root.transform, false);

        promptLabel = labelGO.AddComponent<Text>();
        promptLabel.font = LoadBuiltinFont();
        promptLabel.fontSize = 34;
        promptLabel.fontStyle = FontStyle.Bold;
        promptLabel.color = Color.white;
        promptLabel.alignment = TextAnchor.MiddleCenter;
        promptLabel.horizontalOverflow = HorizontalWrapMode.Overflow;   // never wrap the prompt text
        promptLabel.verticalOverflow = VerticalWrapMode.Overflow;
        promptLabel.raycastTarget = false;                              // never blocks clicks
        promptLabel.text = string.IsNullOrEmpty(promptText) ? "Press E" : promptText;

        RectTransform rt = promptLabel.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(600f, 80f);

        // black outline so the text stays readable on any background
        Shadow shadow = labelGO.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(2f, -2f);
    }

    /// <summary>Built-in font lookup that works across Unity versions (LegacyRuntime since 2022, Arial before).</summary>
    private static Font LoadBuiltinFont()
    {
        Font font = null;
        try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (font == null)
        {
            try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        }
        return font;
    }

    // ------------------------------------------------------------------ //
    // The marker mesh — an open-ended cylinder tube
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Builds the unit tube every marker visual uses: radius 0.5, height 1
    /// (y from -0.5 to +0.5), NO caps — you can see through the middle, which
    /// is half of the GTA "hollowed cylinder" look (the shader's edge glow is
    /// the other half). UV.y runs 0 at the bottom → 1 at the top, normals are
    /// radial. The RUNTIME copy is shared and DontSave; the EDITOR copy uses
    /// HideFlags.None so it serializes into the scene/prefab and the marker
    /// stays visible after a domain reload.
    /// </summary>
    private static Mesh CreateTubeMesh(string meshName, HideFlags flags)
    {
        Mesh mesh = new Mesh();
        mesh.name = meshName;
        mesh.hideFlags = flags;

        int columns = TubeSegments + 1;   // one duplicated seam column keeps the UVs clean
        Vector3[] verts = new Vector3[columns * 2];
        Vector3[] normals = new Vector3[columns * 2];
        Vector2[] uvs = new Vector2[columns * 2];
        int[] tris = new int[TubeSegments * 6];

        for (int i = 0; i < columns; i++)
        {
            float a = (i / (float)TubeSegments) * Mathf.PI * 2f;
            Vector3 n = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));   // radial normal

            verts[2 * i] = new Vector3(n.x * 0.5f, -0.5f, n.z * 0.5f);        // bottom ring
            verts[2 * i + 1] = new Vector3(n.x * 0.5f, 0.5f, n.z * 0.5f);     // top ring
            normals[2 * i] = n;
            normals[2 * i + 1] = n;
            uvs[2 * i] = new Vector2(i / (float)TubeSegments, 0f);
            uvs[2 * i + 1] = new Vector2(i / (float)TubeSegments, 1f);
        }

        for (int i = 0; i < TubeSegments; i++)
        {
            int b0 = 2 * i;        // bottom, angle i
            int t0 = 2 * i + 1;    // top,    angle i
            int b1 = 2 * i + 2;    // bottom, angle i+1
            int t1 = 2 * i + 3;    // top,    angle i+1

            tris[6 * i] = b0; tris[6 * i + 1] = b1; tris[6 * i + 2] = t1;
            tris[6 * i + 3] = b0; tris[6 * i + 4] = t1; tris[6 * i + 5] = t0;
        }

        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>The shared runtime tube (one for all points, never destroyed).</summary>
    private static Mesh MarkerTube
    {
        get
        {
            if (s_tubeMesh == null) s_tubeMesh = CreateTubeMesh("InteractionMarkerTube", HideFlags.DontSave);
            return s_tubeMesh;
        }
    }

    /// <summary>Finds the visual child — current name first, then the v2.0 legacy name.</summary>
    private Transform FindVisualChild()
    {
        Transform t = transform.Find(VisualChildName);
        if (t == null) t = transform.Find(LegacyVisualChildName);
        return t;
    }

    // ------------------------------------------------------------------ //
    // Scene view gizmos (edit-mode marker preview, play-mode outline)
    // ------------------------------------------------------------------ //

    private void OnDrawGizmos()
    {
        if (!drawSceneGizmos) return;
        DrawMarkerPreview(Application.isPlaying ? opacity * 0.35f : opacity * 0.55f);
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawSceneGizmos) return;
        DrawMarkerPreview(opacity);

        // pivot → top guide line (visualizes the cylinder's extent)
        Gizmos.color = new Color(markerColor.r, markerColor.g, markerColor.b, 0.8f);
        Gizmos.DrawLine(transform.position, MarkerTopWorld);
    }

    private Vector3 MarkerTopWorld
    {
        get { return transform.TransformPoint(0f, markerHeight, 0f); }
    }

    private void DrawMarkerPreview(float alpha)
    {
        // After an editor domain reload the private (non-serialized) field
        // references are wiped — re-attach them cheaply so the fallback
        // preview below does not double-draw over the real marker.
        RefreshVisualCache();

        Color col = new Color(markerColor.r, markerColor.g, markerColor.b, Mathf.Clamp01(alpha));
        float radius = Mathf.Max(0.05f, markerRadius);
        float height = Mathf.Max(0.05f, markerHeight);
        Quaternion horizontal = transform.rotation * Quaternion.Euler(90f, 0f, 0f);   // XY → XZ plane

        Vector3 bottom = transform.position;
        Vector3 top = MarkerTopWorld;

        // The filled gizmo preview is only a FALLBACK for scenes that predate
        // the persistent visual child (v1.0): once the real glowing marker
        // exists in the editor, drawing a second tube on top of it would
        // double the visual.
        bool realVisualMissing = visualRenderer == null || visualRenderer.sharedMaterial == null;
        if (!Application.isPlaying && realVisualMissing)
        {
            Gizmos.color = col;
            Gizmos.DrawMesh(MarkerTube,
                            transform.TransformPoint(0f, height * 0.5f, 0f),
                            transform.rotation,
                            new Vector3(radius * 2f, height, radius * 2f));
        }

        // thin outline — always drawn so the point stays findable in the Scene view
        Gizmos.color = col;
        DrawWireCircle(bottom, horizontal, radius, 40);
        DrawWireCircle(top, horizontal, radius, 40);
        DrawWireEdges(bottom, top, radius);
    }

    /// <summary>Re-attaches the visual child references after a domain reload (private non-serialized fields are wiped).</summary>
    private void RefreshVisualCache()
    {
        if (visual == null) visual = FindVisualChild();
        if (visual != null && visualRenderer == null)
            visualRenderer = visual.GetComponent<MeshRenderer>();
    }

    /// <summary>Four vertical lines connecting the bottom and top circles (the classic cylinder wireframe).</summary>
    private void DrawWireEdges(Vector3 bottom, Vector3 top, float radius)
    {
        for (int i = 0; i < 4; i++)
        {
            float a = (i / 4f) * Mathf.PI * 2f + Mathf.PI * 0.25f;
            Vector3 dir = transform.rotation * new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(bottom + dir, top + dir);
        }
    }

    private static void DrawWireCircle(Vector3 center, Quaternion rotation, float radius, int segments)
    {
        Vector3 prev = rotation * new Vector3(radius, 0f, 0f) + center;
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = rotation * new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f) + center;
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    // ------------------------------------------------------------------ //
    // Editor zero-setup helpers (persistent edit-mode marker visual, the
    // shared material asset and the shader build inclusion)
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR

    /// <summary>
    /// Context menu: builds (or repairs) the persistent marker visual in the
    /// EDITOR — useful after upgrading a scene from v1.0/v2.0 (whose visual
    /// was a flat ring) or after deleting the "Interaction Marker Visual"
    /// child by accident.
    /// </summary>
    [ContextMenu("Rebuild Marker Visual (editor)")]
    private void RebuildMarkerVisualMenu()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("[InteractionPoint] 'Rebuild Marker Visual' is an editor-only tool — " +
                             "stop Play mode first (the play-mode visual builds itself).", this);
            return;
        }
        EnsureVisualEditor();
    }

    /// <summary>
    /// EDIT-MODE visual: makes the glowing cylinder REAL in the editor —
    /// a persistent "Interaction Marker Visual" child (tube mesh serialized
    /// into the scene/prefab) using the shared marker material asset — so the
    /// marker is visible BEFORE pressing Play. Idempotent: finds or creates,
    /// never resets user tweaks, never overwrites a material the user chose,
    /// and upgrades v2.0 ring children in place.
    /// </summary>
    private void EnsureVisualEditor()
    {
        if (Application.isPlaying) return;

        bool changed = false;

        Transform existing = FindVisualChild();
        if (existing == null)
        {
            GameObject go = new GameObject(VisualChildName);
            existing = go.transform;
            existing.SetParent(transform, false);
            existing.gameObject.AddComponent<MeshFilter>();
            existing.gameObject.AddComponent<MeshRenderer>();
            changed = true;
        }
        else if (existing.name == LegacyVisualChildName)
        {
            existing.name = VisualChildName;   // upgrade a v2.0 ring child in place
            changed = true;
        }

        visual = existing;
        visualRenderer = existing.GetComponent<MeshRenderer>();
        if (visualRenderer == null) visualRenderer = existing.gameObject.AddComponent<MeshRenderer>();

        MeshFilter mf = existing.GetComponent<MeshFilter>();
        if (mf == null) mf = existing.gameObject.AddComponent<MeshFilter>();

        // persistent tube mesh (HideFlags.None → serialized with the scene,
        // survives domain reloads); replaces the v2.0 flat quad if found
        if (mf.sharedMesh == null || mf.sharedMesh.name == "InteractionRingQuad")
        {
            Mesh oldMesh = mf.sharedMesh;
            mf.sharedMesh = CreateTubeMesh("InteractionMarkerTube", HideFlags.None);
            changed = true;

            if (oldMesh != null && oldMesh.name == "InteractionRingQuad")
                DestroyImmediate(oldMesh);   // clean the old quad out of the scene file
        }

        ApplyRendererPerfFlags(visualRenderer);

        // shared marker material asset — only assigned when the slot is empty,
        // so a user-assigned material is never stomped
        if (visualRenderer.sharedMaterial == null)
        {
            Material asset = EnsureRingMaterialAssetEditor(true);
            if (asset != null)
            {
                visualRenderer.sharedMaterial = asset;
                changed = true;
            }
        }

        ApplyTransform();

        if (changed && gameObject.scene.IsValid())
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
    }

    /// <summary>
    /// OnValidate-safe refresh of an EXISTING visual child (component
    /// creation is not allowed during validation, so unlike
    /// EnsureVisualEditor this never creates the child itself — it only
    /// re-attaches its mesh/material and follows the Inspector values).
    /// </summary>
    private void UpdateEditorVisual()
    {
        if (Application.isPlaying) return;

        Transform existing = FindVisualChild();
        if (existing == null) return;   // created by Reset / the context menu / Play

        // v2.0 scenes upgrade on the very first OnValidate after the script
        // reload — including the rename, so the hierarchy looks right at once
        if (existing.name == LegacyVisualChildName) existing.name = VisualChildName;

        visual = existing;
        visualRenderer = existing.GetComponent<MeshRenderer>();

        MeshFilter mf = existing.GetComponent<MeshFilter>();
        if (mf != null && (mf.sharedMesh == null || mf.sharedMesh.name == "InteractionRingQuad"))
            mf.sharedMesh = CreateTubeMesh("InteractionMarkerTube", HideFlags.None);

        if (visualRenderer != null)
        {
            ApplyRendererPerfFlags(visualRenderer);

            if (visualRenderer.sharedMaterial == null)
            {
                // lookup only — never CREATES the asset during validation
                // (asset creation happens in Reset / the context menu)
                Material asset = EnsureRingMaterialAssetEditor(false);
                if (asset != null) visualRenderer.sharedMaterial = asset;
            }
        }

        ApplyTransform();
    }

    /// <summary>
    /// Finds or creates the shared marker Material asset
    /// (Assets/Materials/ZAI_InteractionRing.mat) that every editor visual
    /// references. Because saved scenes reference this asset, the marker
    /// shader is pulled into every BUILD through the normal dependency
    /// chain — the most robust anti-stripping guarantee there is.
    /// The shader's own default values already equal the component's
    /// defaults, so a fresh asset needs no tweaking.
    /// </summary>
    private static Material EnsureRingMaterialAssetEditor(bool createIfMissing)
    {
        // cached, and Unity's overloaded == makes a deleted asset "null" again
        if (s_ringMaterialAsset != null) return s_ringMaterialAsset;

        Material existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(RingMaterialAssetPath);
        if (existing != null)
        {
            s_ringMaterialAsset = existing;
            return existing;
        }

        if (!createIfMissing) return null;

        Shader shader = Shader.Find(RingShaderName);
        if (shader == null)
        {
            Debug.LogWarning("[InteractionPoint] '" + RingShaderName + "' was not found, so the shared marker " +
                             "material could not be created. Copy 'InteractionRing.shader' (delivered with " +
                             "InteractionPoint.cs) anywhere under Assets/, then use the component's " +
                             "'Rebuild Marker Visual (editor)' context menu. The interaction itself still works.");
            return null;
        }

        try
        {
            if (!UnityEditor.AssetDatabase.IsValidFolder(RingMaterialDir))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "Materials");

            Material mat = new Material(shader);   // shader defaults == component defaults
            UnityEditor.AssetDatabase.CreateAsset(mat, RingMaterialAssetPath);
            UnityEditor.AssetDatabase.SaveAssets();

            s_ringMaterialAsset = mat;
            Debug.Log("[InteractionPoint] Created the shared marker material '" + RingMaterialAssetPath +
                      "' — saved scenes now reference the marker shader directly, so it can never be " +
                      "stripped from builds.");
            return mat;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[InteractionPoint] Could not create the shared marker material '" +
                             RingMaterialAssetPath + "': " + e.Message + " Create it manually (any material " +
                             "using the shader '" + RingShaderName + "' works) and assign it to the " +
                             "'Interaction Marker Visual' child.");
            return null;
        }
    }

    /// <summary>
    /// Adds the marker shader to Project Settings > Graphics > Always Included
    /// Shaders so Shader.Find keeps working in BUILDS (a project shader that
    /// nothing references is otherwise stripped at build time). Second
    /// safety net next to the shared material asset — this covers points
    /// that are created purely from code at runtime.
    /// </summary>
    private static void EnsureShaderIncludedEditor()
    {
        if (s_shaderInclusionDone) return;

        Shader shader = Shader.Find(RingShaderName);
        if (shader == null)
        {
            if (!s_warnedShaderMissing)
            {
                s_warnedShaderMissing = true;
                Debug.LogWarning("[InteractionPoint] '" + RingShaderName + "' was not found in the project. Copy " +
                                 "'InteractionRing.shader' (delivered with InteractionPoint.cs) anywhere under Assets/. " +
                                 "The interaction itself still works — only the glowing marker is missing.");
            }
            return;   // retry on the next OnValidate — the user may import the shader later
        }

        try
        {
            Object[] assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) return;

            UnityEditor.SerializedObject graphicsSettings = new UnityEditor.SerializedObject(assets[0]);
            UnityEditor.SerializedProperty list = graphicsSettings.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) list = graphicsSettings.FindProperty("m_PreloadedShaders");
            if (list == null) return;

            for (int i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    s_shaderInclusionDone = true;   // already included
                    return;
                }
            }

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            graphicsSettings.ApplyModifiedProperties();

            s_shaderInclusionDone = true;
            Debug.Log("[InteractionPoint] '" + RingShaderName + "' was added to Project Settings > Graphics > " +
                      "Always Included Shaders, so the marker keeps working in builds.");
        }
        catch (System.Exception e)
        {
            s_shaderInclusionDone = true;   // do not retry endlessly
            Debug.LogWarning("[InteractionPoint] Could not add the marker shader to Always Included Shaders: " +
                             e.Message + " Add it manually (Project Settings > Graphics) if the marker is missing in builds.");
        }
    }
#endif
}