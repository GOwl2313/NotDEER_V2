// InteractionRing.shader  (v3.0 — GTA "hollowed cylinder" marker)
//
// The visual for InteractionPoint.cs — a glowing GTA-San-Andreas-style marker:
// a VERTICAL TRANSLUCENT CYLINDER of light (see the reference picture: the red
// marker column). The mesh is an open-ended tube (no caps — you can see
// through the middle), built procedurally by InteractionPoint.cs.
//
// THE LOOK (matches the reference image):
//   • the cylinder WALLS glow softly (body translucency, _WallAlpha);
//   • the SILHOUETTE edges glow much brighter — physically what happens when
//     you look through more of a translucent volume at a grazing angle
//     (Fresnel term, _EdgePower/_EdgeBoost) — this is what makes the marker
//     read as a HOLLOWED CYLINDER instead of a flat disc;
//   • hot RIM BANDS at the very top and bottom (_RimWidth/_RimIntensity)
//     sharpen the cylinder shape, exactly like the GTA marker;
//   • a vertical gradient (_TopFade) keeps the bottom strong while the top
//     softly dissolves into the air.
//
// RENDER PIPELINE COMPATIBILITY (ONE shader, TWO SubShaders):
//   • SubShader #1 — URP-native (Unity 6's default pipeline): HLSLPROGRAM,
//     CBUFFER UnityPerMaterial + instancing macros → SRP Batcher
//     compatible, GPU-instancing variants compiled in. URP picks this
//     SubShader automatically because of its "RenderPipeline" tag.
//   • SubShader #2 — Built-in Render Pipeline: the classic CGPROGRAM pass.
//     Built-in RP skips SubShader #1 (tagged for URP) and uses this.
//   Both passes produce the SAME image, so the same file works in URP and
//   Built-in projects without any setup. HDRP is NOT supported.
//
// PERFORMANCE: the whole effect is ONE open tube (48 segments → 96 triangles)
// with ONE cheap unlit transparent pass — no lights, no shadows, no
// particles, no post-processing, no textures. InteractionPoint.cs disables
// the renderer entirely while a point is depleted (zero draw calls), and all
// instances share one shader (SRP Batcher keeps them in one batch group).
//
// ANIMATION: the pulse phase is driven from InteractionPoint.cs through the
// _PulsePhase/_Fade properties instead of shader time, so the glow visibly
// FREEZES when the game is paused (GamePauseManager, Time.timeScale = 0).
// Fog is deliberately NOT applied: a guidance marker should stay readable
// in foggy horror scenes.
//
// SETUP: keep this file anywhere under Assets/ (next to InteractionPoint.cs
// is fine). The shader NAME is unchanged since v1.0, so the auto-created
// material Assets/Materials/ZAI_InteractionRing.mat and every saved scene
// keep referencing it after you overwrite this file — no re-assignment
// needed. InteractionPoint.cs pushes every visual value into the material,
// so old materials with v2 ring values simply take the new cylinder look.

Shader "ZAI/Unlit/InteractionRing"
{
    Properties
    {
        _Color       ("Tint (alpha = opacity)",        Color)            = (1.0, 0.35, 0.06, 0.85)
        _WallAlpha   ("Wall Glow (body, 0-1)",         Range(0.0, 1.0))  = 0.30
        _EdgePower   ("Edge Falloff Power",            Range(0.25, 8.0)) = 2.5
        _EdgeBoost   ("Edge Glow Strength",            Range(0.0, 4.0))  = 1.75
        _RimWidth    ("Rim Band Height (fraction)",    Range(0.0, 0.5))  = 0.12
        _RimIntensity("Rim Band Strength",             Range(0.0, 4.0))  = 2.4
        _TopFade     ("Top Fade (0 = even, 1 = soft)", Range(0.0, 1.0))  = 0.45
        _Fade        ("Fade (0-1)",                    Range(0.0, 1.0))  = 1.0
        _PulseAmount ("Pulse Amount",                  Range(0.0, 1.0))  = 0.15
        _PulsePhase  ("Pulse Phase (0-1)",             Range(0.0, 1.0))  = 0.0
    }

    // ================================================================== //
    // SubShader #1 — Universal Render Pipeline (Unity 6 default).        //
    // URP selects this SubShader because of the RenderPipeline tag;      //
    // the Built-in RP skips it and uses SubShader #2 below.              //
    // ================================================================== //
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalRenderPipeline"
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Cylinder"
        }

        Pass
        {
            Name "UnlitURP"
            Tags { "LightMode"="UniversalForward" }

            Cull Off          // tube walls visible from both sides (also when standing inside)
            ZWrite Off        // never writes depth — never z-fights, never occludes
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB     // do not pollute the frame buffer's alpha channel

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   3.0
            #pragma multi_compile_instancing

            // URP core: TransformObjectToHClip / TransformObjectToWorld /
            // TransformObjectToWorldNormal + the instancing/stereo macros.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // SRP Batcher: every per-material property the shader uses must
            // live in this exact cbuffer. InteractionPoint.cs only calls
            // SetFloat/SetColor on these — the batch stays intact.
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half  _WallAlpha;
                half  _EdgePower;
                half  _EdgeBoost;
                half  _RimWidth;
                half  _RimIntensity;
                half  _TopFade;
                half  _Fade;
                half  _PulseAmount;
                half  _PulsePhase;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;   // v: 0 at the tube's bottom → 1 at the top
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            static const float TAU = 6.28318530718;

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionWS   = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS  = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS     = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv           = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                // ---- view-dependent edge glow (the "hollowed tube" trick) ----
                // Grazing view angles look through much more of the glowing
                // volume than head-on ones, so the silhouette burns bright
                // while the wall centre stays translucent.
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(_WorldSpaceCameraPos - IN.positionWS);
                float  ndv  = abs(dot(N, V));                     // abs → backfaces glow too (Cull Off)
                float  edge = pow(saturate(1.0 - ndv), _EdgePower);

                // ---- hot rim bands at the very top & bottom of the tube ----
                float rimW    = max(_RimWidth, 0.0001);
                float v       = saturate(IN.uv.y);
                float bottomR = 1.0 - smoothstep(0.0, rimW, v);
                float topR    = smoothstep(1.0 - rimW, 1.0, v);
                float rim     = saturate(bottomR + topR);

                // ---- vertical gradient: strong at the ground, soft at the top ----
                float vertFade = 1.0 - _TopFade * v;

                // ---- breathing pulse (phase fed from the script — freezes with the pause menu) ----
                float pulse = 1.0 - _PulseAmount * (0.5 + 0.5 * sin(_PulsePhase * TAU));

                // ---- compose ----
                float shape = saturate(_WallAlpha + edge * _EdgeBoost + rim * _RimIntensity);
                float hot   = saturate(max(edge, rim));
                float3 col  = lerp(_Color.rgb, _Color.rgb * 1.5 + 0.25, hot * 0.85);

                float alpha = saturate(shape * vertFade * pulse * _Fade) * _Color.a;
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    // ================================================================== //
    // SubShader #2 — Built-in Render Pipeline (classic pass).            //
    // Built-in RP skips SubShader #1 (tagged for URP) and renders this.  //
    // Identical math to the URP pass above.                              //
    // ================================================================== //
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Cylinder" }
        Cull Off          // tube walls visible from both sides (also when standing inside)
        ZWrite Off        // never writes depth — never z-fights, never occludes
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask RGB     // do not pollute the frame buffer's alpha channel

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   3.0
            #include "UnityCG.cginc"

            fixed4 _Color;
            half   _WallAlpha;
            half   _EdgePower;
            half   _EdgeBoost;
            half   _RimWidth;
            half   _RimIntensity;
            half   _TopFade;
            half   _Fade;
            half   _PulseAmount;
            half   _PulsePhase;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;   // v: 0 at the tube's bottom → 1 at the top
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float2 uv        : TEXCOORD0;
                float3 normalWS  : TEXCOORD1;
                float3 positionWS: TEXCOORD2;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos        = UnityObjectToClipPos(v.vertex);
                o.normalWS   = UnityObjectToWorldNormal(v.normal);
                o.positionWS = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv         = v.uv;
                return o;
            }

            static const float TAU = 6.28318530718;

            fixed4 frag (v2f i) : SV_Target
            {
                // ---- view-dependent edge glow (the "hollowed tube" trick) ----
                float3 N = normalize(i.normalWS);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - i.positionWS);
                float  ndv  = abs(dot(N, V));                     // abs → backfaces glow too (Cull Off)
                float  edge = pow(saturate(1.0 - ndv), _EdgePower);

                // ---- hot rim bands at the very top & bottom of the tube ----
                float rimW    = max(_RimWidth, 0.0001);
                float v       = saturate(i.uv.y);
                float bottomR = 1.0 - smoothstep(0.0, rimW, v);
                float topR    = smoothstep(1.0 - rimW, 1.0, v);
                float rim     = saturate(bottomR + topR);

                // ---- vertical gradient: strong at the ground, soft at the top ----
                float vertFade = 1.0 - _TopFade * v;

                // ---- breathing pulse (phase fed from the script — freezes with the pause menu) ----
                float pulse = 1.0 - _PulseAmount * (0.5 + 0.5 * sin(_PulsePhase * TAU));

                // ---- compose ----
                float shape = saturate(_WallAlpha + edge * _EdgeBoost + rim * _RimIntensity);
                float hot   = saturate(max(edge, rim));
                float3 col  = lerp(_Color.rgb, _Color.rgb * 1.5 + 0.25, hot * 0.85);

                float alpha = saturate(shape * vertFade * pulse * _Fade) * _Color.a;
                return fixed4(col, alpha);
            }
            ENDCG
        }
    }
    FallBack Off
}