using System.Text;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public static class SceneReport
    {
        public static string Generate(ContentRegistry registry)
        {
            var csv = new StringBuilder();
            registry.Scenes.WriteReport(csv);
            return csv.ToString();
        }

        public static void WriteToFile(ContentRegistry registry, string path)
        {
            System.IO.File.WriteAllText(path, Generate(registry));
        }
    }
}
