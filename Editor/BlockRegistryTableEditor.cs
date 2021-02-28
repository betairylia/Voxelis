using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.IO;

namespace Voxelis.Data
{
    [CustomEditor(typeof(BlockRegistryTable))]
    public class BlockRegistryTableEditor : Editor
    {
        private SerializedObject m_tableObj;
        private BlockRegistryTable table;

        private void OnEnable()
        {
            m_tableObj = serializedObject;
            table = (BlockRegistryTable)m_tableObj.targetObject;
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.Space();

            if(GUILayout.Button("Collect all blocks"))
            {
                CollectBlockDefinitions();
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Bake textures"))
            {
                BakeTextures();
            }
            
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Block Definitions", $"{table.NonNullDefCount} Blocks");

            EditorGUILayout.PropertyField(m_tableObj.FindProperty("BlockLUT"));
            EditorGUILayout.PropertyField(m_tableObj.FindProperty("BlockTexArray"));

            EditorGUILayout.LabelField($"Block Texture Array has {table.pageCount} page(s)");
        }

        private void BakeTextures()
        {
            if(Block.BlockIDCount > 65536)
            {
                Debug.LogError("Current version doesn't support #IDs more than 65536");
            }

            table.BlockLUT = new Texture2D(256, 256, TextureFormat.RGBAFloat, false);
            table.BlockLUT.wrapMode = TextureWrapMode.Clamp;
            table.BlockLUT.filterMode = FilterMode.Point;

            // Check how long the array is needed
            int len = table.blockDefinitions.Length;

            Texture2D first = null;
            uint pxs = 0; // capable for 256 pages
            int texLen = 0; // size of textures

            foreach (var def in table.blockDefinitions)
            {
                if(def == null) { continue; }
                if(def.texture != null)
                {
                    pxs += (uint)(def.texture.width * def.texture.height);

                    // Assign reference for format
                    if(first == null)
                    {
                        first = def.texture;
                        texLen = def.texture.width;

                        if(def.texture.width != def.texture.height)
                        {
                            Debug.LogError($"Texture of Block ID {def.ID.id} must be square");
                            return;
                        }

                        if((texLen & (texLen - 1)) != 0)
                        {
                            Debug.LogError($"Texture of Block ID {def.ID.id} must be power of two");
                            return;
                        }

                        if(def.texture.format != TextureFormat.RGBA32)
                        {
                            Debug.LogError($"Texture of Block ID {def.ID.id} must be format RGBA32");
                            return;
                        }
                    }

                    // Check if format are the same
                    else
                    {
                        if(def.texture.width != texLen || def.texture.height != texLen)
                        {
                            Debug.LogError($"Texture of Block ID {def.ID.id} must have size {texLen} x {texLen}");
                            return;
                        }
                        if (def.texture.mipmapCount != first.mipmapCount)
                        {
                            Debug.LogError($"Texture of Block ID {def.ID.id} should have {first.mipmapCount} mipmaps");
                            return;
                        }

                        if(def.texture.format != first.format)
                        {
                            Debug.LogError($"Texture of Block ID {def.ID.id} should be {first.format} format");
                            return;
                        }
                    }
                }
            }

            // TODO: create empty textures
            if(pxs == 0) { Debug.LogError("Nothing to be baked"); return; }

            // Pack textures
            // TODO: write seperated pipeline for non-uniform textures. I GIVE UP
            // TODO: change pages, calculate sizes, etc ... (HOW ???)
            //       multiple textures for a block
            // TODO: adapt code from TextureRefrence.cs - need to discuss ...

            #region Useless code

            //List<BlockDefinition> defs = new List<BlockDefinition>();
            //List<Texture2D> src = new List<Texture2D>();

            //for(int i = 0; i < len; i++)
            //{
            //    var def = table.blockDefinitions[i];
            //    if (def == null) { continue; }

            //    if (def.texture != null)
            //    {
            //        defs.Add(def);
            //        src.Add(def.texture);
            //    }
            //}

            //Texture2D atlas = new Texture2D(2048, 2048);
            //var rects = atlas.PackTextures(src.ToArray(), 0, 2048);

            // TODO: WTF (Support multiple pages pls)
            // Don't know how to support multiple pages ... just do this now ;w;
            //table.BlockTexArray = new Texture2DArray(2048, 2048, 1, TextureFormat.RGBA32, true);

            // 1 page only
            //Texture2D tmp = new Texture2D(2048, 2048);
            //tmp.SetPixels(0, 0, atlas.width, atlas.height, atlas.GetPixels());
            //tmp.Apply();
            //Graphics.CopyTexture(tmp, 0, table.BlockTexArray, 0);
            //table.BlockTexArray.SetPixels(atlas.GetPixels(), 0);

            //for (int i = 0; i < defs.Count; i++)
            //{
            //    var r = rects[i];
            //    defs[i].uvw = new Vector3(r.min.x, r.min.y, 0);
            //    defs[i].uvSize = new Vector2(r.width, r.height);
            //}

            #endregion

            int texPerSide = (2048 / (int)texLen);
            int texPerPage = texPerSide * texPerSide;
            int pages = (int)((pxs - 1) / 4194304) + 1; // ceiling

            int pageTexIdx = 0;
            int pageIdx = 0;

            bool _flag = false;

            // Ensure mipmap will not go further than every single texture
            int mipCount = 0, _t = texLen;
            while ((_t >>= 1) > 0) { ++mipCount; }

            Texture2D atlas = new Texture2D(2048, 2048, TextureFormat.RGBA32, mipCount, true);
            table.BlockTexArray = new Texture2DArray(2048, 2048, pages, TextureFormat.RGBA32, mipCount, true);
            table.BlockTexArray.filterMode = FilterMode.Point;

            foreach (var def in table.blockDefinitions)
            {
                if(def == null) { continue; }
                if(def.texture == null) { continue; }

                _flag = false;

                Vector2Int origin = new Vector2Int(pageTexIdx / texPerSide, pageTexIdx % texPerSide);
                
                atlas.SetPixels(origin.x * texLen, origin.y * texLen, texLen, texLen, def.texture.GetPixels());

                def.uvw = new Vector3(origin.x / (float)texPerSide, origin.y / (float)texPerSide, pageIdx / (float)pages);
                def.uvSize = new Vector2(1.0f / texPerSide, 1.0f / texPerSide);

                pageTexIdx += 1;

                if(pageTexIdx == texPerPage)
                {
                    atlas.Apply();
                    Graphics.CopyTexture(atlas, 0, table.BlockTexArray, pageIdx);
                    pageTexIdx = 0;
                    pageIdx += 1;

                    _flag = true;
                }
            }

            if(_flag == false)
            {
                atlas.Apply();
                Graphics.CopyTexture(atlas, 0, table.BlockTexArray, pageIdx);
            }

            // Save asset
            //string path = EditorUtility.SaveFilePanelInProject(
            //    "Save Texture Array", "BlockAtlases", "asset", "Save Block Atlas Array"
            //);
            string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(table));
            AssetDatabase.CreateAsset(table.BlockTexArray, Path.Combine(dir, "BlockAtlases.asset"));

            table.pageCount = pages;

            // Create LUT
            for (int i = 0; i < len; i++)
            {
                var def = table.blockDefinitions[i];
                if(def != null)
                {
                    // R: u - G: v - B: w (which slice of array) - A: flags / BitConverter.GetBytes
                    table.BlockLUT.SetPixel(i / 256, i % 256, def.ToColorPixel());
                }
                else
                {
                    table.BlockLUT.SetPixel(i / 256, i % 256, Color.clear);
                }
            }

            table.BlockLUT.Apply();

            // Save asset
            //path = EditorUtility.SaveFilePanelInProject(
            //    "Save Float32 Texture", "BlockLUT", "asset", "Save Block LookUp Texture"
            //);
            AssetDatabase.CreateAsset(table.BlockLUT, Path.Combine(dir, "BlockLUT.asset"));
        }

        private void CollectBlockDefinitions()
        {
            // https://answers.unity.com/questions/1425758/how-can-i-find-all-instances-of-a-scriptable-objec.html
            string[] guids = AssetDatabase.FindAssets("t:" + typeof(BlockDefinition).Name);

            table.blockDefinitions = new BlockDefinition[Block.BlockIDCount];
            table.NonNullDefCount = 0;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                BlockDefinition def = AssetDatabase.LoadAssetAtPath<BlockDefinition>(path);

                if (table.blockDefinitions[def.ID.id] != null)
                {
                    Debug.LogError($"Block ID Duplication for {def.ID.id} at {path}");
                    table.NonNullDefCount = 0;
                    table.blockDefinitions = new BlockDefinition[Block.BlockIDCount];
                    return;
                }

                table.blockDefinitions[def.ID.id] = def;
                table.NonNullDefCount += 1;
            }
        }
    }
}
