using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

using NumSharp;

namespace WorldGen.WorldSketch
{
    [CreateAssetMenu(fileName = "PythonSketcher", menuName = "Sketchers/PythonSketcher")]
    public class PythonSketcher : WorldSketcher
    {
        [Tooltip("List of all outputs from python file.\nmust match with corresponding names in ComputeShader.\npython output are expected to be generated under the same folder with the python file, named [paramName].npy.")]
        public List<string> resultList;

        public WorldGen.Utils.PythonIntegration pythonInstance;
        public string pythonFileName;

        static Texture NDArrayToTex(NDArray arr)
        {
            if(arr.shape[arr.shape.Length - 1] > 4)
            {
                np.expand_dims(arr, -1);
            }

            switch (arr.shape.Length)
            {
                case 1:
                    throw new IncorrectShapeException("Output array must have rank more than 1.");
                    break;
                case 2:
                    throw new NotImplementedException();
                    break;
                case 3:
                    // Create texture
                    var sketchMapTex = new Texture2D(arr.shape[0], arr.shape[1], TextureFormat.RGBAFloat, false);

                    for (int i = 0; i < arr.shape[0]; i++)
                    {
                        for (int j = 0; j < arr.shape[1]; j++)
                        {
                            sketchMapTex.SetPixel(i, j, new Color(
                                arr.shape[2] > 0 ? (float)(arr.GetDouble(i, j, 0)) : 0,
                                arr.shape[2] > 1 ? (float)(arr.GetDouble(i, j, 1)) : 0,
                                arr.shape[2] > 2 ? (float)(arr.GetDouble(i, j, 2)) : 0,
                                arr.shape[2] > 3 ? (float)(arr.GetDouble(i, j, 3)) : 0
                            ));
                        }
                    }

                    sketchMapTex.Apply();

                    return sketchMapTex;
                    break;
                case 4:
                    throw new NotImplementedException();
                    break;
                default:
                    throw new IncorrectShapeException("Output array must have rank less than 4.");
                    break;
            }
        }

        public override SketchResults FillHeightmap(
            int sizeX,
            int sizeY)
        {
            // Find the working directory and file
            string baseName = System.IO.Path.GetFileName(pythonFileName);
            string dircName = System.IO.Path.GetDirectoryName(pythonFileName);

            try
            {
                // Invoke python command
                var proc = pythonInstance.Invoke(
                    $"{baseName} --sizeX {sizeX} --sizeY {sizeY}",
                    prefix: $"cd {dircName} & ");
                //var proc = Utils.ExternalInvoker.Invoke("dir");

#if UNITY_EDITOR
                string output = proc.StandardOutput.ReadToEnd();
                UnityEngine.Debug.Log($"Exec result:\n<color=#cde6c7>{output}</color>");

                string error = proc.StandardError.ReadToEnd();
                //Encoding consoleCode = proc.StandardError.CurrentEncoding;
                //error = Encoding.UTF8.GetString(Encoding.Convert(Encoding.UTF8, consoleCode, consoleCode.GetBytes(error)));
                UnityEngine.Debug.Log($"Exec errors:\n<color=#f8a7a0>{error}</color>");
#endif
                proc.WaitForExit();
            }
            catch(Exception e)
            {
                Debug.Log(e);
            }

            // Collect Results
            SketchResults results = new SketchResults();

            foreach (var res in resultList)
            {
                var resArr = np.load(System.IO.Path.Combine(dircName, $"{res}.npy"));
                //Debug.Log(resArr.GetDouble(0, 0, 0));
                results.result.Add(res, NDArrayToTex(resArr));
            }

            return results;
        }
    }
}