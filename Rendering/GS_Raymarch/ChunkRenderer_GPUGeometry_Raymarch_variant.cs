using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Voxelis.Data;
using BlockID = System.UInt16;

namespace Voxelis.Rendering
{
    // REFACTOR PLEASE WT...H
    public class ChunkRenderer_GPUGeometry_Raymarch_fastvariant : ChunkRenderer_GPUComputeMesh
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

        [StructLayout(LayoutKind.Sequential)]
        public struct Vertex_GR
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv;
            public uint block_vert_meta;
            public Block block;
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
            base.Init(group, chunk);

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

        public override void GenerateGeometry(BlockGroup group, Chunk chunk)
        {
            if (waiting) { return; }
            waiting = true;

            UnityEngine.Profiling.Profiler.BeginSample("CRender_GSRayM: GenerateGeometry");
            
            inputBuffer.SetData(this.chunk.blockData);

            if (buffer == null)
            {
                UnityEngine.Profiling.Profiler.BeginSample("CRender_GSRayM: Dispatch Kernel1");
                _ind = new uint[] { 0, 1, 0, 0, 0 };
                indBuffer.SetData(_ind);

                // Set buffers for I/O
                cs_chunkMeshPopulator.SetBuffer(1, "indirectBuffer", indBuffer);
                cs_chunkMeshPopulator.SetBuffer(1, "chunkData", inputBuffer);

                // Get chunk vert count
                cs_chunkMeshPopulator.Dispatch(1, 32 / 8, 32 / 8, 32 / 8);
                UnityEngine.Profiling.Profiler.EndSample();

                UnityEngine.Profiling.Profiler.BeginSample("CRender_GSRayM: Readback #vert");

                indBuffer.GetData(_ind);

                UnityEngine.Profiling.Profiler.EndSample();

                int allocSize = 65536;

                if (_ind[0] == 0)
                {
                    chunk.dirty = false;
                    populated = true;
                    waiting = false;

                    UnityEngine.Profiling.Profiler.EndSample();

                    return;
                }
                else
                {
                    buffer = new ComputeBuffer(allocSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vertex_GR)));
                    vCount = (uint)allocSize;
                }
            }


            // Set Flatten Factor
            cs_chunkMeshPopulator.SetInts("flatFactor", this.chunk.flatFactor.x, this.chunk.flatFactor.y, this.chunk.flatFactor.z);

            #region Overrided

            // Before readback, fill compute buffer for block ex pointers & tex3D
            if (fsBufSize > 0) // Do only if we need render fs's
            {
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

            #endregion

            UnityEngine.Profiling.Profiler.BeginSample("CRender_GSRayM: Dispatch Kernel0");

            _ind[0] = 0;
            indBuffer.SetData(_ind);

            buffer.SetCounterValue(0);
            cs_chunkMeshPopulator.SetBuffer(0, "vertexBuffer", buffer);
            cs_chunkMeshPopulator.SetBuffer(0, "indirectBuffer", indBuffer);
            cs_chunkMeshPopulator.SetBuffer(0, "chunkData", inputBuffer);
            cs_chunkMeshPopulator.SetBuffer(0, "fsPointerBuffer", blockExtraPointers_buffer);
            cs_chunkMeshPopulator.SetInt("fsBufLenX", fsBufSize / fsBufRowlen);
            cs_chunkMeshPopulator.SetInt("fsBufLenY", fsBufRowlen);

            // Invoke it
            cs_chunkMeshPopulator.Dispatch(0, 32 / 8, 32 / 8, 32 / 8);

            // Set an sync fence
            GPUDispatchManager.Singleton.AppendTask(this);

            // Update my position
            position = chunk.centerPos;
            renderPosition = chunk.positionOffset;
            bound = new Bounds(chunk.centerPos, Vector3.one * 32.0f);

            //ExportMesh(buffer, vCount, $"ExportedMeshes/Chunk_{chunk.positionOffset.x / 32}_{chunk.positionOffset.y / 32}_{chunk.positionOffset.z / 32}.obj");

            chunk.dirty = false;

            UnityEngine.Profiling.Profiler.EndSample();
            UnityEngine.Profiling.Profiler.EndSample();
        }

        public void ExportMesh(ComputeBuffer buffer, uint vCount, string name)
        {
            Vertex_GR[] output = new Vertex_GR[vCount];
            buffer.GetData(output);
            indBuffer.GetData(_ind);

            vCount = _ind[0];

            StringBuilder sb = new StringBuilder();

            sb.Append("mtllib ").Append("material.mtl").Append("\n");
            //sb.Append("usemtl ").Append("BlockAtlas").Append("\n");
            sb.Append("usemtl ").Append("RGB444").Append("\n");

            //sb.Append("g ").Append("Chunk").Append("\n");
            sb.Append("o\n");

            sb.Append("\n");
            foreach (Vertex_GR v in output)
            {
                sb.Append(string.Format("vn {0} {1} {2}\n", v.normal.x, v.normal.y, v.normal.z));
            }

            sb.Append("\n");
            foreach (Vertex_GR v in output)
            {
                if (v.block.id < 0 || v.block.id > 65535)
                {
                    Debug.LogError(v.block.id);
                }

                Vector2 uv = Vector2.zero;

                if (v.block.id == 0xffff)
                {
                    int r = ((v.block.meta >> 12) & 0xf);
                    int g = ((v.block.meta >> 8) & 0xf);
                    int b = ((v.block.meta >> 4) & 0xf);
                    uv.x = ((r * 4 + b / 4) + 0.5f) / 64.0f;
                    uv.y = ((g * 4 + b % 4) + 0.5f) / 64.0f;
                }
                else
                {
                    BlockDefinition def = Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.blockDefinitions[v.block.id];
                    Vector2 uv_org = Vector2.zero, uv_size = Vector2.zero;

                    if (def != null)
                    {
                        uv_org = new Vector2(def.uvw.x, def.uvw.y);
                        uv_size = def.uvSize;
                    }

                    uv = uv_org + v.uv * uv_size;
                }

                sb.Append(string.Format("vt {0} {1}\n", uv.x, uv.y));
                //sb.Append(string.Format("vt {0} {1}\n", v.uv.x, v.uv.y));
            }

            sb.Append("\n");
            foreach (Vertex_GR v in output)
            {
                sb.Append(string.Format("v {0} {1} {2}\n", v.position.x, v.position.y, v.position.z));
            }

            sb.Append("\n");
            for (int i = 0; i < vCount; i += 3)
            {
                sb.Append(string.Format("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n",
                        i + 1, i + 2, i + 3));
            }

            using (StreamWriter sw = new StreamWriter(name))
            {
                sw.Write(sb.ToString());
            }


            // Material
            if (!MaterialExported)
            {
                Texture2D tex = new Texture2D(
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.width,
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.height,
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.format,
                    Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray.mipmapCount, false);
                Graphics.CopyTexture(Globals.voxelisMain.Instance.globalSettings.blockRegistryTable.BlockTexArray, 0, tex, 0);

                File.WriteAllBytes("ExportedMeshes/atlas.png", tex.EncodeToPNG());

                // RGB444 colors
                Texture2D texRGB = new Texture2D(64, 64);
                for (int r = 0; r < 16; r++)
                    for (int g = 0; g < 16; g++)
                        for (int b = 0; b < 16; b++)
                        {
                            texRGB.SetPixel(r * 4 + b / 4, g * 4 + b % 4, new Color(r / 16.0f, g / 16.0f, b / 16.0f));
                        }
                File.WriteAllBytes("ExportedMeshes/rgb.png", texRGB.EncodeToPNG());

                sb = new StringBuilder();
                //sb.Append("newmtl BlockAtlas\nKa 1.0 1.0 1.0\nKd 1.0 1.0 1.0\nKs 0.0 0.0 0.0\nmap_Ka atlas.png\nmap_Kd atlas.png\nmap_Ks atlas.png");
                sb.Append("newmtl BlockAtlas\nKa 0.0 0.0 0.0\nKd 1.0 1.0 1.0\nKs 0.0 0.0 0.0\nmap_Kd atlas.png\n\n");

                sb.Append("newmtl RGB444\nKa 0.0 0.0 0.0\nKd 1.0 1.0 1.0\nKs 0.0 0.0 0.0\nmap_Kd rgb.png");

                using (StreamWriter sw = new StreamWriter("ExportedMeshes/material.mtl"))
                {
                    sw.Write(sb.ToString());
                }

                MaterialExported = true;
            }
        }

        public override void Clean()
        {
            base.Clean();

            if (blockExtraPointers_buffer != null)
            {
                blockExtraPointers_buffer.Dispose();
            }
        }
    }
}
