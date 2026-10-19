using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns the single reusable enemy prefab on a ring around the player, always
/// outside the player's sight – similar to Minecraft mob spawning:
///
///   1. Pick a random point between Min/Max Spawn Distance from the player
///      (uniformly distributed over the ring).
///   2. Reject any candidate that lies inside the camera's view frustum
///      (plus a safety buffer that approximates the enemy's size) – unless
///      solid geometry fully blocks the line of sight, in which case the
///      player cannot actually see the spawn anyway.
///   3. Snap the spawn point onto the ground below — v1.2: ONLY onto colliders
///      tagged "Ground" (tag adjustable). Candidates with no tagged ground
///      underneath are invalid, so the enemy can never appear on props, walls
///      or floating in the air.
///
/// v1.1 – Respawn after flashlight kills: when an enemy finishes its
/// disappear stage it fires OnDespawned and destroys itself; this spawner
/// then rolls a random cooldown (12–18s by default, adjustable) and respawns
/// the enemy out of sight when it expires. The respawn happens even when
/// Auto Spawn is off, because the enemy lifecycle guarantees a comeback.
///
/// v1.2 – Ground-only spawning (tag "Ground") and a full freeze while the
/// pause menu is open (GamePauseManager).
///
/// v1.3 – SAFE ZONE support + SPAWN DELAY:
///   • Spawn Delay (default 5 s, Inspector): counted ONCE at game start and
///     re-counted every time the player leaves a Safe Zone. While it counts,
///     NOTHING spawns — the auto interval, Spawn On Start and flashlight
///     respawns all wait for it. "Spawn On Start" now fires at the END of the
///     start delay (the first enemy appears 5 s into the game by default).
///   • PauseSpawning()/ResumeSpawning() (called by the SafeZone plug-in):
///     while the player stands inside a Safe Zone, every spawn source is
///     frozen. Leaving the zone re-counts the Spawn Delay, then spawning
///     resumes — the enemy re-engages right when the delay expires.
///     Several zones can overlap safely (reference-counted pause).
///
/// Every tuning value is exposed in the Inspector. Use the component's
/// context menu (gear icon) to "Spawn Enemy Now" while testing.
/// </summary>
[DisallowMultipleComponent]
public class EnemySpawner : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Enemy Prefab")]

    [Tooltip("The single reusable enemy prefab (must have EnemyController on its root).")]
    [SerializeField] private GameObject enemyPrefab;

    [Header("Player & Camera")]

    [Tooltip("Player transform to spawn around. Auto-found via the 'Player' tag if left empty.")]
    [SerializeField] private Transform player;

    [Tooltip("Camera used for the visibility check. Auto-found via Camera.main if left empty.")]
    [SerializeField] private Camera playerCamera;

    [Header("Spawn Ring (around the player)")]

    [Tooltip("Minimum spawn distance from the player.")]
    [SerializeField] private float minSpawnDistance = 20f;

    [Tooltip("Maximum spawn distance from the player.")]
    [SerializeField] private float maxSpawnDistance = 30f;

    [Header("Respawn After Flashlight Kill")]

    [Tooltip("Minimum seconds the enemy stays gone after the flashlight makes it disappear.")]
    [SerializeField] private float respawnCooldownMin = 12f;

    [Tooltip("Maximum seconds the enemy stays gone after a flashlight kill. " +
             "A random value between Min and Max is rolled every single time.")]
    [SerializeField] private float respawnCooldownMax = 18f;

    [Header("Spawn Rules")]

    [Tooltip("Spawn a new enemy automatically every Spawn Interval seconds.")]
    [SerializeField] private bool autoSpawn = true;

    [Tooltip("Seconds between automatic spawns.")]
    [SerializeField] private float spawnInterval = 5f;

    [Tooltip("Spawn one enemy as soon as the Start Delay below has counted " +
             "down (instead of waiting for the first full Spawn Interval).")]
    [SerializeField] private bool spawnOnStart = true;

    [Tooltip("Maximum enemies alive at the same time. 0 = unlimited.")]
    [SerializeField] private int maxAliveEnemies = 1;

    [Tooltip("Random position attempts per spawn before falling back / skipping.")]
    [SerializeField] private int maxSpawnAttempts = 20;

    [Header("Spawn Delay (game start & safe zones)")]

    [Tooltip("Seconds the spawner waits before ANY spawn is allowed — counted " +
             "once at game start and re-counted every time the player leaves a " +
             "Safe Zone (Safe Zones pause spawning while the player is inside " +
             "them). While the delay counts, nothing spawns: the auto interval, " +
             "Spawn On Start and flashlight respawns all wait for it. " +
             "0 = no delay.")]
    [SerializeField] private float spawnDelay = 5f;

    [Tooltip("If no out-of-sight position can be found, spawn at a visible tagged-ground position " +
             "anyway (with a warning) so the system never silently stops working. The enemy still " +
             "never spawns off the ground.")]
    [SerializeField] private bool fallbackWhenNoSafeSpot = true;

    [Header("Visibility Safety (Minecraft-style)")]

    [Tooltip("Never spawn inside the camera's view frustum (unless fully occluded).")]
    [SerializeField] private bool avoidCameraView = true;

    [Tooltip("Safety margin around the spawn point when testing visibility. " +
             "Approximates the enemy's radius (0.5 = 1x1x1 placeholder cube).")]
    [SerializeField] private float visibilityBufferRadius = 0.5f;

    [Tooltip("Also raycast from the camera to the spawn point. If solid geometry blocks " +
             "the line of sight, spawning there is allowed even if it is on-screen.")]
    [SerializeField] private bool checkLineOfSight = true;

    [Tooltip("Layers that count as vision blockers (exclude the Player layer to avoid self-hits).")]
    [SerializeField] private LayerMask occlusionMask = ~0;

    [Header("Ground Placement")]

    [Tooltip("Raycast down to place the enemy on the ground below the chosen ring position. " +
             "Keep ON – this is what enforces 'spawn only on ground'.")]
    [SerializeField] private bool snapToGround = true;

    [Tooltip("The enemy ONLY spawns where the downward ray hits a collider with this tag. " +
             "Create the tag 'Ground' in Edit > Project Settings > Tags and Layers, then tag " +
             "your floor / terrain colliders with it.")]
    [SerializeField] private string groundTag = "Ground";

    [Tooltip("Height above the ground point where the enemy's pivot sits (0.5 = half a 1-unit cube).")]
    [SerializeField] private float spawnHeightOffset = 0.5f;

    [Tooltip("How far above the ring position the ground ray starts.")]
    [SerializeField] private float groundRaycastHeight = 10f;

    [Tooltip("Layers considered 'ground' for the snap raycast.")]
    [SerializeField] private LayerMask groundMask = ~0;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private readonly List<GameObject> aliveEnemies = new List<GameObject>();
    private float spawnTimer;

    // v1.1: flashlight-kill respawn queue. One entry per killed enemy; each
    // gets its own random cooldown roll.
    private int pendingFlashlightRespawns;
    private float flashlightRespawnCountdown;

    // v1.2: one-time warning when no 'Ground'-tagged collider can be found.
    private bool warnedNoGround;

    // v1.3: safe-zone pause bookkeeping (reference-counted: overlapping zones
    // each hold one reference) + the spawn-delay state machine.
    private int safeZonePauseCount;
    private float spawnDelayCountdown;
    private bool startDelayPending;    // fire the "Spawn On Start" spawn when the delay ends
    private bool resumeDelayPending;   // re-engage right when a post-zone delay ends

    /// <summary>Number of enemies currently alive (destroyed ones are pruned automatically).</summary>
    public int AliveCount
    {
        get { PruneDestroyed(); return aliveEnemies.Count; }
    }

    /// <summary>
    /// Seconds left until the flashlight-killed enemy respawns
    /// (-1 when no flashlight respawn is pending). Read-only — handy for a HUD.
    /// </summary>
    public float FlashlightRespawnCountdown
    {
        get { return pendingFlashlightRespawns > 0 ? Mathf.Max(0f, flashlightRespawnCountdown) : -1f; }
    }

    /// <summary>v1.3: True while at least one Safe Zone holds the player — spawning is fully paused.</summary>
    public bool IsSpawningPaused
    {
        get { return safeZonePauseCount > 0; }
    }

    /// <summary>v1.3: True while the game-start / post-Safe-Zone spawn delay is counting down.</summary>
    public bool IsSpawnDelayActive
    {
        get { return spawnDelayCountdown > 0f; }
    }

    /// <summary>
    /// v1.3: Seconds left before spawning is allowed again
    /// (-1 when no delay is active). Read-only — handy for a HUD.
    /// </summary>
    public float SpawnDelayCountdown
    {
        get { return spawnDelayCountdown > 0f ? spawnDelayCountdown : -1f; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (player == null)
        {
            GameObject found = GameObject.FindGameObjectWithTag("Player");
            if (found != null) player = found.transform;
        }

        if (playerCamera == null) playerCamera = Camera.main;
    }

    private void Start()
    {
        if (enemyPrefab == null)
        {
            Debug.LogError("[EnemySpawner] No Enemy Prefab assigned on '" + name + "'. Disabling spawner.", this);
            enabled = false;
            return;
        }

        if (player == null)
        {
            Debug.LogError("[EnemySpawner] No Player assigned and no GameObject tagged 'Player' found. Disabling spawner.", this);
            enabled = false;
            return;
        }

        if (playerCamera == null && avoidCameraView)
        {
            Debug.LogWarning("[EnemySpawner] No camera found (tagged MainCamera). " +
                             "Visibility check will be skipped – enemies may spawn on screen.", this);
        }

        // v1.3: the game-start Spawn Delay. No enemy appears until it has
        // fully counted down — "Spawn On Start" now fires at the END of the
        // delay (with a 5 s delay the first enemy appears 5 s into the game).
        spawnDelayCountdown = Mathf.Max(0f, spawnDelay);
        startDelayPending = spawnOnStart;
        if (spawnDelayCountdown <= 0f) FinishSpawnDelay();

        spawnTimer = 0f;
    }

    private void Update()
    {
        // v1.2: fully frozen while the pause menu is open (interval, respawn
        // cooldowns — nothing ticks while the game is paused).
        if (GamePauseManager.IsPaused) return;

        PruneDestroyed();

        // v1.3: SAFE ZONE PAUSE — while at least one Safe Zone holds the
        // player, everything freezes: no interval spawns, no flashlight
        // respawns, no delay countdowns. Nothing spawns, period.
        if (safeZonePauseCount > 0)
        {
            spawnTimer = 0f;   // the auto interval restarts after the pause
            return;
        }

        // v1.3: SPAWN DELAY — counted once at game start and re-counted every
        // time the player leaves a Safe Zone. Nothing spawns while it counts.
        if (spawnDelayCountdown > 0f)
        {
            spawnDelayCountdown -= Time.deltaTime;
            spawnTimer = 0f;   // the auto interval restarts after the delay
            if (spawnDelayCountdown > 0f) return;

            FinishSpawnDelay();
            return;
        }

        // v1.1: flashlight-kill respawn countdown. Runs independently of Auto
        // Spawn — the enemy lifecycle guarantees a comeback after the cooldown.
        if (pendingFlashlightRespawns > 0)
        {
            flashlightRespawnCountdown -= Time.deltaTime;

            if (flashlightRespawnCountdown <= 0f)
            {
                TrySpawnEnemy();

                pendingFlashlightRespawns--;
                if (pendingFlashlightRespawns > 0)
                {
                    RollRespawnCooldown(); // next queued kill gets a fresh random roll
                }
            }
            else
            {
                spawnTimer = 0f; // the auto-spawn interval restarts after the respawn
            }

            return;
        }

        if (!autoSpawn) return;

        if (maxAliveEnemies > 0 && aliveEnemies.Count >= maxAliveEnemies)
        {
            // Restart the countdown so the next spawn happens a full interval
            // after a slot opens up.
            spawnTimer = 0f;
            return;
        }

        spawnTimer += Time.deltaTime;
        if (spawnTimer >= spawnInterval)
        {
            spawnTimer = 0f;
            TrySpawnEnemy();
        }
    }

    /// <summary>Clamps Inspector values so the spawner can never receive nonsense input.</summary>
    private void OnValidate()
    {
        minSpawnDistance = Mathf.Max(1f, minSpawnDistance);
        if (maxSpawnDistance < minSpawnDistance) maxSpawnDistance = minSpawnDistance;
        respawnCooldownMin = Mathf.Max(0f, respawnCooldownMin);
        if (respawnCooldownMax < respawnCooldownMin) respawnCooldownMax = respawnCooldownMin;
        spawnInterval = Mathf.Max(0.1f, spawnInterval);
        spawnDelay = Mathf.Max(0f, spawnDelay);
        maxSpawnAttempts = Mathf.Max(1, maxSpawnAttempts);
        visibilityBufferRadius = Mathf.Max(0f, visibilityBufferRadius);
        groundRaycastHeight = Mathf.Max(1f, groundRaycastHeight);
        spawnHeightOffset = Mathf.Max(0f, spawnHeightOffset);
        if (maxAliveEnemies < 0) maxAliveEnemies = 0;
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Tries to spawn one enemy at an out-of-sight position around the player.
    /// Respects Max Alive Enemies. Call this from your own gameplay events too.
    /// </summary>
    [ContextMenu("Spawn Enemy Now")]
    public void TrySpawnEnemy()
    {
        PruneDestroyed();

        if (enemyPrefab == null || player == null) return;
        if (maxAliveEnemies > 0 && aliveEnemies.Count >= maxAliveEnemies) return;

        Vector3? safeSpot = FindSafeSpawnPosition();

        if (safeSpot.HasValue)
        {
            SpawnAt(safeSpot.Value);
            return;
        }

        // v1.2: ground is a hard requirement, so even the last-resort fallback
        // only spawns on tagged ground – visibility is what we give up on.
        Vector3? groundOnlySpot = FindAnyGroundPosition();

        if (!groundOnlySpot.HasValue)
        {
            WarnNoGroundOnce();
            return;
        }

        if (fallbackWhenNoSafeSpot)
        {
            Debug.LogWarning("[EnemySpawner] No out-of-sight position found after " + maxSpawnAttempts +
                             " attempts – spawning at a visible ground position.", this);
            SpawnAt(groundOnlySpot.Value);
        }
        else
        {
            Debug.LogWarning("[EnemySpawner] No out-of-sight position found after " + maxSpawnAttempts +
                             " attempts. Skipping this spawn cycle.", this);
        }
    }

    /// <summary>Destroys every enemy this spawner created (and cancels pending flashlight respawns).</summary>
    [ContextMenu("Despawn All")]
    public void DespawnAll()
    {
        for (int i = 0; i < aliveEnemies.Count; i++)
        {
            if (aliveEnemies[i] != null) Destroy(aliveEnemies[i]);
        }
        aliveEnemies.Clear();

        // Also cancel respawns that were still counting down — "despawn all"
        // means a clean field.
        pendingFlashlightRespawns = 0;
        flashlightRespawnCountdown = 0f;

        // v1.3: delay bookkeeping resets too (a clean field means no pending
        // start/resume spawn). External SafeZone pauses are NOT touched —
        // those belong to the zones and resolve themselves.
        spawnDelayCountdown = 0f;
        startDelayPending = false;
        resumeDelayPending = false;
    }

    // ------------------------------------------------------------------ //
    // v1.3 — Safe Zone API (called by the SafeZone plug-in)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// v1.3 – SAFE ZONE: freeze every spawn source while the player stands
    /// inside the zone (auto interval, Spawn On Start, flashlight respawns
    /// and the spawn delay all stop ticking). Called once when the player
    /// ENTERS the zone. Overlapping zones each hold their own pause —
    /// spawning only resumes when the last one releases it.
    /// </summary>
    public void PauseSpawning()
    {
        safeZonePauseCount++;
    }

    /// <summary>
    /// v1.3 – SAFE ZONE: releases one pause (the counterpart of exactly one
    /// PauseSpawning call). When the LAST pause is released, the Spawn Delay
    /// is re-counted from its full value and spawning resumes when it
    /// expires — the enemy re-engages right at that moment.
    /// </summary>
    public void ResumeSpawning()
    {
        if (safeZonePauseCount > 0) safeZonePauseCount--;

        if (safeZonePauseCount == 0)
        {
            BeginSpawnDelayAfterZone();
        }
    }

    /// <summary>
    /// v1.3: starts (or restarts) the spawn delay after the player left the
    /// last Safe Zone. A still-pending game-start spawn takes precedence —
    /// the two flags can never both fire.
    /// </summary>
    private void BeginSpawnDelayAfterZone()
    {
        spawnDelayCountdown = Mathf.Max(0f, spawnDelay);
        resumeDelayPending = !startDelayPending;

        // Zero delay = resume immediately (same frame).
        if (spawnDelayCountdown <= 0f) FinishSpawnDelay();
    }

    /// <summary>
    /// v1.3: the spawn delay just expired — hand out the one pending spawn
    /// (if any) and return to the normal cadence.
    /// </summary>
    private void FinishSpawnDelay()
    {
        spawnDelayCountdown = 0f;
        spawnTimer = 0f;

        bool startSpawn = startDelayPending;
        bool resumeSpawn = resumeDelayPending;
        startDelayPending = false;
        resumeDelayPending = false;

        if (startSpawn)
        {
            // "Spawn On Start", now at the end of the game-start delay.
            TrySpawnEnemy();
        }
        else if (resumeSpawn && autoSpawn)
        {
            // The enemy re-engages right when the post-Safe-Zone delay ends
            // (auto spawning only — manual spawners stay manual).
            TrySpawnEnemy();
        }
    }

    // ------------------------------------------------------------------ //
    // Spawning internals
    // ------------------------------------------------------------------ //

    private Vector3? FindSafeSpawnPosition()
    {
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            // v1.2: candidates without tagged ground underneath are invalid.
            Vector3? candidate = ResolveGroundPosition(GetRandomRingPosition());
            if (!candidate.HasValue) continue;

            if (IsOutsidePlayerView(candidate.Value))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>v1.2: any position that has tagged ground under it (visibility ignored).</summary>
    private Vector3? FindAnyGroundPosition()
    {
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector3? candidate = ResolveGroundPosition(GetRandomRingPosition());
            if (candidate.HasValue) return candidate;
        }
        return null;
    }

    private GameObject SpawnAt(Vector3 position)
    {
        GameObject enemy = Instantiate(enemyPrefab, position, Quaternion.identity);
        aliveEnemies.Add(enemy);

        EnemyController controller = enemy.GetComponent<EnemyController>();
        if (controller != null)
        {
            controller.Initialize(player);

            // v1.1: hear when the flashlight makes this enemy disappear, so the
            // random respawn cooldown can start rolling.
            controller.OnDespawned += HandleEnemyDespawned;
        }
        else
        {
            Debug.LogWarning("[EnemySpawner] Enemy prefab '" + enemyPrefab.name +
                             "' has no EnemyController on its root. Movement/color behaviour will not run.", this);
        }

        return enemy;
    }

    /// <summary>
    /// v1.1: an enemy finished its disappear stage and is about to be destroyed.
    /// Queue a respawn with a fresh random cooldown.
    /// </summary>
    private void HandleEnemyDespawned(EnemyController controller)
    {
        // Tidy unsubscribe (the object is being destroyed anyway).
        controller.OnDespawned -= HandleEnemyDespawned;

        pendingFlashlightRespawns++;

        // (Re)start the countdown for the head of the respawn queue.
        if (pendingFlashlightRespawns == 1)
        {
            RollRespawnCooldown();
        }
    }

    /// <summary>Rolls a fresh random cooldown between Min and Max for one queued respawn.</summary>
    private void RollRespawnCooldown()
    {
        float min = Mathf.Min(respawnCooldownMin, respawnCooldownMax);
        float max = Mathf.Max(respawnCooldownMin, respawnCooldownMax);
        flashlightRespawnCountdown = Random.Range(min, max);
    }

    /// <summary>Random point on the ring between Min and Max Spawn Distance around the player.</summary>
    private Vector3 GetRandomRingPosition()
    {
        // sqrt-distribution keeps the points uniformly spread over the ring area
        // instead of clustering near the minimum radius.
        float minSq = minSpawnDistance * minSpawnDistance;
        float maxSq = maxSpawnDistance * maxSpawnDistance;
        float radius = Mathf.Sqrt(Random.Range(minSq, maxSq));
        float angle = Random.Range(0f, Mathf.PI * 2f);

        Vector3 center = player != null ? player.position : transform.position;

        return new Vector3(
            center.x + Mathf.Cos(angle) * radius,
            center.y,
            center.z + Mathf.Sin(angle) * radius);
    }

    /// <summary>
    /// v1.2: resolves a ring position onto tagged ground. Returns null when the
    /// ground ray hits nothing or hits a collider that is NOT tagged as ground —
    /// such spots are invalid, so the enemy can only ever spawn on ground.
    /// </summary>
    private Vector3? ResolveGroundPosition(Vector3 position)
    {
        if (!snapToGround) return position + Vector3.up * spawnHeightOffset;

        Vector3 origin = position + Vector3.up * groundRaycastHeight;
        float rayLength = groundRaycastHeight * 2f;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLength, groundMask,
                            QueryTriggerInteraction.Ignore))
        {
            if (!MatchesGroundTag(hit.collider)) return null;
            return hit.point + Vector3.up * spawnHeightOffset;
        }

        return null; // nothing valid below – never fall back to mid-air
    }

    /// <summary>
    /// Tag check that also survives a tag name that does not exist yet in the
    /// project's tag list (Unity throws in that case instead of returning false).
    /// </summary>
    private bool MatchesGroundTag(Collider collider)
    {
        if (collider == null || string.IsNullOrEmpty(groundTag)) return false;

        try
        {
            return collider.CompareTag(groundTag);
        }
        catch
        {
            // The tag has not been created in the project yet.
            return false;
        }
    }

    /// <summary>Warns exactly once per session when no tagged ground can be found.</summary>
    private void WarnNoGroundOnce()
    {
        if (warnedNoGround) return;
        warnedNoGround = true;

        Debug.LogWarning("[EnemySpawner] No collider tagged '" + groundTag + "' was found under any spawn " +
                         "candidate. Create the tag (Edit > Project Settings > Tags and Layers), tag your " +
                         "floor / terrain colliders, and check the Ground Mask / Ground Raycast Height. " +
                         "Spawning is skipped until tagged ground exists.", this);
    }

    /// <summary>
    /// True when the player cannot see the given position: either it is outside
    /// the camera frustum (with buffer), or solid geometry blocks the line of sight.
    /// </summary>
    private bool IsOutsidePlayerView(Vector3 position)
    {
        if (!avoidCameraView || playerCamera == null) return true;

        // 1) Frustum test with a buffer volume that approximates the enemy's size.
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(playerCamera);
        Bounds probe = new Bounds(position, Vector3.one * (visibilityBufferRadius * 2f));

        if (!GeometryUtility.TestPlanesAABB(planes, probe))
        {
            return true; // fully off-screen
        }

        // 2) On-screen – but maybe hidden behind solid geometry?
        if (checkLineOfSight)
        {
            Vector3 camPos = playerCamera.transform.position;
            Vector3 toSpawn = position - camPos;
            float distance = toSpawn.magnitude;

            if (distance < 0.01f) return false;

            // Stop the ray just short of the enemy body (buffer radius) so the
            // enemy itself can never count as its own occluder.
            if (Physics.Raycast(camPos, toSpawn / distance, out RaycastHit hit,
                                distance - visibilityBufferRadius, occlusionMask,
                                QueryTriggerInteraction.Ignore))
            {
                return true; // something solid blocks the view -> spawn stays invisible
            }
        }

        return false;
    }

    private void PruneDestroyed()
    {
        aliveEnemies.RemoveAll(IsDestroyed);
    }

    private static bool IsDestroyed(GameObject obj)
    {
        return obj == null;
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    private void OnDrawGizmosSelected()
    {
        Transform center = player != null ? player : transform;

        Gizmos.color = Color.cyan;
        DrawRing(center.position, minSpawnDistance);

        Gizmos.color = Color.yellow;
        DrawRing(center.position, maxSpawnDistance);
    }

    private static void DrawRing(Vector3 center, float radius)
    {
        const int segments = 64;
        Vector3 previous = center + new Vector3(radius, 0f, 0f);

        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
    }
}