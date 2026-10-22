using UnityEngine;

/// <summary>
/// VR HAND VISUAL (v1.0) — a small, always-visible controller model on the
/// hand anchor so you can SEE your hands in VR.
///
/// WHY THIS EXISTS
///   The hand anchors themselves are empty GameObjects — without this
///   component there is literally nothing to look at ("no hand existed").
///   It builds a tiny stylized controller (body + short barrel + bright
///   emitter tip) entirely from code — no prefabs, no assets — parented to
///   the hand anchor, so it follows the tracked pose automatically and
///   points where the warp/UI rays point.
///
///   • the materials are UNLIT (Sprites/Default) on purpose: the model
///     stays clearly visible even in NotDEER's dark forest;
///   • the colliders that CreatePrimitive adds are destroyed immediately —
///     the visual can never block the warp ray, the UI ray, the flashlight
///     beam or the touch pickup;
///   • put it on both hands (VRPlayerSetup does that for you).
///
/// v1.0 — initial release with the NotDEER VR layer hardening.
/// </summary>
[DisallowMultipleComponent]
public class VRHandVisual : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Look")]

    [Tooltip("Color of the controller body (unlit — visible in the dark).")]
    [SerializeField] private Color bodyColor = new Color(0.82f, 0.82f, 0.86f, 1f);

    [Tooltip("Color of the emitter tip at the front (unlit).")]
    [SerializeField] private Color tipColor = Color.white;

    [Tooltip("Overall size multiplier for the whole controller model.")]
    [SerializeField, Range(0.5f, 2f)] private float sizeScale = 1f;

    [Tooltip("Turn the visual off (e.g. you attached your own hand model).")]
    [SerializeField] private bool showVisual = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Transform visualRoot;

    /// <summary>The built model's root (null until built).</summary>
    public Transform VisualRoot
    {
        get { return visualRoot; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        Build();
    }

    // ------------------------------------------------------------------ //
    // Building (from code — no prefabs, no assets)
    // ------------------------------------------------------------------ //

    private void Build()
    {
        if (visualRoot != null || !showVisual) return;

        GameObject rootGO = new GameObject("Hand Visual");
        visualRoot = rootGO.transform;
        visualRoot.SetParent(transform, false);
        visualRoot.localPosition = Vector3.zero;
        visualRoot.localRotation = Quaternion.identity;
        visualRoot.localScale = Vector3.one * sizeScale;

        // ---- body: the controller you hold ----
        GameObject body = CreatePart("Body", PrimitiveType.Cube,
            new Vector3(0.034f, 0.026f, 0.088f),          // wide x high x long
            new Vector3(0f, 0f, 0.006f),
            Quaternion.identity, bodyColor);

        // ---- barrel: a short muzzle along the pointing direction (+Z) ----
        GameObject barrel = CreatePart("Barrel", PrimitiveType.Cylinder,
            new Vector3(0.014f, 0.017f, 0.014f),          // cylinder axis is Y…
            new Vector3(0f, 0f, 0.056f),
            Quaternion.Euler(90f, 0f, 0f),                // …rotated onto +Z
            bodyColor);

        // ---- emitter tip: the bright dot where the rays come from ----
        GameObject tip = CreatePart("Tip", PrimitiveType.Sphere,
            new Vector3(0.017f, 0.017f, 0.017f),
            new Vector3(0f, 0f, 0.076f),
            Quaternion.identity, tipColor);

        body.transform.SetParent(visualRoot, false);
        barrel.transform.SetParent(visualRoot, false);
        tip.transform.SetParent(visualRoot, false);
    }

    /// <summary>One primitive part, collider stripped, unlit material applied.</summary>
    private static GameObject CreatePart(string partName, PrimitiveType type, Vector3 scale,
                                         Vector3 localPosition, Quaternion localRotation, Color color)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        part.name = partName;

        // Primitives come with a collider — the visual must never block the
        // warp ray, the UI ray, the flashlight or the touch pickup.
        Collider partCollider = part.GetComponent<Collider>();
        if (partCollider != null) Destroy(partCollider);

        part.transform.localScale = scale;
        part.transform.localPosition = localPosition;
        part.transform.localRotation = localRotation;

        MeshRenderer renderer = part.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            Material material = new Material(Shader.Find("Sprites/Default"));
            material.color = color;
            renderer.sharedMaterial = material;
        }
        return part;
    }

    /// <summary>Clamps Inspector values so the visual can never receive nonsense input.</summary>
    private void OnValidate()
    {
        if (sizeScale < 0.5f) sizeScale = 0.5f;
        if (sizeScale > 2f) sizeScale = 2f;
    }
}
