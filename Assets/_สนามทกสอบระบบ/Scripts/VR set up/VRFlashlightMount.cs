using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// VR FLASHLIGHT MOUNT (v1.0) — the flashlight rides the RIGHT hand in VR
/// and its beam expels enemies exactly like the flat game.
///
/// Attach this component to the RIGHT hand anchor (VRPlayerSetup does that
/// for you). It:
///   • finds the existing flashlight (the Spot Light tagged "Flashlight" —
///     the very same GameObject the flat game uses, with its
///     FlashlightController, battery, HUD and animations untouched);
///   • holds the light IN the hand: every LateUpdate the light's world
///     position and rotation are driven to this hand's pose (+ a small
///     forward offset, like a torch held in a fist);
///   • toggles it with the hand's GRIP button (default) through the real
///     FlashlightController.SetOn() — battery drain, the low-battery
///     flicker, the dead-battery refusal, the on/off UnityEvents, the
///     battery HUD and the character animations all keep working. The
///     keyboard F key keeps working too;
///   • needs ZERO changes to FlashlightController or FlashlightDetector:
///     the detector reads the light's own transform, so a hand-aimed cone
///     is what the enemy sees. The player's body can never block the beam
///     (FlashlightDetector ignores colliders under the light's root —
///     the light stays under the Player root, so the hands are covered).
///
/// WHY A WORLD-POSE OVERRIDE INSTEAD OF RE-PARENTING
///   FlashlightController v2.3 aims the light at the camera transform every
///   LateUpdate (execution order 100). This component runs LATER
///   (DefaultExecutionOrder 200) and overwrites the pose with the hand's —
///   a deterministic last-write-wins, no reflection, no modification of the
///   original script. The light stays wherever it is in the hierarchy; it
///   just points where the hand points.
///
/// FROZEN WITH THE GAME: no aiming and no toggling while the pause menu is
/// open or the jumpscare plays (same rules the original flashlight has).
///
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)] // AFTER FlashlightController (100): our pose write wins
public class VRFlashlightMount : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Flashlight (auto-found by tag)")]

    [Tooltip("The flashlight whose Spot Light GameObject is tagged 'Flashlight'. Auto-found when left empty.")]
    [SerializeField] private FlashlightController flashlight;

    [Tooltip("Tag used to find the flashlight automatically.")]
    [SerializeField] private string flashlightTag = "Flashlight";

    [Header("Toggle Button (this hand's controller)")]

    [Tooltip("Button that switches the flashlight on/off. Grip by default — the trigger is the UI click button.")]
    [SerializeField] private VRXRInput.HandButton toggleButton = VRXRInput.HandButton.Grip;

    [Header("Hold Pose (local space of this hand)")]

    [Tooltip("Where the light sits in the hand (meters). Slightly forward so the beam leaves the fist.")]
    [SerializeField] private Vector3 localPositionOffset = new Vector3(0f, 0f, 0.08f);

    [Tooltip("Extra rotation of the light in the hand (degrees). Zero = beam points along the hand's pointing direction.")]
    [SerializeField] private Vector3 localRotationOffset = Vector3.zero;

    [Header("Diagnostics")]

    [Tooltip("Log the mount (which flashlight was grabbed) once in the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Transform lightTransform;
    private float searchTimer = 1f;   // >= retry interval: the FIRST search happens immediately
    private bool warnedNoFlashlight;
    private bool loggedMount;

    /// <summary>The flashlight this mount drives (null until found).</summary>
    public FlashlightController Flashlight
    {
        get { return flashlight; }
    }

    /// <summary>True on the frame the mount grabbed the light (useful for effects).</summary>
    public bool IsMounted
    {
        get { return lightTransform != null; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        ResolveFlashlight();
    }

    private void LateUpdate()
    {
        // Frozen with the rest of the game while paused or during the
        // jumpscare — the light keeps its last pose, exactly like the
        // original "aim locks" behaviour in FlashlightController v1.2+.
        if (GamePauseManager.IsPaused || JumpscareGameOverManager.IsJumpscareActive) return;

        if (!ResolveFlashlight()) return;

        // ---- pose: in the hand, pointing where the hand points ----
        lightTransform.position = transform.position + transform.rotation * localPositionOffset;
        lightTransform.rotation = transform.rotation * Quaternion.Euler(localRotationOffset);

        // ---- toggle: this hand's button through the real controller ----
        if (VRXRInput.GetButtonDown(XRNode.RightHand, toggleButton) && flashlight != null)
        {
            flashlight.SetOn(!flashlight.IsOn);
        }
    }

    // ------------------------------------------------------------------ //
    // Finding the flashlight (retry quietly — the scene may still load)
    // ------------------------------------------------------------------ //

    private bool ResolveFlashlight()
    {
        if (flashlight != null)
        {
            if (lightTransform == null) lightTransform = flashlight.transform;
            return true;
        }

        searchTimer += Time.unscaledDeltaTime;
        if (searchTimer < 0.5f) return false;
        searchTimer = 0f;

        GameObject found = null;
        try
        {
            found = GameObject.FindGameObjectWithTag(flashlightTag);
        }
        catch
        {
            // Tag not defined in the project — warn once, keep retrying.
        }

        if (found == null)
        {
            if (!warnedNoFlashlight)
            {
                warnedNoFlashlight = true;
                Debug.LogWarning("[VRFlashlightMount] No active object tagged '" + flashlightTag +
                                 "' found — the right hand cannot hold the flashlight yet. Retrying every 0.5 s.", this);
            }
            return false;
        }

        flashlight = found.GetComponent<FlashlightController>();
        if (flashlight == null) flashlight = found.GetComponentInParent<FlashlightController>();

        if (flashlight == null)
        {
            if (!warnedNoFlashlight)
            {
                warnedNoFlashlight = true;
                Debug.LogWarning("[VRFlashlightMount] '" + found.name + "' (tagged '" + flashlightTag +
                                 "') has no FlashlightController — nothing to toggle.", this);
            }
            return false;
        }

        lightTransform = flashlight.transform;

        if (!loggedMount && debugLogs)
        {
            loggedMount = true;
            Debug.Log("[VRFlashlightMount] '" + found.name + "' mounted on the right hand.", this);
        }

        return true;
    }

    /// <summary>Clamps Inspector values so the mount can never receive nonsense input.</summary>
    private void OnValidate()
    {
        if (string.IsNullOrEmpty(flashlightTag)) flashlightTag = "Flashlight";
    }
}
