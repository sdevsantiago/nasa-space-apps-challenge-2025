using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
	public static void NewShelter(string newShelterScene)
	{
		SceneManager.LoadScene(newShelterScene);
	}

	public static void Quit()
	{
		Debug.Log("Quiting...");
#if UNITY_EDITOR
			UnityEditor.EditorApplication.isPlaying = false;
#else
		Application.Quit();
#endif
	}
}
