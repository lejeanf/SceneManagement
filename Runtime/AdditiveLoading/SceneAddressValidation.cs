using System.Collections.Generic;

namespace jeanf.scenemanagement
{
    /// <summary>
    /// Pure checks on scene addresses, shared by SceneLoader (one address) and ScenarioManager
    /// (every address a scenario needs). Scenes load through Addressables.LoadSceneAsync(address),
    /// and a SceneReference serializes an EMPTY address whenever its scene has no Addressables
    /// entry at the moment the referencing asset is saved. That one input has to be named
    /// precisely: before these checks it was dropped silently and the scenario came up "active"
    /// with no scene and no error.
    /// </summary>
    public static class SceneAddressValidation
    {
        public const string MissingAddressHint =
            "the scene has no Addressables entry. Add the scene to an Addressables group, then select the asset that references it (so the SceneReference rewrites its address) and save";

        public static bool IsValidAddress(string address) => !string.IsNullOrWhiteSpace(address);

        /// <summary>
        /// Labels of every scene reference of the scenario whose address is missing: "scene" for
        /// the scenario scene, "dependenciesInThisScenario[i]" for a dependency. Empty when the
        /// scenario can be handed to the loader.
        /// </summary>
        public static List<string> MissingAddresses(Scenario scenario)
        {
            var missing = new List<string>();
            if (scenario == null)
            {
                missing.Add("scenario");
                return missing;
            }

            if (scenario.scene == null || !IsValidAddress(scenario.scene.Address))
                missing.Add("scene");

            var dependencies = scenario.dependenciesInThisScenario;
            if (dependencies == null) return missing;
            for (var i = 0; i < dependencies.Count; i++)
            {
                if (dependencies[i] == null || !IsValidAddress(dependencies[i].Address))
                    missing.Add($"dependenciesInThisScenario[{i}]");
            }
            return missing;
        }

        /// <summary>The one error text for a scenario refused over missing addresses: what, where, and the fix.</summary>
        public static string DescribeMissing(Scenario scenario, IReadOnlyList<string> missing)
        {
            var name = scenario == null ? "<null>"
                : string.IsNullOrEmpty(scenario.scenarioName) ? scenario.name : scenario.scenarioName;
            var id = scenario == null || scenario.id == null ? "?" : scenario.id.ToString();
            return $"Scenario '{name}' (id {id}) cannot be loaded: no scene address on {string.Join(", ", missing)} — {MissingAddressHint}.";
        }
    }
}
