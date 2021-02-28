using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Voxelis.Rendering
{
    public class ChunkRenderer_GPUGeometry_Raymarch : ChunkRenderer_GPUComputeMesh
    {
        public Texture3D fineStructure16_tex3D;
        public ComputeBuffer blockExtraPointers_buffer;

        uint[] blkEx_tmpBuf;
        ushort[] tex3D_tmpBuf;
        int fsBufSize, fsBufRowlen = 16;

        public struct Vertex_GR
        {
            Vector3 position;
            Vector3 normal;
            Vector2 uv;
            int block_vert_meta;
            uint id;
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
                int maxFSs = 1024;
                if(fsBufSize > maxFSs)
                {
                    Debug.LogError($"Too many FS's !! ({fsBufSize} in Chunk {chunk.positionOffset / 32})");
                    fsBufSize = maxFSs;
                }
                fsBufSize = Mathf.CeilToInt(chunk.blockExtrasDict.Count / 16.0f) * 16;

                //fineStructure16_tex3D = new Texture3D(16 * (fsBufSize / fsBufRowlen), 16 * fsBufRowlen, 16, TextureFormat.R16, false);
                fineStructure16_tex3D = new Texture3D(16 * (fsBufSize / fsBufRowlen), 16 * fsBufRowlen, 16, TextureFormat.Alpha8, false);
                fineStructure16_tex3D.filterMode = FilterMode.Point;
                fineStructure16_tex3D.wrapMode = TextureWrapMode.Clamp;

                blkEx_tmpBuf = new uint[this.chunk.blockData.Length];
                tex3D_tmpBuf = new ushort[16 * (fsBufSize / fsBufRowlen) * 16 * fsBufRowlen * 16];
            }
        }

        public override void GenerateGeometry(BlockGroup group, Chunk chunk)
        {
            if (waiting) { return; }
            waiting = true;

            _ind = new uint[] { 0, 1, 0, 0, 0 };
            indBuffer.SetData(_ind);

            inputBuffer.SetData(this.chunk.blockData);

            // Set buffers for I/O
            cs_chunkMeshPopulator.SetBuffer(1, "indirectBuffer", indBuffer);
            cs_chunkMeshPopulator.SetBuffer(1, "chunkData", inputBuffer);

            // Get chunk vert count
            cs_chunkMeshPopulator.Dispatch(1, 32 / 8, 32 / 8, 32 / 8);

            #region Overrided

            // Before readback, fill compute buffer for block ex pointers & tex3D
            if(fsBufSize > 0) // Do only if we need render fs's
            {
                System.Array.Clear(blkEx_tmpBuf, 0, blkEx_tmpBuf.Length);
                uint bexCount = 0;
                foreach (var bexPair in chunk.blockExtrasDict)
                {
                    Vector3Int pos = bexPair.Key;
                    int flatten_ix = pos.x * Chunk.SideLength * Chunk.SideLength + pos.y * Chunk.SideLength + pos.z;
                    blkEx_tmpBuf[flatten_ix] = bexCount | 0x80000000;

                    Vector3Int origin = new Vector3Int((int)bexCount / 16, (int)bexCount % 16, 0) * 16;

                    // Upload texture
                    // TODO: block update ... pixel-wise gotta SLOWWWW
                    for(int x = 0; x < 16; x++)
                    {
                        for(int y = 0; y < 16; y++)
                        {
                            for(int z = 0; z < 16; z++)
                            {
                                //int ix = (origin.x + x) * fsBufRowlen * 16 + (origin.y + y) + (origin.z + z);
                                //tex3D_tmpBuf[ix] = (bexPair.Value as Voxelis.BlockExtras.FineStructure_16).blockData[x * 16 * 16 + y * 16 + z];
                                fineStructure16_tex3D.SetPixel(origin.x + x, origin.y + y, origin.z + z, new Color(0, 0, 0, (bexPair.Value as Voxelis.BlockExtras.FineStructure_16).blockData[x * 16 * 16 + y * 16 + z]));
                            }
                        }
                    }

                    bexCount += 1;
                    //Debug.Log(bexCount);
                    if(bexCount > fsBufSize) { break; }
                }

                //fineStructure16_tex3D.SetPixelData(tex3D_tmpBuf, 0);
                fineStructure16_tex3D.Apply();
                blockExtraPointers_buffer.SetData(blkEx_tmpBuf);

                // Assign to material
                matProp.SetTexture("_FSTex", fineStructure16_tex3D);
                matProp.SetFloat("blockSize", chunk.blockSize);
                matProp.SetVector("FStexGridSize", new Vector3(1.0f / (fsBufSize / 16), 1.0f / fsBufRowlen, 1.0f));
            }

            #endregion

            indBuffer.GetData(_ind);
            inputBuffer.SetData(this.chunk.blockData);

            int allocSize = (int)(_ind[0] * 1.25) + 1024;
            //int allocSize = 65536;
            //_ind[0] = 65536;

            // Need to extend buffer size
            if (vCount < _ind[0])
            {
                // Realloc
                if (buffer != null)
                {
                    buffer_bak = buffer;
                    matProp.SetBuffer("cs_vbuffer", buffer_bak);
                }

                // 1.0 - scale factor for potentially more blocks
                buffer = new ComputeBuffer(allocSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vertex_GR)));
            }
            else if (_ind[0] == 0)
            {
                chunk.dirty = false;
                populated = true;
                waiting = false;
                return;
            }

            vCount = (uint)allocSize;
            _ind[0] = 0;
            indBuffer.SetData(_ind);

            buffer.SetCounterValue(0);
            cs_chunkMeshPopulator.SetBuffer(0, "vertexBuffer", buffer);
            cs_chunkMeshPopulator.SetBuffer(0, "indirectBuffer", indBuffer);
            cs_chunkMeshPopulator.SetBuffer(0, "chunkData", inputBuffer);
            cs_chunkMeshPopulator.SetBuffer(0, "fsPointerBuffer", blockExtraPointers_buffer);
            cs_chunkMeshPopulator.SetInt("fsBufLenX", fsBufSize / 16);
            cs_chunkMeshPopulator.SetInt("fsBufLenY", fsBufRowlen);

            // Invoke it
            cs_chunkMeshPopulator.Dispatch(0, 32 / 8, 32 / 8, 32 / 8);

            // Set an sync fence
            GPUDispatchManager.Singleton.AppendTask(this);

            //Vertex[] output = new Vertex[262144];
            //buffer.GetData(output);
            //indBuffer.GetData(_ind);

            //Debug.Log("CS finished with " + _ind[0] + " vertices");
            //Debug.Log(_ind);

            // Update my position
            position = chunk.centerPos;
            renderPosition = chunk.positionOffset;
            bound = new Bounds(chunk.centerPos, Vector3.one * 32.0f);

            chunk.dirty = false;
        }

        public override void Clean()
        {
            base.Clean();

            if(blockExtraPointers_buffer != null)
            {
                blockExtraPointers_buffer.Dispose();
            }
        }
    }
}
