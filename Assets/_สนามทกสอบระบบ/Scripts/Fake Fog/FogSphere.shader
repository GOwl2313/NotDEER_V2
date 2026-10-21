// ============================================================================
// FogSphere.shader — FAKE FOG SPHERE for NotDEER (v1.1)
//
// v1.1 — Unity 6000.5 compile fix (reported from a live project): the
//        Built-In depth read used UNITY_SAMPLE_DEPTH(tex, uv), but that
//        macro has been the DEPRECATED ONE-ARGUMENT form — (value).r — for
//        years, so Unity 6000.5 rejects the two-argument call. Now it uses
//        the canonical SAMPLE_DEPTH_TEXTURE(tex, uv) (the documented
//        replacement, identical on every version), and declares
//        _CameraDepthTexture itself with UNITY_DECLARE_DEPTH_TEXTURE —
//        exactly the pattern Unity's own Built-In depth shaders use.
//
// One file, TWO render pipelines:
//   • SubShader 1 (tagged UniversalPipeline) is used by URP — what Unity 6
//     projects render with.
//   • SubShader 2 (untagged) is used by the Built-In Render Pipeline.
//   Each one uses only its own pipeline's canonical depth-reading calls,
//   so the engine simply picks the right pass and both stay 100% standard.
//
// WHY IT NEVER LOOKS LIKE A FLAT BARRIER
//   The sphere mesh is just a canvas. Per pixel, the shader:
//     1. reads the SCENE DEPTH behind the sphere surface,
//     2. casts the view ray analytically through the fog ball and measures
//        how many METERS of fog sit in front of that scene surface,
//     3. applies Beer–Lambert absorption — so objects brushing the fog's
//        edge are barely misted and objects deep inside are swallowed,
//        and the ball's silhouette is naturally thin at the rim, thick in
//        the middle, like a real volume.
//   A rolling trig-noise on the fog's midpoint makes it billow, and the
//   flashlight's beam adds a soft in-scattering glow (light through fog,
//   like through blurry glass) — the beam params arrive from FogSphere.cs.
//
// Draw state: transparent blend, no depth write, no face culling, and
// ZTest Always — the sampled scene depth handles occlusion (a wall closer
// than the fog clips the fog to zero meters), which is what lets a sphere
// fragment that is technically behind a wall still fog the pixels that
// need it. All uniforms live in the UnityPerMaterial cbuffer (URP
// subshader) so the SRP Batcher keeps this at one draw call.
// ============================================================================

Shader "NotDEER/FogSphere"
{
    Properties
    {
        _FogColor ("Fog Color", Color) = (0.72, 0.76, 0.82, 1)
        _FogRadius ("Fog Radius (world units)", Float) = 8
        _FogDensity ("Fog Density", Range(0, 1)) = 0.45
        _NoiseAmount ("Noise Amount", Range(0, 1)) = 0.35
        _NoiseScale ("Noise Scale", Float) = 0.35
        _DriftSpeed ("Drift Speed", Range(0, 2)) = 0.35
        _BeamPos ("Beam Position", Vector) = (0, 0, 0, 0)
        _BeamDir ("Beam Direction", Vector) = (0, 0, 1, 0)
        _BeamColor ("Beam Color", Color) = (1, 1, 1, 1)
        _BeamParams ("Beam Params (range, cosOuter, cosInner, strength)", Vector) = (10, 0.9, 0.98, 1)
    }

    // ====================================================================== //
    //  SUBSHADER 1 — UNIVERSAL RENDER PIPELINE (Unity 6 default)            //
    // ====================================================================== //

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Sphere"
        }

        // -- shared fog body (pure HLSL — no pipeline types) ---------------- //
        HLSLINCLUDE

        struct FogInput
        {
            float3 rayOrigin;      // world-space camera position
            float3 surfacePos;     // world-space position of this sphere fragment
            float3 fogCenter;      // world-space sphere centre
            float  sceneDistance;  // linear eye depth of the scene behind the pixel
            float4 fogColor;
            float  radius;
            float  density;
            float  noiseAmount;
            float  noiseScale;
            float  driftSpeed;
            float  time;
            float4 beamPos;        // xyz = light position
            float4 beamDir;        // xyz = normalised light direction
            float4 beamColor;      // rgb = light colour
            float4 beamParams;     // x = range, y = cos(outer), z = cos(inner), w = strength
        };

        // Smooth, cheap "billow" noise: layered sines, no textures.
        float FogNoise(float3 p, float t)
        {
            float n1 = sin(p.x + sin(p.y * 1.13 - t * 0.31));
            float n2 = sin(p.z * 1.07 + sin(p.x * 0.83 + t * 0.27));
            float n3 = sin((p.x + p.y + p.z) * 0.59 - t * 0.17);
            return (n1 + n2 + n3) * (1.0 / 3.0);   // ~[-1, 1]
        }

        float4 FogFragment(FogInput fog)
        {
            // ---- the view ray ----
            float3 ray = fog.surfacePos - fog.rayOrigin;
            float rayLen = length(ray);
            ray = ray / max(rayLen, 1e-4);

            // ---- where the ray enters and leaves the fog sphere ----
            float3 oc = fog.rayOrigin - fog.fogCenter;
            float b = dot(oc, ray);
            float c = dot(oc, oc) - fog.radius * fog.radius;
            float disc = b * b - c;
            if (disc <= 0.0)
            {
                return float4(0.0, 0.0, 0.0, 0.0);   // ray misses (cannot normally happen)
            }

            float sq = sqrt(disc);
            float tEnter = max(-b - sq, 0.0);        // camera inside the sphere -> fog starts at the camera
            float tExit = -b + sq;

            // ---- meters of fog in front of the scene surface ----
            float tScene = max(fog.sceneDistance, 0.0);
            float traversal = min(tExit, tScene) - tEnter;
            if (traversal <= 0.0)
            {
                return float4(0.0, 0.0, 0.0, 0.0);   // surface is closer than the fog
            }

            // ---- fog midpoint (gives the noise + the light glow "volume") ----
            float3 midPoint = fog.rayOrigin + ray * (tEnter + traversal * 0.5);

            // ---- density: base * gentle billow ----
            float noise = FogNoise(midPoint * fog.noiseScale, fog.time * fog.driftSpeed);
            float density = fog.density * (1.0 + noise * fog.noiseAmount * 0.6);

            // ---- Beer-Lambert: thicker fog = deeper burial ----
            float alpha = 1.0 - exp(-density * 1.6 * traversal);
            alpha = saturate(alpha) * fog.fogColor.a;

            // ---- light bleeding through the fog (blurry glass) ----
            float3 glow = float3(0.0, 0.0, 0.0);
            if (fog.beamParams.w > 0.001)
            {
                float3 toFog = midPoint - fog.beamPos.xyz;
                float dist = length(toFog);
                float3 dirToFog = toFog / max(dist, 1e-4);

                float cone = dot(dirToFog, fog.beamDir.xyz);
                cone = saturate((cone - fog.beamParams.y) /
                                max(fog.beamParams.z - fog.beamParams.y, 1e-4));
                cone = cone * cone;                              // soft cone edge

                float att = saturate(1.0 - dist / max(fog.beamParams.x, 1e-4));
                att = att * att;                                 // soft range falloff

                glow = fog.beamColor.rgb * (cone * att * fog.beamParams.w * alpha);
            }

            return float4(fog.fogColor.rgb + glow, alpha);
        }

        ENDHLSL

        Pass
        {
            Name "FogSphereURP"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _FogColor;
                float  _FogRadius;
                float  _FogDensity;
                float  _NoiseAmount;
                float  _NoiseScale;
                float  _DriftSpeed;
                float4 _BeamPos;
                float4 _BeamDir;
                float4 _BeamColor;
                float4 _BeamParams;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.screenPos = vertexInput.positionNDC;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.screenPos.xy / input.screenPos.w;
                float rawDepth = SampleSceneDepth(uv);
                float sceneDistance = LinearEyeDepth(rawDepth, _ZBufferParams);

                FogInput fog;
                fog.rayOrigin = _WorldSpaceCameraPos;
                fog.surfacePos = input.positionWS;
                fog.fogCenter = mul(UNITY_MATRIX_M, float4(0.0, 0.0, 0.0, 1.0)).xyz;
                fog.sceneDistance = sceneDistance;
                fog.fogColor = _FogColor;
                fog.radius = _FogRadius;
                fog.density = _FogDensity;
                fog.noiseAmount = _NoiseAmount;
                fog.noiseScale = _NoiseScale;
                fog.driftSpeed = _DriftSpeed;
                fog.time = _Time.y;
                fog.beamPos = _BeamPos;
                fog.beamDir = _BeamDir;
                fog.beamColor = _BeamColor;
                fog.beamParams = _BeamParams;

                return FogFragment(fog);
            }
            ENDHLSL
        }
    }

    // ====================================================================== //
    //  SUBSHADER 2 — BUILT-IN RENDER PIPELINE                                //
    // ====================================================================== //

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Sphere"
        }

        // -- the same shared fog body (pure HLSL, identical math) ------------ //
        CGINCLUDE

        struct FogInput
        {
            float3 rayOrigin;
            float3 surfacePos;
            float3 fogCenter;
            float  sceneDistance;
            float4 fogColor;
            float  radius;
            float  density;
            float  noiseAmount;
            float  noiseScale;
            float  driftSpeed;
            float  time;
            float4 beamPos;
            float4 beamDir;
            float4 beamColor;
            float4 beamParams;
        };

        float FogNoise(float3 p, float t)
        {
            float n1 = sin(p.x + sin(p.y * 1.13 - t * 0.31));
            float n2 = sin(p.z * 1.07 + sin(p.x * 0.83 + t * 0.27));
            float n3 = sin((p.x + p.y + p.z) * 0.59 - t * 0.17);
            return (n1 + n2 + n3) * (1.0 / 3.0);
        }

        float4 FogFragment(FogInput fog)
        {
            float3 ray = fog.surfacePos - fog.rayOrigin;
            float rayLen = length(ray);
            ray = ray / max(rayLen, 1e-4);

            float3 oc = fog.rayOrigin - fog.fogCenter;
            float b = dot(oc, ray);
            float c = dot(oc, oc) - fog.radius * fog.radius;
            float disc = b * b - c;
            if (disc <= 0.0)
            {
                return float4(0.0, 0.0, 0.0, 0.0);
            }

            float sq = sqrt(disc);
            float tEnter = max(-b - sq, 0.0);
            float tExit = -b + sq;

            float tScene = max(fog.sceneDistance, 0.0);
            float traversal = min(tExit, tScene) - tEnter;
            if (traversal <= 0.0)
            {
                return float4(0.0, 0.0, 0.0, 0.0);
            }

            float3 midPoint = fog.rayOrigin + ray * (tEnter + traversal * 0.5);

            float noise = FogNoise(midPoint * fog.noiseScale, fog.time * fog.driftSpeed);
            float density = fog.density * (1.0 + noise * fog.noiseAmount * 0.6);

            float alpha = 1.0 - exp(-density * 1.6 * traversal);
            alpha = saturate(alpha) * fog.fogColor.a;

            float3 glow = float3(0.0, 0.0, 0.0);
            if (fog.beamParams.w > 0.001)
            {
                float3 toFog = midPoint - fog.beamPos.xyz;
                float dist = length(toFog);
                float3 dirToFog = toFog / max(dist, 1e-4);

                float cone = dot(dirToFog, fog.beamDir.xyz);
                cone = saturate((cone - fog.beamParams.y) /
                                max(fog.beamParams.z - fog.beamParams.y, 1e-4));
                cone = cone * cone;

                float att = saturate(1.0 - dist / max(fog.beamParams.x, 1e-4));
                att = att * att;

                glow = fog.beamColor.rgb * (cone * att * fog.beamParams.w * alpha);
            }

            return float4(fog.fogColor.rgb + glow, alpha);
        }

        ENDCG

        Pass
        {
            Name "FogSphereBuiltIn"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            // UnityCG does not declare the depth texture — every Built-In
            // depth-reading shader (Unity's own included) declares it with
            // this macro; it expands to the right sampler type per platform.
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4 _FogColor;
            float  _FogRadius;
            float  _FogDensity;
            float  _NoiseAmount;
            float  _NoiseScale;
            float  _DriftSpeed;
            float4 _BeamPos;
            float4 _BeamDir;
            float4 _BeamColor;
            float4 _BeamParams;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float sceneDistance = LinearEyeDepth(rawDepth);

                FogInput fog;
                fog.rayOrigin = _WorldSpaceCameraPos;
                fog.surfacePos = i.worldPos;
                fog.fogCenter = mul(unity_ObjectToWorld, float4(0.0, 0.0, 0.0, 1.0)).xyz;
                fog.sceneDistance = sceneDistance;
                fog.fogColor = _FogColor;
                fog.radius = _FogRadius;
                fog.density = _FogDensity;
                fog.noiseAmount = _NoiseAmount;
                fog.noiseScale = _NoiseScale;
                fog.driftSpeed = _DriftSpeed;
                fog.time = _Time.y;
                fog.beamPos = _BeamPos;
                fog.beamDir = _BeamDir;
                fog.beamColor = _BeamColor;
                fog.beamParams = _BeamParams;

                return FogFragment(fog);
            }
            ENDCG
        }
    }

    // The pipeline-specific subshaders above are the complete surface;
    // a generic fallback would render wrong, so there is none.
    FallBack Off
}
