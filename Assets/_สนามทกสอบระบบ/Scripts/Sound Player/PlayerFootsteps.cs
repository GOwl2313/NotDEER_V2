using System;
using UnityEngine;

/// <summary>
/// Player footstep sounds with surface detection (v1.0).
///
/// HOW IT WORKS
///   The script measures how far the player actually walks (horizontal
///   movement per frame). Every "Step Distance" metres it plays one footstep
///   clip — so the cadence automatically matches the walking speed and no
///   step ever plays while standing still.
///
///   At the moment of each step it identifies the ground under the player:
///     • Terrain  → the dominant ground TEXTURE (splatmap TerrainLayer) at
///                  the player's feet. Each surface lists which terrain
///                  layers belong to it (drop your grass layers into the
///                  "Grass" surface). This reads the painted ground itself,
///                  so it works no matter what happened to the billboard
///                  grass.
///     • Optional → a surface with "Also When Detail Grass Present" enabled
///                  plays wherever terrain detail (billboard) grass exists
///                  at the player's feet. Checked BEFORE the ground texture,
///                  so a grass tuft at the feet wins even on a dirt texture.
///     • Anything else (bridges, rocks, props) → the Default Surface.
///
///   Silent while GamePauseManager.IsPaused. Self-measuring: no wiring to
///   PlayerMovementController (or any movement script) needed.
///
/// SETUP
///   1. Add this component to the Player root (an AudioSource is added
///      automatically).
///   2. For each ground material create one surface ("Grass", "Dirt", ...)
///      and assign 3–6 footstep variation clips. A surface with no clips
///      stays silent.
///   3. Drag the matching TerrainLayer assets (from the Terrain's
///      "Paint Texture" tab) into each surface's "Terrain Layers" list.
///      Don't know your layers? Play, walk onto the ground, then right-click
///      this component's header → "Log Surface Under Player" — the Console
///      prints the dominant layer's name at the player's position.
///   4. Assign clips to the Default Surface too (played on anything that
///      matches no surface).
///
/// v1.0 – first version.
/// v1.1 – compile fix: TerrainData.alphamapLayers (misspelled before).
/// v1.2 – "Cut Previous Step" option (default ON): a new footstep now ends
///        the previous one instead of stacking on top of it — footsteps can
///        never overlap, even at fast cadences or with long clips.
/// </summary>
[RequireComponent(typeof(AudioSource))]
[DisallowMultipleComponent]
public class PlayerFootsteps : MonoBehaviour
{
    /// <summary>One ground material (grass, dirt, wood ...) and its sounds.</summary>
    [Serializable]
    public class FootstepSurface
    {
        [Tooltip("Display name, e.g. Grass / Dirt / Wood.")]
        public string name = "Grass";

        [Tooltip("Footstep variations — one is picked at random each step. Leave empty to stay silent on this surface.")]
        public AudioClip[] clips;

        [Tooltip("Terrain ground-texture layers that count as this surface. Drag them from the Terrain's Paint Texture tab.")]
        public TerrainLayer[] terrainLayers;

        [Tooltip("GRASS DETAIL MODE: when ON, this surface also plays wherever terrain detail (billboard) grass exists at the player's feet — checked BEFORE the ground texture, so a tuft at the feet wins even on a dirt texture.")]
        public bool alsoWhenDetailGrassPresent = false;

        [Tooltip("Loudness of steps on this surface (0–1).")]
        [Range(0f, 1f)] public float volume = 1f;
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Surfaces")]

    [Tooltip("One entry per ground material. The first surface that matches the ground under the player plays.")]
    [SerializeField] private FootstepSurface[] surfaces = new FootstepSurface[1];

    [Tooltip("Played when the ground matches no surface above (bridges, props, unmapped textures).")]
    [SerializeField] private FootstepSurface defaultSurface = new FootstepSurface { name = "Default" };

    [Header("Step Cadence")]

    [Tooltip("Horizontal metres walked per footstep (lower = faster steps).")]
    [SerializeField] private float stepDistance = 0.7f;

    [Tooltip("Movement slower than this (units per second) is ignored — physics jitter and micro-slides never play steps.")]
    [SerializeField] private float minimumWalkSpeed = 0.15f;

    [Tooltip("A single-frame position change bigger than this counts as a teleport — the cadence resets instead of bursting steps.")]
    [SerializeField] private float teleportDistance = 1.5f;

    [Header("Ground Detection")]

    [Tooltip("How far down to search for the ground, in metres. Should exceed the player's height above the ground.")]
    [SerializeField] private float groundRaycastHeight = 3.5f;

    [Tooltip("Which layers count as ground. Default: everything. The player's own colliders are always skipped.")]
    [SerializeField] private LayerMask groundMask = ~0;

    [Header("Audio")]

    [Tooltip("Configure the AudioSource automatically (2D sound, no loop, no play-on-awake). Turn OFF to keep an existing source's settings.")]
    [SerializeField] private bool configureAudioSource = true;

    [Tooltip("Random pitch offset per step — makes repeated clips feel natural (0 = always the same pitch).")]
    [SerializeField, Range(0f, 0.25f)] private float pitchVariation = 0.08f;

    [Tooltip("Random volume drop per step, as a fraction of the surface volume (0 = always full volume).")]
    [SerializeField, Range(0f, 0.5f)] private float volumeVariation = 0.15f;

    [Tooltip("A new footstep CUTS the previous one instead of playing on top of it (steps never overlap). Turn OFF to let long steps blend together.")]
    [SerializeField] private bool cutPreviousStep = true;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private AudioSource audioSource;
    private Rigidbody playerBody;
    private Vector3 lastPosition;
    private float distanceToNextStep;

    private static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        playerBody = GetComponentInParent<Rigidbody>();

        if (configureAudioSource && audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f; // the player's own steps are 2D
        }

        lastPosition = transform.position;
        distanceToNextStep = stepDistance;
    }

    private void Update()
    {
        if (GamePauseManager.IsPaused)
        {
            // Keep the reference position fresh so unpausing never triggers
            // a catch-up burst.
            lastPosition = transform.position;
            return;
        }

        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;
        delta.y = 0f;

        float moved = delta.magnitude;

        // Teleport guard: huge single-frame jumps reset the cadence instead
        // of firing several steps at once.
        if (moved > teleportDistance)
        {
            distanceToNextStep = stepDistance;
            return;
        }

        // Standing still (or physics jitter) never accumulates.
        if (Time.deltaTime <= 0f || moved / Time.deltaTime < minimumWalkSpeed) return;

        distanceToNextStep -= moved;
        if (distanceToNextStep > 0f) return;

        distanceToNextStep += stepDistance;
        PlayFootstep();
    }

    /// <summary>Clamps Inspector values so the cadence can never divide by zero or stutter.</summary>
    private void OnValidate()
    {
        stepDistance = Mathf.Max(0.1f, stepDistance);
        minimumWalkSpeed = Mathf.Max(0f, minimumWalkSpeed);
        teleportDistance = Mathf.Max(0.5f, teleportDistance);
        groundRaycastHeight = Mathf.Max(1f, groundRaycastHeight);
    }

    // ------------------------------------------------------------------ //
    // Playing
    // ------------------------------------------------------------------ //

    private void PlayFootstep()
    {
        FootstepSurface surface = DetectSurface();
        if (surface == null) return;
        if (surface.clips == null || surface.clips.Length == 0) return;
        if (audioSource == null) return;

        AudioClip clip = surface.clips[UnityEngine.Random.Range(0, surface.clips.Length)];
        if (clip == null) return;

        float volumeScale = surface.volume * (1f - volumeVariation * UnityEngine.Random.value);
        if (volumeScale <= 0f) return;

        if (pitchVariation > 0f)
        {
            audioSource.pitch = 1f + (UnityEngine.Random.value * 2f - 1f) * pitchVariation;
        }

        // PlayOneShot voices stack — at fast cadences a new step would play on
        // top of the still-sounding previous one. Stop() ends it first so two
        // footsteps never overlap (the fresh step's attack masks the cut).
        if (cutPreviousStep)
        {
            audioSource.Stop();
        }

        audioSource.PlayOneShot(clip, volumeScale);
    }

    // ------------------------------------------------------------------ //
    // Surface detection
    // ------------------------------------------------------------------ //

    /// <summary>Identifies the ground under the player and returns the matching surface.</summary>
    private FootstepSurface DetectSurface()
    {
        Collider ground = FindGroundBelow(out Vector3 hitPoint);
        if (ground == null) return defaultSurface;

        TerrainCollider terrainCollider = ground as TerrainCollider;
        if (terrainCollider != null)
        {
            Terrain terrain = terrainCollider.GetComponent<Terrain>();
            if (terrain != null && terrain.terrainData != null)
            {
                FootstepSurface surface = MatchTerrainSurface(terrain, hitPoint);
                if (surface != null) return surface;
            }
        }

        // Bridges, rocks, props, unmapped textures...
        return defaultSurface;
    }

    /// <summary>
    /// Raycasts straight down and returns the nearest collider that is not part
    /// of the player. Returns null when nothing is within range.
    /// </summary>
    private Collider FindGroundBelow(out Vector3 hitPoint)
    {
        hitPoint = default;

        int count = Physics.RaycastNonAlloc(transform.position, Vector3.down, hitBuffer,
            groundRaycastHeight, groundMask, QueryTriggerInteraction.Ignore);

        Collider best = null;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider candidate = hitBuffer[i].collider;
            if (candidate == null) continue;

            // Never let the player's own colliders (hitbox etc.) block the search.
            if (candidate.transform.IsChildOf(transform)) continue;
            if (playerBody != null && candidate.attachedRigidbody == playerBody) continue;

            if (hitBuffer[i].distance < bestDistance)
            {
                bestDistance = hitBuffer[i].distance;
                best = candidate;
                hitPoint = hitBuffer[i].point;
            }
        }

        return best;
    }

    /// <summary>Maps a world position on a terrain to a footstep surface (or null).</summary>
    private FootstepSurface MatchTerrainSurface(Terrain terrain, Vector3 worldPoint)
    {
        TerrainData data = terrain.terrainData;
        Vector3 size = data.size;
        if (size.x <= 0f || size.z <= 0f) return null;

        Vector3 local = terrain.transform.InverseTransformPoint(worldPoint);
        float u = local.x / size.x;
        float v = local.z / size.z;
        if (u < 0f || u > 1f || v < 0f || v > 1f) return null;

        // 1) Detail (billboard) grass — checked first when a surface opted in,
        //    so a grass tuft at the feet wins even on a non-grass ground texture.
        FootstepSurface detailSurface = FindDetailGrassSurface();
        if (detailSurface != null && HasDetailGrassAt(data, u, v)) return detailSurface;

        // 2) Dominant ground texture (splatmap) at the player's feet.
        TerrainLayer dominant = GetDominantLayer(data, u, v, out _);
        if (dominant == null) return null;

        if (surfaces == null) return null;

        for (int i = 0; i < surfaces.Length; i++)
        {
            FootstepSurface surface = surfaces[i];
            if (surface == null || surface.terrainLayers == null) continue;

            for (int j = 0; j < surface.terrainLayers.Length; j++)
            {
                if (surface.terrainLayers[j] == dominant) return surface;
            }
        }

        return null;
    }

    /// <summary>The first surface that opted into detail-grass detection, if any.</summary>
    private FootstepSurface FindDetailGrassSurface()
    {
        if (surfaces == null) return null;

        for (int i = 0; i < surfaces.Length; i++)
        {
            FootstepSurface surface = surfaces[i];
            if (surface != null && surface.alsoWhenDetailGrassPresent) return surface;
        }
        return null;
    }

    /// <summary>Returns the strongest splatmap layer at the given normalized position (or null).</summary>
    private static TerrainLayer GetDominantLayer(TerrainData data, float u, float v, out float weight)
    {
        weight = 0f;

        int x = Mathf.Clamp((int)(u * data.alphamapWidth), 0, data.alphamapWidth - 1);
        int y = Mathf.Clamp((int)(v * data.alphamapHeight), 0, data.alphamapHeight - 1);

        float[,,] alphamaps = data.GetAlphamaps(x, y, 1, 1);

        int bestLayer = -1;
        float bestWeight = -1f;
        for (int i = 0; i < data.alphamapLayers; i++)
        {
            float w = alphamaps[0, 0, i];
            if (w > bestWeight)
            {
                bestWeight = w;
                bestLayer = i;
            }
        }

        if (bestLayer < 0 || data.terrainLayers == null || bestLayer >= data.terrainLayers.Length)
        {
            return null;
        }

        weight = bestWeight;
        return data.terrainLayers[bestLayer];
    }

    /// <summary>True when any terrain detail (billboard grass etc.) exists at the given normalized position.</summary>
    private static bool HasDetailGrassAt(TerrainData data, float u, float v)
    {
        DetailPrototype[] prototypes = data.detailPrototypes;
        if (prototypes == null || prototypes.Length == 0) return false;
        if (data.detailWidth <= 0 || data.detailHeight <= 0) return false;

        int x = Mathf.Clamp((int)(u * data.detailWidth), 0, data.detailWidth - 1);
        int y = Mathf.Clamp((int)(v * data.detailHeight), 0, data.detailHeight - 1);

        for (int i = 0; i < prototypes.Length; i++)
        {
            int[,] density = data.GetDetailLayer(x, y, 1, 1, i);
            if (density != null &&
                density.GetLength(0) > 0 && density.GetLength(1) > 0 &&
                density[0, 0] > 0)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ //
    // Debug helpers
    // ------------------------------------------------------------------ //

    [ContextMenu("Log Surface Under Player")]
    private void LogSurfaceUnderPlayer()
    {
        Collider ground = FindGroundBelow(out Vector3 point);
        if (ground == null)
        {
            Debug.Log("[PlayerFootsteps] No ground within " + groundRaycastHeight +
                      " m below the player — the Default Surface is used.", this);
            return;
        }

        TerrainCollider terrainCollider = ground as TerrainCollider;
        if (terrainCollider != null)
        {
            Terrain terrain = terrainCollider.GetComponent<Terrain>();
            if (terrain != null && terrain.terrainData != null)
            {
                TerrainData data = terrain.terrainData;
                Vector3 local = terrain.transform.InverseTransformPoint(point);
                float u = local.x / data.size.x;
                float v = local.z / data.size.z;

                TerrainLayer dominant = GetDominantLayer(data, u, v, out float weight);
                bool detail = FindDetailGrassSurface() != null && HasDetailGrassAt(data, u, v);
                FootstepSurface matched = MatchTerrainSurface(terrain, point);
                FootstepSurface effective = matched != null ? matched : defaultSurface;

                Debug.Log("[PlayerFootsteps] Ground: terrain '" + terrain.name +
                          "'. Dominant layer: " + (dominant != null ? "'" + dominant.name + "'" : "none") +
                          " (" + weight.ToString("0.00") + "). Detail grass at feet: " + (detail ? "YES" : "no") +
                          ". Surface used: '" + (effective != null ? effective.name : "none") + "'." +
                          (matched == null
                              ? " (no surface matched — drag the dominant layer into a surface's Terrain Layers list.)"
                              : ""), this);
                return;
            }
        }

        Debug.Log("[PlayerFootsteps] Ground: '" + ground.name + "' (non-terrain) → Default Surface '" +
                  (defaultSurface != null ? defaultSurface.name : "none") + "'.", this);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, transform.position + Vector3.down * groundRaycastHeight);
    }
}
