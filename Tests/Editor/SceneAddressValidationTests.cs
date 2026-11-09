using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace jeanf.scenemanagement.tests
{
    /// <summary>
    /// The address check that stands between a scenario and Addressables.LoadSceneAsync. The
    /// input to lock is the EMPTY address: that is what a SceneReference serializes once its
    /// scene has no Addressables entry, and before this check it was dropped without an error.
    /// </summary>
    public class SceneAddressValidationTests
    {
        private readonly List<Object> _created = new();

        private Scenario MakeScenario(string sceneAddress, params string[] dependencyAddresses)
        {
            var scenario = ScriptableObject.CreateInstance<Scenario>();
            _created.Add(scenario);
            scenario.scenarioName = "Test scenario";
            scenario.id = "TEST_01";
            scenario.scene = sceneAddress == null ? null : new SceneReference(sceneAddress);
            scenario.dependenciesInThisScenario = new List<SceneReference>();
            foreach (var address in dependencyAddresses)
                scenario.dependenciesInThisScenario.Add(address == null ? null : new SceneReference(address));
            return scenario;
        }

        [TearDown]
        public void DestroyCreated()
        {
            foreach (var o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void IsValidAddress_RejectsEmpty(string address)
        {
            Assert.IsFalse(SceneAddressValidation.IsValidAddress(address));
        }

        [Test]
        public void IsValidAddress_AcceptsPath()
        {
            Assert.IsTrue(SceneAddressValidation.IsValidAddress("Assets/Scenes/Clinic/GMF_SeguinGermaine01.unity"));
        }

        [Test]
        public void MissingAddresses_NoneWhenEverythingIsAddressed()
        {
            var scenario = MakeScenario("Assets/Scenes/Room.unity", "Assets/Scenes/Dep0.unity", "Assets/Scenes/Dep1.unity");
            Assert.IsEmpty(SceneAddressValidation.MissingAddresses(scenario));
        }

        [Test]
        public void MissingAddresses_EmptyScenarioScene_IsNamed()
        {
            var scenario = MakeScenario("", "Assets/Scenes/Dep0.unity");
            CollectionAssert.AreEqual(new[] { "scene" }, SceneAddressValidation.MissingAddresses(scenario));
        }

        [Test]
        public void MissingAddresses_EmptyDependency_IsNamedByIndex()
        {
            var scenario = MakeScenario("Assets/Scenes/Room.unity", "Assets/Scenes/Dep0.unity", "", "Assets/Scenes/Dep2.unity");
            CollectionAssert.AreEqual(new[] { "dependenciesInThisScenario[1]" }, SceneAddressValidation.MissingAddresses(scenario));
        }

        [Test]
        public void MissingAddresses_NullSceneReference_IsNamed()
        {
            var scenario = MakeScenario(null);
            CollectionAssert.AreEqual(new[] { "scene" }, SceneAddressValidation.MissingAddresses(scenario));
        }

        [Test]
        public void MissingAddresses_NullDependencyList_IsNotAProblem()
        {
            var scenario = MakeScenario("Assets/Scenes/Room.unity");
            scenario.dependenciesInThisScenario = null;
            Assert.IsEmpty(SceneAddressValidation.MissingAddresses(scenario));
        }

        [Test]
        public void MissingAddresses_NullScenario()
        {
            CollectionAssert.AreEqual(new[] { "scenario" }, SceneAddressValidation.MissingAddresses(null));
        }

        [Test]
        public void DescribeMissing_NamesTheScenarioTheFieldAndTheFix()
        {
            var scenario = MakeScenario("");
            var text = SceneAddressValidation.DescribeMissing(scenario, SceneAddressValidation.MissingAddresses(scenario));
            StringAssert.Contains("Test scenario", text);
            StringAssert.Contains("TEST_01", text);
            StringAssert.Contains("scene", text);
            StringAssert.Contains(SceneAddressValidation.MissingAddressHint, text);
        }
    }
}
