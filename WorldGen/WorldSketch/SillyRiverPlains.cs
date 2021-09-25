using System;
using System.Collections;
using UnityEngine;

namespace WorldGen.WorldSketch
{
    [Serializable]
    [CreateAssetMenu(fileName = "SillyRiverPlains", menuName = "Sketchers/SillyRiverPlains")]
    public class SillyRiverPlains : WorldSketcher
    {
        public int test = 0;

        public override SketchResults FillHeightmap(
            int sizeX,
            int sizeY)
        {
            var heightMap = new float[sizeX * sizeY];
            var erosionMap = new float[sizeX * sizeY];
            var waterMap = new float[sizeX * sizeY];

            // noise generators
            var mountain = new FastNoiseLite();
            mountain.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
            //mountain.SetFractalType(FastNoiseLite.FractalType.Ridged);

            mountain.SetFractalType(FastNoiseLite.FractalType.FBm);
            mountain.SetFractalOctaves(5);
            mountain.SetFractalGain(0.5f);
            //mountain.SetFrequency(0.00078f);
            mountain.SetFrequency(0.006f);
            mountain.SetFractalLacunarity(2.0f);

            // noise generators
            var water = new FastNoiseLite();
            water.SetSeed(123555);
            water.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
            water.SetFractalType(FastNoiseLite.FractalType.Ridged);

            water.SetFractalOctaves(1);
            water.SetFractalGain(0.5f);
            water.SetFrequency(0.02f);
            water.SetFractalLacunarity(0.5f);

            // base shape
            float minv = 100.0f, maxv = -100.0f;
            for (int i = 0; i < sizeX; i++)
            {
                for (int j = 0; j < sizeY; j++)
                {
                    waterMap[i * sizeY + j] = water.GetNoise(i, j) - 0.85f;
                    float isRiver = Mathf.Max(0.0f, waterMap[i * sizeY + j]) * 10.0f;
                    heightMap[i * sizeY + j] = mountain.GetNoise(i, j);
                    minv = Mathf.Min(heightMap[i * sizeY + j], minv);
                    maxv = Mathf.Max(heightMap[i * sizeY + j], maxv);
                }
            }

            Debug.Log($"Range: {minv} ~ {maxv}");

            // normalize
            for (int i = 0; i < sizeX; i++)
            {
                for (int j = 0; j < sizeY; j++)
                {
                    heightMap[i * sizeY + j] = (heightMap[i * sizeY + j] - minv) / (maxv - minv);
                    heightMap[i * sizeY + j] *= 0.08f;
                    heightMap[i * sizeY + j] -= Mathf.Max(-0.06f, waterMap[i * sizeY + j]) * 0.04f;
                    heightMap[i * sizeY + j] = Mathf.Max(heightMap[i * sizeY + j], 0.0f) + 0.1f;
                }
            }

            // No erosion is applied

            return new SketchResults(new System.Collections.Generic.Dictionary<string, Texture>()
            {
                ["Sketch"] = SketchResults.ComposeTex2D(sizeX, sizeY, heightMap, erosionMap, waterMap)
            });
        }
    }
}