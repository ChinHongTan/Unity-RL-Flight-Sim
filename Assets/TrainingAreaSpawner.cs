using UnityEngine;

public class TrainingAreaSpawner : MonoBehaviour
{
    public GameObject areaPrefab;
    public int rows = 4, cols = 4;
    public float spacing = 300f;

    void Awake()
    {
        // refuse to run if the prefab we'd spawn contains a spawner
        if (areaPrefab.GetComponentInChildren<TrainingAreaSpawner>(true) != null)
        {
            Debug.LogError("Area prefab contains a TrainingAreaSpawner — remove it from the prefab. Aborting spawn.");
            return;
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                Instantiate(areaPrefab, new Vector3(c * spacing, 0, r * spacing), Quaternion.identity, transform);
            }
        }
    }
}