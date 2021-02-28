using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Voxelis
{
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct Block
    {
        public static int BlockIDCount = 65536;

        public ushort id;
        public ushort meta;

        public static Block Empty = new Block() { id = 0, meta = 0 };
        public static Block FromID(ushort id)
        {
            return new Block() { id = id, meta = 0 };
        }

        public static Block From32bitColor(uint color)
        {
            // Convert RGBA888 (32bit) to RGBA444 (16bit)
            ushort color_16 = (ushort)(
                (ushort)(((color >> 24 & (0x000000FF)) >> 4) << 12) +   /* r */
                (ushort)(((color >> 16 & (0x000000FF)) >> 4) << 8)  +   /* g */
                (ushort)(((color >>  8 & (0x000000FF)) >> 4) << 4)   +   /* b */
                (ushort)(((color       & (0x000000FF)) >> 4))                 /* a */
            );

            return new Block() { id = 0xffff, meta = color_16 };
        }

        public bool IsSolid()
        {
            return !(id == 0 || (id == 0xffff && meta == 0));
        }

        public override string ToString()
        {
            return $"Voxelis.Block:[ID = {id}, meta = {meta}]";
        }
    }

    public enum BlockGeometryType
    {
        CUBE = 0,       // Unit cube from [0,0,0] to [1,1,1]
        MESH = 1,       // Triangular mesh
        FINE_4 = 2,     // Contains 4x4x4 subvoxels
        FINE_8 = 3,     // Contains 8x8x8 subvoxels
        FINE_16 = 4,    // Contains 16x16x16 subvoxels
    }

    public struct EnviormentBlock
    {
        // Total - 32 bits
        // 5 Light
        // 5 Temperature
        // 5 Energy (Spirit Density)

        // 1 Fog

        // 16 Reserved

        public uint env;
    }

    // TODO: Implement this
    public abstract class BlockEntityBase 
    {
        public BlockGroup group;
        public Vector3Int pos;
        public Block block;

        public BlockEntityBase(BlockGroup group, Vector3Int pos, Block block)
        {
            this.group = group;
            this.pos = pos;
            this.block = block;
        }
    }
}