using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A "+" crosshair locked to the exact middle of the screen.
///
/// The whole UI is generated from code at runtime — just add this component to
/// any GameObject (the player, an empty "UI" object, anything) and press Play.
/// No editor setup is needed.
///
/// It hides itself automatically while the pause menu is open
/// (see GamePauseManager). Every value is adjustable in the Inspector and
/// updates live while playing.
/// </summary>
[DisallowMultipleComponent]
public class CrosshairUI : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Crosshair Shape (pixels)")]

    [Tooltip("Length of each of the four arms (total '+' size = 2 x Arm Length).")]
    [SerializeField] private float armLength = 10f;

    [Tooltip("Thickness of the crosshair lines.")]
    [SerializeField] private float thickness = 2f;

    [Tooltip("Empty space in the very middle. 0 = a solid '+', higher = a hollow cross.")]
    [SerializeField] private float centerGap = 0f;

    [Header("Color")]

    [Tooltip("Crosshair color.")]
    [SerializeField] private Color crosshairColor = Color.white;

    [Header("Behaviour")]

    [Tooltip("Hide the crosshair while the pause menu is open.")]
    [SerializeField] private bool hideWhenPaused = true;

    [Tooltip("Canvas sorting order. Kept far below the pause menu (32767) so the menu always covers it.")]
    [SerializeField] private int sortingOrder = 10;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private RectTransform root;
    private readonly Image[] arms = new Image[4];
    private readonly RectTransform[] armRects = new RectTransform[4];

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        BuildCrosshair();
        ApplyStyle();
    }

    private void Update()
    {
        if (root == null) return;

        bool show = !(hideWhenPaused && GamePauseManager.IsPaused);
        if (root.gameObject.activeSelf != show)
        {
            root.gameObject.SetActive(show);
        }

        if (show)
        {
            ApplyStyle();   // keeps the Inspector values live while playing
        }
    }

    /// <summary>Clamps Inspector values so the crosshair can never receive nonsense input.</summary>
    private void OnValidate()
    {
        armLength = Mathf.Max(0f, armLength);
        thickness = Mathf.Max(1f, thickness);
        centerGap = Mathf.Max(0f, centerGap);
        sortingOrder = Mathf.Clamp(sortingOrder, 0, 32767);
    }

    // ------------------------------------------------------------------ //
    // Building the UI
    // ------------------------------------------------------------------ //

    private void BuildCrosshair()
    {
        GameObject canvasGO = new GameObject("CrosshairCanvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;   // sizes are true screen pixels

        // No GraphicRaycaster on purpose: none of the crosshair images can
        // ever intercept clicks meant for the game or the pause menu.

        GameObject rootGO = new GameObject("Crosshair");
        root = rootGO.AddComponent<RectTransform>();
        root.SetParent(canvasGO.transform, false);
        root.anchorMin = Vector2.one * 0.5f;    // locked to the screen center
        root.anchorMax = Vector2.one * 0.5f;
        root.sizeDelta = Vector2.zero;

        for (int i = 0; i < arms.Length; i++)
        {
            GameObject armGO = new GameObject("Arm_" + i);
            Image image = armGO.AddComponent<Image>();
            image.raycastTarget = false;        // never blocks clicks
            arms[i] = image;
            armRects[i] = image.rectTransform;

            RectTransform rt = armRects[i];
            rt.SetParent(root, false);
            rt.anchorMin = Vector2.one * 0.5f;
            rt.anchorMax = Vector2.one * 0.5f;
            rt.pivot = Vector2.one * 0.5f;
        }
    }

    private void ApplyStyle()
    {
        float halfGap = centerGap * 0.5f;
        float halfLength = armLength * 0.5f;
        float offset = halfGap + halfLength;

        // up / down arms — thin and tall
        SetArm(armRects[0], new Vector2(thickness, armLength), new Vector2(0f, offset));
        SetArm(armRects[1], new Vector2(thickness, armLength), new Vector2(0f, -offset));
        // left / right arms — wide and thin
        SetArm(armRects[2], new Vector2(armLength, thickness), new Vector2(-offset, 0f));
        SetArm(armRects[3], new Vector2(armLength, thickness), new Vector2(offset, 0f));

        for (int i = 0; i < arms.Length; i++)
        {
            arms[i].color = crosshairColor;
        }
    }

    private static void SetArm(RectTransform rt, Vector2 size, Vector2 position)
    {
        rt.sizeDelta = size;
        rt.anchoredPosition = position;
    }
}