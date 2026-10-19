using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A UnityEvent that carries the enemy it fired for (drag in any method that
/// takes an EnemyController — VFX hooks like a hiss or a smoke burst).
/// </summary>
[Serializable]
public class SafeZoneEnemyEvent : UnityEvent<EnemyController>
{
}

/// <summary>
/// SAFE ZONE (v1.1) — the sanctuary circle: a place where the player can
/// breathe and the forest keeps its distance.
///
/// TWO INDEPENDENT EFFECTS (each can be switched off in the Inspector):
///
///   1. SPAWN PAUSE — while the PLAYER stands inside the zone, every
///      EnemySpawner in the scene is frozen (EnemySpawner v1.3's
///      PauseSpawning()/ResumeSpawning()): no auto spawns, no flashlight
///      respawns, no countdowns run — nothing appears while the player
///      rests. The moment the player steps out, the spawner re-counts its
///      Spawn Delay and the hunt resumes right when it expires.
///
///   2. ENEMY STUN — any ENEMY that enters the zone is treated EXACTLY
///      like the flashlight being held on it (EnemyController v2.4's
///      SetSafeZoneStun()): it freezes in the stun stage (red), runs the
///      disappear stage (blue), then flees and despawns. Enemies cannot
///      camp on the border, cannot follow the player inside, and cannot
///      threaten anyone standing in the circle. The call is repeated every
///      frame while the enemy remains inside — the exact contract
///      EnemyController v2.4 documents — and releasing it never breaks a
///      flashlight stun (the two inputs are OR-combined inside the enemy).
///
/// NO COLLIDER, NO SETUP:
///   This is NOT a trigger volume — it needs no collider, no Rigidbody, no
///   layer configuration. Drop it on an empty GameObject at ground level
///   and the zone simply IS:
///     • the PLAYER is inside when their tagged root is within 'Radius'
///       horizontally (the same center-distance measure the rest of the
///       project uses — ProximityText, EnemyController's stop distance),
///       optionally also within 'Height Range' vertically;
///     • every ENEMY is tracked through a live registry of all
///       EnemyControllers in the scene, refreshed every 'Rescan Interval'
///       seconds (new spawns are picked up within a second of appearing —
///       they spawn 20–30 m away, so there is always plenty of time), and
///       is inside under the same distance rule (with its own radius if
///       you want one).
///   Why not a trigger collider? A registry + distance test works with ANY
///   enemy prefab (Rigidbody or not, collider or not), needs zero physics
///   setup, never misses a fast body between physics steps, and the gizmo
///   shows EXACTLY the area that counts — the circle you tune is the
///   circle that applies.
///
/// OVERLAPPING ZONES ARE SAFE (and so is everything else):
///   • Multiple zones may overlap. Spawner pauses are reference-counted
///     inside EnemySpawner — each zone holds exactly its own pause, and
///     spawning only resumes when the LAST zone releases it.
///   • An enemy standing in two zones at once receives 'true' from both
///     every frame; leaving one while still inside the other is resolved
///     within the SAME frame, because this script runs at execution order
///     -20 — BEFORE the order-0 EnemyController reads its stun input. No
///     one-frame flicker, ever.
///   • The flashlight is never fought: the stun input inside EnemyController
///     is 'flashlight OR safe zone' — this zone releasing an enemy never
///     cuts a real flashlight stun short, and vice versa.
///
/// PAUSE MENU / ENDING / DESPAWNS — all handled:
///   • Frozen with the rest of the game while GamePauseManager's pause menu
///     is open (held pauses and stuns keep their last state, exactly like
///     ProximityText and the spawner). The cache re-scan keeps ticking on
///     unscaled time — it only refreshes lists, it never calls anything.
///   • The ending sequence can disable spawners and despawn every enemy
///     underneath this zone: destroyed enemies are pruned from the registry
///     silently, and a pause held on a disabled spawner is a harmless
///     counter that resolves whenever it is released.
///   • Switching the zone off (component or GameObject) releases EVERYTHING
///     it holds: spawner pauses, enemy stuns, and the player's 'inside'
///     state (the On Player Exited event fires). Nothing can get stuck.
///     Every state is also re-derived from raw inputs every frame, so
///     runtime Inspector changes apply instantly and safely.
///
/// VR-READY:
///   No camera, no input, no UI references — the zone reads world positions
///   only. It works unchanged for today's third-person build and for the VR
///   build later.
///
/// STATIC QUERY FOR YOUR HUD:
///   SafeZone.IsPlayerInAnySafeZone — true while the player stands in at
///   least one active zone. Poll it from any HUD script for a 'SAFE'
///   indicator; zero wiring needed.
///
/// SETUP (one step):
///   1. Create an empty GameObject at the campfire / cabin / clearing, at
///      GROUND level. Add Safe Zone. Set the Radius. Done.
///   Optional:
///   • a separate 'Enemy Radius' (e.g. slightly larger, so enemies are
///     repelled before they can loom at the border);
///   • 'Limit Height' + 'Height Range' for multi-level maps;
///   • drag specific EnemySpawners into 'Spawners' to control only those
///     (leave empty = every spawner in the scene is controlled);
///   • the four UnityEvents for feedback: campfire audio / light intensity
///     on player enter/exit, a hiss VFX when an enemy gets zapped.
///
/// UNITY 6 / URP / BUILT-IN / BOTH INPUT BACKENDS: uses no deprecated API
/// on ANY Unity version — the scene scans are version-aware (Unity 6's
/// current sort-free FindObjectsByType(FindObjectsInactive), Unity
/// 2023–2025's two-argument form, FindObjectsOfType on older Unity) — and
/// no rendering API is used at all, so it works everywhere.
/// Pairs with EnemyController v2.4+ and EnemySpawner v1.3+ (both already
/// in the project — their safe-zone hooks were built for this script).
///
/// v1.0 — initial release.
/// v1.1 — Unity 6000.5 API fix: the scene scans used the single-argument
///        FindObjectsByType(FindObjectsSortMode) overload, which Unity
///        deprecated in 6000.4 — it compiles as a CS0618 warning on this
///        project's Unity 6000.5 (the same deprecation InteractionPoint
///        v3.0 already fixed for its own scan). Both scans now use the
///        version-aware overload set described above. NO behavior change:
///        they still find active objects only.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-20)]   // after the early pickup/input scripts (-50/-40),
                               // BEFORE the order-0 controllers (EnemyController,
                               // EnemySpawner) — a zone's stun and pause inputs
                               // are final before anything reads them this frame.
                               // All zones run here, so an enemy standing in two
                               // zones gets its last write before its first read.
[AddComponentMenu("Gameplay/Safe Zone (sanctuary — pauses spawning, repels enemies)")]
public class SafeZone : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Area")]

    [Tooltip("Radius of the sanctuary in meters (horizontal distance from the zone's center to the player's root). Select the zone in the Scene view to see the circle.")]
    [SerializeField, Min(0.1f)] private float radius = 5f;

    [Tooltip("Use the same radius for the enemy stun check as for the player. Turn OFF to give enemies their own radius (e.g. slightly larger, so enemies are repelled before they can loom at the border).")]
    [SerializeField] private bool enemyRadiusMatchesPlayer = true;

    [Tooltip("Own radius for the enemy stun check (only used when 'Enemy Radius Matches Player' is OFF).")]
    [SerializeField, Min(0.1f)] private float enemyRadius = 6f;

    [Tooltip("Also require a vertical distance limit (multi-level maps). OFF = the zone counts at any height (the same flat-disc measure ProximityText uses).")]
    [SerializeField] private bool limitHeight = false;

    [Tooltip("How far above/below the zone's center something may be and still count as inside (only used while 'Limit Height' is ON).")]
    [SerializeField, Min(0.1f)] private float heightRange = 3f;

    [Header("Player")]

    [Tooltip("The player the zone measures. Auto-found by tag when left empty.")]
    [SerializeField] private Transform playerTransform;

    [Tooltip("Tag used to find the player automatically. Tag the player's ROOT 'Player' (Tags & Layers).")]
    [SerializeField] private string playerTag = "Player";

    [Header("Spawner Control")]

    [Tooltip("While the player stands inside, every controlled EnemySpawner freezes: no auto spawns, no flashlight respawns, no countdowns run. On exit the spawner re-counts its Spawn Delay and resumes.")]
    [SerializeField] private bool pauseSpawning = true;

    [Tooltip("Which spawners this zone controls. EMPTY = every EnemySpawner in the scene (auto, re-found periodically). Assign specific ones to control only those.")]
    [SerializeField] private List<EnemySpawner> spawners = new List<EnemySpawner>();

    [Header("Enemy Control")]

    [Tooltip("Enemies that enter the zone are stunned exactly like a flashlight hit (red freeze → blue disappear → flee → despawn). Turn OFF for a zone that ONLY pauses spawning (e.g. a hiding spot).")]
    [SerializeField] private bool stunEnemies = true;

    [Header("Events (feedback hooks)")]

    [Tooltip("Fires the frame the player enters the zone (also fires on the first frame if the game starts with the player inside). Hook campfire audio / light here.")]
    [SerializeField] private UnityEvent onPlayerEntered = new UnityEvent();

    [Tooltip("Fires the frame the player leaves the zone (or the zone is switched off while they are inside).")]
    [SerializeField] private UnityEvent onPlayerExited = new UnityEvent();

    [Tooltip("Fires when an enemy enters the zone's area (carries that enemy — hook a hiss / smoke VFX).")]
    [SerializeField] private SafeZoneEnemyEvent onEnemyEntered = new SafeZoneEnemyEvent();

    [Tooltip("Fires when a live enemy leaves the zone's area on its own (carries that enemy). An enemy that despawns inside the zone does NOT fire this — it is gone.")]
    [SerializeField] private SafeZoneEnemyEvent onEnemyExited = new SafeZoneEnemyEvent();

    [Header("Maintenance")]

    [Tooltip("Freeze with the rest of the game while GamePauseManager's pause menu is open (held pauses and stuns keep their last state).")]
    [SerializeField] private bool respectPause = true;

    [Tooltip("Re-scan the scene for spawners and enemies every N seconds (new spawns are picked up within one interval — they appear 20–30 m away, so there is always plenty of time). 0 = scan only once when the zone is enabled.")]
    [SerializeField, Min(0f)] private float rescanInterval = 1f;

    [Tooltip("Write enter/leave transitions and pause holds to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    // Every EnemySpawner this zone knows (manual list, or the auto scene scan).
    private readonly List<EnemySpawner> spawnerCache = new List<EnemySpawner>();
    // The spawners this zone currently holds a pause on (exactly one
    // PauseSpawning() each — the counterpart ResumeSpawning() is guaranteed).
    private readonly List<EnemySpawner> pausedSpawners = new List<EnemySpawner>();

    // The live enemy registry (every active EnemyController in the scene).
    private readonly List<EnemyController> enemyCache = new List<EnemyController>();
    // The enemies currently inside this zone's area (for events + releases).
    private readonly List<EnemyController> insideEnemies = new List<EnemyController>();

    // All enabled zones — backs the static IsPlayerInAnySafeZone query.
    private static readonly List<SafeZone> activeZones = new List<SafeZone>();

    private bool playerInside;
    private float rescanTimer;
    private float playerSearchTimer;
    private bool refreshPending;     // set by OnEnable — the first Update re-scans
    private bool warnedNoPlayer;
    private bool warnedNoSpawner;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>True while the player stands inside this zone (live, read-only).</summary>
    public bool IsPlayerInside
    {
        get { return playerInside; }
    }

    /// <summary>True while this zone holds a spawn pause on at least one spawner.</summary>
    public bool IsSpawnerPauseHeld
    {
        get { return pausedSpawners.Count > 0; }
    }

    /// <summary>How many enemies are currently inside this zone's area.</summary>
    public int EnemiesInsideCount
    {
        get { return insideEnemies.Count; }
    }

    /// <summary>
    /// The zone's radius in meters. Assignable from code at runtime — the
    /// next frame re-evaluates everything against the new circle.
    /// </summary>
    public float Radius
    {
        get { return radius; }
        set { radius = Mathf.Max(0.1f, value); }
    }

    /// <summary>
    /// True while the player stands inside at least one ACTIVE zone in the
    /// scene. Poll this from any HUD script for a 'SAFE' indicator — zero
    /// wiring needed. (Destroys/deactivates cleanly: zones unregister
    /// themselves the moment they switch off.)
    /// </summary>
    public static bool IsPlayerInAnySafeZone
    {
        get
        {
            for (int i = 0; i < activeZones.Count; i++)
            {
                SafeZone zone = activeZones[i];
                if (zone != null && zone.isActiveAndEnabled && zone.playerInside) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Re-scan spawners and enemies and re-apply the current state
    /// immediately — the same thing the component's context menu does.
    /// </summary>
    public void RefreshNow()
    {
        RescanNow();
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        activeZones.Add(this);
        refreshPending = true;   // the first Update scans with fresh eyes
    }

    private void Start()
    {
        // Immediate first search — the Update retry takes over if the player
        // does not exist yet (enemy stunning works without the player; only
        // the spawn pause needs them).
        TryFindPlayer();
    }

    private void Update()
    {
        // A re-enable (or the very first frame) re-scans before anything else.
        if (refreshPending)
        {
            refreshPending = false;
            RefreshCaches();
            rescanTimer = 0f;
        }

        // Periodic re-scan on UNSCALED time — deliberately still ticking while
        // the pause menu is open: it only REFRESHES the caches, it never calls
        // anything, so the game stays frozen while the zone still wakes up
        // with fresh lists the moment play resumes.
        if (rescanInterval > 0f)
        {
            rescanTimer += Time.unscaledDeltaTime;
            if (rescanTimer >= rescanInterval)
            {
                rescanTimer = 0f;
                RefreshCaches();
            }
        }

        // Frozen with the rest of the game while the pause menu is open —
        // held pauses and stuns keep their last state, exactly like
        // ProximityText and the EnemySpawner itself.
        if (respectPause && GamePauseManager.IsPaused) return;

        EvaluatePlayer();
        ReconcileSpawnerPause();
        EvaluateEnemies();
    }

    private void OnDisable()
    {
        activeZones.Remove(this);
        ReleaseEverything();
    }

    /// <summary>Clamps Inspector values so the zone can never receive nonsense input.</summary>
    private void OnValidate()
    {
        radius = Mathf.Max(0.1f, radius);
        enemyRadius = Mathf.Max(0.1f, enemyRadius);
        heightRange = Mathf.Max(0.1f, heightRange);
        rescanInterval = Mathf.Max(0f, rescanInterval);
        if (string.IsNullOrEmpty(playerTag)) playerTag = "Player";
    }

    // ------------------------------------------------------------------ //
    // Player
    // ------------------------------------------------------------------ //

    private void EvaluatePlayer()
    {
        if (!ResolvePlayer())
        {
            // No player (yet / anymore). If we were inside, that state is over.
            if (playerInside) SetPlayerInside(false);
            return;
        }

        bool inside = IsInsidePlayerArea(playerTransform.position);
        if (inside != playerInside) SetPlayerInside(inside);
    }

    private void SetPlayerInside(bool inside)
    {
        playerInside = inside;

        if (debugLogs)
            Debug.Log("[SafeZone] '" + name + "': player " + (inside ? "ENTERED the zone." : "left the zone."), this);

        if (inside) onPlayerEntered.Invoke();
        else onPlayerExited.Invoke();
    }

    /// <summary>True when a world position is inside the player check area (horizontal disc, optional height limit).</summary>
    private bool IsInsidePlayerArea(Vector3 position)
    {
        Vector3 center = transform.position;
        float dx = position.x - center.x;
        float dz = position.z - center.z;
        if (dx * dx + dz * dz > radius * radius) return false;

        if (limitHeight && Mathf.Abs(position.y - center.y) > heightRange) return false;

        return true;
    }

    /// <summary>True when a world position is inside the enemy check area (own radius when configured, otherwise the same circle as the player).</summary>
    private bool IsInsideEnemyArea(Vector3 position)
    {
        float r = EffectiveEnemyRadius();
        Vector3 center = transform.position;
        float dx = position.x - center.x;
        float dz = position.z - center.z;
        if (dx * dx + dz * dz > r * r) return false;

        if (limitHeight && Mathf.Abs(position.y - center.y) > heightRange) return false;

        return true;
    }

    private float EffectiveEnemyRadius()
    {
        return enemyRadiusMatchesPlayer ? radius : enemyRadius;
    }

    // ------------------------------------------------------------------ //
    // Spawner pause bookkeeping (reconciled every frame — self-healing)
    // ------------------------------------------------------------------ //

    private void ReconcileSpawnerPause()
    {
        bool wantPause = pauseSpawning && playerInside;

        if (wantPause)
        {
            // Pause every known spawner we do not already hold. Re-checked
            // (not one-shot) so a spawner discovered at runtime — or this
            // zone being re-enabled — applies the pause the very frame it
            // applies. Overlapping zones each hold their own pause
            // (reference-counted inside EnemySpawner).
            for (int i = spawnerCache.Count - 1; i >= 0; i--)
            {
                EnemySpawner spawner = spawnerCache[i];

                if (spawner == null)   // destroyed since the scan
                {
                    spawnerCache.RemoveAt(i);
                    continue;
                }

                if (pausedSpawners.Contains(spawner)) continue;

                spawner.PauseSpawning();
                pausedSpawners.Add(spawner);

                if (debugLogs)
                    Debug.Log("[SafeZone] '" + name + "': spawn pause held on '" + spawner.name + "'.", this);
            }
        }
        else if (pausedSpawners.Count > 0)
        {
            // Release everything we hold — the player left, the feature was
            // switched off at runtime, or the zone's state says pause no
            // longer applies. The LAST release makes the spawner re-count
            // its Spawn Delay (the documented v1.3 behaviour).
            for (int i = 0; i < pausedSpawners.Count; i++)
            {
                EnemySpawner spawner = pausedSpawners[i];
                if (spawner == null) continue;   // destroyed while held — its counter died with it

                spawner.ResumeSpawning();

                if (debugLogs)
                    Debug.Log("[SafeZone] '" + name + "': spawn pause released on '" + spawner.name + "'.", this);
            }
            pausedSpawners.Clear();
        }
    }

    // ------------------------------------------------------------------ //
    // Enemy stun bookkeeping
    // ------------------------------------------------------------------ //

    private void EvaluateEnemies()
    {
        bool wantStun = stunEnemies;

        // ---- main pass: every enemy on the live registry ----
        for (int i = enemyCache.Count - 1; i >= 0; i--)
        {
            EnemyController enemy = enemyCache[i];

            if (enemy == null)   // destroyed since the scan
            {
                enemyCache.RemoveAt(i);
                continue;
            }

            if (!enemy.isActiveAndEnabled)
            {
                // Deactivated while inside (pooled / taken over by another
                // system): release our stun claim and report the exit.
                if (insideEnemies.Contains(enemy))
                {
                    enemy.SetSafeZoneStun(false);
                    insideEnemies.Remove(enemy);
                    if (debugLogs)
                        Debug.Log("[SafeZone] '" + name + "': enemy '" + enemy.name + "' left the zone (deactivated).", this);
                    onEnemyExited.Invoke(enemy);
                }
                continue;
            }

            bool inside = IsInsideEnemyArea(enemy.transform.position);

            if (inside)
            {
                // The documented v2.4 contract: called EVERY FRAME while the
                // enemy is inside. Idempotent inside EnemyController, and it
                // is OR-combined with the flashlight there — this can never
                // fight the player's light.
                enemy.SetSafeZoneStun(wantStun);

                if (!insideEnemies.Contains(enemy))
                {
                    insideEnemies.Add(enemy);
                    if (debugLogs)
                        Debug.Log("[SafeZone] '" + name + "': enemy '" + enemy.name + "' entered the zone" +
                                  (wantStun ? " — stunning." : " (stun disabled)."), this);
                    onEnemyEntered.Invoke(enemy);
                }
            }
            else if (insideEnemies.Contains(enemy))
            {
                enemy.SetSafeZoneStun(false);
                insideEnemies.Remove(enemy);
                if (debugLogs)
                    Debug.Log("[SafeZone] '" + name + "': enemy '" + enemy.name + "' left the zone.", this);
                onEnemyExited.Invoke(enemy);
            }
        }

        // ---- sweep pass: enemies that were inside but left the registry ----
        // The only live case: deactivated between scans and dropped by a
        // re-scan before the main pass could see it. Destroyed ones are
        // fake-null and are pruned silently (nothing to release, no event —
        // the enemy is gone).
        for (int i = insideEnemies.Count - 1; i >= 0; i--)
        {
            EnemyController enemy = insideEnemies[i];

            if (enemy == null)
            {
                insideEnemies.RemoveAt(i);
                continue;
            }

            if (enemyCache.Contains(enemy)) continue;   // the main pass owns this one

            enemy.SetSafeZoneStun(false);
            insideEnemies.RemoveAt(i);
            if (debugLogs)
                Debug.Log("[SafeZone] '" + name + "': enemy '" + enemy.name + "' left the zone (deactivated).", this);
            onEnemyExited.Invoke(enemy);
        }
    }

    // ------------------------------------------------------------------ //
    // Registry / caches
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Re-reads which spawners this zone controls and which enemies exist.
    /// Never calls anything on them — safe to run at any time, including
    /// while the game is paused.
    /// </summary>
    private void RefreshCaches()
    {
        // ---- spawners: the manual list wins; empty = every spawner in the scene ----
        spawnerCache.Clear();

        if (spawners != null && spawners.Count > 0)
        {
            for (int i = 0; i < spawners.Count; i++)
            {
                EnemySpawner spawner = spawners[i];
                if (spawner != null) spawnerCache.Add(spawner);
            }
        }
        else
        {
            EnemySpawner[] found = FindAllActiveSpawners();
            for (int i = 0; i < found.Length; i++) spawnerCache.Add(found[i]);

            if (found.Length == 0 && pauseSpawning && !warnedNoSpawner)
            {
                warnedNoSpawner = true;
                Debug.LogWarning("[SafeZone] '" + name + "': no EnemySpawner found in the scene — the spawn pause " +
                                 "has nothing to control (enemy stunning works without one). Add an EnemySpawner, drag " +
                                 "one into 'Spawners', or ignore this if the zone is only meant to stun.", this);
            }
        }

        // ---- enemies: the live registry (every active EnemyController) ----
        enemyCache.Clear();
        EnemyController[] enemies = FindAllActiveEnemies();
        for (int i = 0; i < enemies.Length; i++) enemyCache.Add(enemies[i]);
    }

    // ------------------------------------------------------------------ //
    // Version-aware scene scans — the same overload set InteractionPoint
    // v3.0 uses. Unity 6000.4 deprecated every FindObjectsByType overload
    // that takes a FindObjectsSortMode (CS0618 on this project's 6000.5);
    // the sort-free FindObjectsByType(FindObjectsInactive) is the current
    // Unity 6 API. Every branch below finds ACTIVE objects only — exactly
    // what the scans always found.
    // ------------------------------------------------------------------ //

    /// <summary>Every active EnemySpawner in the scene.</summary>
    private static EnemySpawner[] FindAllActiveSpawners()
    {
#if UNITY_6000_0_OR_NEWER
        return FindObjectsByType<EnemySpawner>(FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<EnemySpawner>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<EnemySpawner>();
#endif
    }

    /// <summary>Every active EnemyController in the scene.</summary>
    private static EnemyController[] FindAllActiveEnemies()
    {
#if UNITY_6000_0_OR_NEWER
        return FindObjectsByType<EnemyController>(FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<EnemyController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<EnemyController>();
#endif
    }

    // ------------------------------------------------------------------ //
    // Player finding (retry quietly until the player exists)
    // ------------------------------------------------------------------ //

    private bool ResolvePlayer()
    {
        if (playerTransform != null) return true;   // also survives destroy: Unity fake-null

        playerSearchTimer += Time.unscaledDeltaTime;
        if (playerSearchTimer < 0.5f) return false;   // retry every 0.5 seconds only
        playerSearchTimer = 0f;

        TryFindPlayer();
        return playerTransform != null;
    }

    private void TryFindPlayer()
    {
        if (playerTransform != null) return;

        GameObject player = FindPlayerByTag();
        if (player != null)
        {
            playerTransform = player.transform;
            return;
        }

        if (!warnedNoPlayer)
        {
            warnedNoPlayer = true;
            Debug.LogWarning("[SafeZone] '" + name + "': no GameObject tagged '" + playerTag + "' found yet. Tag the " +
                             "player's ROOT '" + playerTag + "' (or drag it into 'Player Transform'). Retrying every " +
                             "0.5 s — enemy stunning works without the player, only the spawn pause needs them.", this);
        }
    }

    /// <summary>Tag lookup that survives a tag that does not exist in the project yet.</summary>
    private GameObject FindPlayerByTag()
    {
        if (string.IsNullOrEmpty(playerTag)) return null;

        try
        {
            return GameObject.FindGameObjectWithTag(playerTag);
        }
        catch
        {
            return null;   // the tag is not defined — the warning above already said it
        }
    }

    // ------------------------------------------------------------------ //
    // Release (zone switched off / destroyed — nothing can get stuck)
    // ------------------------------------------------------------------ //

    private void ReleaseEverything()
    {
        // Release every spawner pause we hold — the exact counterpart of our
        // PauseSpawning() calls. The last release makes each spawner re-count
        // its Spawn Delay, exactly like a normal player exit.
        for (int i = 0; i < pausedSpawners.Count; i++)
        {
            EnemySpawner spawner = pausedSpawners[i];
            if (spawner != null) spawner.ResumeSpawning();
        }
        pausedSpawners.Clear();

        // Release every enemy we hold in the stun stage (EnemyController
        // resumes whatever it was doing — or keeps its flashlight stun).
        for (int i = 0; i < insideEnemies.Count; i++)
        {
            EnemyController enemy = insideEnemies[i];
            if (enemy != null) enemy.SetSafeZoneStun(false);
        }
        insideEnemies.Clear();

        // The player's 'safe' state ends with the zone.
        if (playerInside)
        {
            playerInside = false;
            if (debugLogs)
                Debug.Log("[SafeZone] '" + name + "': zone switched off — player state released.", this);
            onPlayerExited.Invoke();
        }
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    [ContextMenu("Rescan Now")]
    private void RescanNow()
    {
        RefreshCaches();
        TryFindPlayer();
        EvaluatePlayer();
        ReconcileSpawnerPause();
        EvaluateEnemies();

        Debug.Log("[SafeZone] '" + name + "' re-scanned: " + spawnerCache.Count + " spawner(s), " +
                  enemyCache.Count + " enemy/enemies, player " + (playerInside ? "INSIDE." : "outside."), this);
    }

    /// <summary>Scene-view gizmo: the sanctuary circle, the enemy circle, the optional height limit, and links to the controlled spawners.</summary>
    private void OnDrawGizmosSelected()
    {
        Vector3 center = transform.position;

        // The player area: a green disc on the ground plane through the zone.
        // While playing, the center marker grows while the player is inside —
        // you can SEE the sanctuary being active from across the Scene view.
        Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.9f);
        DrawWireDisc(center, radius, 48);
        Gizmos.DrawWireSphere(center, playerInside ? 0.4f : 0.25f);

        // The enemy area: orange, slightly raised so both stay readable when
        // the radii differ. Hidden while it matches the player radius (the
        // green circle already IS the enemy circle then).
        if (!enemyRadiusMatchesPlayer)
        {
            Gizmos.color = new Color(1f, 0.6f, 0.15f, 0.9f);
            DrawWireDisc(center + Vector3.up * 0.05f, enemyRadius, 48);
        }

        // Optional vertical limit: a wire cylinder around the zone.
        if (limitHeight)
        {
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.35f);
            Vector3 top = center + Vector3.up * heightRange;
            Vector3 bottom = center - Vector3.up * heightRange;
            DrawWireDisc(top, radius, 48);
            DrawWireDisc(bottom, radius, 48);
            Gizmos.DrawLine(top + new Vector3(radius, 0f, 0f), bottom + new Vector3(radius, 0f, 0f));
            Gizmos.DrawLine(top - new Vector3(radius, 0f, 0f), bottom - new Vector3(radius, 0f, 0f));
            Gizmos.DrawLine(top + new Vector3(0f, 0f, radius), bottom + new Vector3(0f, 0f, radius));
            Gizmos.DrawLine(top - new Vector3(0f, 0f, radius), bottom - new Vector3(0f, 0f, radius));
        }

        // Thin lines to the spawners this zone controls — the manual list in
        // edit mode, the live cache while playing (select the zone during
        // Play to verify the auto-found links at a glance).
        DrawSpawnerLinks(center, spawners != null && spawners.Count > 0 ? spawners : spawnerCache);
    }

    private static void DrawSpawnerLinks(Vector3 center, List<EnemySpawner> list)
    {
        if (list == null || list.Count == 0) return;

        Gizmos.color = new Color(0.85f, 0.85f, 0.85f, 0.5f);
        for (int i = 0; i < list.Count; i++)
        {
            EnemySpawner spawner = list[i];
            if (spawner == null) continue;

            Gizmos.DrawLine(center, spawner.transform.position);
            Gizmos.DrawWireCube(spawner.transform.position, new Vector3(0.5f, 0.5f, 0.5f));
        }
    }

    private static void DrawWireDisc(Vector3 center, float discRadius, int segments)
    {
        Vector3 prev = center + new Vector3(discRadius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * discRadius, 0f, Mathf.Sin(angle) * discRadius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
