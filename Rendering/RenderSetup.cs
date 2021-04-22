using System.Collections;
using System.Collections.Generic;
using TypeReferences;
using UnityEngine;

namespace Voxelis.Rendering
{
    [CreateAssetMenu(fileName = "VoxelisRenderSetup", menuName = "Voxelis/RenderSetup", order = 100)]
    public class RenderSetup : ScriptableObject
    {
        [Inherits(typeof(ChunkRenderableBase))]
        public TypeReference renderableClass;
        public Material ChunkMaterial;
        public ComputeShader associatedCS;
    }
}
