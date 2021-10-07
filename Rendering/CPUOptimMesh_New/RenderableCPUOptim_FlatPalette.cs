using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using UnityEngine;
using Voxelis.CustomJobs;
using Voxelis.Data;

// BlockIdentifier = Block, BlockID = Block.id
using BlockID = System.UInt32; // TODO: what should we use ??

using System;
using System.Text;
using System.IO;
using System.Threading;

namespace Voxelis.Rendering
{
    // TODO: Dirty workaround for Demo version - Water blocks, Foilages, Invisible air walls
    public partial class ChunkRenderer_CPUOptimMeshPureVoxel : ChunkRenderableBase, INeighborAwareChunkRenderable
    {
        // Static members 
        public static bool MaterialExported = false;
        public static long gTimeMS = 0;

        public static Material chunkMat;
        public static Material chunkMatWater;
        public MaterialPropertyBlock matProp;

        Transform targetTransform;
        Transform waterChildTransform;

        public Chunk chunk;

        Task task = null;

        protected Vector3Int myPos;
        protected bool waiting = false;
        public bool populated;

        public Mesh mesh, meshWater;

        bool meshDirty = false;

        [StructLayout(LayoutKind.Sequential)]
        public struct MeshQuad
        {
            public int next;
            public Block block;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MergedQuad
        {
            public Vector2Int min;
            public Vector2Int max;
            public Block block;
        }

        public enum RenderableBlockType
        {
            Opaque,
            Cutoff,
            Transparent,
            Structure,
            Mesh
        }

        public enum FACE_BIT
        {
            X_POS = 0x1,
            X_NEG = 0x2,
            Y_POS = 0x4,
            Y_NEG = 0x8,
            Z_POS = 0x10,
            Z_NEG = 0x20
        }

        private static float PackFloat_safe(uint v)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        private static float PackFloat_safe(int v)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        private static unsafe float PackFloat(uint v)
        {
            return *(float*)(&v);
        }

        private static unsafe float PackFloat(int v)
        {
            return *(float*)(&v);
        }

        private static float PackNormalFloat(Vector3Int normal)
        {
            // TODO
            //return BitConverter.ToSingle(
            //    BitConverter.GetBytes(
            //        (normal.x << 2) +
            //        (normal.y << 1) +
            //        (normal.z << 0)
            //    )
            //    , 0);
            return 0;
        }

        private static Vector2 PackDataToUV_Regular(FaceInfo info, Vector2Int inFacePos, Block block)
        {
            return new Vector2(
                PackFloat(
                    ((uint)inFacePos.x << 10) +
                    ((uint)inFacePos.y << 4) +
                    ((uint)info)
                ),
                PackFloat(block.id)
            );
        }

        private static float PackBlockPositionFloat(int x, int y, int z, int blockUVX, int blockUVY)
        {
            return PackFloat(
                    (x << 24) +
                    (y << 18) +
                    (z << 12) +
                    (blockUVX << 6) +
                    (blockUVY));
        }

        private static float PackFloat(Color c)
        {
            uint v = (
                ((uint)(c.r * 255) << 24) +
                ((uint)(c.g * 255) << 16) +
                ((uint)(c.b * 255) << 8) +
                ((uint)(c.a * 255)));

            return PackFloat(v);
        }

        // TODO: Make this Async
        public override void Init(BlockGroup group, Chunk chunk)
        {
            this.chunk = chunk;

            // Duplicate the material
            matProp = new MaterialPropertyBlock();

            // Update my initial position
            renderPosition = Vector3.zero;
            position = Vector3.one * (Chunk.SideLength / 2);
        }

        int Flatten(Vector3Int flatFactor, Vector3Int coord)
        {
            Vector3Int res = flatFactor * coord;
            return res.x + res.y + res.z;
        }

        public enum FaceInfo
        {
            Xpos = 0,
            Xneg = 1,
            Ypos = 2,
            Yneg = 3,
            Zpos = 4,
            Zneg = 5,
            Chisel = 6,
        }

        public enum FaceQueue
        {
            RegularOpaque,
            RegularNonOpaque,
            FineStructures,
            Water,
            Foilage
        }

        FaceQueue GetQueue(Block block)
        {
            return GetQueue(block.id);
        }

        FaceQueue GetQueue(BlockID id)
        {
            //if ( ? )
            //{
            //    return FaceQueue.Water;
            //}

            return FaceQueue.RegularOpaque;
        }

        static readonly int[] primaryIndex = new int[6] { 0, 0, 1, 1, 2, 2 };
        static readonly int[] secondaryIndex = new int[6] { 1, 1, 2, 2, 0, 0 };
        static readonly int[] thirdIndex = new int[6] { 2, 2, 0, 0, 1, 1 };
        static readonly int[] direction = new int[6] { 1, -1, 1, -1, 1, -1 };
        static readonly int[] neighborIdx = new int[6] { 0, 1, 0, 1, 0, 1 };

        static readonly int[,] indicesOrder = new int[6, 4]
        {
            { 3, 2, 1, 0 },
            { 0, 1, 2, 3 },
            { 3, 2, 1, 0 },
            { 0, 1, 2, 3 },
            { 3, 2, 1, 0 },
            { 0, 1, 2, 3 },
        };

        protected static int vertCapacity = 204800;
        protected static int idxCapacity = 409600;

        static ThreadLocal<Vector3[]> verticesP
            = new ThreadLocal<Vector3[]>(() => new Vector3[vertCapacity]);
        static ThreadLocal<int[]> indicesP
            = new ThreadLocal<int[]>(() => new int[idxCapacity]);

        static ThreadLocal<MeshOptimizer[]> mesher_chunk_pool
            = new ThreadLocal<MeshOptimizer[]>(
                () => new MeshOptimizer[1] {
                    new MeshOptimizer_Graphics(Chunk.SideLength),
                });

        static ThreadLocal<System.Random> RNG_pool = new ThreadLocal<System.Random>(() => new System.Random());

        bool IsBlockRenderable(Block block)
        {
            return (block.id != Block.Empty.id);
        }

        // TODO: make this work instead of copy IsBlockRenderable()
        bool IsBlockSolid(Block block)
        {
            return IsBlockRenderable(block);
        }

        // solidCheckType: Switched from a Func<> to enum, try to avoid GC.Allocs
        // ^ Literally this was the problem. WTF ... LETS GOOOOOOOOOOOOOOOOOOOOOOOOOOO
        protected void EmitFacesAt(MeshOptimizer optim, Vector3Int pos, Block block, Chunk dest, Chunk[] neighbors, int chunkSize, bool differentDest = false, int dX = 0, int dY = 0, int dZ = 0)
        {
            //UnityEngine.Profiling.Profiler.BeginSample("EmitFacesAt");
            Vector3Int neighborPos;

            // [X+, X-, Y+, Y-, Z+, Z-]
            for (int f = 0; f < 6; f++)
            {
                bool isNotBoundary = (f % 2 == 0) ? (pos[primaryIndex[f]] < (chunkSize - 1)) : (pos[primaryIndex[f]] > 0);

                // Reset Neighbor block in-chunk coordinate
                neighborPos = pos;
                Block neighborBlock = Block.Empty;

                // Neighbor block in self chunk
                if (isNotBoundary)
                {
                    neighborPos[primaryIndex[f]] += direction[f];
                    neighborBlock = dest.GetBlock(neighborPos.x, neighborPos.y, neighborPos.z);
                }
                // Neighbor block in neighbor chunks
                else if (neighbors != null && neighbors[f] != null)
                {
                    neighborPos[primaryIndex[f]] = neighborIdx[f] * (chunkSize - 1);
                    neighborBlock = neighbors[f].GetBlock(neighborPos.x, neighborPos.y, neighborPos.z);
                }

                if (IsBlockSolid(neighborBlock)) { continue; }

                if (differentDest)
                {
                    optim.EmitFaceAt(f, new Vector3Int(dX, dY, dZ), block);
                }
                else
                {
                    optim.EmitFaceAt(f, pos, block);
                }
            }
            //UnityEngine.Profiling.Profiler.EndSample();
        }

        protected async Task<Mesh/*, Mesh water*/> GenerateMesh(Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            UnityEngine.Profiling.Profiler.BeginSample("GenerateMesh - prepare");

            uint vertCount = 0;

            Chunk[] neighbors;

            //////////////////////////////////////////
            //// MESH CREATION
            //////////////////////////////////////////

            Mesh _mesh = new Mesh();
            //Mesh _mesh_water = new Mesh();

            MeshData mdataMain = new MeshData();
            //MeshData mdataWater = new MeshData();

            UnityEngine.Profiling.Profiler.EndSample();

            await Task.Run(async () =>
            {
                neighbors = new Chunk[6]
                {
                    Xpos,
                    Xneg,
                    Ypos,
                    Yneg,
                    Zpos,
                    Zneg
                };

                UnityEngine.Profiling.Profiler.BeginSample("GenerateMesh");

                UnityEngine.Profiling.Profiler.BeginSample("Initialization");

                long _gStart = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                int len = Chunk.SideLength;
                foreach (var me in mesher_chunk_pool.Value)
                {
                    me.Clear();
                    me.ClearMeshResults();
                }

                MeshOptimizer[] mesher = mesher_chunk_pool.Value;

                UnityEngine.Profiling.Profiler.EndSample();
                UnityEngine.Profiling.Profiler.BeginSample("Emit faces");

                //////////////////////////////////////////
                //// EMIT FACES
                //////////////////////////////////////////

                for (int x = 0; x < len; x++)
                {
                    for (int y = 0; y < len; y++)
                    {
                        for (int z = 0; z < len; z++)
                        {
                            Block currentBlock = chunk.GetBlock(x, y, z);

                            if (!IsBlockRenderable(currentBlock)) { continue; } // empty or ignored

                            // Get My Queue
                            FaceQueue queue = GetQueue(currentBlock);

                            // Ugly af
                            Vector3Int currentPos = new Vector3Int(x, y, z);

                            // Emit to renderer
                            if (queue == FaceQueue.RegularOpaque)
                            {
                                EmitFacesAt(mesher[0], currentPos, currentBlock, chunk, neighbors, Chunk.SideLength);
                            }
                        }
                    }
                }

                UnityEngine.Profiling.Profiler.EndSample();
                UnityEngine.Profiling.Profiler.BeginSample("Merge faces");

                //////////////////////////////////////////
                //// MERGE FACES
                //////////////////////////////////////////

                foreach (var m in mesher)
                {
                    m.Build();
                }

                long _gEnd = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                System.Threading.Interlocked.Add(ref gTimeMS, _gEnd - _gStart);

                UnityEngine.Profiling.Profiler.EndSample();
                UnityEngine.Profiling.Profiler.EndSample();

                //////////////////////////////////////////
                //// COPY TO OUTPUT BUFFERS
                //////////////////////////////////////////

                mesher[0].GetMeshData(ref mdataMain);
                //mesher[1].GetMeshData(ref mdataWater);
            });

            //////////////////////////////////////////
            //// MESH ASSEMBLY
            //////////////////////////////////////////

            MeshOptimizer.AssignToMeshStatic(ref _mesh, mdataMain);
            //MeshOptimizer.AssignToMeshStatic(ref _mesh_water, mdataWater);

            return _mesh/*, _mesh_water*/;
        }

        protected async Task<uint> DoWork(Transform transform, Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            try
            {
                var res = GenerateMesh(chunk, Xpos, Xneg, Ypos, Yneg, Zpos, Zneg);

                mesh/*, meshWater*/ = await res;

                DirtyQueue.Enqueue(this);

                return (uint)mesh.vertexCount;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                throw;
            }
        }

        void Afterwards(uint vCountRequired)
        {
            populated = true;
            meshDirty = true;
            waiting = false;
        }

        public void GenerateGeometryNeighborAware(BlockGroup group, Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            if (waiting) { return; }
            waiting = true;

            //this.targetTransform = transform; // how?

            UnityEngine.Profiling.Profiler.BeginSample("GenerateGeometryNeighborAware");

            // Dispatch work thread
            //task = DoWork(transform, chunk, Xpos, Xneg, Ypos, Yneg, Zpos, Zneg);

            // Capture faults
            task.ContinueWith(task => { UnityEngine.Debug.LogException(task.Exception); }, TaskContinuationOptions.OnlyOnFaulted);

            chunk.dirty = false;

            // Update my position
            renderPosition = Vector3.zero;
            position = renderPosition + Vector3.one * (Chunk.SideLength / 2);

            UnityEngine.Profiling.Profiler.EndSample();
        }
        /*
        public Task GenerateGeometryNeighborAwareAsync(Transform transform, Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            if (waiting) { return Task.CompletedTask; }
            waiting = true;

            // TEST
            // TODO: FIXME: Remove me.
            //chunk.SetBlock(16, 16, 16, VoxelInfoFactory.Mesh(Guid.Parse("1ae35e50-edbc-4f28-9c60-6c68ecd24886")));

            this.targetTransform = transform;

            UnityEngine.Profiling.Profiler.BeginSample("GenerateGeometryNeighborAware");

            // Dispatch work thread
            task = DoWork(transform, chunk, Xpos, Xneg, Ypos, Yneg, Zpos, Zneg);

            // Capture faults
            task.ContinueWith(task => { UnityEngine.Debug.LogException(task.Exception); }, TaskContinuationOptions.OnlyOnFaulted);
            // TODO: FIXME
            //chunk.dirty = false;

            // Update my position
            renderPosition = Vector3.zero;
            position = renderPosition + Vector3.one * (Chunk.ChunkSize / 2);

            UnityEngine.Profiling.Profiler.EndSample();
            return task;
        }
        */
        public override void GenerateGeometry(BlockGroup group, Chunk chunk)
        {
            GenerateGeometryNeighborAware(group, chunk, null, null, null, null, null, null);
        }

        public override void Clean()
        {
            if (Application.isEditor)
            {
                if (mesh) { Mesh.DestroyImmediate(mesh); }
                if (meshWater) { Mesh.DestroyImmediate(meshWater); }
            }
            else
            {
                if (mesh) { Mesh.Destroy(mesh); }
                if (meshWater) { Mesh.Destroy(meshWater); }
            }

            populated = false;
            chunk.renderer = null;
        }

        public static void CleanUpStatic()
        {
            // Nothing to do
        }

        public override bool IsReadyForPresent()
        {
            return (populated && matProp != null && mesh != null) || task != null;
        }

        public override Chunk GetChunk()
        {
            return chunk;
        }

        public override void Render(BlockGroup group)
        {
            if (task != null)
            {
                if (task.IsCompleted)
                {
                    //Afterwards(task.Result);
                    task = null;
                }
                //else
                //{
                //    return;
                //}
            }

            if (IsReadyForPresent())
            {
                //RenderAt(transform);
            }
        }

        public void AssignMesh()
        {
            if (targetTransform == null) { return; }
            if (meshDirty)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Assign Mesh");

                targetTransform.GetComponent<MeshFilter>().mesh = mesh;
                targetTransform.GetComponent<MeshRenderer>().material = chunkMat;
                targetTransform.GetComponent<MeshRenderer>().SetPropertyBlock(matProp);

                if (mesh.vertexCount <= 0)
                {
                    targetTransform.GetComponent<MeshRenderer>().enabled = false;
                }

                mesh.UploadMeshData(true);

                // Check water
                if (meshWater.vertexCount > 0)
                {
                    if (waterChildTransform == null)
                    {
                        var go = new GameObject();
                        go.transform.parent = targetTransform;

                        var mf = go.AddComponent<MeshFilter>();
                        var mr = go.AddComponent<MeshRenderer>();

                        waterChildTransform = go.transform;

                        waterChildTransform.localPosition = Vector3.zero;
                        waterChildTransform.localRotation = Quaternion.identity;
                    }

                    waterChildTransform.GetComponent<MeshFilter>().mesh = meshWater;
                    waterChildTransform.GetComponent<MeshRenderer>().material = chunkMatWater;

                    meshWater.UploadMeshData(true);
                }

                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        public void RenderAt(Transform transform)
        {
            if (mesh == null || mesh.vertexCount <= 0) { return; }

            /*
            var mat = Matrix4x4.TRS(transform.position + renderPosition, transform.rotation, transform.lossyScale);

            //Graphics.DrawMesh(
            //    mesh,
            //    mat,
            //    chunkMat,
            //    0,
            //    null,
            //    0,
            //    matProp
            //);

            Graphics.DrawMesh(
                mesh,
                mat,
                chunkMat,
                0
            );
            */
        }

        public override uint GetVertCount()
        {
            uint count = 0U;
            if (mesh != null) { count += (uint)mesh.vertexCount; }
            if (meshWater != null) { count += (uint)meshWater.vertexCount; }
            //if(meshTailor != null) { count += (uint)meshTailor.vertexCount; }
            return count;
        }

        public static ConcurrentQueue<ChunkRenderer_CPUOptimMeshPureVoxel> DirtyQueue = new ConcurrentQueue<ChunkRenderer_CPUOptimMeshPureVoxel>();

        public static void RefreshTasks()
        {
            ChunkRenderer_CPUOptimMeshPureVoxel fp;

            while (DirtyQueue.TryDequeue(out fp))
            {
                UnityEngine.Profiling.Profiler.BeginSample("ChunkRenderable_PostGenerationMainThreadWork");
                fp.Afterwards((uint)fp.mesh.vertexCount);
                fp.AssignMesh();
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }
    }
}