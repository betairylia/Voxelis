using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Voxelis.CustomJobs;
using Voxelis.Data;
using BlockID = System.UInt32;

namespace Voxelis.Rendering
{
    // REFACTOR PLEASE WT...H
    public class ChunkRenderer_CPUOptimMesh : ChunkRenderableBase, INeighborAwareChunkRenderable
    {
        public static bool MaterialExported = false;

        public static Material chunkMat;
        public MaterialPropertyBlock matProp;

        public Chunk chunk;

        public Texture3D fineStructure16_tex3D;
        protected uint[] blkEx_tmpBuf;
        protected BlockID[] tex3D_tmpBuf;
        public int fsBufSize { get; protected set; }
        public int totalMipSize { get; protected set; }
        protected int fsBufRowlen = 32, fsResolution = 16, mipCount = 0;
        protected int[] mipOffset;

        Task<uint> task = null;

        protected Vector3Int myPos;
        protected bool waiting = false;
        public bool populated;

        public Mesh mesh;

        [StructLayout(LayoutKind.Sequential)]
        public struct Vertex_GR
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;
            public uint block_vert_meta;
            public Block block;
        }

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

        public enum FACE_BIT
        {
            X_POS = 0x1,
            X_NEG = 0x2,
            Y_POS = 0x4,
            Y_NEG = 0x8,
            Z_POS = 0x10,
            Z_NEG = 0x20
        }

        public virtual void CreateFSTex3D(out Texture3D tex3D, out BlockID[] hostBuffer, int bufferSize, int rowLength = 32, int resolution = 16)
        {
            // Ensure mipmap will not go further than every single texture
            int _t = resolution;
            while ((_t >>= 1) > 0) { ++mipCount; }

            bufferSize = Mathf.CeilToInt(bufferSize / (float)rowLength) * rowLength;
            tex3D = new Texture3D(resolution * (bufferSize / rowLength), resolution * fsBufRowlen, resolution, TextureFormat.R16, mipCount);
            tex3D.filterMode = FilterMode.Point;
            tex3D.wrapMode = TextureWrapMode.Clamp;

            // Umm ... ugly
            fsBufSize = bufferSize;
            fsBufRowlen = rowLength;
            fsResolution = resolution;

            // Calculate mipmap offset
            mipOffset = new int[mipCount];
            totalMipSize = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int sLen = fsResolution >> m;
                mipOffset[m] = totalMipSize;
                totalMipSize += (sLen * sLen * sLen) * bufferSize;
            }

            hostBuffer = new BlockID[totalMipSize];

            return;
        }

        public override void Init(BlockGroup group, Chunk chunk)
        {
            this.chunk = chunk;

            // Duplicate the material
            matProp = new MaterialPropertyBlock();

            // Update my initial position
            position = chunk.centerPos;
            renderPosition = chunk.positionOffset;

            //blockExtraPointers_buffer = new ComputeBuffer(this.chunk.blockData.Length, sizeof(uint));
            fsBufSize = 0;

            // TODO: count FS's only
            if (chunk.blockExtrasDict.Count > 0)
            {
                // TODO: variable
                fsBufSize = chunk.blockExtrasDict.Count;
                int maxFSs = 4096;
                if (fsBufSize > maxFSs)
                {
                    Debug.LogError($"Too many FS's !! ({fsBufSize} in Chunk {chunk.positionOffset / 32})");
                    fsBufSize = maxFSs;
                }

                CreateFSTex3D(out fineStructure16_tex3D, out tex3D_tmpBuf, fsBufSize, fsBufRowlen, 16);

                blkEx_tmpBuf = new uint[this.chunk.blockData.Length];
            }
        }

        public virtual void FillHostBuffer(ref BlockID[] hostBuffer, BlockID[] content, int index)
        {
            FillHostBuffer(ref hostBuffer, content, index, fsResolution * fsResolution, fsResolution, 1);
        }

        // Mainly this guy takes huge time (~247.22ms when 1504 FS16's)
        public virtual void FillHostBuffer(ref BlockID[] hostBuffer, BlockID[] content, int index, int flatX, int flatY, int flatZ)
        {
            UnityEngine.Profiling.Profiler.BeginSample("FillHostBuffer()");

            Vector3Int rawOrigin = new Vector3Int(index / fsBufRowlen, index % fsBufRowlen, 0);

            // Upload texture
            // TODO: block update ... pixel-wise gotta SLOWWWW
            for (int m = 0; m < mipCount; m++)
            {
                int mipRes = fsResolution >> m;
                Vector3Int origin = rawOrigin * mipRes;
                for (int x = 0; x < (16 >> m); x++)
                {
                    for (int y = 0; y < (16 >> m); y++)
                    {
                        for (int z = 0; z < (16 >> m); z++)
                        {
                            int ix =
                                (origin.x + x)
                              + (origin.y + y) * (fsBufSize / fsBufRowlen) * mipRes
                              + (origin.z + z) * fsBufSize * mipRes * mipRes
                              + mipOffset[m];

                            // TODO: use mode across grids
                            // TODO: make this async & calculate in cpp
                            hostBuffer[ix] = content[
                                ((x << m) + ((m > 0) ? (1 << (m - 1)) : 0)) * flatX + 
                                ((y << m) + ((m > 0) ? (1 << (m - 1)) : 0)) * flatY + 
                                ((z << m) + ((m > 0) ? (1 << (m - 1)) : 0)) * flatZ];
                            //hostBuffer[ix] = (ushort)(0xf00f | (ushort)(0x0100 << m));

                            //fineStructure16_tex3D.SetPixel(origin.x + x, origin.y + y, origin.z + z, new Color((content[x * 16 * 16 + y * 16 + z] / 4096) / 16.0f, (content[x * 16 * 16 + y * 16 + z] % 4096 / 256) / 16.0f, (content[x * 16 * 16 + y * 16 + z] % 256 / 16) / 16.0f, (content[x * 16 * 16 + y * 16 + z] % 16) / 16.0f));
                        }
                    }
                }
            }

            UnityEngine.Profiling.Profiler.EndSample();
        }

        int Flatten(Vector3Int flatFactor, Vector3Int coord)
        {
            Vector3Int res = flatFactor * coord;
            return res.x + res.y + res.z;
        }

        static readonly int[] primaryIndex = new int[6] { 0, 0, 1, 1, 2, 2 };
        static readonly int[] secondaryIndex = new int[6] { 1, 1, 2, 2, 0, 0 };
        static readonly int[] thirdIndex = new int[6] { 2, 2, 0, 0, 1, 1 };
        static readonly int[] direction = new int[6] { 1, -1, 1, -1, 1, -1 };
        static readonly int[] neighborIdx = new int[6] { 0, Chunk.SideLength - 1, 0, Chunk.SideLength - 1, 0, Chunk.SideLength - 1 };

        static readonly int[,] indicesOrder = new int[6, 4]
        {
            { 3, 2, 1, 0 },
            { 0, 1, 2, 3 },
            { 3, 2, 1, 0 },
            { 0, 1, 2, 3 },
            { 3, 2, 1, 0 },
            { 0, 1, 2, 3 },
        };

        protected async Task<Mesh> GenerateMesh(Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            uint vertCount = 0;

            Chunk[] neighbors = new Chunk[6] { Xpos, Xneg, Ypos, Yneg, Zpos, Zneg };

            //////////////////////////////////////////
            //// MESH CREATION
            //////////////////////////////////////////

            Mesh _mesh = new Mesh();

            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector2> uv2s = new List<Vector2>();
            //List<Vector3> colors = new List<Vector3>();
            List<int> indices = new List<int>();

            await Task.Run(() =>
            {
                // Tempory buffers
                // TODO: move me to thread pool
                int len = Chunk.SideLength;

                MeshQuad[,,,] quads = new MeshQuad[6, len, len, len];
                int[,,] heads = new int[6, len, len];
                int[,,] tails = new int[6, len, len];

                int numMergedQuads = 0;
                int[,] mergedQuadPtr = new int[len, len];
                MergedQuad[] mergedQuads = new MergedQuad[len * len];

                // Initialization
                Array.Clear(quads, 0, quads.Length);
                Array.Clear(heads, 0, heads.Length);
                Array.Clear(tails, 0, tails.Length);

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
                            if (!currentBlock.IsRenderable()) { continue; } // empty or ignored

                            // Ugly af
                            Vector3Int currentPos = new Vector3Int(x, y, z);
                            Vector3Int neighborPos;

                            #region Emit faces

                            // [X+, X-, Y+, Y-, Z+, Z-]
                            for(int f = 0; f < 6; f++)
                            {
                                bool isNotBoundary = (f % 2 == 0) ? (currentPos[primaryIndex[f]] < (len - 1)) : (currentPos[primaryIndex[f]] > 0);

                                // Reset Neighbor block in-chunk coordinate
                                neighborPos = currentPos;

                                // Neighbor block in self chunk
                                if (isNotBoundary)
                                {
                                    neighborPos[primaryIndex[f]] += direction[f];
                                    if (chunk.GetBlock(neighborPos.x, neighborPos.y, neighborPos.z).IsSolid()) { continue; }
                                }
                                // Neighbor block in neighbor chunks
                                else if (neighbors[f] != null)
                                {
                                    neighborPos[primaryIndex[f]] = neighborIdx[f];
                                    if(neighbors[f].GetBlock(neighborPos.x, neighborPos.y, neighborPos.z).IsSolid()) { continue; }
                                }

                                ///// Passed neighbor check, emit face /////

                                // Set block data
                                quads[
                                    f,
                                    currentPos[primaryIndex[f]],
                                    currentPos[secondaryIndex[f]],
                                    currentPos[thirdIndex[f]]
                                ].block = currentBlock;

                                // Set self as end
                                quads[
                                    f,
                                    currentPos[primaryIndex[f]],
                                    currentPos[secondaryIndex[f]],
                                    currentPos[thirdIndex[f]]
                                ].next = currentPos[thirdIndex[f]];

                                // Link previous tail to self
                                quads[
                                    f,
                                    currentPos[primaryIndex[f]],
                                    currentPos[secondaryIndex[f]],
                                    tails[
                                        f,
                                        currentPos[primaryIndex[f]],
                                        currentPos[secondaryIndex[f]]
                                    ]
                                ].next = currentPos[thirdIndex[f]];

                                // Set head if not set
                                if(heads[f, currentPos[primaryIndex[f]], currentPos[secondaryIndex[f]]] == 0)
                                {
                                    heads[f, currentPos[primaryIndex[f]], currentPos[secondaryIndex[f]]] = currentPos[thirdIndex[f]] + 1;
                                }

                                // Move tail to self
                                tails[
                                    f,
                                    currentPos[primaryIndex[f]],
                                    currentPos[secondaryIndex[f]]
                                ] = currentPos[thirdIndex[f]];
                            }

                            #endregion
                        }
                    }
                }

                //////////////////////////////////////////
                //// MERGE FACES
                //////////////////////////////////////////

                // [X+, X-, Y+, Y-, Z+, Z-]
                for (int f = 0; f < 6; f++)
                {
                    // Primary idx
                    for (int i = 0; i < len; i++)
                    {
                        // Initilze slice
                        Array.Clear(mergedQuadPtr, 0, mergedQuadPtr.Length);
                        numMergedQuads = 0;

                        // Secondary idx
                        for(int j = 0; j < len; j++)
                        {
                            // Start from HEAD
                            int k = heads[f, i, j] - 1;

                            // Empty column j, ignore
                            if(k < 0) { continue; }

                            //if (quads[f, i, j, k].block.id == 0) { continue; }

                            int continuous = 0;

                            while(k < len)
                            {
                                continuous++;

                                // Need to check if we having a new face
                                if (quads[f, i, j, k].next != k + 1 || !Block.CanMergeRenderable(quads[f, i, j, quads[f, i, j, k].next].block, quads[f, i, j, k].block))
                                {
                                    // This column : from (k - continuous + 1) => (k + 1)
                                    Vector2Int minPt = new Vector2Int(j, k - continuous + 1);
                                    Vector2Int maxPt = new Vector2Int(j + 1, k + 1);

                                    // Check if can be merged with left
                                    if (j > 0 &&
                                       (mergedQuadPtr[j - 1, k] > 0) &&
                                       (Block.CanMergeRenderable(quads[f, i, j - 1, k].block, quads[f, i, j, k].block)) &&
                                       (mergedQuads[mergedQuadPtr[j - 1, k] - 1].min.y == minPt.y)
                                    )
                                    {
                                        mergedQuadPtr[j, k] = mergedQuadPtr[j - 1, k];
                                        minPt = mergedQuads[mergedQuadPtr[j - 1, k] - 1].min;
                                    }
                                    // New merged face
                                    else
                                    {
                                        mergedQuadPtr[j, k] = numMergedQuads + 1;
                                        numMergedQuads++;
                                    }

                                    // Modify face information
                                    mergedQuads[mergedQuadPtr[j, k] - 1].block = quads[f, i, j, k].block;
                                    mergedQuads[mergedQuadPtr[j, k] - 1].min = minPt;
                                    mergedQuads[mergedQuadPtr[j, k] - 1].max = maxPt;

                                    continuous = 0;
                                }

                                // Check if is final
                                if(quads[f, i, j, k].next == k) { break; }

                                // Move to next quad
                                k = quads[f, i, j, k].next;

                            }

                            //for (int k = 0; k < len; k++)
                            //{
                            //    if (quads[f, i, j, k].block.id == 0) { continue; }

                            //    // This column : from (k - continuous + 1) => (k + 1)
                            //    Vector2Int minPt = new Vector2Int(j, k);
                            //    Vector2Int maxPt = new Vector2Int(j + 1, k + 1);

                            //    mergedQuadPtr[j, k] = numMergedQuads;
                            //    numMergedQuads++;

                            //    mergedQuads[mergedQuadPtr[j, k]].block = quads[f, i, j, k].block;
                            //    mergedQuads[mergedQuadPtr[j, k]].min = minPt;
                            //    mergedQuads[mergedQuadPtr[j, k]].max = maxPt;
                            //}
                        }

                        // Emit faces to mesh arrays
                        for(int q = 0; q < numMergedQuads; q++)
                        {
                            var mQ = mergedQuads[q];

                            // Position

                            Vector3 pt = new Vector3();
                            
                            pt[primaryIndex[f]] = i + (direction[f] > 0 ? 1 : 0);
                            pt[secondaryIndex[f]] = mQ.min.x;
                            pt[thirdIndex[f]] = mQ.min.y;
                            vertices.Add(pt); // (min.x, min.y)

                            pt[thirdIndex[f]] = mQ.max.y;
                            vertices.Add(pt); // (min.x, max.y)

                            pt[secondaryIndex[f]] = mQ.max.x;
                            vertices.Add(pt); // (max.x, max.y)

                            pt[thirdIndex[f]] = mQ.min.y;
                            vertices.Add(pt); // (max.x, min.y)

                            // Normal

                            pt = Vector3.zero;
                            pt[primaryIndex[f]] = direction[f];
                            normals.Add(pt);
                            normals.Add(pt);
                            normals.Add(pt);
                            normals.Add(pt);

                            // UV

                            // TODO: Values for testing - no textures now
                            uvs.Add(new Vector2(0, 0));
                            uvs.Add(new Vector2(0, 1));
                            uvs.Add(new Vector2(1, 1));
                            uvs.Add(new Vector2(1, 0));

                            // Block data
                            uv2s.Add(new Vector2(mQ.block.PackFloat(), 0));
                            uv2s.Add(new Vector2(mQ.block.PackFloat(), 0));
                            uv2s.Add(new Vector2(mQ.block.PackFloat(), 0));
                            uv2s.Add(new Vector2(mQ.block.PackFloat(), 0));

                            // Indices

                            int vC = (int)vertCount;

                            indices.Add(vC + indicesOrder[f, 0]);
                            indices.Add(vC + indicesOrder[f, 1]);
                            indices.Add(vC + indicesOrder[f, 2]);
                            
                            indices.Add(vC + indicesOrder[f, 0]);
                            indices.Add(vC + indicesOrder[f, 2]);
                            indices.Add(vC + indicesOrder[f, 3]);

                            // Finish

                            vertCount += 4;
                        }
                    }
                }
            });

            //////////////////////////////////////////
            //// MESH ASSEMBLY
            //////////////////////////////////////////

            // TODO: ToArray() - performance good ?
            _mesh.vertices = vertices.ToArray();
            _mesh.normals = normals.ToArray();
            _mesh.uv = uvs.ToArray();
            _mesh.uv2 = uv2s.ToArray();

            _mesh.triangles = indices.ToArray();

            return _mesh;
        }

        protected async Task FillFSBuffers()
        {
            await Task.Run(() =>
            {
                // Before readback, fill compute buffer for block ex pointers & tex3D
                UnityEngine.Profiling.Profiler.BeginSample($"FS16: Fill HostBuffer ({fsBufSize})");

                System.Array.Clear(blkEx_tmpBuf, 0, blkEx_tmpBuf.Length);
                uint bexCount = 0;
                foreach (var bexPair in chunk.blockExtrasDict)
                {
                    Vector3Int pos = bexPair.Key;
                    int flatten_ix = pos.x * Chunk.SideLength * Chunk.SideLength + pos.y * Chunk.SideLength + pos.z;
                    blkEx_tmpBuf[flatten_ix] = bexCount | 0x80000000;

                    FillHostBuffer(ref tex3D_tmpBuf, (bexPair.Value as Voxelis.BlockExtras.FineStructure_16).blockData, (int)bexCount);

                    bexCount += 1;
                    if (bexCount >= fsBufSize) { break; }
                }

                UnityEngine.Profiling.Profiler.EndSample();
            }
            );
        }

        protected async Task<uint> DoWork(Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            var res = GenerateMesh(chunk, Xpos, Xneg, Ypos, Yneg, Zpos, Zneg);

            if (fsBufSize > 0)
            { 
                await FillFSBuffers();
            }

            mesh = await res;
            return (uint)mesh.vertexCount;
        }

        void Afterwards(uint vCountRequired)
        {
            if (fsBufSize > 0) // Do only if we need render fs's
            {
                UnityEngine.Profiling.Profiler.BeginSample($"FS16: SetPixelData ({totalMipSize / 512} KiB)");

                for (int m = 0; m < mipCount; m++)
                {
                    fineStructure16_tex3D.SetPixelData(tex3D_tmpBuf, m, mipOffset[m]);
                }

                UnityEngine.Profiling.Profiler.EndSample();
                UnityEngine.Profiling.Profiler.BeginSample($"FS16: Upload ({totalMipSize / 512} KiB)");

                fineStructure16_tex3D.Apply(false, true);
                Globals.voxelisMain.Instance.lastFSTex3D = fineStructure16_tex3D;
                //blockExtraPointers_buffer.SetData(blkEx_tmpBuf);

                // Assign to material
                matProp.SetTexture("_FSTex", fineStructure16_tex3D);
                matProp.SetFloat("blockSize", chunk.blockSize);
                matProp.SetVector("FStexGridSize", new Vector3(1.0f / (fsBufSize / fsBufRowlen), 1.0f / fsBufRowlen, 1.0f));

                UnityEngine.Profiling.Profiler.EndSample();
            }

            populated = true;
            waiting = false;

            if(mesh.vertexCount > 0)
            {
                Globals.voxelisMain.Instance.lastMesh = mesh;
            }
        }

        public void GenerateGeometryNeighborAware(BlockGroup group, Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            if (waiting) { return; }
            waiting = true;

            // Dispatch work thread
            task = DoWork(chunk, Xpos, Xneg, Ypos, Yneg, Zpos, Zneg);

            chunk.dirty = false;

            // Update my position
            position = chunk.centerPos;
            renderPosition = chunk.positionOffset;
            bound = new Bounds(chunk.centerPos, Vector3.one * 32.0f);
        }

        public override void GenerateGeometry(BlockGroup group, Chunk chunk)
        {
            GenerateGeometryNeighborAware(group, chunk, null, null, null, null, null, null);
        }

        public override void Clean()
        {
            populated = false;
            chunk.renderer = null;
        }

        public override bool IsReadyForPresent()
        {
            return (populated && matProp != null && mesh != null) || task != null;
        }

        public override void Render(BlockGroup group)
        {
            if(task != null)
            {
                if(task.IsCompleted)
                {
                    Afterwards(task.Result);
                    task = null;
                }
                //else
                //{
                //    return;
                //}
            }

            if(IsReadyForPresent())
            {
                RenderAt(group.transform);
            }
        }

        public void RenderAt(Transform transform)
        {
            if (mesh == null || mesh.vertexCount <= 0) { return; }

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
        }

        public override uint GetVertCount()
        {
            return (mesh == null) ? 0U : (uint)mesh.vertexCount;
        }

        public override Chunk GetChunk()
        {
            return chunk;
        }

        public void ExportMesh(string prefix)
        {
            if (mesh == null || mesh.vertexCount <= 0) { return; }
            string name = $"{prefix}_{chunk.positionOffset.x / 32}_{chunk.positionOffset.y / 32}_{chunk.positionOffset.z / 32}.obj";

            StringBuilder sb = new StringBuilder();

            sb.Append("mtllib ").Append("material.mtl").Append("\n");
            //sb.Append("usemtl ").Append("BlockAtlas").Append("\n");
            sb.Append("usemtl ").Append("RGB444").Append("\n");

            //sb.Append("g ").Append("Chunk").Append("\n");
            sb.Append("o\n");

            sb.Append("\n");
            foreach (var v in mesh.normals)
            {
                sb.Append(string.Format("vn {0} {1} {2}\n", v.x, v.y, v.z));
            }

            sb.Append("\n");
            foreach (var v in mesh.uv2)
            {
                Block blk = Block.UnpackFloat(v.x);

                if (blk.id < 0 || blk.id > 65535)
                {
                    Debug.LogError(blk.id);
                }

                Vector2 uv = Vector2.zero;

                //if (v.block.id == 0xffff)
                if (true) // All blocks are pure color now
                {
                    int r = (int)((blk.meta >> 24) & 0xff);
                    int g = (int)((blk.meta >> 16) & 0xff);
                    int b = (int)((blk.meta >> 8) & 0xff);
                    uv.x = ((r * 4 + b / 4) + 0.5f) / 64.0f;
                    uv.y = ((g * 4 + b % 4) + 0.5f) / 64.0f;
                }
                else
                {
                    BlockDefinition def = Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.blockDefinitions[blk.id];
                    Vector2 uv_org = Vector2.zero, uv_size = Vector2.zero;

                    if (def != null)
                    {
                        uv_org = new Vector2(def.uvw.x, def.uvw.y);
                        uv_size = def.uvSize;
                    }

                    // TODO: FIXME: use correct texcoords
                    uv = uv_org + Vector2.zero * uv_size;
                }

                sb.Append(string.Format("vt {0} {1}\n", uv.x, uv.y));
                //sb.Append(string.Format("vt {0} {1}\n", v.uv.x, v.uv.y));
            }

            sb.Append("\n");
            foreach (var v in mesh.vertices)
            {
                sb.Append(string.Format("v {0} {1} {2}\n", v.x, v.y, v.z));
            }

            sb.Append("\n");
            int idxCount = mesh.triangles.Length;
            for (int i = 0; i < idxCount; i += 3)
            {
                sb.Append(string.Format("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n",
                        mesh.triangles[i] + 1, mesh.triangles[i+1] + 1, mesh.triangles[i+2] + 1));
            }

            using (StreamWriter sw = new StreamWriter(name))
            {
                sw.Write(sb.ToString());
            }
        }

        public static void ExportMeshMaterials(string prefix)
        {
            StringBuilder sb;

            // Material
            if (!MaterialExported)
            {
                Texture2D tex = new Texture2D(
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.width,
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.height,
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.format,
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.mipmapCount, false);
                Graphics.CopyTexture(Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray, 0, tex, 0);

                File.WriteAllBytes($"{prefix}/atlas.png", tex.EncodeToPNG());

                // RGB444 colors
                Texture2D texRGB = new Texture2D(64, 64);
                for (int r = 0; r < 16; r++)
                    for (int g = 0; g < 16; g++)
                        for (int b = 0; b < 16; b++)
                        {
                            texRGB.SetPixel(r * 4 + b / 4, g * 4 + b % 4, new Color(r / 16.0f, g / 16.0f, b / 16.0f));
                        }
                File.WriteAllBytes($"{prefix}/rgb.png", texRGB.EncodeToPNG());

                sb = new StringBuilder();
                //sb.Append("newmtl BlockAtlas\nKa 1.0 1.0 1.0\nKd 1.0 1.0 1.0\nKs 0.0 0.0 0.0\nmap_Ka atlas.png\nmap_Kd atlas.png\nmap_Ks atlas.png");
                sb.Append("newmtl BlockAtlas\nKa 0.0 0.0 0.0\nKd 1.0 1.0 1.0\nKs 0.0 0.0 0.0\nmap_Kd atlas.png\n\n");

                sb.Append("newmtl RGB444\nKa 0.0 0.0 0.0\nKd 1.0 1.0 1.0\nKs 0.0 0.0 0.0\nmap_Kd rgb.png");

                using (StreamWriter sw = new StreamWriter($"{prefix}/material.mtl"))
                {
                    sw.Write(sb.ToString());
                }

                MaterialExported = true;
            }
        }
    }
}
