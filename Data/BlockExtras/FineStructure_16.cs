using System.Collections;
using UnityEngine;

namespace Voxelis.BlockExtras
{
    public class FineStructure_16 : BlockEntityBase
    {
        public ushort[] blockData = new ushort[4096];

        public FineStructure_16()
        {
            System.Array.Clear(blockData, 0, 4096);
            //blockData[0] = 0x0f0f;
            for (int x = 6; x < 10; x++)
            {
                for(int y = 0; y < 16; y++)
                {
                    for (int z = 6; z < 10; z++)
                    {
                        blockData[x * 16 * 16 + y * 16 + z] = 0x0f0f;
                    }
                }
            }
        }
    }
}