using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Toggles the flashlight with the controller's Primary Button.
/// Quest: A (left) / X (right) button. XR Interaction Simulator: keyboard key 1.
/// Everything is created in code - there is NOTHING to configure in the Inspector.
/// The F key (FlashlightController's own toggle) keeps working alongside this.
///
/// v1.1 — PAUSE FIX: Update() now returns while GamePauseManager's pause menu
/// is open, matching FlashlightController's own F-key path. Previously the
/// controller button could silently toggle the flashlight (playing its audio
/// hooks) while the game was paused.
/// </summary>
public class VRFlashlightToggle : MonoBehaviour
{
    [Tooltip("Optional - auto-finds the FlashlightController in the scene if left empty.")]
    [SerializeField] private FlashlightController flashlight;

    private InputAction toggleAction;

    private void Awake()
    {
        // 1. Find the flashlight (same object first, then anywhere in the scene).
        if (flashlight == null)
            flashlight = GetComponent<FlashlightController>();

        if (flashlight == null)
            flashlight = FindAnyObjectByType<FlashlightController>();

        if (flashlight == null)
        {
            Debug.LogError("[VRFlashlightToggle] No FlashlightController found in the scene - VR button toggle disabled. " +
                "Assign the flashlight reference in the Inspector, or put this script on the same GameObject as the FlashlightController.", this);
            enabled = false;
            return;
        }

        // 2. Build the input action entirely in code.
        //    Both hands = works whichever controller the simulator is manipulating.
        toggleAction = new InputAction("VR Flashlight Toggle", InputActionType.Button);
        toggleAction.AddBinding("<XRController>{RightHand}/primaryButton"); // Quest: X, simulator: 1
        toggleAction.AddBinding("<XRController>{LeftHand}/primaryButton");  // Quest: A, simulator: 1
        toggleAction.AddBinding("<Keyboard>/1");                            // direct keyboard fallback

        Debug.Log("[VRFlashlightToggle] Ready on '" + name + "' - controlling FlashlightController on '" +
            flashlight.gameObject.name + "'. Toggle = Primary Button (Quest A/X, simulator key 1).", this);
    }

    private void OnEnable()
    {
        toggleAction?.Enable();
    }

    private void OnDisable()
    {
        toggleAction?.Disable();
    }

    private void OnDestroy()
    {
        toggleAction?.Dispose();
    }

    private void Update()
    {
        // v1.1: frozen with the rest of the game while the pause menu is open
        // (same rule FlashlightController applies to its own F-key toggle).
        if (GamePauseManager.IsPaused)
            return;

        if (flashlight == null || toggleAction == null)
            return;

        if (toggleAction.WasPressedThisFrame())
        {
            flashlight.SetOn(!flashlight.IsOn);
            Debug.Log("[VRFlashlightToggle] Flashlight -> " + (flashlight.IsOn ? "ON" : "OFF") +
                "  [input: " + (toggleAction.activeControl != null ? toggleAction.activeControl.path : "unknown") + "]", this);
        }
    }
}
