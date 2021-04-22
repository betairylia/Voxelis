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
using BlockID = System.UInt16;

namespace Voxelis.Rendering
{
    // REFACTOR PLEASE WT...H
    public class ChunkRenderer_GS_Raymarch : ChunkRenderer_GPUComputeMesh, INeighborAwareChunkRenderable
    {
        public static bool MaterialExported = false;

        public Texture3D fineStructure16_tex3D;
        public ComputeBuffer blockExtraPointers_buffer;

        protected uint[] blkEx_tmpBuf;
        protected BlockID[] tex3D_tmpBuf;
        public int fsBufSize { get; protected set; }
        public int totalMipSize { get; protected set; }
        protected int fsBufRowlen = 32, fsResolution = 16, mipCount = 0;
        protected int[] mipOffset;

        Task<uint> task = null;
        protected int realVertCount;

        [StructLayout(LayoutKind.Sequential)]
        public struct GS_PointVertex
        {
            public uint data;
            // 6: Faces, 5: X, 5: Y, 5: Z

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

        protected GS_PointVertex[] primaryBuffer;

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

            blockExtraPointers_buffer = new ComputeBuffer(this.chunk.blockData.Length, sizeof(uint));
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

        protected async Task<uint> FillPrimaryBuffer(Chunk chunk, Chunk Xpos, Chunk Xneg, Chunk Ypos, Chunk Yneg, Chunk Zpos, Chunk Zneg)
        {
            uint vertCount = 0;

            await Task.Run(() =>
            {
                GS_PointVertex[] verts = new GS_PointVertex[32768];
                int len = Chunk.SideLength;
                for (int x = 0; x < len; x++)
                {
                    for (int y = 0; y < len; y++)
                    {
                        for (int z = 0; z < len; z++)
                        {
                            Block currentBlock = chunk.GetBlock(x, y, z);
                            if (!currentBlock.IsRenderable()) { continue; } // empty or ignored

                            // Ugly af
                            uint faceBits = 0x0000;

                            #region Fill Face Bits

                            // X+
                            if (x < (len - 1))
                            {
                                // https://www.dotnetperls.com/convert-bool-int
                                faceBits |= !chunk.GetBlock(x + 1, y, z).IsSolid() ? 1U : 0U;
                            }
                            else
                            {
                                faceBits |= ((Xpos == null) || !(Xpos.GetBlock(0, y, z).IsSolid())) ? 1U : 0U;
                            }

                            // X-
                            if (x > 0)
                            {
                                faceBits |= !chunk.GetBlock(x - 1, y, z).IsSolid() ? 2U : 0U;
                            }
                            else
                            {
                                faceBits |= ((Xneg == null) || !Xneg.GetBlock(len - 1, y, z).IsSolid()) ? 2U : 0U;
                            }

                            // Y+
                            if (y < (len - 1))
                            {
                                faceBits |= !chunk.GetBlock(x, y + 1, z).IsSolid() ? 4U : 0U;
                            }
                            else
                            {
                                faceBits |= ((Ypos == null) || !Ypos.GetBlock(x, 0, z).IsSolid()) ? 4U : 0U;
                            }

                            // Y-
                            if (y > 0)
                            {
                                faceBits |= !chunk.GetBlock(x, y - 1, z).IsSolid() ? 8U : 0U;
                            }
                            else
                            {
                                faceBits |= ((Yneg == null) || !Yneg.GetBlock(x, len - 1, z).IsSolid()) ? 8U : 0U;
                            }

                            // Z+
                            if (z < (len - 1))
                            {
                                faceBits |= !chunk.GetBlock(x, y, z + 1).IsSolid() ? 16U : 0U;
                            }
                            else
                            {
                                faceBits |= ((Zpos == null) || !Zpos.GetBlock(x, y, 0).IsSolid()) ? 16U : 0U;
                            }

                            // Z-
                            if (z > 0)
                            {
                                faceBits |= !chunk.GetBlock(x, y, z - 1).IsSolid() ? 32U : 0U;
                            }
                            else
                            {
                                faceBits |= ((Zneg == null) || !Zneg.GetBlock(x, y, len - 1).IsSolid()) ? 32U : 0U;
                            }

                            #endregion

                            // We don't render this block.
                            if (faceBits == 0)
                            {
                                continue;
                            }

                            verts[vertCount].block = currentBlock;
                            verts[vertCount].data = (
                                (faceBits  << 15) + 
                                (((uint)x) << 10) +
                                (((uint)y) <<  5) +
                                (((uint)z))
                            );

                            vertCount++;
                        }
                    }
                }

                // Copy to main buffer
                // TODO: avoid realloc
                primaryBuffer = new GS_PointVertex[vertCount];
                Array.Copy(verts, 0, primaryBuffer, 0, vertCount);
            });

            return vertCount;
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
            var res = FillPrimaryBuffer(chunk, Xpos, Xneg, Ypos, Yneg, Zpos, Zneg);

            if (fsBufSize > 0)
            { 
                await FillFSBuffers();
            }

            return await res;
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
                blockExtraPointers_buffer.SetData(blkEx_tmpBuf);

                // Assign to material
                matProp.SetTexture("_FSTex", fineStructure16_tex3D);
                matProp.SetFloat("blockSize", chunk.blockSize);
                matProp.SetVector("FStexGridSize", new Vector3(1.0f / (fsBufSize / fsBufRowlen), 1.0f / fsBufRowlen, 1.0f));

                UnityEngine.Profiling.Profiler.EndSample();
            }

            UnityEngine.Profiling.Profiler.BeginSample("CRender_GSRayM: Primary buffer");

            int allocSize = Mathf.Min(Chunk.SideLength * Chunk.SideLength * Chunk.SideLength, (int)(vCountRequired * 1.25) + 1024);
            //int allocSize = (int)(vCountRequired * 1.25) + 1024;

            // Need to extend buffer size
            if (buffer == null || (buffer.count < vCountRequired))
            {
                // Realloc
                // TODO: carefully select the type / mode of the buffer ?
                buffer?.Dispose();
                buffer = new ComputeBuffer(allocSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(GS_PointVertex)));
            }
            else if (vCountRequired == 0)
            {
                chunk.dirty = false;
                populated = true;
                waiting = false;

                // TODO: FIXME: ?
                buffer?.Dispose();
                vCount = 0;
                realVertCount = 0;

                UnityEngine.Profiling.Profiler.EndSample();

                return;
            }

            vCount = (uint)allocSize;

            realVertCount = (int)vCountRequired;

            buffer.SetData(primaryBuffer, 0, 0, realVertCount);
            matProp.SetBuffer("vbuffer", buffer);

            populated = true;
            waiting = false;

            UnityEngine.Profiling.Profiler.EndSample();
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
            base.Clean();

            if (blockExtraPointers_buffer != null)
            {
                blockExtraPointers_buffer.Dispose();
            }
        }

        public override bool IsReadyForPresent()
        {
            return (populated && matProp != null && buffer != null) || task != null;
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
                else
                {
                    return;
                }
            }

            if(IsReadyForPresent())
            {
                base.Render(group);
            }
        }

        public override void RenderAt(Transform transform)
        {
            //if(vCount <= 0) { return; }

            var mat = Matrix4x4.TRS(transform.position + renderPosition, transform.rotation, transform.lossyScale);
            matProp.SetMatrix("_LocalToWorld", mat);
            matProp.SetMatrix("_WorldToLocal", mat.inverse);

            Graphics.DrawProcedural(
                chunkMat,
                new Bounds(transform.position + position, transform.lossyScale * 32),
                MeshTopology.Points,
                realVertCount, 1, null, matProp
            );
        }
    }
}
