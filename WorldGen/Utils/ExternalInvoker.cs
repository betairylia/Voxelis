using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using UnityEngine;

namespace WorldGen.Utils
{
    public class ExternalInvoker
    {
        public static System.Diagnostics.Process Invoke(string command)
        {
            System.Diagnostics.Process process = new System.Diagnostics.Process();
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.StandardOutputEncoding = Encoding.GetEncoding(936); // Simplified Chinese
            startInfo.RedirectStandardError = true;
            startInfo.StandardErrorEncoding = Encoding.GetEncoding(936); // Simplified Chinese
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/C {command}";
            process.StartInfo = startInfo;
            process.Start();

#if UNITY_EDITOR
            UnityEngine.Debug.Log($"Exec: <b><color=#afdfe4>cmd.exe /C {command}</color></b>");
#endif

            return process;
        }
    }
}
