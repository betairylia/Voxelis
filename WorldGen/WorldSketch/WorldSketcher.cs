using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WorldGen.WorldSketch
{
    public class SketchResults
    {
        public Dictionary<string, Texture> result;

        public SketchResults()
        {
            this.result = new Dictionary<string, Texture>();
        }

        public SketchResults(Dictionary<string, Texture> result)
        {
            this.result = result;
        }

        public static Texture2D ComposeTex2D(
            int sizeX,
            int sizeY,
            float[] r,
            float[] g = null,
            float[] b = null,
            float[] a = null
        )
        {
            // Create texture
            var sketchMapTex = new Texture2D(sizeX, sizeY, TextureFormat.RGBAFloat, false);

            for (int i = 0; i < sizeX; i++)
            {
                for (int j = 0; j < sizeY; j++)
                {
                    sketchMapTex.SetPixel(i, j, new Color(
                        r == null ? 0 : r[i * sizeY + j],
                        g == null ? 0 : g[i * sizeY + j],
                        b == null ? 0 : b[i * sizeY + j],
                        a == null ? 0 : a[i * sizeY + j]
                    ));
                }
            }

            sketchMapTex.Apply();

            return sketchMapTex;
        }

        public void ApplyToComputeShader(ComputeShader cs)
        {
            foreach (var entry in result)
            {
                cs.SetTexture(0, entry.Key, entry.Value);
            }
        }
    }

    public abstract class WorldSketcher : ScriptableObject
    {
        public abstract SketchResults FillHeightmap(
            int sizeX,
            int sizeY
        );
    }
}