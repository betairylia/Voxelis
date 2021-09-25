using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using UnityEngine;

namespace WorldGen.Utils
{
    [Serializable]
    public class PythonIntegration
    {
        [TextArea]
        public string pythonPrefix = "python";
        [TextArea]
        public string pythonPostfix = "";

        public System.Diagnostics.Process Invoke(string pyCommand, string prefix = "")
        {
            return ExternalInvoker.Invoke($"{prefix}{pythonPrefix} {pyCommand}{pythonPostfix}");
        }
    }
}
