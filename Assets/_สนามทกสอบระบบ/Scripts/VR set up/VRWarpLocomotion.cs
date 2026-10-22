using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

/// <summary>
/// VR WARP LOCOMOTION (v1.0) — the player's VR movement: point the LEFT hand
/// at the ground, a straight WHITE LINE is drawn from the hand to the spot
/// and a CIRCLE RING hovers on the ground exactly where the player is
/// pointing. Press the warp button (left Trigger by default) and the player
/// warps there instantly.
///
/// HOW IT MOVES THE PLAYER (important for the rest of the game)
///   The warp moves the PLAYER ROOT — the GameObject tagged "Player" — not
///   just the XR rig. Every original system keeps working exactly like in
///   the flat game because they all follow that root:
///     • the PlayerHitbox dead zone travels with the player (one-hit kill),
///     • InteractionPoints still fire when the PLAYER enters them,
///     • enemies and the spawner keep chasing the same "Player" tag,
///     • proximity prompts and pickups still measure to the root.
///   The head lands exactly over the pointed spot: the root is shifted by
///   (spot - head position) with the height cancelled, so wherever you
///   physically stand inside your play space, YOUR VIEW lands on the ring.
///
/// VALIDITY RULES (when the ring shows)
///   • the hand ray must hit something within Max Warp Distance,
///   • the surface must be walkable (not steeper than Max Ground Slope),
///   • the spot must not be below/above the player by more than
///     Max Vertical Difference (a cliff drop is not a warp),
///   • the spot must be at least Min Warp Distance away horizontally.
///   Invalid → line and ring are simply hidden (nothing to warp to).
///
/// INPUT (via VRXRInput — no XR Interaction Toolkit dependency)
///   Left Trigger by default; configurable to Grip/Primary/Secondary.
///
/// FREEZES LIKE THE REST OF THE GAME
///   No warping while GamePauseManager's menu is open or while the
///   jumpscare is playing; visuals hidden while frozen. When the same
///   hand's VRUIPointer is aiming at a world-space UI that frame, the warp
///   stands down so the trigger click can activate the UI instead.
///
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRWarpLocomotion : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Warp Button (this hand's controller)")]

    [Tooltip("Button that performs the warp while a valid spot is pointed at.")]
    [SerializeField] private VRXRInput.HandButton warpButton = VRXRInput.HandButton.Trigger;

    [Header("Warp Rules")]

    [Tooltip("Maximum ray length — how far away a warp spot can be.")]
    [SerializeField, Min(1f)] private float maxWarpDistance = 12f;

    [Tooltip("Minimum horizontal distance from the player's head — closer spots are ignored (prevents accidental micro-warps).")]
    [SerializeField, Min(0f)] private float minWarpDistance = 0.4f;

    [Tooltip("How steep a surface may be and still count as walkable. 0.7 ≈ 45° slopes, 1.0 = perfectly flat only.")]
    [SerializeField, Range(0.5f, 1f)] private float minGroundUpDot = 0.7f;

    [Tooltip("Maximum height difference between the head and the warp spot (meters). Prevents warping down a cliff or onto a rooftop. 0 = no limit.")]
    [SerializeField, Min(0f)] private float maxVerticalDifference = 3f;

    [Tooltip("Seconds after a warp before the next one is allowed (anti double-tap).")]
    [SerializeField, Range(0f, 2f)] private float warpCooldown = 0.25f;

    [Header("Layers")]

    [Tooltip("What the warp ray can hit. Defaults to Everything; remove layers to make them unwarpable (e.g. UI).")]
    [SerializeField] private LayerMask warpLayers = ~0;

    [Header("White Line (hand → spot)")]

    [Tooltip("Colour of the straight white line from the hand to the pointed spot.")]
    [SerializeField] private Color lineColor = Color.white;

    [Tooltip("Width of the line in meters.")]
    [SerializeField, Range(0.001f, 0.05f)] private float lineWidth = 0.01f;

    [Header("Ground Ring")]

    [Tooltip("Outer radius of the circle ring that hovers on the ground (meters).")]
    [SerializeField, Range(0.1f, 2f)] private float ringOuterRadius = 0.35f;

    [Tooltip("Inner radius of the ring band (meters).")]
    [SerializeField, Range(0.05f, 2f)] private float ringInnerRadius = 0.27f;

    [Tooltip("How high above the pointed surface the ring hovers (meters, along the surface normal).")]
    [SerializeField, Range(0f, 0.2f)] private float ringHoverHeight = 0.02f;

    [Tooltip("Colour of the ground ring.")]
    [SerializeField] private Color ringColor = Color.white;

    [Header("Events")]

    [Tooltip("Fired the moment a warp happens: (fromPosition, toPosition) — the player root's position before and after. Wire warping sounds or comfort effects here.")]
    [SerializeField] private UnityEvent<Vector3, Vector3> onWarped = new UnityEvent<Vector3, Vector3>();

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Transform headCamera;
    private Transform rigRoot;
    private VRUIPointer siblingPointer;

    private LineRenderer warpLine;
    private Transform ringTransform;
    private Material ringMaterial;
    private Material lineMaterial;

    private float cooldownUntil;

    /// <summary>True while the hand currently points at a valid warp spot (the ring is showing).</summary>
    public bool IsPointingAtValidSpot { get; private set; }

    /// <summary>The current warp spot (valid only while IsPointingAtValidSpot).</summary>
    public Vector3 CurrentWarpPoint { get; private set; }

    /// <summary>(from, to) positions of the last warp — handy for debugging and effects.</summary>
    public UnityEvent<Vector3, Vector3> OnWarped
    {
        get { return onWarped; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        ResolveHeadCamera();
        ResolveRigRoot();
    }

    private void Update()
    {
        if (warpLine == null) BuildVisuals();

        // Frozen with the rest of the game — no pointing, no warping.
        if (GamePauseManager.IsPaused || JumpscareGameOverManager.IsJumpscareActive)
        {
            HideVisuals();
            return;
        }

        // The same hand's UI pointer owns the trigger while it aims at a
        // world-space UI — the warp stands down that frame.
        if (SiblingPointerOverUI())
        {
            HideVisuals();
            return;
        }

        bool valid = ComputeWarpSpot(out Vector3 spot, out Vector3 surfaceNormal);

        if (valid)
        {
            ShowVisuals(spot, surfaceNormal);
        }
        else
        {
            HideVisuals();
            return;
        }

        // Only ask for the edge AFTER we know a warp is possible (the
        // frame-cached edge stays valid for other callers either way).
        if (VRXRInput.GetButtonDown(XRNode.LeftHand, warpButton) && Time.unscaledTime >= cooldownUntil)
        {
            Warp(spot);
        }
    }

    // ------------------------------------------------------------------ //
    // Warp spot search
    // ------------------------------------------------------------------ //

    private bool ComputeWarpSpot(out Vector3 spot, out Vector3 surfaceNormal)
    {
        spot = Vector3.zero;
        surfaceNormal = Vector3.up;

        Transform head = ResolveHeadCamera();
        if (head == null) return false;

        Vector3 origin = transform.position;
        Vector3 direction = transform.forward;

        if (!Physics.Raycast(origin, direction, out RaycastHit hit, maxWarpDistance, warpLayers, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        // Walkable surface? (steep walls and ceilings are not warp spots)
        if (Vector3.Dot(hit.normal, Vector3.up) < minGroundUpDot) return false;

        // Not a cliff dive / rooftop hop.
        if (maxVerticalDifference > 0f && Mathf.Abs(hit.point.y - head.position.y) > maxVerticalDifference) return false;

        // Head lands over the spot — its horizontal distance IS the warp.
        Vector3 delta = hit.point - head.position;
        delta.y = 0f;
        if (delta.magnitude < minWarpDistance) return false;

        spot = hit.point;
        surfaceNormal = hit.normal;
        return true;
    }

    private void Warp(Vector3 spot)
    {
        Transform head = ResolveHeadCamera();
        Transform rig = ResolveRigRoot();
        if (head == null || rig == null) return;

        Vector3 from = rig.position;
        Vector3 delta = spot - head.position;
        delta.y = 0f;                       // height is owned by the tracking rig

        rig.position = from + delta;

        cooldownUntil = Time.unscaledTime + warpCooldown;
        HideVisuals();

        if (onWarped != null) onWarped.Invoke(from, rig.position);
    }

    // ------------------------------------------------------------------ //
    // Visuals (built from code — no prefabs, no assets)
    // ------------------------------------------------------------------ //

    private void BuildVisuals()
    {
        // ---- straight white line from the hand to the spot ----
        GameObject lineGO = new GameObject("Warp Line");
        lineGO.transform.SetParent(transform, false);
        warpLine = lineGO.AddComponent<LineRenderer>();

        lineMaterial = new Material(Shader.Find("Sprites/Default"));
        warpLine.material = lineMaterial;
        warpLine.useWorldSpace = true;
        warpLine.positionCount = 2;
        warpLine.startColor = lineColor;
        warpLine.endColor = lineColor;
        warpLine.widthMultiplier = lineWidth;
        warpLine.numCapVertices = 4;
        warpLine.enabled = false;

        // ---- circle ring hovering on the ground ----
        GameObject ringGO = new GameObject("Warp Ring");
        ringTransform = ringGO.transform;
        ringTransform.SetParent(transform, false);

        MeshFilter meshFilter = ringGO.AddComponent<MeshFilter>();
        meshFilter.mesh = BuildRingMesh(ringOuterRadius, ringInnerRadius, 48);

        MeshRenderer meshRenderer = ringGO.AddComponent<MeshRenderer>();
        ringMaterial = new Material(Shader.Find("Sprites/Default"));
        meshRenderer.sharedMaterial = ringMaterial;
        ringGO.SetActive(false);
    }

    /// <summary>A flat ring (annulus) mesh lying in the XZ plane, facing up.</summary>
    private static Mesh BuildRingMesh(float outerRadius, float innerRadius, int segments)
    {
        Mesh mesh = new Mesh();
        mesh.name = "WarpRing";

        Vector3[] vertices = new Vector3[segments * 2];
        int[] triangles = new int[segments * 6];

        for (int i = 0; i < segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            vertices[i * 2] = new Vector3(cos * innerRadius, 0f, sin * innerRadius);
            vertices[i * 2 + 1] = new Vector3(cos * outerRadius, 0f, sin * outerRadius);

            int a = i * 2;
            int b = i * 2 + 1;
            int c = ((i + 1) % segments) * 2;
            int d = ((i + 1) % segments) * 2 + 1;

            triangles[i * 6] = a;
            triangles[i * 6 + 1] = c;
            triangles[i * 6 + 2] = b;
            triangles[i * 6 + 3] = b;
            triangles[i * 6 + 4] = c;
            triangles[i * 6 + 5] = d;
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ShowVisuals(Vector3 spot, Vector3 surfaceNormal)
    {
        if (warpLine == null) BuildVisuals();

        // Line: hand → spot.
        warpLine.enabled = true;
        warpLine.SetPosition(0, transform.position);
        warpLine.SetPosition(1, spot);

        // Ring: hovering on the surface, aligned to its normal.
        ringTransform.gameObject.SetActive(true);
        ringTransform.position = spot + surfaceNormal * ringHoverHeight;
        ringTransform.rotation = Quaternion.FromToRotation(Vector3.up, surfaceNormal);

        IsPointingAtValidSpot = true;
        CurrentWarpPoint = spot;
    }

    private void HideVisuals()
    {
        IsPointingAtValidSpot = false;

        if (warpLine != null) warpLine.enabled = false;
        if (ringTransform != null) ringTransform.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ //
    // Lazy references
    // ------------------------------------------------------------------ //

    private Transform ResolveHeadCamera()
    {
        if (headCamera != null) return headCamera;

        Camera main = Camera.main;
        if (main != null) headCamera = main.transform;
        return headCamera;
    }

    /// <summary>
    /// The PLAYER ROOT (tagged "Player") — the transform warps move. Found
    /// by climbing up from this hand; falls back to the hierarchy root with
    /// a warning when the tag is missing.
    /// </summary>
    private Transform ResolveRigRoot()
    {
        if (rigRoot != null) return rigRoot;

        Transform t = transform;
        while (t != null)
        {
            if (t.CompareTag("Player"))
            {
                rigRoot = t;
                return rigRoot;
            }
            t = t.parent;
        }

        rigRoot = transform.root;
        Debug.LogWarning("[VRWarpLocomotion] No ancestor of '" + name +
                         "' is tagged 'Player' — warping will move '" + rigRoot.name +
                         "' instead. Tag the player root 'Player' so the hitbox, event points " +
                         "and enemies keep following the warps.", this);
        return rigRoot;
    }

    private bool SiblingPointerOverUI()
    {
        if (siblingPointer == null) siblingPointer = GetComponent<VRUIPointer>();
        return siblingPointer != null && siblingPointer.IsOverInteractiveUI;
    }

    /// <summary>Clamps Inspector values so a warp can never receive nonsense input.</summary>
    private void OnValidate()
    {
        if (ringInnerRadius > ringOuterRadius) ringInnerRadius = ringOuterRadius * 0.8f;
        if (ringInnerRadius < 0.02f) ringInnerRadius = 0.02f;
    }
}
