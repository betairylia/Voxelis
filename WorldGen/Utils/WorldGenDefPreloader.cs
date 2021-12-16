using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WorldGenDefPreloader : MonoBehaviour
{
    public Voxelis.World world;

    private void Awake()
    {
        if(Globals.defaultWDef != null)
        {
            world.worldGenDef = Globals.defaultWDef;
        }
    }
}
