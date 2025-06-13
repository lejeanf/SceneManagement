using jeanf.scenemanagement;
using UnityEngine;
using TMPro;

public class LoadingInformation : MonoBehaviour
{
    public delegate void LoadingStatusDelegate(string status);
    public static LoadingStatusDelegate LoadingStatus;
    
    [SerializeField]private TextMeshProUGUI tmp;
    private void OnEnable() => Subscribe();
    private void OnDisable() => Unsubscribe();
    private void OnDestroy() => Unsubscribe();
    
    private void Subscribe()
    {
        LoadingStatus += UpdateLoadingText;
        LoadPersistentSubScenes.PersistentLoadingComplete += ctx => UpdateLoadingText("");
    }
    
    private void Unsubscribe()
    {
        LoadingStatus -= UpdateLoadingText;
        LoadPersistentSubScenes.PersistentLoadingComplete -= ctx => UpdateLoadingText("");
    }

    private void UpdateLoadingText(string status)
    {
        tmp.text = status;
    }
}
