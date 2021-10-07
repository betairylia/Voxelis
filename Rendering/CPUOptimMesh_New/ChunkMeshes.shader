Shader "Custom/ChunkMeshes"
{
    Properties
    {
        [HDR]_Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("MainAtlas", 2D) = "white" { }
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0.0

        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.7
        _SunDirc ("Sun Direction (Normalized)", Vector) = (0, 1, 0, 0)
        _SunFogFactorPower ("Sun Fog Factor Power", range(1.0, 10.0)) = 1.0
    }
    SubShader
    {
        Pass
        {

            Tags { "LightMode" = "UniversalForward" "RenderType" = "Opaque" }
            
            Cull Off

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float _Metallic;
            float _Glossiness;
            
            float _Cutoff;
            float4 _SunDirc;
            float _SunFogFactorPower;
            float4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;

                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                
                float4 worldPos : POSITION2;
                float3 worldNormal : NORMAL;
                
                float3 viewDir : TEXCOORD1;
                float3 color : COLOR;
                
                #ifdef LIGHTMAP_OFF
                    #if UNITY_SHOULD_SAMPLE_SH
                        half3 sh : TEXCOORD2;
                    #endif
                #endif
                float fogFactor : TEXCOORD3;
            };

            TEXTURE2D(_MainTex);SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            

            //PBR
            float DistributionGGX(float NoH, float a)
            {
                float a2 = a * a;
                float NoH2 = NoH * NoH;

                float nom = a2;
                float denom = NoH2 * (a2 - 1) + 1;
                denom = denom * denom * PI;
                return nom / denom;
            }

            float GeometrySchlickGGX(float NoV, float k)
            {
                float nom = NoV;
                float denom = NoV * (1.0 - k) + k;
                return nom / denom;
            }

            float GeometrySmith(float NoV, float NoL, float k)
            {
                float ggx1 = GeometrySchlickGGX(NoV, k);
                float ggx2 = GeometrySchlickGGX(NoL, k);
                return ggx1 * ggx2;
            }

            float3 FresnelSchlick(float cosTheta, float3 F0)
            {
                return F0 + pow(1.0 - cosTheta, 5.0);
            }

            float3 IndirFresnelSchlick(float cosTheta, float3 F0, float roughness)
            {
                return F0 + (max(float3(1, 1, 1) * (1 - roughness), F0) - F0) * pow(1.0 - cosTheta, 5.0);
            }


            float3 SH_Process(float3 N)
            {
                float4 SH[7];
                SH[0] = unity_SHAr;
                SH[1] = unity_SHAg;
                SH[2] = unity_SHAb;
                SH[3] = unity_SHBr;
                SH[4] = unity_SHBr;
                SH[5] = unity_SHBr;
                SH[6] = unity_SHC;

                return max(0.0, SampleSH9(SH, N));
            }

            real CustomComputeFogIntensity(real fogFactor)
            {
                real fogIntensity = 0.0h;
                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                    #if defined(FOG_EXP)
                        // factor = exp(-density*z)
                        // fogFactor = density*z compute at vertex
                        fogIntensity = saturate(exp2(-fogFactor));
                    #elif defined(FOG_EXP2)
                        // factor = exp(-(density*z)^2)
                        // fogFactor = density*z compute at vertex
                        fogIntensity = saturate(exp2(-fogFactor * fogFactor));
                    #elif defined(FOG_LINEAR)
                        fogIntensity = fogFactor;
                    #endif
                #endif
                return fogIntensity;
            }

            half3 CustomMixFogColor(real3 fragColor, real3 fogColor, real fogFactor, real3 N, real3 L, real3 V, real3 LightColor)
            {
                #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                    real fogIntensity = CustomComputeFogIntensity(fogFactor);
                    real NoL = saturate(dot(N, L));
                    V = -V;
                    real VoL = pow(saturate(dot(V, L)), _SunFogFactorPower * _SunFogFactorPower);
                    if (L.y >= 0.0)
                    {
                        fogColor = lerp(fogColor, LightColor, VoL);
                    }
                    else
                    {
                        fogColor = lerp(fogColor, LightColor, VoL * saturate(1 + 5 * L.y));
                    }
                    fragColor = lerp(fogColor, fragColor, fogIntensity);
                #endif
                return fragColor;
            }

            half3 CustomMixFog(real3 fragColor, real fogFactor, real3 N, real3 L, real3 V, real3 LightColor)
            {
                return CustomMixFogColor(fragColor, unity_FogColor.rgb, fogFactor, N, L, V, LightColor);
            }
            //PBR
            v2f vert(appdata v)
            {
                v2f o;

                o.worldPos = mul(unity_ObjectToWorld, v.vertex);

                o.pos = TransformObjectToHClip(v.vertex);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);
                // o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.uv = v.uv;

                o.viewDir = GetWorldSpaceViewDir(o.worldPos.xyz);

                o.color = v.color.rgb;

                #ifdef LIGHTMAP_OFF
                    #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                        #if UNITY_SHOULD_SAMPLE_SH
                            o.sh = 0;
                            o.sh = SH_Process(o.worldNormal);
                        #endif
                    #endif
                #endif
                o.fogFactor = ComputeFogFactor(o.pos.z);
                
                return o;
            }
            
            float4 frag(v2f i) : SV_Target
            {
                SurfaceData so;
                so = (SurfaceData)0;

                so.albedo = float3(1, 1, 1);// i.color;
                so.metallic = _Metallic;
                so.smoothness = _Glossiness;
                //so.Emission = i.color;
                so.emission = 0.0;
                so.alpha = 1.0;
                so.occlusion = 1.0;
                //so.Normal = i.worldNormal;

                float2 targetUV = i.uv;

                float4 tex;

                tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, targetUV);
                
                if (tex.a <= _Cutoff)
                {
                    discard;
                }// cutoff

                so.albedo = tex.rgb;
                // so.Emission = fixed3(0.5, 0.5, 0.5);

                // Well now all meshes are foilages so
                // so.normalTS = lerp(i.worldNormal, -_SunDirc.xyz, i.color.r * 0.5);

                /////////////////////////// Fill G-Buffer ///////////////////////////

                // Also remove specular
                half spec = 0.00f;
                so.specular = half4(spec, spec, spec, spec);
                //PBR
                //base things
                uint pixelLightCount = GetAdditionalLightsCount();
                float3 AdditionalLightColor = float3(0.0, 0.0, 0.0);
                for (uint lightIndex = 0u; lightIndex < pixelLightCount; ++lightIndex)
                {
                    Light light = GetAdditionalLight(lightIndex, i.worldPos);
                    AdditionalLightColor += light.color * light.distanceAttenuation * light.shadowAttenuation;
                }
                float4 SHADOW_COORDS = TransformWorldToShadowCoord(i.worldPos);
                Light mainLight = GetMainLight(SHADOW_COORDS);
                mainLight.color.rgb += AdditionalLightColor;
                half shadow = MainLightRealtimeShadow(SHADOW_COORDS);
                float3 BaseColor = so.albedo * _Color.rgb;
                float Metallic = so.metallic;
                float3 F0 = lerp(0.04, BaseColor, Metallic);

                #if defined(_SCREEN_SPACE_OCCLUSION)
                    float2 normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.pos);
                    AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(normalizedScreenSpaceUV);
                    mainLight.color *= aoFactor.directAmbientOcclusion;
                    so.occlusion = min(so.occlusion, aoFactor.indirectAmbientOcclusion);
                #endif

                float AO = so.occlusion;
                float smoothness = so.smoothness;
                float TEMProughness = 1 - smoothness;
                float roughness = pow(TEMProughness, 2);

                float3 position = i.worldPos;
                float3 N = i.worldNormal;
                float3 L = normalize(mainLight.direction);
                float3 V = normalize(_WorldSpaceCameraPos - position);
                float3 H = normalize(V + L);
                float3 R = reflect(-V, N);

                float NoV = max(saturate(dot(N, V)), 0.000001);
                float NoL = max(
                    max(saturate(dot(N, L)), 0.000001),
                    i.color.r * 0.75
                );
                float HoV = max(saturate(dot(H, V)), 0.000001);
                float NoH = max(saturate(dot(H, N)), 0.000001);
                float LoH = max(saturate(dot(H, L)), 0.000001);

                float D = DistributionGGX(NoH, roughness);
                float k = pow(1 + roughness, 2) / 8;
                float G = GeometrySmith(NoV, NoL, k);
                float3 F = FresnelSchlick(LoH, F0);

                float3 specular = D * G * F / (4 * NoV * NoL);

                float3 ks = F;
                float3 kd = (1 - ks) * (1 - Metallic);
                float3 diffuse = kd * BaseColor / PI;

                // FIXME: Too bright. Dim the texture and remove *0.82.
                float3 DirectColor = (diffuse + specular) * NoL * PI * mainLight.color * 0.82;

                float3 SH = SH_Process(N) * AO;
                float3 IndirKS = IndirFresnelSchlick(NoV, F0, roughness);
                float3 IndirKD = (1 - IndirKS) * (1 - Metallic);
                float3 IndirDiffColor = SH * IndirKD * BaseColor;

                roughness = roughness * (1.7 - 0.7 * roughness);
                float mip_level = roughness * 6.0;
                float3 IndirSpecularBaseColor = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, R, mip_level) * AO;

                float surfaceReduction = 1.0 / (roughness * roughness + 1.0);
                float ReflectivitySpecular;
                #if defined(SHADER_API_GLES)
                    ReflectivitySpecular = specular.r; // Red channel - because most metals are either monocrhome or with redish/yellowish tint
                #else
                    ReflectivitySpecular = max(max(specular.r, specular.g), specular.b);
                #endif
                half grazingTerm = saturate((1 - roughness) + (1 - (1 - ReflectivitySpecular)));
                half t = pow(1 - NoV, 5);
                float3 FresnelLerp = lerp(F0, grazingTerm, t);

                float3 IndirSpecularResult = IndirSpecularBaseColor * FresnelLerp * surfaceReduction;

                float3 IndirColor = IndirSpecularResult + IndirDiffColor;

                //PBR

                /*
                Unity PBR
                InputData inputData;
                inputData = (InputData)0;
                inputData.positionWS = i.worldPos;
                inputData.normalWS = i.worldNormal;
                float4 SHADOW_COORDS = TransformWorldToShadowCoord(i.worldPos);
                inputData.shadowCoord = half4(0.0,0.0,0.0,0.0);
                inputData.viewDirectionWS = normalize(_WorldSpaceCameraPos - i.pos);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.pos);
                float4 final_color = UniversalFragmentPBR(inputData, so.albedo, so.metallic, so.specular,so.smoothness, so.occlusion, so.emission, so.alpha);
                Unity PBR
                */

                /*
                o = ApplyUnityStandardLit(i, o, so);

                if ((material.properties & MATPROP_ZEROSPEC_BIT) > 0)
                {
                    half spec = 0.0f;
                    o.specular = half4(spec, spec, spec, spec);
                }
                */

                float3 final_color = IndirColor * shadow + DirectColor * shadow;
                final_color = CustomMixFog(final_color, i.fogFactor, N, L, V, mainLight.color.rgb);

                return half4(final_color, 1.0);
            }
            ENDHLSL

        }

        pass
        {
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float _Metallic;
            float _Glossiness;
            
            float _Cutoff;
            float4 _SunDirc;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;

                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                
                float4 worldPos : POSITION2;
                float3 worldNormal : NORMAL;
                
                float3 viewDir : TEXCOORD1;
                float3 color : COLOR;
                
                #ifdef LIGHTMAP_OFF
                    #if UNITY_SHOULD_SAMPLE_SH
                        half3 sh : TEXCOORD2;
                    #endif
                #endif
            };

            TEXTURE2D(_MainTex);SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            float3 _LightDirection;
            
            v2f vert(appdata v)
            {
                v2f o;

                o.worldPos = mul(unity_ObjectToWorld, v.vertex);

                o.pos = TransformWorldToHClip(o.worldPos);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);
                // o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.uv = v.uv;

                o.viewDir = GetWorldSpaceViewDir(o.worldPos.xyz);

                o.color = v.color.rgb;

                #ifdef LIGHTMAP_OFF
                    #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                        #if UNITY_SHOULD_SAMPLE_SH
                            o.sh = 0;
                            o.sh = SH_Process(o.worldNormal);
                        #endif
                    #endif
                #endif

                float invNdotL = 1.0 - saturate(dot(_LightDirection, o.worldNormal));
                float scale = invNdotL * _ShadowBias.y;

                // normal bias is negative since we want to apply an inset normal offset
                o.worldPos.xyz = _LightDirection.xyz * _ShadowBias.xxx + o.worldPos.xyz;
                o.worldPos.xyz = o.worldNormal * scale.xxx + o.worldPos.xyz;

                o.pos = TransformWorldToHClip(o.worldPos.xyz);//【不确定】遗留一个深度未解决，死活汇入不了光照方向
                #if UNITY_REVERSED_Z
                    o.pos.z = min(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    o.pos.z = max(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                
                return o;
            }

            
            float4 frag(v2f i) : SV_Target
            {
                SurfaceData so;
                so = (SurfaceData)0;
                float2 targetUV = i.uv;

                float4 tex;

                tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, targetUV);
                
                if (tex.a <= _Cutoff)
                {
                    discard;
                }// cutoff

                so.albedo = tex.rgb;
                // so.Emission = fixed3(0.5, 0.5, 0.5);

                // Well now all meshes are foilages so
                so.normalTS = lerp(i.worldNormal, -_SunDirc.xyz, i.color.r * 0.5);

                return half4(0.0, 0.0, 0.0, 1.0);
            }
            ENDHLSL

        }

        
        pass
        {
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float _Metallic;
            float _Glossiness;
            
            float _Cutoff;
            float4 _SunDirc;

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;

                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                
                float4 worldPos : POSITION2;
                float3 worldNormal : NORMAL;
                
                float3 viewDir : TEXCOORD1;
                float3 color : COLOR;
                
                #ifdef LIGHTMAP_OFF
                    #if UNITY_SHOULD_SAMPLE_SH
                        half3 sh : TEXCOORD2;
                    #endif
                #endif
            };

            TEXTURE2D(_MainTex);SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            float3 _LightDirection;
            
            v2f vert(appdata v)
            {
                v2f o;

                o.worldPos = mul(unity_ObjectToWorld, v.vertex);

                o.pos = TransformWorldToHClip(o.worldPos);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);
                // o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.uv = v.uv;

                o.viewDir = GetWorldSpaceViewDir(o.worldPos.xyz);

                o.color = v.color.rgb;

                #ifdef LIGHTMAP_OFF
                    #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                        #if UNITY_SHOULD_SAMPLE_SH
                            o.sh = 0;
                            o.sh = SH_Process(o.worldNormal);
                        #endif
                    #endif
                #endif
                
                return o;
            }

            
            float4 frag(v2f i) : SV_Target
            {
                SurfaceData so;
                so = (SurfaceData)0;
                float2 targetUV = i.uv;

                float4 tex;

                tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, targetUV);
                
                if (tex.a <= _Cutoff)
                {
                    discard;
                }// cutoff

                so.albedo = tex.rgb;
                // so.Emission = fixed3(0.5, 0.5, 0.5);

                // Well now all meshes are foilages so
                so.normalTS = lerp(i.worldNormal, -_SunDirc.xyz, i.color.r * 0.5);

                return half4(0.0, 0.0, 0.0, 1.0);
            }
            ENDHLSL

        }

        /*
        pass
        {
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float _Metallic;
            float _Glossiness;
            
            float _Cutoff;
            float4 _SunDirc;

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;

                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                
                float4 worldPos : POSITION2;
                float3 worldNormal : NORMAL;
                
                float3 viewDir : TEXCOORD1;
                float3 color : COLOR;
                
                #ifdef LIGHTMAP_OFF
                    #if UNITY_SHOULD_SAMPLE_SH
                        half3 sh : TEXCOORD2;
                    #endif
                #endif
            };

            TEXTURE2D(_MainTex);SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            float3 _LightDirection;
            
            v2f vert(appdata v)
            {
                v2f o;

                o.worldPos = mul(unity_ObjectToWorld, v.vertex);

                o.pos = TransformWorldToHClip(o.worldPos);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);
                // o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.uv = v.uv;

                o.viewDir = GetWorldSpaceViewDir(o.worldPos.xyz);

                o.color = v.color.rgb;

                #ifdef LIGHTMAP_OFF
                    #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                        #if UNITY_SHOULD_SAMPLE_SH
                            o.sh = 0;
                            o.sh = SH_Process(o.worldNormal);
                        #endif
                    #endif
                #endif
                
                return o;
            }

            
            float4 frag(v2f i) : SV_Target
            {
                SurfaceData so;
                so = (SurfaceData)0;
                float2 targetUV = i.uv;

                float4 tex;

                tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, targetUV);
                
                if (tex.a <= _Cutoff)
                {
                    discard;
                }// cutoff

                so.albedo = tex.rgb;
                // so.Emission = fixed3(0.5, 0.5, 0.5);

                // Well now all meshes are foilages so
                so.normalTS = lerp(i.worldNormal, -_SunDirc.xyz, i.color.r * 0.5);

                return float4(PackNormalOctRectEncode(TransformWorldToViewDir(i.worldNormal, true)), 0.0, 0.0);
            }
            ENDHLSL

        }
        */
    }
    Fallback "Transparent/Cutout/VertexLit"
    // Fallback "Diffuse"

}
