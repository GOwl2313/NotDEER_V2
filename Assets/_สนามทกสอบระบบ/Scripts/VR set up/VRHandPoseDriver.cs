using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// VR HAND POSE DRIVER (v1.0) — moves a hand anchor to the tracked pose of
/// an XR controller.
///
/// Attach this to the GameObject that represents one hand under the XR
/// Origin (VRPlayerSetup creates "VR Left Hand" / "VR Right Hand" under the
/// Camera Offset automatically). Every frame it copies the controller's
/// tracking-space position/rotation onto that anchor's LOCAL transform:
///
///   • the anchor is a child of the XR Origin's Camera Offset — the same
///     parent the XR Origin's camera lives under — so the hand and the
///     head always share the same offset conventions, whatever tracking
///     origin mode (Device / Floor) the rig uses;
///   • poses are written in Update AND re-written in LateUpdate so the
///     warp line, the UI ray and the touch colliders all start the frame
///     from a fresh pose and render with the freshest one;
///   • this driver contains ZERO XR Interaction Toolkit references — it
///     talks straight to UnityEngine.XR, so it works with the XR
///     Interaction Simulator in the editor AND with a real headset.
///
/// If an existing XRI "LeftHand Controller"/"RightHand Controller" object
/// (with its own TrackedPoseDriver) is already in the rig, VRPlayerSetup
/// reuses it and does NOT add this component — both drive the same device.
///
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRHandPoseDriver : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Tooltip("Which controller this hand follows.")]
    [SerializeField] private XRNode node = XRNode.LeftHand;

    [Tooltip("Write the pose in LateUpdate too, so lines/rays attached to this hand render with the freshest position (disable only if another pose driver already writes LateUpdate).")]
    [SerializeField] private bool alsoDriveInLateUpdate = true;

    [Tooltip("Seconds to wait after Play starts before warning that no XR controller was found (the XR Interaction Simulator takes a moment to create its devices). 0 disables the warning.")]
    [SerializeField, Min(0f)] private float noDeviceWarningDelay = 5f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private bool warnedNoDevice;

    /// <summary>The controller this hand follows (settable from code — VRPlayerSetup uses it when it creates hands).</summary>
    public XRNode Node
    {
        get { return node; }
        set { node = value; }
    }

    /// <summary>True while the configured controller device exists.</summary>
    public bool HasDevice
    {
        get { return VRXRInput.HasDevice(node); }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Update()
    {
        Drive();
    }

    private void LateUpdate()
    {
        if (alsoDriveInLateUpdate) Drive();
    }

    // ------------------------------------------------------------------ //
    // Driving
    // ------------------------------------------------------------------ //

    private void Drive()
    {
        if (VRXRInput.TryGetLocalPose(node, out Vector3 localPosition, out Quaternion localRotation))
        {
            transform.localPosition = localPosition;
            transform.localRotation = localRotation;
            return;
        }

        // No valid device (yet) — keep the last known pose so the hand
        // never snaps to the rig's origin, and hint once after a grace
        // period. This is a normal state while the simulator boots.
        if (!warnedNoDevice && noDeviceWarningDelay > 0f)
        {
            VRXRInput.WarnNoControllersOnce(noDeviceWarningDelay);
            if (VRXRInput.HasNoControllers()) warnedNoDevice = true;
        }
    }
}
