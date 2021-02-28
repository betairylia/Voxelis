using System.Collections;
using UnityEngine;

namespace Voxelis.Data
{
    [CreateAssetMenu(menuName = "Block Registry Table")]
    public class BlockRegistryTable : ScriptableObject
    {
        public BlockDefinition[] blockDefinitions;
        public int NonNullDefCount;

        // TODO: make this thing an array / buffer ...
        public Texture2D BlockLUT;
        public Texture2DArray BlockTexArray;

        public int pageCount;
    }
}