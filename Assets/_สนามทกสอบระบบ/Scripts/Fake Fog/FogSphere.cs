using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// FAKE FOG SPHERE (v1.1) — a thick, believable fog ball that travels with
/// the player. Unity's own fog is a flat screen tint that gets thinner the
/// closer things are — this is the opposite: a real SPHERE of fog around
/// the player with actual DEPTH, so anything that pokes into it gets
/// progressively buried the deeper it goes, exactly like walking into a
/// cloud.
///
/// HOW IT FAKES DEPTH (why it never looks like a flat barrier)
///   The fog is drawn as one see-through sphere mesh around the player, but
///   the shader does not just paint the shell. For EVERY pixel it reads the
///   scene depth behind it and works out how many METERS of fog the view
///   ray crosses before hitting that surface. Something brushing the fog's
///   edge is barely misted; the same object a few steps deeper is swallowed
///   by it; the sphere's own silhouette is naturally thin at the rim and
///   thick in the middle (a real volume look, not a decal). A soft animated
///   noise rolls through the fog so it breathes instead of sitting still.
///
/// LIGHT SHINES THROUGH IT (like blurry glass)
///   The fog blocks NOTHING: it casts no shadows, has no collider, and is
///   ignored by lights — the flashlight keeps lighting the world exactly as
///   before. On top of that, the fog shows a soft GLOW where the flashlight
///   beam passes through it (auto-detected from the player's Spot Light —
///   the FlashlightController's light), so the beam visibly diffuses inside
///   the cloud instead of cutting through it invisibly.
///
/// ZERO INTERFERENCE — GUARANTEED
///   • NO collider is ever created (not even a trigger): the fog cannot
///     block, push, trigger or be seen by ANY physics system, raycast,
///     trigger event, enemy vision cone or interaction point.
///   • The visual sphere is a child object that only has a MeshFilter and
///     a MeshRenderer — no AudioSource, no scripts, nothing to hook into.
///   • It casts no shadows, samples no light probes, and writes no depth,
///     so other renderers never sort against it incorrectly.
///
/// OPTIMIZED
///   One sphere mesh (~760 triangles), one draw call, one depth-sample per
///   pixel, no post-processing, no render textures, no per-frame garbage.
///   Material values are only uploaded when they actually change, so a
///   standing-still player costs almost nothing. Density 0 hides the fog
///   completely (the draw call disappears with it).
///
/// ZERO SETUP
///   1. Put FogSphere.shader in the project (same folder as the scripts is
///      fine — Unity imports it on its own).
///   2. Add this component to any object — an empty "Fake Fog" object or
///      the player root, both work. The shader field fills itself in.
///   3. Press Play. The fog appears in PLAY mode and follows the player.
///
/// RULES IT FOLLOWS
///   • Works on BOTH render pipelines out of the box: one shader file
///     contains a URP pass and a Built-In pass; the engine picks the right
///     one automatically (URP is what Unity 6 projects use by default).
///     HDRP is not supported — the fog says so once and hides itself.
///   • "Cover Camera" (default on) quietly grows the sphere if the camera
///     would ever poke outside it, so the player is always INSIDE the fog
///     and it always covers the whole view.
///   • The fog's drift animation is driven by the game's own clock, so it
///     freezes when the pause menu freezes the world (GamePauseManager).
///   • No statics anywhere — scene reloads and domain-reload-off are safe.
///
/// INSPECTOR REFERENCE
///   Fog Color        — the fog's colour (pale blue-grey by default).
///   Radius           — how big the sphere is, in meters (player to rim).
///   Density          — how CLEAR it is: low = see-through mist,
///                      high = thick soup. Bigger spheres also read thicker
///                      at the same density (more meters of fog per ray).
///   Noise Amount     — how much the fog billows/swirls (0 = perfectly
///                      uniform).
///   Noise Scale      — the size of the billow pattern in meters (smaller
///                      value = bigger, softer clouds).
///   Drift Speed      — how fast the fog rolls (0 = frozen).
///   Cover Camera     — keep the main camera inside the sphere always.
///   Beam Light       — the light that glows through the fog. Auto-finds
///                      the player's/camera's Spot Light (the flashlight);
///                      drag one in to choose manually.
///   Beam Glow        — how strongly the light diffuses inside the fog
///                      (0 = off).
///   Follow Target    — optional override; defaults to the "Player"-tagged
///                      object, exactly like the other scripts.
///   Height Offset    — nudges the sphere's centre up/down on the body.
///
/// PUBLIC API (for UnityEvents / other scripts)
///   Radius, Density, FogColor  — read or change at runtime; changes apply
///                                on the same frame, values are clamped.
///   IsFogBuilt                 — true once the visual sphere exists.
///   RebuildFogSphere()         — also on the component's right-click menu.
///
/// CHANGELOG
///   v1.1 — Unity 6000.5 compile fixes, reported from a live project:
///          • CS0619: Object.GetInstanceID() is obsolete-as-error in
///            Unity 6000.5+ — the depth-mode camera is now tracked by
///            plain reference comparison instead (works on every version).
///          • CS0034: 'Color != Vector4' is an ambiguous operator — the
///            beam-colour cache compare now casts to Vector4 explicitly.
///          • FogSphere.shader: the Built-In depth read used the long-
///            deprecated one-argument UNITY_SAMPLE_DEPTH macro as if it
///            took two — now the canonical SAMPLE_DEPTH_TEXTURE(tex, uv),
///            and _CameraDepthTexture is declared with
///            UNITY_DECLARE_DEPTH_TEXTURE like Unity's own shaders do.
/// </summary>
[DisallowMultipleComponent]
public class FogSphere : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Fog Look")]

    [Tooltip("The fog's colour.")]
    [SerializeField] private Color fogColor = new Color(0.72f, 0.76f, 0.82f, 1f);

    [Tooltip("How big the fog sphere is, in meters (player to rim).")]
    [SerializeField, Range(1f, 50f)] private float radius = 8f;

    [Tooltip("How CLEAR the fog is. Low = see-through mist, high = thick " +
             "soup. Bigger spheres also read thicker at the same density " +
             "(more meters of fog per view ray).")]
    [SerializeField, Range(0f, 1f)] private float density = 0.45f;

    [Tooltip("How much the fog billows and swirls (0 = perfectly uniform).")]
    [SerializeField, Range(0f, 1f)] private float noiseAmount = 0.35f;

    [Tooltip("Size of the billow pattern in meters (smaller = bigger, " +
             "softer clouds).")]
    [SerializeField, Min(0.05f)] private float noiseScale = 0.35f;

    [Tooltip("How fast the fog rolls (0 = frozen).")]
    [SerializeField, Range(0f, 2f)] private float driftSpeed = 0.35f;

    [Header("Camera Coverage")]

    [Tooltip("Keep the main camera inside the sphere at all times. If the " +
             "camera would ever poke out (tiny radius, tall rig), the " +
             "rendered sphere quietly grows just enough to cover it.")]
    [SerializeField] private bool coverCamera = true;

    [Header("Light Through The Fog")]

    [Tooltip("The light that glows through the fog like blurry glass. " +
             "Auto-finds the Spot Light on the player or the camera (the " +
             "flashlight). Drag one in to choose manually. Leave the glow " +
             "off with Beam Glow = 0.")]
    [SerializeField] private Light beamLight;

    [Tooltip("How strongly the chosen light diffuses inside the fog " +
             "(0 = off). Follows the light's own colour; flickering " +
             "intensity passes through.")]
    [SerializeField, Range(0f, 3f)] private float beamGlow = 1f;

    [Header("Player")]

    [Tooltip("Optional. Leave empty to follow the object tagged '" +
             "Player' (same convention as the other scripts).")]
    [SerializeField] private Transform followTargetOverride;

    [Tooltip("Tag used to find the player when no override is assigned.")]
    [SerializeField] private string playerTag = "Player";

    [Tooltip("Nudges the sphere's centre up/down on the player's body.")]
    [SerializeField, Range(-2f, 2f)] private float centerHeightOffset = 0.25f;

    [Header("Shader")]

    [Tooltip("The fake fog shader. Fills itself in with " +
             "'NotDEER/FogSphere' — only assign manually if you renamed " +
             "the shader or spawn this component from code in a build.")]
    [SerializeField] private Shader fogShader;

    [Header("Debug")]

    [Tooltip("Extra info messages in the Console (setup, player/light " +
             "pickups). Warnings are always shown — this only adds info.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string ShaderName = "NotDEER/FogSphere";
    private const string FogChildName = "Fog Sphere (auto)";
    private const string BuiltinSphereMesh = "New-Sphere.fbx";

    private const float CameraMargin = 0.75f;    // cover-camera breathing room
    private const float RefindInterval = 0.5f;   // player/camera retry cadence
    private const float BeamFindInterval = 2f;   // flashlight retry cadence

    // Material property ids are string-based here (SetFloat(string...)) —
    // the calls only happen on value CHANGES, so there is no per-frame
    // cost to the strings being interned by the engine.

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Transform fogTransform;          // the visual sphere's transform
    private MeshRenderer fogRenderer;        // its renderer
    private Material fogMaterial;            // owned instance (destroyed with us)

    private Transform followTarget;          // cached player
    private Camera mainCamera;               // cached main camera
    private Light autoBeamLight;             // cached auto-found flashlight
    private float refindTimer;               // player retry clock
    private float cameraRefindTimer;         // camera retry clock (independent)
    private float beamFindTimer;             // flashlight retry clock
    private int playerFindAttempts;

    private RenderPipelineAsset lastPipeline;    // pipeline-change detection
    private bool hdrpBlocked;                    // HDRP → hide the fog
    private Camera depthModeCamera;              // depthTextureMode set once per camera
                                                 // (a plain reference — GetInstanceID is
                                                 //  obsolete-as-error in Unity 6000.5)

    // warn-once flags (per component — no statics, reload-safe by design)
    private bool warnedNoShader;
    private bool warnedNoPlayer;
    private bool warnedOrtho;
    private bool warnedDepthUrp;
    private bool warnedHdrp;

    // equality-guard caches: "unset" sentinels force the first upload
    private Color lastColor = new Color(-1f, -1f, -1f, -1f);
    private float lastDensity = -1f;
    private float lastNoiseAmount = -1f;
    private float lastNoiseScale = -1f;
    private float lastDrift = -1f;
    private float lastRadius = -1f;
    private Vector4 lastBeamPos = new Vector4(0f, 0f, 0f, -1f);
    private Vector4 lastBeamDir = new Vector4(0f, 0f, 0f, -1f);
    private Vector4 lastBeamColor = new Vector4(-1f, -1f, -1f, -1f);
    private Vector4 lastBeamParams = new Vector4(-1f, -1f, -1f, -1f);

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>The fog sphere's radius in meters (clamped 1–50).</summary>
    public float Radius
    {
        get { return radius; }
        set { radius = Mathf.Clamp(value, 1f, 50f); }
    }

    /// <summary>The fog density 0–1 (clamped). 0 hides the fog.</summary>
    public float Density
    {
        get { return density; }
        set { density = Mathf.Clamp01(value); }
    }

    /// <summary>The fog's colour.</summary>
    public Color FogColor
    {
        get { return fogColor; }
        set { fogColor = value; }
    }

    /// <summary>True once the visual fog sphere has been built.</summary>
    public bool IsFogBuilt { get { return fogTransform != null; } }

    /// <summary>
    /// Destroys and rebuilds the visual sphere — also available on the
    /// component's right-click menu. Useful after assigning a renamed
    /// shader; does nothing outside Play mode.
    /// </summary>
    [ContextMenu("Rebuild Fog Sphere")]
    public void RebuildFogSphere()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[FogSphere] '" + name + "': the fog builds itself " +
                      "when Play starts — nothing to rebuild in edit mode.");
            return;
        }

        if (fogTransform != null) Destroy(fogTransform.gameObject);
        fogTransform = null;
        fogRenderer = null;
        if (fogMaterial != null) Destroy(fogMaterial);
        fogMaterial = null;
        warnedNoShader = false;

        BuildFogSphere();
    }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        BuildFogSphere();
    }

    private void OnEnable()
    {
        SyncRendererEnabled();
    }

    private void OnDisable()
    {
        // never leave a visible fog when the component is off
        if (fogRenderer != null) fogRenderer.enabled = false;
    }

    private void OnDestroy()
    {
        // owned objects — no leaks, no orphans
        if (fogTransform != null) Destroy(fogTransform.gameObject);
        if (fogMaterial != null) Destroy(fogMaterial);
        fogTransform = null;
        fogRenderer = null;
        fogMaterial = null;
    }

    private void LateUpdate()
    {
        // cheap every-frame validation: the child can be deleted by other
        // scripts or by the user mid-play — quietly rebuild it then.
        if (fogTransform == null && !BuildFogSphere()) return;

        RefreshCamera();
        EnsureDepthTexture();
        RefreshPipelineState();

        // ---- follow the player ----
        Vector3 center = transform.position + Vector3.up * centerHeightOffset;
        Transform target = ResolveFollowTarget();
        if (target != null)
        {
            center = target.position + Vector3.up * centerHeightOffset;
        }
        fogTransform.position = center;

        // ---- size (covering the camera when asked) ----
        float effectiveRadius = radius;
        if (coverCamera && mainCamera != null)
        {
            float cameraDistance = Vector3.Distance(center, mainCamera.transform.position);
            float needed = cameraDistance + CameraMargin;
            if (needed > effectiveRadius) effectiveRadius = needed;
        }
        if (Mathf.Abs(effectiveRadius - lastRadius) > 0.01f)
        {
            lastRadius = effectiveRadius;
            float diameter = effectiveRadius * 2f;
            fogTransform.localScale = new Vector3(diameter, diameter, diameter);
            fogMaterial.SetFloat("_FogRadius", effectiveRadius);
        }

        SyncLookParams();
        SyncBeamLight();
        SyncRendererEnabled();
    }

    // ------------------------------------------------------------------ //
    // Setup
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Creates the visual sphere (once). The child gets ONLY a MeshFilter
    /// and a MeshRenderer — no collider, ever, so nothing in the game can
    /// collide with, trigger on, or raycast into the fog.
    /// </summary>
    private bool BuildFogSphere()
    {
        if (fogTransform != null) return true;

        // a previous sphere may have been destroyed externally — its material
        // is a separate object, so clean the stale one up explicitly (no leaks)
        if (fogMaterial != null)
        {
            Destroy(fogMaterial);
            fogMaterial = null;
        }

        if (fogShader == null) fogShader = Shader.Find(ShaderName);
        if (fogShader == null)
        {
            if (!warnedNoShader)
            {
                warnedNoShader = true;
                Debug.LogWarning("[FogSphere] '" + name + "': the fog shader " +
                    "was not found. Make sure FogSphere.shader is in the " +
                    "project (shader name '" + ShaderName + "'). If it was " +
                    "just added, assign it to the Shader field or use the " +
                    "component menu → Rebuild Fog Sphere. The fog stays " +
                    "hidden until then.");
            }
            enabled = false;
            return false;
        }

        GameObject fogGo = new GameObject(FogChildName);
        fogTransform = fogGo.transform;
        fogTransform.SetParent(transform, false);

        MeshFilter filter = fogGo.AddComponent<MeshFilter>();
        filter.sharedMesh = Resources.GetBuiltinResource<Mesh>(BuiltinSphereMesh);

        fogRenderer = fogGo.AddComponent<MeshRenderer>();

        fogMaterial = new Material(fogShader);
        fogMaterial.renderQueue = (int)RenderQueue.Transparent;
        fogRenderer.sharedMaterial = fogMaterial;

        // invisible to every other system:
        fogRenderer.shadowCastingMode = ShadowCastingMode.Off;   // no shadow casting
        fogRenderer.receiveShadows = false;                       // no shadow receiving
        fogRenderer.lightProbeUsage = LightProbeUsage.Off;        // no probe sampling
        fogRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        fogRenderer.enabled = false;                              // LateUpdate shows it

        // force a full parameter upload on the first LateUpdate
        InvalidateParamCache();

        if (debugLogs)
        {
            Debug.Log("[FogSphere] '" + name + "': fog sphere built (" +
                      "shader '" + fogShader.name + "', no collider added).");
        }
        return true;
    }

    private void InvalidateParamCache()
    {
        lastColor = new Color(-1f, -1f, -1f, -1f);
        lastDensity = -1f;
        lastNoiseAmount = -1f;
        lastNoiseScale = -1f;
        lastDrift = -1f;
        lastRadius = -1f;
        lastBeamPos = new Vector4(0f, 0f, 0f, -1f);
        lastBeamDir = new Vector4(0f, 0f, 0f, -1f);
        lastBeamColor = new Vector4(-1f, -1f, -1f, -1f);
        lastBeamParams = new Vector4(-1f, -1f, -1f, -1f);
    }

    // ------------------------------------------------------------------ //
    // Target / camera resolution
    // ------------------------------------------------------------------ //

    private Transform ResolveFollowTarget()
    {
        if (followTargetOverride != null) return followTargetOverride;
        if (followTarget != null) return followTarget;

        refindTimer -= Time.deltaTime;
        if (refindTimer > 0f) return null;
        refindTimer = RefindInterval;

        playerFindAttempts++;
        GameObject player = null;
        try { player = GameObject.FindGameObjectWithTag(playerTag); }
        catch { /* undefined tag — treated as "not found", like Unity */ }

        if (player != null)
        {
            followTarget = player.transform;
            if (debugLogs)
            {
                Debug.Log("[FogSphere] '" + name + "': following '" +
                          player.name + "' (tag '" + playerTag + "').");
            }
        }
        else if (playerFindAttempts >= 3 && !warnedNoPlayer)
        {
            warnedNoPlayer = true;
            Debug.LogWarning("[FogSphere] '" + name + "': no object tagged '" +
                playerTag + "' found. Until one appears, the fog follows " +
                "this object's own position. Drag the player into 'Follow " +
                "Target' if the player uses a different tag.");
        }
        return null;
    }

    private void RefreshCamera()
    {
        if (mainCamera != null) return;

        cameraRefindTimer -= Time.deltaTime;
        if (cameraRefindTimer > 0f) return;
        cameraRefindTimer = RefindInterval;

        mainCamera = Camera.main;
        if (mainCamera != null)
        {
            if (mainCamera.orthographic && !warnedOrtho)
            {
                warnedOrtho = true;
                Debug.LogWarning("[FogSphere] '" + name + "': the main " +
                    "camera is orthographic — fog distances are " +
                    "approximate with a flat camera. A perspective camera " +
                    "shows the intended depth-accurate look.");
            }
        }
    }

    /// <summary>
    /// Asks the camera for its depth texture once per camera. Built-In
    /// rendering requires this for the fog's depth reads; URP reads the
    /// same flag and most setups honour it too.
    /// </summary>
    private void EnsureDepthTexture()
    {
        if (mainCamera == null) return;
        if (mainCamera == depthModeCamera) return;   // already asked this one

        depthModeCamera = mainCamera;
        mainCamera.depthTextureMode |= DepthTextureMode.Depth;
    }

    /// <summary>
    /// Pipeline awareness: HDRP is not supported (the shader ships URP +
    /// Built-In passes) — hide the fog there and say so once. On URP, check
    /// once that the asset produces a depth texture, which the fog's depth
    /// needs, and explain the toggle if it does not.
    /// </summary>
    private void RefreshPipelineState()
    {
        RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
        if (pipeline == lastPipeline) return;   // same pipeline as last frame
        lastPipeline = pipeline;

        hdrpBlocked = false;
        if (pipeline == null) return;           // Built-In — nothing to check

        string pipelineType = pipeline.GetType().Name;
        if (pipelineType.Contains("HDRenderPipeline"))
        {
            hdrpBlocked = true;
            if (!warnedHdrp)
            {
                warnedHdrp = true;
                Debug.LogWarning("[FogSphere] '" + name + "': HDRP detected. " +
                    "The fake fog shader supports URP and the Built-In " +
                    "Render Pipeline; the fog is hidden under HDRP.");
            }
            return;
        }

        // An SRP that is not HDRP — treat as URP-family and ask (via
        // reflection, so no compile-time package dependency) whether the
        // asset renders a depth texture.
        if (warnedDepthUrp) return;
        try
        {
            PropertyInfo property = pipeline.GetType()
                .GetProperty("supportsCameraDepthTexture", BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.PropertyType == typeof(bool))
            {
                object value = property.GetValue(pipeline, null);
                if (value is bool && !(bool)value)
                {
                    warnedDepthUrp = true;
                    Debug.LogWarning("[FogSphere] '" + name + "': your URP " +
                        "asset appears to have Depth Texture turned OFF. The " +
                        "fog reads it to get its fake depth — without it the " +
                        "fog looks like a flat shell. Fix: Project Settings → " +
                        "Graphics (or Quality → Rendering) → select the URP " +
                        "asset → Rendering → tick 'Depth Texture'.");
                }
            }
        }
        catch { /* reflection is best-effort — never let it break the fog */ }
    }

    // ------------------------------------------------------------------ //
    // Parameter sync (equality-guarded — zero writes when nothing changes)
    // ------------------------------------------------------------------ //

    private void SyncLookParams()
    {
        if (fogColor != lastColor)
        {
            lastColor = fogColor;
            fogMaterial.SetColor("_FogColor", fogColor);
        }
        if (density != lastDensity)
        {
            lastDensity = density;
            fogMaterial.SetFloat("_FogDensity", density);
        }
        if (noiseAmount != lastNoiseAmount)
        {
            lastNoiseAmount = noiseAmount;
            fogMaterial.SetFloat("_NoiseAmount", noiseAmount);
        }
        if (noiseScale != lastNoiseScale)
        {
            lastNoiseScale = noiseScale;
            fogMaterial.SetFloat("_NoiseScale", Mathf.Max(noiseScale, 0.05f));
        }
        if (driftSpeed != lastDrift)
        {
            lastDrift = driftSpeed;
            fogMaterial.SetFloat("_DriftSpeed", driftSpeed);
        }
    }

    private void SyncBeamLight()
    {
        Light light = beamLight != null ? beamLight : ResolveAutoBeamLight();

        bool lit = light != null && light.enabled && beamGlow > 0.001f;
        float strength = 0f;
        if (lit)
        {
            // dim lights glow less, normal flashlights saturate quickly
            strength = beamGlow * Mathf.Clamp01(light.intensity * 0.25f);
        }

        if (lit)
        {
            Vector3 position = light.transform.position;
            Vector3 forward = light.transform.forward;
            Vector4 beamPos = new Vector4(position.x, position.y, position.z, 0f);
            Vector4 beamDir = new Vector4(forward.x, forward.y, forward.z, 0f);
            Color beamColor = light.color;

            if (beamPos != lastBeamPos)
            {
                lastBeamPos = beamPos;
                fogMaterial.SetVector("_BeamPos", beamPos);
            }
            if (beamDir != lastBeamDir)
            {
                lastBeamDir = beamDir;
                fogMaterial.SetVector("_BeamDir", beamDir);
            }
            if ((Vector4)beamColor != lastBeamColor)
            {
                lastBeamColor = beamColor;
                fogMaterial.SetColor("_BeamColor", beamColor);
            }

            float halfAngleRad = light.spotAngle * 0.5f * Mathf.Deg2Rad;
            float cosOuter = Mathf.Cos(halfAngleRad);
            float cosInner = Mathf.Cos(halfAngleRad * 0.6f);   // soft core
            Vector4 beamParams = new Vector4(light.range, cosOuter, cosInner, strength);
            if (beamParams != lastBeamParams)
            {
                lastBeamParams = beamParams;
                fogMaterial.SetVector("_BeamParams", beamParams);
            }
        }
        else if (lastBeamParams.w != 0f)
        {
            // one final write hides the glow (the shader skips all beam
            // math when the strength component is 0)
            lastBeamParams = new Vector4(lastBeamParams.x, lastBeamParams.y,
                                         lastBeamParams.z, 0f);
            fogMaterial.SetVector("_BeamParams", lastBeamParams);
        }
    }

    /// <summary>
    /// Auto-finds the flashlight: the first Spot Light under the player
    /// (or, failing that, under the main camera) — the usual mountings for
    /// a FlashlightController light. Retries quietly every couple of
    /// seconds; absence is a valid state (no glow), so it never warns.
    /// </summary>
    private Light ResolveAutoBeamLight()
    {
        if (autoBeamLight != null) return autoBeamLight;

        beamFindTimer -= Time.deltaTime;
        if (beamFindTimer > 0f) return null;
        beamFindTimer = BeamFindInterval;

        Transform follow = followTargetOverride != null ? followTargetOverride : followTarget;
        Light found = FindSpotLightUnder(follow) ?? FindSpotLightUnder(
            mainCamera != null ? mainCamera.transform : null);
        if (found != null)
        {
            autoBeamLight = found;
            if (debugLogs)
            {
                Debug.Log("[FogSphere] '" + name + "': light '" + found.name +
                          "' glows through the fog (auto-found).");
            }
        }
        return null;
    }

    private static Light FindSpotLightUnder(Transform root)
    {
        if (root == null) return null;
        Light[] lights = root.GetComponentsInChildren<Light>(true);
        Light firstAny = null;
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].type == LightType.Spot) return lights[i];
            if (firstAny == null) firstAny = lights[i];
        }
        return firstAny;
    }

    // ------------------------------------------------------------------ //
    // Visibility
    // ------------------------------------------------------------------ //

    private void SyncRendererEnabled()
    {
        if (fogRenderer == null) return;
        bool want = isActiveAndEnabled && !hdrpBlocked && density > 0.001f;
        if (fogRenderer.enabled != want) fogRenderer.enabled = want;
    }

    // ------------------------------------------------------------------ //
    // Editor support
    // ------------------------------------------------------------------ //

    private void OnValidate()
    {
        // clamp anything the Inspector or a direct assignment could break
        radius = Mathf.Clamp(radius, 1f, 50f);
        density = Mathf.Clamp01(density);
        noiseAmount = Mathf.Clamp01(noiseAmount);
        noiseScale = Mathf.Max(noiseScale, 0.05f);
        driftSpeed = Mathf.Clamp(driftSpeed, 0f, 2f);
        beamGlow = Mathf.Clamp(beamGlow, 0f, 3f);
        centerHeightOffset = Mathf.Clamp(centerHeightOffset, -2f, 2f);

        // auto-fill the shader slot so the scene keeps a direct reference
        // to it (that reference is what carries the shader into builds)
        if (fogShader == null)
        {
            Shader found = Shader.Find(ShaderName);
            if (found != null) fogShader = found;
        }

        // if the shader was missing earlier and has just been assigned,
        // bring the fog back to life right away during Play
        if (warnedNoShader && fogShader != null && Application.isPlaying && fogTransform == null)
        {
            enabled = true;
            BuildFogSphere();
        }
    }

    private void Reset()
    {
        fogShader = Shader.Find(ShaderName);
    }
}
