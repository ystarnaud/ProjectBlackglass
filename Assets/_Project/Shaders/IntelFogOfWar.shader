// Fog of war (decision 037, presentation only). Drawn on a box covering the mission, after everything else: for each pixel it
// reads the scene depth, rebuilds the world position of whatever is drawn there, looks up the brightness grid at that
// position's x and z, and blends toward the dark colour by (1 - brightness). The grid is feathered on the CPU, so the edge
// of the dark is a smooth ramp whatever the height of what it falls on: floor, wall face, wall top, cover. Needs the
// camera's depth texture (FogPresenter asks for it).
Shader "Blackglass/IntelFogOfWar"
{
    Properties
    {
        [NoScaleOffset] _VisTex ("Brightness grid", 2D) = "white" {}
        _VisRect ("Grid area (x, z, 1/width, 1/depth)", Vector) = (0, 0, 1, 1)
        _DarkColor ("Dark", Color) = (0.02, 0.02, 0.03, 1)
        _MaxHeight ("Highest point darkened (m)", Float) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent+100" }

        Pass
        {
            Name "FogOfWar"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_VisTex);
            SAMPLER(sampler_VisTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _VisRect;
                half4 _DarkColor;
                float _MaxHeight;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float depth = SampleSceneDepth(uv);
                // Nothing drawn here (sky or clear colour): leave it alone.
                #if UNITY_REVERSED_Z
                    if (depth < 0.000001) discard;
                #else
                    if (depth > 0.999999) discard;
                #endif

                float3 world = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float2 grid = (world.xz - _VisRect.xy) * _VisRect.zw;
                if (any(grid < 0.0) || any(grid > 1.0) || world.y > _MaxHeight)
                    discard;

                half brightness = SAMPLE_TEXTURE2D_LOD(_VisTex, sampler_VisTex, grid, 0).r;
                return half4(_DarkColor.rgb, (1.0h - brightness) * _DarkColor.a);
            }
            ENDHLSL
        }
    }
}
