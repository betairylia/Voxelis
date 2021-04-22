using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Voxelis.BlockExtras;
using Voxelis.Rendering;
using Voxelis.WorldGen;

namespace Voxelis
{
    public class VoxelisMain : MonoBehaviour
    {
        List<BlockGroup> allBlockGroups = new List<BlockGroup>();

        public ComputeShader cs_generation;
        public int cs_generation_batchsize;

        public Voxelis.VoxelisGlobalSettings globalSettings;
        //public Material chunkMat;
        //public ComputeShader cs_chunkMeshPopulator;

        public Camera mainCam;

        [Header("DEBUG")]
        public TextAsset FS16Geometry;
        public Vector3Int GeometrySize;
        public Texture3D lastFSTex3D;
        public Mesh lastMesh;

        [DllImport("VoxLib")]
        private static extern bool ReadVoxFileSingle([In, Out] byte[] fileContent, uint fileLength, [In, Out] uint[] contentHolder, int sizeX, int sizeY, int sizeZ);

        private void Awake()
        {
            GeometryIndependentPass.cs_generation = cs_generation;
            GeometryIndependentPass.cs_generation_batchsize = cs_generation_batchsize;
            GeometryIndependentPass.Init();

            Material chunkMat = globalSettings.renderSetup.ChunkMaterial;
            ComputeShader cs_chunkMeshPopulator = globalSettings.renderSetup.associatedCS;

            chunkMat.SetTexture("_MainTexArr", globalSettings.blockRegistryTable.BlockTexArray);
            chunkMat.SetTexture("_BlockLUT", globalSettings.blockRegistryTable.BlockLUT);

            ChunkRenderer_GPUComputeMesh.cs_chunkMeshPopulator = cs_chunkMeshPopulator;
            ChunkRenderer_GPUComputeMesh.chunkMat = chunkMat;
            ChunkRenderer_CPUOptimMesh.chunkMat = chunkMat;
        }

        // Use this for initialization
        void Start()
        {
            if (FS16Geometry != null)
            {
                byte[] fileContent = FS16Geometry.bytes;
                uint fileSize = (uint)fileContent.Length;
                Vector3Int voxSize = GeometrySize;

                uint[] holder = new uint[512]; ;
                if (voxSize.x == 16)
                {
                    holder = new uint[4096];
                }
                FineStructure_16.fixedGeometry = new ushort[4096];

                if (ReadVoxFileSingle(fileContent, fileSize, holder, voxSize.x, voxSize.y, voxSize.z))
                {
                    for (int x = 0; x < 16; x++)
                    {
                        for (int y = 0; y < 16; y++)
                        {
                            for (int z = 0; z < 16; z++)
                            {
                                if(voxSize.x == 16)
                                {
                                    FineStructure_16.fixedGeometry[x * 16 * 16 + y * 16 + z] = Block.From32bitColor(holder[x * 256 + y * 16 + z]).meta;
                                    //FineStructure_16.fixedGeometry[x * 16 * 16 + y * 16 + z] = (ushort)(holder[x * 256 + y * 16 + z] > 0 ? 65535 : 0);
                                }
                                else if(voxSize.x == 8)
                                {
                                    FineStructure_16.fixedGeometry[x * 16 * 16 + y * 16 + z] = Block.From32bitColor(holder[(x / 2) * 64 + (y / 2) * 8 + (z / 2)]).meta;
                                }
                            }
                        }
                    }
                }
            }

            worldGeneratingCoroutine = StartCoroutine(WorldUpdateCoroutine());
        }

        // Update is called once per frame
        void Update()
        {
            CustomJobs.CustomJob.UpdateAllJobs();
            GeometryIndependentPass.Update();
        }

        #region WorldUpdate

        protected Coroutine worldGeneratingCoroutine;
        protected BlockGroup currentUpdatingBlockGroup;

        [Header("WorldUpdate")]
        public float UpdateCoroutineBudgetMS = 15.0f;

        protected IEnumerator WorldUpdateCoroutine()
        {
            while(true)
            {
                BlockGroup[] allBG_curr = new BlockGroup[allBlockGroups.Count];
                allBlockGroups.CopyTo(allBG_curr);

                foreach (var bg in allBG_curr)
                {
                    bg.budgetMS = UpdateCoroutineBudgetMS;
                    bg.StartWorldUpdateSingleLoop();

                    while (!bg.UpdateLoopFinished)
                    {
                        yield return null;
                    }
                }

                yield return null;
            }
        }

        #endregion

        #region BlockGroupContainer Ops

        public void Destroy()
        {
            GeometryIndependentPass.Destroy();
        }

        public bool Contains(BlockGroup bg)
        {
            return allBlockGroups.Contains(bg);
        }

        public void Add(BlockGroup bg)
        {
            allBlockGroups.Add(bg);

            bg.globalSettings = globalSettings;
            bg.chunkMat = globalSettings.renderSetup.ChunkMaterial;
            bg.cs_chunkMeshPopulator = globalSettings.renderSetup.associatedCS;

            bg.follows = mainCam.transform;
            bg.mainCam = mainCam;
        }

        #endregion

        #region CleanUp

        private void ClearAllImmediate()
        {
            GeometryIndependentPass.Destroy();
        }

        public void Refresh()
        {
            ClearAllImmediate();
        }

        protected void OnDestroy()
        {
            ClearAllImmediate();
        }

        protected virtual void AssemblyReloadEvents_beforeAssemblyReload()
        {
            ClearAllImmediate();
        }

        #endregion
    }
}