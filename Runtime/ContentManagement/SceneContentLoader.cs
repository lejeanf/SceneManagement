using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace jeanf.ContentManagement
{
    public class SceneContentLoader : ContentLoader<SceneContentSo>
    {
        private readonly List<SceneContentSo> _scenes = new();

        public SceneContentLoader(params string[] tags) : base(tags) { }

        protected override void HandleLoadResource(SceneContentSo resource)
        {
            if (!resource.IsActive) return;
            _scenes.Add(resource);
        }

        public IReadOnlyList<SceneContentSo> GetAll() => _scenes;

        public SceneContentSo Find(string sceneName)
            => _scenes.Find(s => s.Scene.Name == sceneName);

        public void WriteReport(StringBuilder csv)
        {
            csv.AppendLine("SceneName,ContentId,IsActive,Comments");
            foreach (var info in _scenes)
            {
                csv.AppendLine($"{info.Scene.Name},{info.ContentId},{info.IsActive},{info.Comments}");
            }
        }

        public override void Dispose()
        {
            if (!Loaded) return;
            _scenes.Clear();
            base.Dispose();
        }
    }
}
