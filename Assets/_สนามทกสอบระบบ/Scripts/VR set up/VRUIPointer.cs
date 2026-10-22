using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// VR UI POINTER (v1.0) — the WHITE LINE that appears from a hand whenever
/// a world-space UI is up, and the clicking finger behind it.
///
/// Put this component on BOTH hand anchors. Each frame the hand casts a ray
/// along its pointing direction against every registered VRWorldUICanvas
/// (the pause menu, popup panels — the "every UI except HUD and jumpscare"
/// rule). While the ray is over an interactive element:
///   • a straight white line is drawn from the hand to the exact point on
///     the UI ("white line should appear too when UI pop up"),
///   • the element under the ray is HOVERED (it gets the same pointer
///     enter/exit events a mouse would give it, so button highlights work),
///   • pressing this hand's Trigger CLICKS it (pointer down/up/click events
///     through Unity's own ExecuteEvents pipeline — Buttons, toggles,
///     anything clickable works unmodified).
///
/// ON THE LEFT HAND the pointer has priority over warping: while its ray is
/// over a UI, VRWarpLocomotion stands down and the trigger clicks the UI
/// instead of warping (both read the same frame-cached button edge from
/// VRXRInput, so the click and the warp can never both fire).
///
/// STAYS ALIVE WHILE PAUSED on purpose — the pause menu is the UI you most
/// need to click. Frozen while the jumpscare plays (nothing to click there).
///
/// The ray does its own canvas math (plane hit + rect containment in canvas
/// space) — no XR Interaction Toolkit, no GraphicRaycaster, no InputModule
/// needed; it works with any world-space canvas made of Unity UI.
///
/// v1.1 — the head camera for screen-point hints is resolved through
///        VRPlayerSetup.XRCamera (the XR rig's camera) instead of a
///        coin-flip Camera.main.
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRUIPointer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Click Button (this hand's controller)")]

    [Tooltip("Button that presses/clicks the UI element under the white line.")]
    [SerializeField] private VRXRInput.HandButton clickButton = VRXRInput.HandButton.Trigger;

    [Header("Ray")]

    [Tooltip("How far the UI ray can reach (meters from the hand).")]
    [SerializeField, Min(0.3f)] private float maxDistance = 5f;

    [Header("White Line (hand → UI)")]

    [Tooltip("Colour of the white line while aiming at a UI element.")]
    [SerializeField] private Color lineColor = Color.white;

    [Tooltip("Width of the line in meters.")]
    [SerializeField, Range(0.001f, 0.05f)] private float lineWidth = 0.01f;

    [Header("Diagnostics")]

    [Tooltip("Log every hover/click to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private LineRenderer line;
    private Material lineMaterial;
    private PointerEventData eventData;
    private Camera headCamera;
    private XRNode node;

    private Graphic hoverGraphic;
    private GameObject pressedTarget;

    /// <summary>True on frames where this hand's ray is over an interactive world UI element.</summary>
    public bool IsOverInteractiveUI
    {
        get { return hoverGraphic != null; }
    }

    /// <summary>The UI element currently under the white line (null when none).</summary>
    public Graphic HoveredGraphic
    {
        get { return hoverGraphic; }
    }

    /// <summary>The world point where the ray currently hits the UI (valid while IsOverInteractiveUI).</summary>
    public Vector3 LastHitPoint { get; private set; }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        BuildLine();
        node = NodeForThisHand();
        eventData = new PointerEventData(EventSystem.current);
        eventData.button = PointerEventData.InputButton.Left;
    }

    private void Update()
    {
        if (line == null) BuildLine();

        // The jumpscare owns the screen — no UI pointing during it.
        if (JumpscareGameOverManager.IsJumpscareActive || JumpscareGameOverManager.IsGameOver)
        {
            ClearHover();
            HideLine();
            pressedTarget = null;
            return;
        }

        // NOTE: no GamePauseManager.IsPaused check here — the pause menu is
        // exactly the UI that must stay clickable while paused.

        bool hit = TryGetUIHit(out Graphic graphic, out Vector3 worldPoint);

        UpdateHover(hit ? graphic : null);

        if (hit)
        {
            LastHitPoint = worldPoint;
            ShowLine(worldPoint);
        }
        else
        {
            HideLine();
        }

        HandleClickInput();
    }

    private void OnDisable()
    {
        // Never leave a hover highlight or a stuck press behind.
        ClearHover();
        HideLine();
        pressedTarget = null;
    }

    // ------------------------------------------------------------------ //
    // Ray vs registered world canvases
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Casts this hand's pointing ray against every interactive
    /// VRWorldUICanvas. A canvas hit requires: plane intersection within
    /// Max Distance, and the point landing inside a raycast-target graphic's
    /// rect. Closest canvas wins; inside a canvas the topmost (last in
    /// hierarchy order) matching graphic wins, exactly like Unity draws it.
    /// </summary>
    private bool TryGetUIHit(out Graphic hitGraphic, out Vector3 worldPoint)
    {
        hitGraphic = null;
        worldPoint = Vector3.zero;

        Vector3 origin = transform.position;
        Vector3 direction = transform.forward;

        float bestDistance = float.MaxValue;

        IList<VRWorldUICanvas> canvases = VRWorldUICanvas.RegisteredCanvases;
        for (int c = 0; c < canvases.Count; c++)
        {
            VRWorldUICanvas vrCanvas = canvases[c];
            if (vrCanvas == null || !vrCanvas.IsInteractive) continue;

            Canvas canvas = vrCanvas.Canvas;
            if (canvas == null) continue;

            // Ray into the canvas' local space (its plane is the XY plane).
            Matrix4x4 worldToLocal = canvas.transform.worldToLocalMatrix;
            Vector3 localOrigin = worldToLocal.MultiplyPoint3x4(origin);
            Vector3 localDirection = worldToLocal.MultiplyVector(direction);

            if (Mathf.Abs(localDirection.z) < 0.00001f) continue;   // parallel to the canvas
            float t = -localOrigin.z / localDirection.z;
            if (t < 0f || t > maxDistance) continue;                 // behind the hand or out of reach

            Vector3 localPoint = localOrigin + localDirection * t;
            Vector3 candidateWorld = canvas.transform.TransformPoint(localPoint);

            // Which raycast-target rect contains the point? The LAST match
            // in hierarchy order is drawn on top — it gets the pointer.
            Graphic best = null;
            Graphic[] graphics = vrCanvas.CachedGraphics;
            for (int g = 0; g < graphics.Length; g++)
            {
                Graphic graphic = graphics[g];
                if (graphic == null || !graphic.raycastTarget || !graphic.enabled) continue;

                Vector3 graphicLocal = graphic.rectTransform.InverseTransformPoint(candidateWorld);
                Rect rect = graphic.rectTransform.rect;
                if (rect.Contains(new Vector2(graphicLocal.x, graphicLocal.y))) best = graphic;
            }

            if (best == null) continue;

            if (t < bestDistance)
            {
                bestDistance = t;
                hitGraphic = best;
                worldPoint = candidateWorld;
            }
        }

        return hitGraphic != null;
    }

    // ------------------------------------------------------------------ //
    // Hover tracking (pointer enter / exit — drives button highlights)
    // ------------------------------------------------------------------ //

    private void UpdateHover(Graphic target)
    {
        GameObject targetGO = target != null ? target.gameObject : null;
        GameObject currentGO = hoverGraphic != null ? hoverGraphic.gameObject : null;

        // Same element (or both nothing) — nothing to do.
        if (targetGO == currentGO) return;

        if (currentGO != null)
        {
            ExecuteEvents.ExecuteHierarchy(currentGO, eventData, ExecuteEvents.pointerExitHandler);
            if (debugLogs) Debug.Log("[VRUIPointer] " + name + " exit: " + currentGO.name, this);
        }

        if (targetGO != null)
        {
            eventData.position = HeadScreenPoint(targetGO.transform.position);
            ExecuteEvents.ExecuteHierarchy(targetGO, eventData, ExecuteEvents.pointerEnterHandler);
            if (debugLogs) Debug.Log("[VRUIPointer] " + name + " enter: " + targetGO.name, this);
        }

        hoverGraphic = target;
    }

    private void ClearHover()
    {
        if (hoverGraphic != null)
        {
            GameObject currentGO = hoverGraphic.gameObject;
            ExecuteEvents.ExecuteHierarchy(currentGO, eventData, ExecuteEvents.pointerExitHandler);
            hoverGraphic = null;
        }
    }

    // ------------------------------------------------------------------ //
    // Clicking (pointer down / up / click)
    // ------------------------------------------------------------------ //

    private void HandleClickInput()
    {
        // Only THIS hand's button — each hand clicks with its own trigger.
        bool down = VRXRInput.GetButtonDown(node, clickButton);
        bool up = VRXRInput.GetButtonUp(node, clickButton);

        if (down && hoverGraphic != null)
        {
            GameObject target = hoverGraphic.gameObject;
            pressedTarget = target;

            eventData.position = HeadScreenPoint(LastHitPoint);
            eventData.pressPosition = eventData.position;
            ExecuteEvents.ExecuteHierarchy(target, eventData, ExecuteEvents.pointerDownHandler);

            if (debugLogs) Debug.Log("[VRUIPointer] " + name + " down: " + target.name, this);
        }

        if (up)
        {
            GameObject releasedOver = hoverGraphic != null ? hoverGraphic.gameObject : null;

            if (releasedOver != null)
            {
                eventData.position = HeadScreenPoint(LastHitPoint);
                ExecuteEvents.ExecuteHierarchy(releasedOver, eventData, ExecuteEvents.pointerUpHandler);
            }

            // A click needs down and up on the SAME element.
            if (pressedTarget != null && pressedTarget == releasedOver)
            {
                eventData.position = HeadScreenPoint(LastHitPoint);
                ExecuteEvents.ExecuteHierarchy(pressedTarget, eventData, ExecuteEvents.pointerClickHandler);

                if (debugLogs) Debug.Log("[VRUIPointer] " + name + " CLICK: " + pressedTarget.name, this);
            }

            pressedTarget = null;
        }
    }

    // ------------------------------------------------------------------ //
    // Line visuals (built from code — no prefabs)
    // ------------------------------------------------------------------ //

    private void BuildLine()
    {
        if (line != null) return;

        GameObject lineGO = new GameObject("UI Line");
        lineGO.transform.SetParent(transform, false);

        line = lineGO.AddComponent<LineRenderer>();
        lineMaterial = new Material(Shader.Find("Sprites/Default"));
        line.material = lineMaterial;
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startColor = lineColor;
        line.endColor = lineColor;
        line.widthMultiplier = lineWidth;
        line.numCapVertices = 4;
        line.enabled = false;
    }

    private void ShowLine(Vector3 worldPoint)
    {
        line.enabled = true;
        line.SetPosition(0, transform.position);
        line.SetPosition(1, worldPoint);
    }

    private void HideLine()
    {
        if (line != null) line.enabled = false;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>Best-effort screen position for the PointerEventData (world UI doesn't need it, but handlers sometimes read it).</summary>
    private Vector3 HeadScreenPoint(Vector3 worldPoint)
    {
        Camera head = ResolveHeadCamera();
        return head != null ? head.WorldToScreenPoint(worldPoint) : Vector3.zero;
    }

    /// <summary>The head is the XR rig's camera (setup static first, Camera.main fallback).</summary>
    private Camera ResolveHeadCamera()
    {
        if (headCamera != null) return headCamera;

        if (VRPlayerSetup.XRCamera != null)
        {
            headCamera = VRPlayerSetup.XRCamera;
            return headCamera;
        }

        headCamera = Camera.main;
        return headCamera;
    }

    /// <summary>Which hand this pointer lives on (the node its button reads).</summary>
    private XRNode NodeForThisHand()
    {
        VRHandPoseDriver driver = GetComponent<VRHandPoseDriver>();
        if (driver != null) return driver.Node;

        // No driver (e.g. an XRI controller object) — fall back to the
        // naming convention VRPlayerSetup/XRI use.
        return name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0
            ? XRNode.LeftHand
            : XRNode.RightHand;
    }
}
