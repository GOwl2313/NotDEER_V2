using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// VRXRInput (v1.0) — the ONE place every VR script reads controller input from.
///
/// WHAT IT IS
///   A tiny, dependency-free facade over UnityEngine.XR (the low-level XR
///   device API that ships with Unity itself — NOT the XR Interaction
///   Toolkit). Every NotDEER VR component reads poses and buttons through
///   this class, which buys us three things:
///
///     1. ONE device cache. XR controllers can appear late (the XR
///        Interaction Simulator takes a moment to spin up its devices,
///        a headset can re-connect). The facade re-acquires invalid
///        devices automatically instead of every script doing it wrong
///        in its own way.
///     2. FRAME-CACHED EDGE DETECTION. "Was the trigger pressed THIS
///        frame?" must be a stable answer even when several components
///        ask about the same button in the same frame (the left hand's
///        VRUIPointer and VRWarpLocomotion BOTH watch the left trigger).
///        The first caller computes the edge, everyone else this frame
///        reads the same cached result — exactly like KeyCodeBridge's
///        frame-based key arbitration does for the keyboard.
///     3. One place to stub for testing — the whole VR layer can be
///        simulated without any XR runtime.
///
/// BUTTONS AND POSES
///   • Buttons  — Trigger / Grip / Primary / Secondary / Menu on either
///                controller, via the standard CommonUsages bools the
///                XR Interaction Simulator also feeds.
///   • Values   — analog trigger / grip squeeze (0..1).
///   • Poses    — device position + rotation for a node (tracking-space
///                local pose, the same space XR Origin anchors use).
///
/// v1.0 — initial release with the NotDEER VR layer (warp locomotion,
///        UI pointers, hand flashlight, pause menu, touch pickup).
/// </summary>
public static class VRXRInput
{
    // ------------------------------------------------------------------ //
    // Public enum (shared by every VR component)
    // ------------------------------------------------------------------ //

    /// <summary>The controller buttons the VR layer can read.</summary>
    public enum HandButton
    {
        Trigger,    // index finger rest — the big one
        Grip,       // middle finger rest
        Primary,    // the "A/X" face button
        Secondary,  // the "B/Y" face button
        Menu        // the little system/menu button (usually left controller only)
    }

    // ------------------------------------------------------------------ //
    // Device cache (one handle per node; re-acquired when invalid)
    // ------------------------------------------------------------------ //

    private static readonly Dictionary<XRNode, InputDevice> s_Devices = new Dictionary<XRNode, InputDevice>();
    private static bool s_WarnedNoDevices;

    // ------------------------------------------------------------------ //
    // Frame-cached edge states (key = node * buttonHash)
    // ------------------------------------------------------------------ //

    private static readonly Dictionary<int, bool> s_LastValue = new Dictionary<int, bool>();        // freshest poll per key
    private static readonly Dictionary<int, bool> s_HeldAtFrameStart = new Dictionary<int, bool>(); // held state entering the frame (frozen per frame)
    private static readonly Dictionary<int, int> s_LastPolledFrame = new Dictionary<int, int>();    // which frame a key was last polled in

    // ------------------------------------------------------------------ //
    // Static reset (Play Mode restarts with Domain Reload disabled)
    // ------------------------------------------------------------------ //

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_Devices.Clear();
        s_LastValue.Clear();
        s_HeldAtFrameStart.Clear();
        s_LastPolledFrame.Clear();
        s_WarnedNoDevices = false;
    }

    // ------------------------------------------------------------------ //
    // Device presence
    // ------------------------------------------------------------------ //

    /// <summary>
    /// True when a valid XR device exists for the node RIGHT NOW (controller
    /// connected and tracked by the runtime — or simulated by the XR
    /// Interaction Simulator).
    /// </summary>
    public static bool HasDevice(XRNode node)
    {
        return ResolveDevice(node).isValid;
    }

    /// <summary>
    /// True when NEITHER controller exists — useful for one-time setup hints
    /// ("no XR input found — is the XR Interaction Simulator enabled?").
    /// </summary>
    public static bool HasNoControllers()
    {
        return !ResolveDevice(XRNode.LeftHand).isValid && !ResolveDevice(XRNode.RightHand).isValid;
    }

    /// <summary>Logs a one-time hint that no XR controllers were found (editor convenience).</summary>
    public static void WarnNoControllersOnce(float afterSecondsHint)
    {
        if (s_WarnedNoDevices) return;
        if (Time.unscaledTime < afterSecondsHint) return;
        if (!HasNoControllers()) return;

        s_WarnedNoDevices = true;
        Debug.LogWarning("[VRXRInput] No XR controller devices found after " + afterSecondsHint.ToString("F0") +
                         "s. For editor testing enable the XR Interaction Simulator " +
                         "(Project Settings > XR Plug-in Management) and add its prefab to the scene. " +
                         "Poses stay at their last known values and controller buttons are ignored until a device appears.");
    }

    // ------------------------------------------------------------------ //
    // Poses (tracking-space local position/rotation)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The device's position in TRACKING space — i.e. relative to the XR
    /// Origin, exactly the local pose a hand anchor parented under the XR
    /// Origin's Camera Offset should receive.
    /// </summary>
    public static bool TryGetLocalPose(XRNode node, out Vector3 localPosition, out Quaternion localRotation)
    {
        InputDevice device = ResolveDevice(node);
        localPosition = Vector3.zero;
        localRotation = Quaternion.identity;

        if (!device.isValid) return false;

        bool gotPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out localPosition);
        bool gotRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out localRotation);
        return gotPosition && gotRotation;
    }

    // ------------------------------------------------------------------ //
    // Buttons (held state + frame-cached edges)
    // ------------------------------------------------------------------ //

    /// <summary>True while the button is held down (false when no device exists).</summary>
    public static bool GetButton(XRNode node, HandButton button)
    {
        InputDevice device = ResolveDevice(node);
        bool pressed = device.isValid && device.TryGetFeatureValue(UsageFor(button), out bool value) && value;

        TouchEdgeState(EdgeKey(node, button), pressed);
        return pressed;
    }

    /// <summary>
    /// True only on the frame the button went DOWN. Frame-cached: every
    /// caller in the same frame sees the SAME answer, no matter who asked
    /// first or in which order (multiple components watch the same trigger,
    /// and one of them may only ever call GetButton).
    /// </summary>
    public static bool GetButtonDown(XRNode node, HandButton button)
    {
        int key = EdgeKey(node, button);
        bool current = GetButton(node, button);   // rolls the frame bookkeeping first

        s_HeldAtFrameStart.TryGetValue(key, out bool frameStart);
        return current && !frameStart;
    }

    /// <summary>
    /// True only on the frame the button came UP. Frame-cached like
    /// GetButtonDown — every caller in the same frame sees the same answer.
    /// </summary>
    public static bool GetButtonUp(XRNode node, HandButton button)
    {
        int key = EdgeKey(node, button);
        bool current = GetButton(node, button);   // rolls the frame bookkeeping first

        s_HeldAtFrameStart.TryGetValue(key, out bool frameStart);
        return !current && frameStart;
    }

    // ------------------------------------------------------------------ //
    // Edge bookkeeping (shared by GetButton / GetButtonDown / GetButtonUp)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Every poll passes through here. On the FIRST poll of a new frame the
    /// state the frame STARTED with is frozen from the last poll of the
    /// previous frame — after that the frame's start state never changes,
    /// which is what makes every edge answer identical for all callers.
    /// </summary>
    private static void TouchEdgeState(int key, bool current)
    {
        int frame = Time.frameCount;

        if (!s_LastPolledFrame.TryGetValue(key, out int polledFrame) || polledFrame != frame)
        {
            s_HeldAtFrameStart[key] = s_LastValue.TryGetValue(key, out bool last) && last;
            s_LastPolledFrame[key] = frame;
        }

        s_LastValue[key] = current;
    }

    // ------------------------------------------------------------------ //
    // Analog values
    // ------------------------------------------------------------------ //

    /// <summary>Analog squeeze 0..1 (false/no device → 0).</summary>
    public static float GetTriggerValue(XRNode node)
    {
        InputDevice device = ResolveDevice(node);
        if (!device.isValid) return 0f;

        return device.TryGetFeatureValue(CommonUsages.trigger, out float value) ? value : 0f;
    }

    /// <summary>Analog squeeze 0..1 (false/no device → 0).</summary>
    public static float GetGripValue(XRNode node)
    {
        InputDevice device = ResolveDevice(node);
        if (!device.isValid) return 0f;

        return device.TryGetFeatureValue(CommonUsages.grip, out float value) ? value : 0f;
    }

    // ------------------------------------------------------------------ //
    // Internals
    // ------------------------------------------------------------------ //

    private static InputFeatureUsage<bool> UsageFor(HandButton button)
    {
        switch (button)
        {
            case HandButton.Trigger: return CommonUsages.triggerButton;
            case HandButton.Grip: return CommonUsages.gripButton;
            case HandButton.Primary: return CommonUsages.primaryButton;
            case HandButton.Secondary: return CommonUsages.secondaryButton;
            case HandButton.Menu: return CommonUsages.menuButton;
            default: return CommonUsages.triggerButton;
        }
    }

    private static int EdgeKey(XRNode node, HandButton button)
    {
        return ((int)node << 8) | (int)button;
    }

    /// <summary>
    /// Cached device lookup per node. Devices can appear late or die
    /// mid-session (simulator start, controller sleep) — an invalid handle
    /// is simply re-acquired on the next call.
    /// </summary>
    private static InputDevice ResolveDevice(XRNode node)
    {
        if (s_Devices.TryGetValue(node, out InputDevice cached) && cached.isValid)
        {
            return cached;
        }

        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid)
        {
            s_Devices[node] = device;
        }
        else if (cached.isValid)
        {
            // The cached device went invalid (disconnect) — drop it so the
            // next successful lookup can replace it.
            s_Devices.Remove(node);
        }

        return device;
    }
}
