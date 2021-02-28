using System.Collections;
using UnityEngine;

namespace Voxelis.BlockExtras
{
    public class FineStructure_16 : BlockEntityBase
    {
        public ushort[] blockData = new ushort[4096];

        public FineStructure_16(BlockGroup group, Vector3Int pos, Block block) : base(group, pos, block)
        {
            System.Array.Clear(blockData, 0, 4096);
            //blockData[0] = 0x0f0f;
            //for (int x = 6; x < 10; x++)
            //{
            //    for(int y = 0; y < 16; y++)
            //    {
            //        for (int z = 6; z < 10; z++)
            //        {
            //            blockData[x * 16 * 16 + y * 16 + z] = 0x0f0f;
            //        }
            //    }
            //}
            System.Random rand = new System.Random(pos.x ^ pos.y ^ pos.z);
            for(int i = 0; i < 4; i++)
            {
                int x = rand.Next(0, 16);
                int z = rand.Next(0, 16);
                int h1 = rand.Next(6, 10);
                int h2 = rand.Next(4, 6);

                for (int y = 0; y < h1; y++)
                {
                    blockData[x * 16 * 16 + y * 16 + z] = 0x0f0f;
                }

                x += rand.Next(-1, 1);
                z += rand.Next(-1, 1);

                x = x > 15 ? 15 : x;
                x = x < 0 ? 0 : x;
                z = z > 15 ? 15 : z;
                z = z < 0 ? 0 : z;

                for (int y = h1; y < h1+h2; y++)
                {
                    blockData[x * 16 * 16 + y * 16 + z] = 0x0f0f;
                }
            }
        }
    }
}