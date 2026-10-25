using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// VR CONTROLLER INPUT for the systems that used to be keyboard-only.
/// Zero setup: the actions are built in code the first time they are read,
/// nothing to configure in the Inspector and nothing to add to the scene.
///
///   Interact   Trigger (either hand)          — InteractionPoint, ItemPickup
///   Pause      Menu button (left controller)  — GamePauseManager
///   Skip       Secondary button (B / Y)       — CassetteTapePlayer
///
/// The flashlight keeps its own Primary-button toggle (VRFlashlightToggle).
/// Movement, snap turn, gravity and jump come from the XR Origin rig's own
/// providers (Locomotion object), so they are not duplicated here.
///
/// The keyboard paths in every script keep working exactly as before — each
/// script simply ORs the VR press into its existing input read, so the
/// project's one-press-one-action arbitration (KeyCodeBridge) still applies.
///
/// Bindings use the usage syntax ({TriggerButton}) that XR Interaction Toolkit's
/// own default actions use, so they work on OpenXR controllers and on the
/// XR Interaction Simulator alike. No statics survive a domain reload.
/// </summary>
public static class VRInputBridge
{
#if ENABLE_INPUT_SYSTEM
    private static InputAction interactAction;
    private static InputAction pauseAction;
    private static InputAction skipAction;

    /// <summary>True on the frame the Trigger was pressed on either controller.</summary>
    public static bool InteractPressedThisFrame
    {
        get
        {
            return Pressed(ref interactAction, "VR Interact",
                "<XRController>{RightHand}/{TriggerButton}",
                "<XRController>{LeftHand}/{TriggerButton}");
        }
    }

    /// <summary>True on the frame the left controller's Menu button was pressed.</summary>
    public static bool PausePressedThisFrame
    {
        get
        {
            return Pressed(ref pauseAction, "VR Pause",
                "<XRController>{LeftHand}/{MenuButton}");
        }
    }

    /// <summary>True on the frame the Secondary button (B / Y) was pressed on either controller.</summary>
    public static bool SkipPressedThisFrame
    {
        get
        {
            return Pressed(ref skipAction, "VR Skip",
                "<XRController>{RightHand}/{SecondaryButton}",
                "<XRController>{LeftHand}/{SecondaryButton}");
        }
    }

    private static bool Pressed(ref InputAction action, string actionName, params string[] bindings)
    {
        if (action == null)
        {
            action = new InputAction(actionName, InputActionType.Button);
            for (int i = 0; i < bindings.Length; i++)
                action.AddBinding(bindings[i]);
            action.Enable();
        }

        return action.WasPressedThisFrame();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Release(ref interactAction);
        Release(ref pauseAction);
        Release(ref skipAction);
    }

    private static void Release(ref InputAction action)
    {
        if (action == null) return;
        action.Disable();
        action.Dispose();
        action = null;
    }
#else
    public static bool InteractPressedThisFrame { get { return false; } }
    public static bool PausePressedThisFrame { get { return false; } }
    public static bool SkipPressedThisFrame { get { return false; } }
#endif
}
