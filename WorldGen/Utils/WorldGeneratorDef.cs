using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using WorldGen.WorldSketch;
using Voxelis.Rendering;
using Voxelis.WorldGen;

// TODO: Refactor for editor usage
namespace Voxelis
{
    [CreateAssetMenu(fileName = "WorldGen Definition", menuName = "New WorldGen Definition")]
    public class WorldGeneratorDef : ScriptableObject
    {
        public WorldSketcher sketcher;
        public int worldSketchSize = 1024;

        public ComputeShader cs_generation;
        public int cs_generation_batchsize = 512;

        public Matryoshka.MatryoshkaGraph matryoshkaGraph;

        [EnumNamedArray(typeof(WorldGen.StructureType))]
        public Matryoshka.MatryoshkaGraph[] structureGraphs = new Matryoshka.MatryoshkaGraph[8];
    }
}
