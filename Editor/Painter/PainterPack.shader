// The Substance 3D Painter link's texture work on the GPU.
// Pass 0 packs the metallic and roughness images Painter streams into the texture a material reads them from (a Standard
// metallic-smoothness map, Poiyomi's packed maps...): each output channel takes the material's own texture, the metallic
// value (or its inverse), the smoothness (one minus roughness) or the roughness, as the masks say.
// Pass 1 turns a normal map as Unity stores it (DXT5nm, BC5...) into plain RGB, for Painter.
// Pass 2 reads one channel of a texture (inverted or not) into a gray image, for Painter.
Shader "Hidden/Orbiters/MyAvatar/PainterPack"
{
    Properties
    {
        _MainTex ("Material's texture", 2D) = "black" {}
        _Metal ("Metallic", 2D) = "black" {}
        _Rough ("Roughness", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex, _Metal, _Rough;
            float4 _KeepMask, _Constant, _MetalMask, _InvertedMetalMask, _SmoothMask, _RoughMask;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 base = tex2D(_MainTex, i.uv);
                float metal = tex2D(_Metal, i.uv).r;
                float rough = tex2D(_Rough, i.uv).r;
                return base * _KeepMask + _Constant + metal * _MetalMask + (1 - metal) * _InvertedMetalMask + (1 - rough) * _SmoothMask + rough * _RoughMask;
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            float4 frag(v2f_img i) : SV_Target
            {
                float3 normal = UnpackNormal(tex2D(_MainTex, i.uv));
                return float4(normal * 0.5 + 0.5, 1);
            }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _ChannelMask;
            float _Invert;

            float4 frag(v2f_img i) : SV_Target
            {
                float value = dot(tex2D(_MainTex, i.uv), _ChannelMask);
                value = lerp(value, 1 - value, _Invert);
                return float4(value, value, value, 1);
            }
            ENDCG
        }
    }
}
