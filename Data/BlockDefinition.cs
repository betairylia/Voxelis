using System;
using System.Collections;
using System.Collections.Generic;
using TypeReferences;
using UnityEngine;

namespace Voxelis.Data
{
    [Serializable]
    [CreateAssetMenu(menuName = "Block Definition")]
    public class BlockDefinition : ScriptableObject
    {
        // ID of this block; meta got ignored
        public Block ID;

        // Texture used by this kind of block
        public Texture2D texture;

        // Should we tint the texture from vertex colors ?
        public bool doTint = false;

        // How to sample the texture
        [HideInInspector]
        public Vector3 uvw;

        [HideInInspector]
        public Vector2 uvSize;

        // Is its geometry always an unit cube from [0,0,0] to [1,1,1] ?
        public BlockGeometryType geometryType = BlockGeometryType.CUBE;

        // Is it opaque or have any transparency / alpha cutoff ?
        public bool isOpaque = true;

        // Can we omit faces next to them ?
        public bool isRenderSolid 
        { get { return geometryType == BlockGeometryType.CUBE && isOpaque; } }

        // Is it considered logically solid ? (Not related to rendering)
        public bool isSolid = true;

        // Does this block have extra data ?
        public bool hasExtraData = false;

        // If so, which one should it use ?
        [Inherits(typeof(BlockEntityBase))]
        public TypeReference extraDataType;

        public Color ToColorPixel()
        {
            return new Color
            (
                uvw.x,
                uvw.y,
                uvw.z,

                // In texture it is float so make it preserve bit structure
                BitConverter.ToSingle(BitConverter.GetBytes
                (
                    (isOpaque ? 0x80000000 : 0) +
                    (isRenderSolid ? 0x40000000 : 0) +
                    (isSolid ? 0x20000000 : 0) +
                    (doTint ? 0x10000000 : 0) +
                    (((uint)geometryType) << 22) +
                    (((uint)(uvSize.x * 2048)) << 11) +
                    (((uint)(uvSize.y * 2048)))
                ), 0)
            );
        }
    }
}
