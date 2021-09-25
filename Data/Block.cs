using System;
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

        public uint id;
        public uint meta => id;

        public static Block Empty = new Block() { id = 0 };
        public static Block FromID(ushort id)
        {
            return new Block() { id = id };
        }

        public static Block From32bitColor(uint color)
        {
            // Convert RGBA888 (32bit) to RGBA444 (16bit)
            // ^ Not this

            //ushort color_16 = (ushort)(
            //    (ushort)(((color >> 24 & (0x000000FF)) >> 4) << 12) +   /* r */
            //    (ushort)(((color >> 16 & (0x000000FF)) >> 4) << 8)  +   /* g */
            //    (ushort)(((color >>  8 & (0x000000FF)) >> 4) << 4)   +   /* b */
            //    (ushort)(((color       & (0x000000FF)) >> 4))                 /* a */
            //);

            return new Block() { id = color };
        }

        public static Block FromColor(Color color)
        {
            // Convert RGBA888 (32bit) to RGBA444 (16bit)
            // ^ Not this

            uint color_32 = (uint)(
                (((uint)(color.r * 255)) << 24) +   /* r */
                (((uint)(color.g * 255)) << 16) +   /* g */
                (((uint)(color.b * 255)) << 8) +   /* b */
                (((uint)(color.a * 255)))                 /* a */
            );

            return From32bitColor(color_32);
        }

        public static bool CanMergeRenderable(Block a, Block b)
        {
            return (a.id == b.id) && (a.meta == b.meta);
        }

        public float PackFloat()
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(
                ((uint)id << 16) + (uint)meta
                ), 0);
        }

        public static Block UnpackFloat(float value)
        {
            Block b;
            uint raw = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            b.id = raw;
            //b.meta = (ushort)(raw & 0xFFFF);

            return b;
        }

        public bool IsSolid()
        {
            // TODO: Check Block Definitions instead of this.
            //return !(id == 0 || (id == 0xffff && meta == 0));
            return ((meta & 0xff) > 0);
        }

        public bool IsRenderable()
        {
            // TODO: Check Block Definitions instead of this.
            //return !(id == 0 || (id == 0xffff && meta == 0));
            return ((meta & 0xff) > 0);
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