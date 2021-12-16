using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WorldSelectDropdown : MonoBehaviour
{
    [SerializeField]
    UnityEngine.UI.Dropdown dropdown;

    [SerializeField]
    Voxelis.WorldGeneratorDef[] wDefs;
    Dictionary<string, Voxelis.WorldGeneratorDef> defMap = new Dictionary<string, Voxelis.WorldGeneratorDef>();

    private void Start()
    {
        int cnt = 1;
        foreach (var wd in wDefs)
        {
            string name = $"{cnt}. {wd.name}";
            cnt++;
            defMap.Add(name, wd);

            dropdown.AddOptions(new List<string> { name });
        }

        dropdown.onValueChanged.AddListener(delegate
        {
            Globals.defaultWDef = defMap[dropdown.options[dropdown.value].text];
        });
    }

    public void Go(string levelName)
    {
        UnityEngine.SceneManagement.SceneManager.LoadScene(levelName);
    }
}
