using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// WAYPOINT EVENT GATE (v1.0) — the "event spot" plug-in for WorldWaypoint.
///
/// WHAT IT DOES
///   Put it on the SAME GameObject as a WorldWaypoint and the waypoint's
///   marker stays COMPLETELY hidden until a game event happens — then it
///   appears. Built for exactly your End Game Point case:
///
///     "a waypoint of the End game point (place all totems spot) should
///      be hidden until the End point appear"
///
///   Put the waypoint + this gate under your RitualEndingSpot object and
///   you are DONE — zero wiring:
///     • before all totems are collected  → no marker at all,
///     • the moment the spot is revealed  → the marker appears (icon +
///       label + live distance), visible from anywhere on the map.
///
/// TRIGGER SOURCES (Inspector: "Trigger")
///   • Ritual Ending Spot (default) — watches the RitualEndingSpot on
///     this object (or any parent) and opens the moment it is revealed
///     (all totems collected). Works with EVERY reveal route: automatic
///     totem counting, RevealRitualSpot(), the TotemCollectionHUD
///     fallback and ForceEnding() — it polls the spot's IsRevealed flag,
///     so nothing can slip past it.
///   • Game Event — waits until any script anywhere calls
///       WaypointEvents.Raise("AllTotemsCollected");
///     (friend-friendly: one line, no references, no setup).
///   • Manual — opens only when YOU say so: call Trigger() or
///     ShowWaypoint() from any UnityEvent or script.
///
/// DIRECTION
///   • Show When Triggered (default) — hidden until the event, then shown.
///   • Hide When Triggered — visible until the event, then hidden (e.g. a
///     marker that disappears when an escape sequence starts).
///
/// WORKS WITH THE PARENT, NOT AGAINST IT
///   The gate only decides "allowed to show". Everything else behaves
///   exactly like a normal waypoint: parent deactivated or destroyed →
///   the marker is gone in the same instant. The gate re-applies itself
///   when the parent comes back, so a hidden-by-gate waypoint can never
///   leak through a parent toggle.
///
/// RESTART-SAFE (scene reload)
///   Game Events are remembered only for the lifetime of the scene:
///   reloading the scene (your restart flow) automatically forgets them,
///   so a retry starts with the End Game point hidden again — no code
///   needed, in the Editor AND in builds.
///
/// SETUP (End Game Point — your case)
///   1. On your RitualEndingSpot object: create an empty child
///      "Waypoint" where the marker should float (usually above the
///      spot).
///   2. Add WorldWaypoint to it: icon + label ("End Game Point") as
///      usual.
///   3. Add Waypoint Event Gate to the same child. Keep the defaults
///      (Ritual Ending Spot / Show When Triggered). Press Play. Done.
///
/// v1.0 — initial release.
/// </summary>
[RequireComponent(typeof(WorldWaypoint))]
[DisallowMultipleComponent]
public class WaypointEventGate : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Trigger")]

    [Tooltip("What event opens this gate:\n• Ritual Ending Spot — hidden until the RitualEndingSpot on this object (or any parent) is revealed: all totems collected. Fully automatic, no wiring.\n• Game Event — hidden until any script calls WaypointEvents.Raise() with the event name below.\n• Manual — hidden until Trigger() / ShowWaypoint() is called (wire those to any UnityEvent).")]
    [SerializeField] private TriggerSource trigger = TriggerSource.RitualEndingSpot;

    [Tooltip("[Game Event] The exact event name to wait for. Any script can fire it from anywhere: WaypointEvents.Raise(\"AllTotemsCollected\"). Must match letter-for-letter (a warning-free Trim runs in the Inspector).")]
    [SerializeField] private string gameEventName = "AllTotemsCollected";

    [Header("Direction")]

    [Tooltip("Show When Triggered (default): the marker is HIDDEN until the event happens (the End Game Point case).\nHide When Triggered: the marker is VISIBLE until the event happens, then disappears.")]
    [SerializeField] private GateDirection direction = GateDirection.ShowWhenTriggered;

    [Header("Debug")]

    [Tooltip("Write gate open/close changes to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    /// <summary>What this gate waits for.</summary>
    public enum TriggerSource
    {
        /// <summary>The RitualEndingSpot on this object (or a parent) gets revealed — all totems collected. Automatic.</summary>
        RitualEndingSpot = 0,

        /// <summary>WaypointEvents.Raise() is called with the matching event name. Works from any script.</summary>
        GameEvent = 1,

        /// <summary>Nothing automatic — the gate only opens on Trigger() / ShowWaypoint().</summary>
        Manual = 2
    }

    /// <summary>Which way the gate swings when the trigger happens.</summary>
    public enum GateDirection
    {
        /// <summary>Hidden until the trigger, then shown (the End Game Point case).</summary>
        ShowWhenTriggered = 0,

        /// <summary>Visible until the trigger, then hidden.</summary>
        HideWhenTriggered = 1
    }

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private WorldWaypoint waypoint;
    private RitualEndingSpot spot;
    private bool localTriggered;
    private bool warnedNoSpot;

    // ------------------------------------------------------------------ //
    // Read-only state (for your scripts)
    // ------------------------------------------------------------------ //

    /// <summary>True once the trigger happened (spot revealed, event raised, or Trigger() called).</summary>
    public bool IsTriggered
    {
        get
        {
            if (localTriggered) return true;
            switch (trigger)
            {
                case TriggerSource.RitualEndingSpot: return spot != null && spot.IsRevealed;
                case TriggerSource.GameEvent: return WaypointEvents.HasFired(gameEventName);
            }
            return false;
        }
    }

    /// <summary>True while the gate allows the marker to show (trigger state + direction applied).</summary>
    public bool IsGateOpen
    {
        get { return (direction == GateDirection.ShowWhenTriggered) == IsTriggered; }
    }

    /// <summary>What this gate waits for.</summary>
    public TriggerSource Source { get { return trigger; } }

    /// <summary>The event name this gate waits for (Game Event mode).</summary>
    public string GameEventName { get { return gameEventName; } }

    // ------------------------------------------------------------------ //
    // Public API — wire these to any UnityEvent, call them from any script
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Fires the gate's trigger manually (the event this gate waits for
    /// "happened"). Wire this to any UnityEvent — e.g. a button, a
    /// trigger zone, your own scripts. Idempotent.
    /// </summary>
    public void Trigger()
    {
        localTriggered = true;
        ApplyGate();
    }

    /// <summary>
    /// Re-arms the gate (back to waiting). The waypoint is hidden again —
    /// unless its trigger source has already happened (e.g. the ritual
    /// spot is already revealed: that door does not close again).
    /// </summary>
    public void ResetGate()
    {
        localTriggered = false;
        ApplyGate();
    }

    /// <summary>
    /// Shows the waypoint NOW, bypassing the gate (for special cases).
    /// The gate re-asserts itself on its next trigger / reset / re-enable.
    /// </summary>
    public void ShowWaypoint()
    {
        if (waypoint == null) return;
        waypoint.Show();
    }

    /// <summary>
    /// Hides the waypoint NOW, bypassing the gate (for special cases).
    /// The gate re-asserts itself on its next trigger / reset / re-enable.
    /// </summary>
    public void HideWaypoint()
    {
        if (waypoint == null) return;
        waypoint.Hide();
    }

    /// <summary>Direct show/hide switch, bypassing the gate (see ShowWaypoint()).</summary>
    public void SetWaypointVisible(bool visible)
    {
        if (waypoint == null) return;
        waypoint.SetVisible(visible);
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        waypoint = GetComponent<WorldWaypoint>();
        localTriggered = false;
        FindSpot();
        WaypointEvents.Raised += HandleGameEventRaised;
        WaypointEvents.Cleared += HandleEventsCleared;
        ApplyGate();
    }

    private void OnDisable()
    {
        WaypointEvents.Raised -= HandleGameEventRaised;
        WaypointEvents.Cleared -= HandleEventsCleared;
    }

    private void Update()
    {
        // The RitualEndingSpot watch: its reveal is a one-way flag, so we
        // simply poll it (cheaper and more robust than hooking every
        // reveal route — and it can never miss one). Everything else is
        // fully event-driven.
        if (localTriggered || trigger != TriggerSource.RitualEndingSpot) return;
        if (spot == null) return;
        if (!spot.IsRevealed) return;
        ApplyGate();
    }

    /// <summary>Trims the event name so a trailing space can never silently break the match.</summary>
    private void OnValidate()
    {
        if (gameEventName == null) gameEventName = string.Empty;
        else gameEventName = gameEventName.Trim();
    }

    // ------------------------------------------------------------------ //
    // Internals
    // ------------------------------------------------------------------ //

    private void FindSpot()
    {
        if (trigger != TriggerSource.RitualEndingSpot)
        {
            spot = null;
            return;
        }
        if (spot != null) return;
        spot = GetComponentInParent<RitualEndingSpot>();
        if (spot == null && !warnedNoSpot)
        {
            warnedNoSpot = true;
            Debug.LogWarning("[WaypointEventGate] No RitualEndingSpot found on '" + name + "' or its parents — the waypoint stays hidden. Put this gate under your RitualEndingSpot object, or switch its Trigger to Game Event / Manual.", this);
        }
    }

    private void HandleGameEventRaised(string eventName)
    {
        if (trigger != TriggerSource.GameEvent) return;
        if (eventName != gameEventName) return;
        ApplyGate();
    }

    private void HandleEventsCleared()
    {
        // WaypointEvents was reset (scene reload / ResetAll) — re-apply,
        // so a restarted game starts with the waypoint gated again.
        ApplyGate();
    }

    private void ApplyGate()
    {
        if (waypoint == null) return;
        bool open = IsGateOpen;
        bool wasHidden = waypoint.IsManuallyHidden;
        waypoint.SetVisible(open);
        if (debugLogs && waypoint.IsManuallyHidden != wasHidden)
        {
            Debug.Log("[WaypointEventGate] '" + name + "' " + (open ? "opened — waypoint shown." : "closed — waypoint hidden."), this);
        }
    }
}

/// <summary>
/// WAYPOINT EVENTS (v1.0) — a tiny global "named event" service: any script
/// can fire one, any WaypointEventGate can wait for one.
///
///   // anywhere in your game — your totem manager, a friend's script…:
///   WaypointEvents.Raise("AllTotemsCollected");
///
///   // any WaypointEventGate whose Game Event name is
///   // "AllTotemsCollected" opens at that exact moment.
///
/// RULES
///   • An event name fires exactly ONCE per scene lifetime — raising it
///     again does nothing (perfect for one-way story beats like "all
///     totems collected").
///   • WaypointEvents.HasFired("name") tells you (and late-arriving
///     gates) whether an event already happened.
///   • Scene reload forgets everything automatically (restart-safe in
///     builds too). For a restart WITHOUT reloading the scene, call
///     WaypointEvents.ResetAll().
///
/// v1.0 — initial release.
/// </summary>
public static class WaypointEvents
{
    private static readonly HashSet<string> firedEvents = new HashSet<string>();

    /// <summary>
    /// Fired the first time each event name is raised. Subscribe in
    /// OnEnable, unsubscribe in OnDisable — never leaks across scene
    /// reloads. The parameter is the event name.
    /// </summary>
    public static event Action<string> Raised;

    /// <summary>Internal: raised on ResetAll / scene unload so gates re-apply themselves.</summary>
    internal static event Action Cleared;

    /// <summary>True once this event name has been raised during the current scene lifetime.</summary>
    public static bool HasFired(string eventName)
    {
        return !string.IsNullOrEmpty(eventName) && firedEvents.Contains(eventName);
    }

    /// <summary>
    /// Raises a named game event. The FIRST call per name (per scene)
    /// fires the Raised event and latches HasFired(name) to true; further
    /// calls are ignored. Empty or null names are ignored.
    /// </summary>
    public static void Raise(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return;
        if (!firedEvents.Add(eventName)) return;
        if (Raised != null) Raised(eventName);
    }

    /// <summary>
    /// Forgets every fired event — call this when restarting a game
    /// WITHOUT reloading the scene. Every WaypointEventGate re-applies
    /// itself automatically.
    /// </summary>
    public static void ResetAll()
    {
        firedEvents.Clear();
        if (Cleared != null) Cleared();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init()
    {
        // Re-arm statics + the scene hook exactly once per play session
        // (also covers "Enter Play Mode without Domain Reload").
        firedEvents.Clear();
        Raised = null;
        Cleared = null;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private static void OnSceneUnloaded(Scene unloadedScene)
    {
        // Restart-safe: a reloaded scene starts with a clean event memory.
        ResetAll();
    }
}
