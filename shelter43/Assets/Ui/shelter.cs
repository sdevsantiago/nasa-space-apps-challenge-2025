using UnityEngine;
using UnityEngine.UIElements;

public class shelter : MonoBehaviour
{
    // Store applied values
    private string appliedMissionType = "";
    private string appliedDuration = "";
    private int appliedCrewSize = 2;

    void Start()
    {
        var uiDocument = GetComponent<UIDocument>();
        if (uiDocument == null)
        {
            Debug.LogError("UIDocument component not found on the GameObject.");
            return;
        }
        var uiRoot = uiDocument.rootVisualElement;

        Button btnMissionDesigner = uiRoot.Q<Button>("btnMissionDesigner");
        VisualElement popup = uiRoot.Q<VisualElement>("popup");
        Button popupClose = uiRoot.Q<Button>("popupClose");
        Button popupApply = uiRoot.Q<Button>("popupApply");
        IntegerField crewSizeField = uiRoot.Q<IntegerField>("crewSize");
        DropdownField missionTypeField = uiRoot.Q<DropdownField>("missionType");
        DropdownField durationField = uiRoot.Q<DropdownField>("duration");

        void ShowPopup(string title)
        {
            popup.style.display = DisplayStyle.Flex;
            var popupLabel = popup.Q<Label>("popup-title");
            if (popupLabel == null)
            {
                Debug.LogError("Label 'popup-title' not found inside the popup.");
                return;
            }
            popupLabel.text = $"Fill in the data for: {title}";
            if (crewSizeField != null && crewSizeField.value < 2)
                crewSizeField.SetValueWithoutNotify(2);
        }
        void HidePopup()
        {
            popup.style.display = DisplayStyle.None;
        }

        btnMissionDesigner.clicked += () => ShowPopup("Mission Designer");
        popupClose.clicked += HidePopup;
        HidePopup();

        // Prevent crew size from being less than 2 and only allow increments/decrements of 1
        if (crewSizeField != null)
        {
            crewSizeField.value = 2; // Default to 2
            int lastValue = crewSizeField.value;
            crewSizeField.RegisterValueChangedCallback(evt =>
            {
                int newValue = evt.newValue;
                // Clamp to minimum 2
                if (newValue < 2)
                {
                    crewSizeField.SetValueWithoutNotify(2);
                    lastValue = 2;
                    return;
                }
                // Only allow increments/decrements of 1
                if (Mathf.Abs(newValue - lastValue) > 1)
                {
                    crewSizeField.SetValueWithoutNotify((int)(lastValue + Mathf.Sign(newValue - lastValue)));
                    lastValue = crewSizeField.value;
                }
                else
                {
                    lastValue = newValue;
                }
            });
        }

        // Apply button logic: store values only when Apply is pressed
        if (popupApply != null)
        {
            popupApply.clicked += () =>
            {
                appliedMissionType = missionTypeField != null ? missionTypeField.value : "";
                appliedDuration = durationField != null ? durationField.value : "";
                appliedCrewSize = crewSizeField != null ? crewSizeField.value : 2;
                Debug.Log($"Applied: MissionType={appliedMissionType}, Duration={appliedDuration}, CrewSize={appliedCrewSize}");
                HidePopup();
            };
        }
    }
}
