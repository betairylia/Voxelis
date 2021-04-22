Shader "Custom/InstancedTest_GS" {
    Properties{
        _MainTex("Albedo (RGB)", 2D) = "white" {}
        _LODDistance("LOD distance to billboards", Float) = 100.0
        _Size("Billboard size", Float) = 1.0
    }
    SubShader{

        Pass {

            Tags {"LightMode" = "ForwardBase"}

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma geometry geom
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            #pragma target 4.5

            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"
            #include "AutoLight.cginc"

            #include "primitives.cginc"

            sampler2D _MainTex;
            float _LODDistance;
            float _Size;

            #include "../../Data/Block.cginc"

            struct GS_PointVertex
            {
                uint data;
                Block block;
            };

        #if SHADER_TARGET >= 45
            StructuredBuffer<GS_PointVertex> vbuffer;
        #endif

            struct v2g
            {
                float4 pos : SV_POSITION;
            };

            struct g2f
            {
                float4 pos : SV_POSITION;
                //float2 uv_MainTex : TEXCOORD0;
                float3 ambient : TEXCOORD1;
                float3 diffuse : TEXCOORD2;
                float3 color : TEXCOORD3;
                SHADOW_COORDS(4)
            };

            void rotate2D(inout float2 v, float r)
            {
                float s, c;
                sincos(r, s, c);
                v = float2(v.x * c - v.y * s, v.x * s + v.y * c);
            }

            float4x4 _LocalToWorld;
            float4x4 _WorldToLocal;

            v2g vert(appdata_full v, uint vID : SV_VertexID)
            {
            #if SHADER_TARGET >= 45
                GS_PointVertex vdata = vbuffer[vID];
                float4 data = float4(
                    (vdata.data >> 10) & 0x1f,
                    (vdata.data >> 5) & 0x1f,
                    (vdata.data) & 0x1f,
                    1
                );

                data = mul(_LocalToWorld, data);
            #else
                float4 data = 0;
            #endif

                /*float rotation = data.w * data.w * _Time.x * 0.5f;
                rotate2D(data.xz, rotation);*/

                // Transform modification
                unity_ObjectToWorld = _LocalToWorld;
                unity_WorldToObject = _WorldToLocal;

                v2g o;
                o.pos = float4(data.xyz, 1);
                return o;

                /*float3 localPosition = v.vertex.xyz * data.w;
                float3 worldPosition = data.xyz + localPosition;
                float3 worldNormal = v.normal;

                float3 ndotl = saturate(dot(worldNormal, _WorldSpaceLightPos0.xyz));
                float3 ambient = ShadeSH9(float4(worldNormal, 1.0f));
                float3 diffuse = (ndotl * _LightColor0.rgb);
                float3 color = v.color;

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPosition, 1.0f));
                o.uv_MainTex = v.texcoord;
                o.ambient = ambient;
                o.diffuse = diffuse;
                o.color = color;
                TRANSFER_SHADOW(o)
                return o;*/
            }

            [maxvertexcount(60)]
            void geom(point v2g points[1], inout TriangleStream<g2f> triStream)
            {
                g2f o;

                float dist = length(_WorldSpaceCameraPos - points[0].pos);
                //float3 color = float3(dist / _LODDistance, dist / _LODDistance, dist / _LODDistance);
                float3 color = float3(1.0, 1.0, 1.0);

                int i = 0;
                for (int f = 0; f < 20; f++)
                {
                    for (int t = 0; t < 3; t++)
                    {
                        i = f * 3 + t;
                        o.pos = UnityObjectToClipPos(points[0].pos + ico_pos[i]);
                        UNITY_TRANSFER_FOG(o, o.pos);

                        // Basic lighting
                        float3 worldNormal = ico_normal[i];
                        float3 ndotl = saturate(dot(worldNormal, _WorldSpaceLightPos0.xyz));
                        float3 ambient = ShadeSH9(float4(worldNormal, 1.0f));
                        float3 diffuse = (ndotl * _LightColor0.rgb);

                        o.ambient = ambient;
                        o.diffuse = diffuse;
                        o.color = color;

                        TRANSFER_SHADOW(o)

                        triStream.Append(o);
                    }
                    triStream.RestartStrip();
                }
            }

            fixed4 frag(g2f i) : SV_Target
            {
                fixed shadow = SHADOW_ATTENUATION(i);
                fixed4 albedo = float4(1.0, 1.0, 1.0, 1.0);
                float3 lighting = i.diffuse * shadow + i.ambient;
                fixed4 output = fixed4(albedo.rgb * i.color * lighting, albedo.w);
                UNITY_APPLY_FOG(i.fogCoord, output);
                return output;
            }

            ENDCG
        }
    }
}