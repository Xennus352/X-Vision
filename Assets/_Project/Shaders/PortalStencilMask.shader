// An invisible surface that marks its own pixels in the stencil buffer, writing no color and no
// depth, and draws after the passthrough image and before the scene's opaque objects (see
// CardboardXR/DepthMask, which writes depth instead). Any interior material that tests the
// stencil for 1 then shows only through the portal; everything else fails the test and the
// passthrough shows through instead. Double-sided, so the door writes from either way round.
// ReadMask and WriteMask are 1 so only stencil bit 0 is involved, leaving the other 7 bits for
// whatever else uses them.
Shader "CardboardXR/PortalStencilMask"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            // After the passthrough image (Background), before the scene (Geometry).
            "Queue" = "Geometry-10"
        }

        Pass
        {
            Name "PortalStencilMask"
            Tags { "LightMode" = "UniversalForward" }

            ColorMask 0
            ZWrite Off
            // Something closer to the camera than the portal occludes the aperture too.
            ZTest LEqual
            Cull Off

            Stencil
            {
                Ref 1
                ReadMask 1
                WriteMask 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            float4 Vert(Attributes input) : SV_POSITION
            {
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half4 Frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
