#define PROFILE
// #define BALL_MOVING_TEST
#define FALLING_SAND_TEST
//#define ALLOC_TIME_TEST

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TypeReferences;
using System;
using Unity.Jobs;
using UnityEngine.Rendering;
using WorldGen.WorldSketch;
using Voxelis.Rendering;
using Voxelis.WorldGen;

// TODO: Refactor for editor usage
namespace Voxelis
{
    public class World : BlockGroup
    {
        public int worldHeight = 4;
        public float waterHeight = 10.0f;

        public int worldSize = 1024;

        public TMPro.TextMeshProUGUI debugText;
        bool worldGenerationCRInProgress;

        protected ChunkGenerator chunkGenerator;

        public Transform groundPlane, capPlane;

        // World Sketch stuffs
        [HideInInspector]
        public bool isSketchReady { get; private set; }

        public SketchResults sketchResults;

        [HideInInspector]
        public int mapLen;

        [Header("Erosion Settings")]
        public ComputeShader erosion_cs;

        [Header("World Sketch Preview")]
        public Texture2D sketchMapTex;
        public UnityEngine.UI.RawImage sketchMinimap;
        public int minimapSize = 300;
        public RectTransform playerPointer;

        public bool showSketchMesh = true;
        public Material sketchMeshMat;

        protected bool loadFreezed = false;
        protected bool showDebugText = true;

        [Space]
        [Header("World Generation")]
        public bool isMainWorld = false;
        public WorldGeneratorDef worldGenDef;

        /*
        [Space]
        [SerializeField]
        protected WorldSketcher sketcher;
        int worldSketchSize = 1024;

        //[Space]
        //[Inherits(typeof(ChunkGenerator))]
        //public TypeReference generatorType;
        public ComputeShader cs_generation;
        public int cs_generation_batchsize = 512;

        [Space]
        public Matryoshka.MatryoshkaGraph matryoshkaGraph;

        [EnumNamedArray(typeof(WorldGen.StructureType))]
        public Matryoshka.MatryoshkaGraph[] structureGraphs = new Matryoshka.MatryoshkaGraph[8];
        */

        // Start is called before the first frame update
        protected override void Start()
        {
            base.Start();

            //ChunkRenderer_GPUComputeMesh.cs_chunkMeshPopulator = cs_chunkMeshPopulator;
            //ChunkRenderer_GPUComputeMesh.chunkMat = chunkMat;

#if ALLOC_TIME_TEST
            // Time test
            var watch = new System.Diagnostics.Stopwatch();
            int count = 1024;

            watch.Start();
            for(int i = 0; i < count; i++)
            {
                var test1 = new Unity.Collections.NativeArray<Block>(32768, Unity.Collections.Allocator.Persistent);
                
                for(int j = 0; j < 32768; j++)
                {
                    var b = test1[j];
                    if(b.id != 0 || b.meta != 0) { Debug.LogError("BAD VALUE"); };
                }
            }
            watch.Stop();
            Debug.Log($"NativeArray<Block>: {watch.ElapsedMilliseconds} ms");

            watch.Restart();
            for (int i = 0; i < count; i++)
            {
                var test1 = new Unity.Collections.NativeArray<uint>(32768, Unity.Collections.Allocator.Persistent);
                test1[0] = 0;
            }
            watch.Stop();
            Debug.Log($"NativeArray<uint>: {watch.ElapsedMilliseconds} ms");

            watch.Restart();
            for (int i = 0; i < count; i++)
            {
                var test1 = new uint[32768];
                test1[0] = 0;
            }
            watch.Stop();
            Debug.Log($"uint[]: {watch.ElapsedMilliseconds} ms");

            watch.Restart();
            for (int i = 0; i < count; i++)
            {
                var test1 = new Block[32768];

                for (int j = 0; j < 32768; j++)
                {
                    var b = test1[j];
                    if (b.id != 0 || b.meta != 0) { Debug.LogError("BAD VALUE"); };
                }
            }
            watch.Stop();
            Debug.Log($"Block[]: {watch.ElapsedMilliseconds} ms");
#endif

            SetWorld();
        }

        protected virtual void SetWorld()
        {
            // Do world sketch
            SketchWorld();

            if(isMainWorld)
            {
                // Initialize GeometryIndependentPass
                GeometryIndependentPass.cs_generation = worldGenDef.cs_generation;
                GeometryIndependentPass.cs_generation_batchsize = worldGenDef.cs_generation_batchsize;
                
                GeometryIndependentPass.Init();

                GeometryIndependentPass.SetWorld(this);
            }

            // Setup generators
            // FIXME: Now fixed to CSGenerator
            chunkGenerator = (ChunkGenerator)System.Activator.CreateInstance(typeof(CSGenerator));

            for (int i = 0; i < 0; i++)
            //for (int i = 0; i < 64; i++)
            {
                Vector2Int pos = new Vector2Int(UnityEngine.Random.Range(-800, 800), UnityEngine.Random.Range(-800, 800));
                CreateStructure(new BoundsInt(pos.x, 5, pos.y, 96, 96, 96), new TestTree());
            }
            //CreateStructure(new BoundsInt(-122, 32, 225, 140, 120, 140), new TestTree());
            //CreateStructure(new BoundsInt(56, 10, 371, 140, 120, 140), new TestTree());
            //CreateStructure(new BoundsInt(143, 0, 177, 140, 120, 140), new TestTree());
        }

        private void SketchWorld(int seed = -1)
        {
            mapLen = worldGenDef.worldSketchSize;

            // Setup computeshaders
            HydraulicErosionGPU.erosion = erosion_cs;

            // Generate height maps
            sketchResults = worldGenDef.sketcher.FillHeightmap(worldGenDef.worldSketchSize, worldGenDef.worldSketchSize);
            //WorldGen.WorldSketch.SillyRiverPlains.FillHeightmap(ref heightMap, ref erosionMap, ref waterMap, worldSketchSize, worldSketchSize);

            if(sketchResults.result.TryGetValue("Sketch", out Texture _tmpTex))
            {
                sketchMapTex = _tmpTex as Texture2D;

                if(sketchMapTex != null)
                {
                    if (sketchMinimap)
                    {
                        sketchMinimap.texture = sketchMapTex;
                        sketchMinimap.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, minimapSize);
                        sketchMinimap.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, minimapSize);
                    }

                    if (showSketchMesh)
                    {
                        ShowSketchMesh();
                    }
                }
            }
        }

        int sketchMeshSize = 256;
        float sketchScale = 4.0f;

        Vector3 GetHeightmapPoint(float uvx, float uvy)
        {
            if(sketchMapTex == null) { return Vector3.zero; }

            // Only used in ShowSketchMesh() so sketchMapTex is not null
            float h = sketchMapTex.GetPixelBilinear(uvx, uvy).r;
            return new Vector3(
                (uvx - 0.5f) * worldGenDef.worldSketchSize * sketchScale,
                h * 256.0f,
                (uvy - 0.5f) * worldGenDef.worldSketchSize * sketchScale
                );
        }

        private void ShowSketchMesh()
        {
            GameObject obj = new GameObject("SketchMesh");
            obj.transform.parent = this.transform;
            obj.transform.position = Vector3.zero;
            obj.AddComponent<MeshFilter>();
            obj.AddComponent<MeshRenderer>();

            Mesh mesh = new Mesh();
            obj.GetComponent<MeshFilter>().mesh = mesh;
            obj.GetComponent<MeshRenderer>().material = new Material(sketchMeshMat);
            obj.GetComponent<MeshRenderer>().material.SetTexture("_Control", sketchMapTex);
            obj.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            obj.GetComponent<MeshRenderer>().receiveShadows = true;

            Vector3[] vert = new Vector3[(sketchMeshSize) * (sketchMeshSize)];
            Vector3[] normal = new Vector3[(sketchMeshSize) * (sketchMeshSize)];
            Vector2[] uv = new Vector2[(sketchMeshSize) * (sketchMeshSize)];
            int[] triangles = new int[6 * (sketchMeshSize - 1) * (sketchMeshSize - 1)];

            for (int i = 0; i < sketchMeshSize; i++)
            {
                for (int j = 0; j < sketchMeshSize; j++)
                {
                    vert[i * sketchMeshSize + j] = GetHeightmapPoint((float)i / sketchMeshSize, (float)j / sketchMeshSize);

                    // Normal calculation
                    Vector3 xx = new Vector3(0, 0, 0), zz = new Vector3(0, 0, 0);

                    if (j > 0)
                    {
                        zz += GetHeightmapPoint((float)(i) / sketchMeshSize, (float)(j) / sketchMeshSize) - GetHeightmapPoint((float)(i) / sketchMeshSize, (float)(j - 1) / sketchMeshSize);
                    }
                    if (j < sketchMeshSize - 1)
                    {
                        zz += GetHeightmapPoint((float)(i) / sketchMeshSize, (float)(j + 1) / sketchMeshSize) - GetHeightmapPoint((float)(i) / sketchMeshSize, (float)(j) / sketchMeshSize);
                    }
                    if (i > 0)
                    {
                        xx += GetHeightmapPoint((float)(i) / sketchMeshSize, (float)(j) / sketchMeshSize) - GetHeightmapPoint((float)(i - 1) / sketchMeshSize, (float)(j) / sketchMeshSize);
                    }
                    if (i < sketchMeshSize - 1)
                    {
                        xx += GetHeightmapPoint((float)(i + 1) / sketchMeshSize, (float)(j) / sketchMeshSize) - GetHeightmapPoint((float)(i) / sketchMeshSize, (float)(j) / sketchMeshSize);
                    }

                    Vector3 yy = Vector3.Cross(zz, xx);
                    yy = yy.normalized;

                    normal[i * sketchMeshSize + j] = yy;

                    uv[i * sketchMeshSize + j] = new Vector2((float)i / sketchMeshSize, (float)j / sketchMeshSize);

                    if (i > 0 && j > 0)
                    {
                        int s = (i - 1) * (sketchMeshSize - 1) + j - 1;
                        s *= 6;

                        triangles[s + 0] = (i - 1) * sketchMeshSize + (j - 1);
                        triangles[s + 1] = (i - 1) * sketchMeshSize + (j - 0);
                        triangles[s + 2] = (i - 0) * sketchMeshSize + (j - 1);
                        triangles[s + 3] = (i - 1) * sketchMeshSize + (j - 0);
                        triangles[s + 4] = (i - 0) * sketchMeshSize + (j - 0);
                        triangles[s + 5] = (i - 0) * sketchMeshSize + (j - 1);
                    }
                }
            }

            mesh.vertices = vert;
            mesh.normals = normal;
            mesh.uv = uv;
            mesh.triangles = triangles;
        }

        int f = 0;
#if BALL_MOVING_TEST
        Vector3 _pos;
        float mspd = 0.2f;
#endif
#if FALLING_SAND_TEST
        [SerializeField] private Transform player;
        [SerializeField] private BoundsInt bounds;
        static Vector3Int[] offsets = new[]
        {
            new Vector3Int(0, -1, 0),
            new Vector3Int(1, -1, 0),
            new Vector3Int(-1, -1, 0),
            new Vector3Int(0, -1, 1),
            new Vector3Int(0, -1, -1),
        };
#endif
        // Update is called once per frame
        protected override void Update()
        {
            base.Update();

            //if (!worldGenerationCRInProgress)
            //{
            //    worldGeneratingCoroutine = StartCoroutine(WorldUpdateCoroutine());
            //}

            // Randomly set some blocks
            //for(int ti = 0; ti < 1000; ti ++)
            //{
            //    SetBlock(new Vector3Int(
            //        UnityEngine.Random.Range(-worldSize, worldSize),
            //        UnityEngine.Random.Range(0, 32 * worldHeight),
            //        UnityEngine.Random.Range(-worldSize, worldSize)
            //    ), 0xffffffff);
            //}

#if BALL_MOVING_TEST
            if (f % 1 == 0)
            {
                int size = 15;
                if (_pos == null) { _pos = new Vector3(mspd * Time.time - 10, 80, -10); }
                (new UglySphere(Block.Empty)).Generate(new BoundsInt(Mathf.FloorToInt(_pos.x), Mathf.FloorToInt(_pos.y), Mathf.FloorToInt(_pos.z), size, size, size), this);
                _pos = new Vector3(Mathf.Sin(mspd * Time.time - 10) * 128.0f, 80, -Mathf.Cos(mspd * Time.time - 10) * 128.0f);
                (new UglySphere(Block.From32bitColor(0x00ff00ff))).Generate(new BoundsInt(Mathf.FloorToInt(_pos.x), Mathf.FloorToInt(_pos.y), Mathf.FloorToInt(_pos.z), size, size, size), this);
            }
            f++;
#endif
            
#if FALLING_SAND_TEST
            if (f % 1 == 0)
            {
                Vector3Int playerPos = Vector3Int.RoundToInt(player.position);
                for (int x = playerPos.x + bounds.min.x; x < playerPos.x + bounds.max.x; x++)
                {
                    for (int z = playerPos.z + bounds.min.z; z < playerPos.z + bounds.max.z; z++)
                    {
                        // for (int y = playerPos.y + bounds.min.y; y < playerPos.y + bounds.max.y; y++)
                        for (int y = 1; y < 128; y++)
                        {
                            Block b = GetBlock(x, y, z);
                            if (b.id == 0)
                            {
                                continue;
                            }
                            
                            for (int offi = 0; offi < offsets.Length; offi++)
                            {
                                Vector3Int offset = offsets[offi];
                                Block bo = GetBlock(x + offset.x, y + offset.y, z + offset.z);
                                if (bo.id == 0)
                                {
                                    SetBlock(new Vector3Int(x, y, z), bo);
                                    SetBlock(new Vector3Int(x + offset.x, y + offset.y, z + offset.z), b);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            f++;
#endif

            // SUPER HEAVY - FIXME: Optimize it orz
            //RefreshRenderables();
            if (debugText != null && showDebugText)
            {
                debugText.enabled = true;

                // Get renderable size
                uint vCount = 0;
                uint fsCount = 0;
                uint fsBufSize = 0;
                foreach (var r in renderables)
                {
                    vCount += r.GetVertCount();
                    if(r is ChunkRenderer_GPUGeometry_Raymarch)
                    {
                        fsCount += (uint)((r as ChunkRenderer_GPUGeometry_Raymarch).fsBufSize);
                        fsBufSize += (uint)(r as ChunkRenderer_GPUGeometry_Raymarch).totalMipSize / 1024;
                    }
                }

                uint bexCount = 0;
                foreach (var chk in chunks.Values)
                {
                    bexCount += (uint)chk.blockExtrasDict.Count;
                }

                Vector3Int pointerPos = Vector3Int.RoundToInt(follows.GetComponent<VoxelRayCast>().pointed.position);

                debugText.text = $"" +
                    $"CHUNKS:\n" +
                    $"Loaded:   {chunks.Count} ({chunks.Count * sizeof(uint) * 32 / 1024} MB)\n" +
                    $"   BEx:   {bexCount}\n" +
                    $"Rendered: {renderables.Count} ({(vCount / 1024.0f) * System.Runtime.InteropServices.Marshal.SizeOf(typeof(ChunkRenderer_GPUComputeMesh.Vertex)) / 1024.0f} MB)\n" +
                    $"          {vCount} verts\n" +
                    $"  - FS16: {fsCount} ({fsBufSize * 2 / 1024} MB)\n" +
                    $"\n" +
                    $"~@ {(int)follows.position.x}, {(int)follows.position.y}, {(int)follows.position.z}\n" +
                    $"-> {pointerPos.x}, {pointerPos.y}, {pointerPos.z}" +
                    $"\n" +
                    $"FPS: {EMAFPS(1.0f / Time.unscaledDeltaTime).ToString("N1")}\n" +
                    $"Render distance: {showDistance} blocks\n" +
                    $" (discards from: {disappearDistance} blocks)\n" +
                    $"\n" +
                    $"Jobs:\n" +
                    $"Total  Queued {CustomJobs.CustomJob.Count}\n" +
                    $"Unique Queued {CustomJobs.CustomJob.queuedUniqueJobs.Count}\n" +
                    $"Scheduled     {CustomJobs.CustomJob.scheduledJobs.Count}\n" +
                    $"\n" +
                    $"Loop stage:\n" +
                    $"{_worldUpdateStageStr[(int)currentWorldUpdateStage]}\n" +
                    $"{Matryoshka.Utils.NoisePools.OS2S_FBm_3oct_f1.instances.Count}\n" +
                    $"\n" +
                    $"[C] - Toggle freeview\n" +
                    $"[V] - Freeze current world\n" +
                    $"[L] - Reload current world\n" +
                    $"[F3]- Show / Hide Debug Text\n" +
                    $"     (Current = {(loadFreezed ? "FREEZE" : "LOAD")})";
            }
            else if(showDebugText == false)
            {
                debugText.enabled = false;
            }

            // Forced fence placement
            GPUDispatchManager.Singleton.CheckTasks();
            GPUDispatchManager.Singleton.SyncTasks();

            groundPlane.position = new Vector3(follows.position.x, waterHeight, follows.position.z);
            capPlane.position = new Vector3(follows.position.x, worldHeight * 32, follows.position.z);

            // Update player pointer on minimap
            if (playerPointer)
            {
                playerPointer.anchoredPosition = new Vector2(
                    Mathf.Clamp((mainCam.transform.position.x / (float)worldSize) * (minimapSize / 2.0f), -(minimapSize / 2.0f), (minimapSize / 2.0f)),
                    Mathf.Clamp((mainCam.transform.position.z / (float)worldSize) * (minimapSize / 2.0f), -(minimapSize / 2.0f), (minimapSize / 2.0f))
                );
            }

            if(Input.GetKeyDown(KeyCode.V))
            {
                loadFreezed = !loadFreezed;
            }

            if(Input.GetKeyDown(KeyCode.L))
            {
                Refresh();
            }

            if(Input.GetKeyDown(KeyCode.F3))
            {
                showDebugText = !showDebugText;
            }
        }

        float avgFPS = 0;
        float EMAFPS(float val)
        {
            float eps = 0.13f;
            avgFPS = eps * val + (1 - eps) * avgFPS;
            return avgFPS;
        }

        public override Chunk GetChunk(Vector3Int chunkCoord, bool create = false)
        {
            Chunk chk;
            if (chunks.TryGetValue(chunkCoord, out chk))
            {
                return chk;
            }

            if (create == true && InsideWorld(chunkCoord)) // <- diff with base; TODO: refactor
            {
                chk = CreateChunk(chunkCoord);

                return chk;
            }

            return null;
        }

        public override void StartWorldUpdateSingleLoop()
        {
            if(!loadFreezed)
            {
                base.StartWorldUpdateSingleLoop();
            }
        }

        // Difference with blockgroup version: viewCull implementation
        protected override void Render()
        {
            Plane[] planes = null;
            if (viewCull)
            {
                planes = GeometryUtility.CalculateFrustumPlanes(mainCam);

                // Modify the plane to a "taller" and "flatten" shape, to avoid shadow artifacts
                //planes[2].normal = Vector3.up;
                planes[2].distance += worldHeight * 32.0f; // push them further
                                                           //planes[3].normal = Vector3.down;
                planes[3].distance += worldHeight * 32.0f;
            }

            foreach (var r in renderables)
            {
                //if (r.populated && r.matProp != null && r.indBuffer != null && r.buffer != null && r.indBuffer.IsValid() && r.buffer.IsValid())
                if (r.IsReadyForPresent())
                {
                    if (viewCull)
                    {
                        if (!(GeometryUtility.TestPlanesAABB(planes, r.bound) ||
                                (r.bound.center - mainCam.transform.position).sqrMagnitude <= 16384.0f // 4 chunks
                            ))
                        {
                            continue;
                        }
                    }

                    r.Render(this);
                }
            }
        }

        bool InsideWorld(Vector3Int chunkCoord)
        {
            if (Mathf.Abs(chunkCoord.x) > worldSize / 32 || Mathf.Abs(chunkCoord.z) > worldSize / 32)
            {
                return false;
            }
            return true;
        }

        // Heavy
        // Difference with blockgroup version: Y
        protected override bool ShouldPrepareData(Vector3Int chunkCoord)
        {
#if PROFILE
        UnityEngine.Profiling.Profiler.BeginSample("ShouldPrepareData");
#endif
            if (!InsideWorld(chunkCoord))
            {
#if PROFILE
                UnityEngine.Profiling.Profiler.EndSample();
#endif
                return false;
            }

            Vector3 cp = chunkCoord * 32 + Vector3.one * 16.0f;

            bool res = (new Vector2(follows.position.x, follows.position.z) - new Vector2(cp.x, cp.z)).magnitude <= (showDistance) && cp.y <= (worldHeight * 32.0f);

#if PROFILE
        UnityEngine.Profiling.Profiler.EndSample();
#endif
            return res;
        }

        // Heavy
        protected override bool ShouldShow(Chunk chunk)
        {
#if PROFILE
        UnityEngine.Profiling.Profiler.BeginSample("ShouldShow");
#endif
            Vector3 cp = chunk.centerPos;

            bool res = (new Vector2(follows.position.x, follows.position.z) - new Vector2(cp.x, cp.z)).magnitude <= (showDistance) && cp.y <= (worldHeight * 32.0f);
#if PROFILE
        UnityEngine.Profiling.Profiler.EndSample();
#endif

            return res;
        }

        protected override bool ShouldDisappear(ChunkRenderableBase r)
        {
            return (new Vector2(follows.position.x, follows.position.z) - new Vector2(r.position.x, r.position.z)).magnitude > (disappearDistance) || r.position.y > (worldHeight * 32.0f);
        }

        // Y
        protected override IEnumerator BuildTasks()
        {
            Vector3Int currentChunk = new Vector3Int((int)(follows.position.x / 32), 0, (int)(follows.position.z / 32));
            int range = Mathf.CeilToInt(showDistance * 1.5f / 32.0f);

            // Heavy; no need to check every chunk each frame. 512 render distance = 9604 iterations
            // Build new chunks
            for (int cX = -range; cX <= range; cX++)
            {
                for (int cY = 0; cY < worldHeight; cY++)
                {
                    for (int cZ = -range; cZ <= range; cZ++)
                    {
                        Vector3Int dest = currentChunk + new Vector3Int(cX, cY, cZ);

                        // Profiler: ShouldPrepareData 51.33% of BuildTasks()
                        if (ShouldPrepareData(dest))
                        {
                            // Profiler: 21.16% of BuildTasks()
#if PROFILE
                        UnityEngine.Profiling.Profiler.BeginSample("chunks.TryGetValue / Generation");
#endif
                            Chunk chk;
                            if (!chunks.TryGetValue(dest, out chk))
                            {
                                chk = CreateChunk(dest);
                            }
#if PROFILE
                        UnityEngine.Profiling.Profiler.EndSample();
#endif

                            // Let the chunk populate itself if the chunk is not prepared
                            if (!chk.prepared && !chk.populating)
                            {
                                PopulateChunk(chk, dest * 32);
                            }
                        }
                    }

                    if ((Time.realtimeSinceStartup - startTime) > (budgetMS / 1000.0f))
                    {
                        yield return null;
                    }
                }
            }
        }

        protected override void PopulateChunk(Chunk chunk, Vector3Int chunkPos)
        {
            base.PopulateChunk(chunk, chunkPos);

            chunk._PopulateStart(chunkPos);
            if(chunkGenerator.Generate(chunk, this))
            {
                chunk._PopulateFinish();
            }
        }

        public void CreateStructure(BoundsInt bound, IStructureGenerator structureGen)
        {
            // Generate all chunks inside bound
            //Vector3Int min = bound.min / 32;
            //Vector3Int max = bound.max / 32;

            //for (int cX = min.x; cX <= max.x; cX++)
            //{
            //    for (int cY = min.y; cY <= max.y; cY++)
            //    {
            //        for (int cZ = min.z; cZ <= max.z; cZ++)
            //        {
            //            Vector3Int dest = new Vector3Int(cX, cY, cZ);
            //            if (InsideWorld(dest) && !chunks.ContainsKey(dest))
            //            {
            //                CreateChunk(dest);
            //            }
            //        }
            //    }
            //}

            //structureGen.Generate(bound, this);

            CustomJobs.CustomJob.TryAddJob(new WorldGen.GenericStructureGeneration(this, structureGen, bound));
        }

        protected override void AssemblyReloadEvents_afterAssemblyReload()
        {
            SetWorld();
        }

        private void OnGUI()
        {
            if(showDebugText)
            {
                // Test
                float _size = GUI.HorizontalSlider(new Rect(340, 10, 800, 30), (float)showDistance, 32.0f, 640.0f);

                showDistance = (int)_size;
                disappearDistance = (int)(_size + 40);
            }
        }

        public void Refresh()
        {
            if (CustomJobs.CustomJob.Count == 0)
            {
                foreach (var p in renderables)
                {
                    chunks.Remove(p.GetChunk().positionOffset / 32);
                    p.Clean();
                }
                renderables.Clear();

                // TODO: Do this more elegantly
                Globals.voxelisMain.Instance.Refresh();
                SetWorld();
                //UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            }
            else
            {
                Debug.LogError("Preview refresh failed - Unfinished job exists");
            }
        }
    }
}

