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
        public struct MeshData
        {
            public Vector3[] vertex;
            public Vector3[] normal;
            public Color[] colors;
            public Vector4[] tangent;
            public Vector2[] uv;

            public UnityEngine.Rendering.IndexFormat idxFormat;
            public int[] index;

            public Bounds bounds;
        }

        protected abstract class MeshOptimizer
        {
            public int len, numMergedQuads;
            public MeshQuad[,,,] quads;
            public int[,,] heads, tails;
            public int[,] mergedQuadPtr;
            public MergedQuad[] mergedQuads;

            public Vector3[] vertices;
            public Vector2[] uvs;
            public int[] indices;

            public int vertCount;
            public int idxCount;

            public Bounds bound = new Bounds(new Vector3(16, 16, 16), new Vector3(32, 32, 32));

            public abstract void MergedQuadProcessor(int faceID, int primaryIdx, MergedQuad mQ);
            public abstract bool RenderableMergeable(Block left, Block right);

            public MeshOptimizer(
                int size, Vector3[] vertices = null, Vector2[] uvs = null, int[] indices = null, int vertCount = 0)
            {
                if(vertices == null) { vertices = new Vector3[vertCapacity]; }
                if(uvs == null) { uvs = new Vector2[vertCapacity]; }
                if(indices == null) { indices = new int[idxCapacity]; }

                this.vertices = vertices;
                this.uvs = uvs;
                this.indices = indices;
                this.vertCount = vertCount;

                // Tempory buffers
                // TODO: move me to thread pool
                len = size;

                quads = new MeshQuad[6, len, len, len]; // Using too much memory ...
                heads = new int[6, len, len];
                tails = new int[6, len, len];

                numMergedQuads = 0;
                mergedQuadPtr = new int[len, len];
                mergedQuads = new MergedQuad[len * len];

                // Initialization
                Array.Clear(quads, 0, quads.Length);
                Array.Clear(heads, 0, heads.Length);
                Array.Clear(tails, 0, tails.Length);
            }

            public void SetResultBuffers(Vector3[] vertices, Vector2[] uvs, int[] indices, int vertCount, int idxCount)
            {
                this.vertices = vertices;
                this.uvs = uvs;
                this.indices = indices;

                this.vertCount = vertCount;
                this.idxCount = idxCount;
            }

            public MeshOptimizer(MeshOptimizer from)
            {
                len = from.len;

                quads = from.quads;
                heads = from.heads;
                tails = from.tails;

                numMergedQuads = 0;
                mergedQuadPtr = new int[len, len];
                mergedQuads = new MergedQuad[len * len];
            }

            /// <summary>
            /// Emit face (quad) at pos, facing "faceID".
            /// This serves as "input function" for this optimizer.
            /// </summary>
            /// <param name="faceID">0, 1, 2, 3, 4, 5 = Xp, Xn, Yp, Yn, Zp, Zn</param>
            /// <param name="pos"></param>
            /// <param name="block">Face info, e.g. blockID, chiselID, etc.</param>
            public void EmitFaceAt(int faceID, Vector3Int pos, Block block)
            {
                // Set block data
                quads[
                    faceID,
                    pos[primaryIndex[faceID]],
                    pos[secondaryIndex[faceID]],
                    pos[thirdIndex[faceID]]
                ].block = block;

                // Set self as end
                quads[
                    faceID,
                    pos[primaryIndex[faceID]],
                    pos[secondaryIndex[faceID]],
                    pos[thirdIndex[faceID]]
                ].next = pos[thirdIndex[faceID]];

                // Link previous tail to self
                quads[
                    faceID,
                    pos[primaryIndex[faceID]],
                    pos[secondaryIndex[faceID]],
                    tails[
                        faceID,
                        pos[primaryIndex[faceID]],
                        pos[secondaryIndex[faceID]]
                    ]
                ].next = pos[thirdIndex[faceID]];

                // Set head if not set
                if (heads[faceID, pos[primaryIndex[faceID]], pos[secondaryIndex[faceID]]] == 0)
                {
                    heads[faceID, pos[primaryIndex[faceID]], pos[secondaryIndex[faceID]]] = pos[thirdIndex[faceID]] + 1;
                }

                // Move tail to self
                tails[
                    faceID,
                    pos[primaryIndex[faceID]],
                    pos[secondaryIndex[faceID]]
                ] = pos[thirdIndex[faceID]];
            }

            // Quads, Heads & Tails will not be modified in Build().
            public void Build()
            {
                UnityEngine.Profiling.Profiler.BeginSample("Mesher.Build");

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
                        for (int j = 0; j < len; j++)
                        {
                            // Start from HEAD
                            int k = heads[f, i, j] - 1;

                            // Empty column j, ignore
                            if (k < 0) { continue; }

                            //if (quads[f, i, j, k].block.id == 0) { continue; }

                            int continuous = 0;

                            while (k < len)
                            {
                                continuous++;

                                // Need to check if we having a new face
                                if (quads[f, i, j, k].next != k + 1 || !RenderableMergeable(quads[f, i, j, quads[f, i, j, k].next].block, quads[f, i, j, k].block))
                                {
                                    // This column : from (k - continuous + 1) => (k + 1)
                                    Vector2Int minPt = new Vector2Int(j, k - continuous + 1);
                                    Vector2Int maxPt = new Vector2Int(j + 1, k + 1);

                                    // Check if can be merged with left
                                    if (j > 0 &&
                                       (mergedQuadPtr[j - 1, k] > 0) &&
                                       (RenderableMergeable(quads[f, i, j - 1, k].block, quads[f, i, j, k].block)) &&
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
                                if (quads[f, i, j, k].next == k) { break; }

                                // Move to next quad
                                k = quads[f, i, j, k].next;

                            }
                        }

                        // Emit faces to mesh arrays
                        for (int q = 0; q < numMergedQuads; q++)
                        {
                            MergedQuadProcessor(f, i, mergedQuads[q]);
                        }
                    }
                }

                UnityEngine.Profiling.Profiler.EndSample();
            }

            public virtual void Clear()
            {
                numMergedQuads = 0;

                // Initialization
                Array.Clear(quads, 0, quads.Length);
                Array.Clear(heads, 0, heads.Length);
                Array.Clear(tails, 0, tails.Length);
                Array.Clear(mergedQuadPtr, 0, mergedQuadPtr.Length);
                Array.Clear(mergedQuads, 0, mergedQuads.Length);
            }

            public virtual void ClearMeshResults()
            {
                vertCount = 0;
                idxCount = 0;
            }

            public virtual void AssignToMesh(ref Mesh mesh)
            {
                MeshData d = new MeshData();
                GetMeshData(ref d);
                AssignToMeshStatic(ref mesh, d);
            }

            public virtual void GetMeshData(ref MeshData data)
            {
                if (vertCount > 65534)
                {
                    data.idxFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                }

                data.vertex = new Vector3[vertCount];
                data.uv = new Vector2[vertCount];
                data.index = new int[idxCount];

                Array.Copy(vertices, 0, data.vertex, 0, vertCount);
                Array.Copy(uvs, 0, data.uv, 0, vertCount);
                Array.Copy(indices, 0, data.index, 0, idxCount);

                data.normal = null;
                data.tangent = null;

                data.bounds = bound;
            }

            public static void AssignToMeshStatic(ref Mesh mesh, MeshData mdata)
            {
                mesh.indexFormat = mdata.idxFormat;

                if (mdata.vertex != null)
                {
                    mesh.vertices = mdata.vertex;
                }

                if (mdata.uv != null)
                {
                    mesh.uv = mdata.uv;
                }

                if (mdata.normal != null)
                {
                    mesh.normals = mdata.normal;
                }

                if (mdata.tangent != null)
                {
                    mesh.tangents = mdata.tangent;
                }

                if (mdata.colors != null)
                {
                    mesh.colors = mdata.colors;
                }

                if (mdata.index != null)
                {
                    mesh.triangles = mdata.index;
                }

                mesh.bounds = mdata.bounds;
            }
        }

        protected class MeshOptimizer_Graphics : MeshOptimizer
        {
            public MeshOptimizer_Graphics(int size, Vector3[] vertices = null, Vector2[] uvs = null, int[] indices = null) : base(size, vertices, uvs, indices)
            {
            }

            public override void MergedQuadProcessor(int f, int i, MergedQuad mQ)
            {
                // UV

                // Block data
                Vector2 uv;

                uv = PackDataToUV_Regular((FaceInfo)f, new Vector2Int(0, 0), mQ.block);
                uvs[vertCount + 0] = uv;

                uv = PackDataToUV_Regular((FaceInfo)f, new Vector2Int(0, mQ.max.y - mQ.min.y), mQ.block);
                uvs[vertCount + 1] = uv;
                    
                uv = PackDataToUV_Regular((FaceInfo)f, new Vector2Int(mQ.max.x - mQ.min.x, mQ.max.y - mQ.min.y), mQ.block);
                uvs[vertCount + 2] = uv;

                uv = PackDataToUV_Regular((FaceInfo)f, new Vector2Int(mQ.max.x - mQ.min.x, 0), mQ.block);
                uvs[vertCount + 3] = uv;

                // Position

                Vector3Int pt = new Vector3Int();

                pt[primaryIndex[f]] = i + (direction[f] > 0 ? 1 : 0);
                pt[secondaryIndex[f]] = mQ.min.x;
                pt[thirdIndex[f]] = mQ.min.y;
                vertices[vertCount + 0] = pt; // (min.x, min.y)

                pt[thirdIndex[f]] = mQ.max.y;
                vertices[vertCount + 1] = pt; // (min.x, max.y)

                pt[secondaryIndex[f]] = mQ.max.x;
                vertices[vertCount + 2] = pt; // (max.x, max.y)

                pt[thirdIndex[f]] = mQ.min.y;
                vertices[vertCount + 3] = pt; // (max.x, min.y)

                // Indices

                int vC = (int)vertCount;

                indices[idxCount++] = vC + indicesOrder[f, 0];
                indices[idxCount++] = vC + indicesOrder[f, 1];
                indices[idxCount++] = vC + indicesOrder[f, 2];

                indices[idxCount++] = vC + indicesOrder[f, 0];
                indices[idxCount++] = vC + indicesOrder[f, 2];
                indices[idxCount++] = vC + indicesOrder[f, 3];

                // Finish

                vertCount += 4;
            }

            public override bool RenderableMergeable(Block left, Block right)
            {
                return (left.id == right.id);
            }
        }
    }
}