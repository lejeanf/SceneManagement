using jeanf.validationTools;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace jeanf.scenemanagement
{
    public enum ProxyColliderShape : byte
    {
        Box = 0,
        Sphere = 1,
        Capsule = 2,
    }

    /// <summary>
    /// One baked collider, expressed in the space of the <see cref="StaticColliderAuthoring"/>
    /// that collected it. <see cref="StaticColliderBridge"/> rebuilds it as a PhysX collider on a
    /// child of a proxy placed at the entity's LocalToWorld.
    /// </summary>
    public struct ProxyColliderElement : IBufferElementData
    {
        public ProxyColliderShape Shape;

        // Collider transform relative to the authoring transform. Applying this as a child of a
        // proxy placed at the authoring's world pose reproduces the collider's original world pose,
        // whatever its depth in the prefab.
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;

        /// <summary>The collider's own center offset (all shapes).</summary>
        public float3 Center;
        /// <summary>Box only.</summary>
        public float3 Size;
        /// <summary>Sphere and capsule.</summary>
        public float Radius;
        /// <summary>Capsule only.</summary>
        public float Height;
        /// <summary>Capsule only: 0 = X, 1 = Y, 2 = Z.</summary>
        public int Direction;

        public int Layer;
        public byte IsTrigger;
    }

    /// <summary>
    /// Drop this on a prop prefab and its colliders survive being baked into a SubScene: the shapes
    /// are baked to data and <see cref="StaticColliderBridge"/> respawns them as real PhysX colliders
    /// near the player, so the <c>CharacterController</c> is blocked by them.
    ///
    /// It exists because baking STRIPS <see cref="Collider"/> components — a chair authored in a
    /// SubScene renders through Entities Graphics but is not solid, and this project has no
    /// ECS physics package to replace it with.
    ///
    /// Put it anywhere in the prefab: offsets are resolved relative to THIS transform at bake time,
    /// so hierarchy depth and layout do not matter. In a classic (non-baked) additive scene the
    /// component is inert — the real colliders are already there and already work.
    /// </summary>
    [DisallowMultipleComponent]
    public class StaticColliderAuthoring : MonoBehaviour, IValidatable
    {
        [Tooltip("Also collect colliders on child GameObjects. Leave on unless the prop's children carry colliders that should NOT block the player.")]
        [SerializeField] private bool includeChildren = true;

        [Tooltip("Bake trigger colliders too. Off by default: triggers are not blockers, and baking them spends pool slots on volumes that stop nothing.")]
        [SerializeField] private bool includeTriggers = false;

        public bool IncludeChildren => includeChildren;
        public bool IncludeTriggers => includeTriggers;

        /// <summary>
        /// Valid when at least one bakeable (Box/Sphere/Capsule, non-trigger unless opted in) collider
        /// is in reach — without one the prop bakes to nothing and the player walks through it.
        /// Surfaced through the propertyDrawer validation framework (inspector banner, hierarchy
        /// highlight, play-mode console scan); the project-wide prefab sweep lives in
        /// Tools/SceneManagement/Validate Static Colliders.
        /// </summary>
        public bool IsValid
        {
            get
            {
                var colliders = includeChildren ? GetComponentsInChildren<Collider>(true) : GetComponents<Collider>();
                foreach (var collider in colliders)
                {
                    if (collider == null) continue;
                    if (collider.isTrigger && !includeTriggers) continue;
                    if (StaticColliderBake.TryDescribe(collider, out _)) return true;
                }
                return false;
            }
        }

        private class StaticColliderBaker : Baker<StaticColliderAuthoring>
        {
            public override void Bake(StaticColliderAuthoring authoring)
            {
                var colliders = authoring.IncludeChildren
                    ? GetComponentsInChildren<Collider>()
                    : GetComponents<Collider>();
                if (colliders == null || colliders.Length == 0)
                {
                    Debug.LogWarning($"{StaticColliderBake.LogPrefix} StaticColliderAuthoring on '{authoring.name}': no collider found" +
                        $"{(authoring.IncludeChildren ? "" : " on this GameObject (Include Children is OFF)")} — " +
                        "nothing will block the player once this prop is baked into a SubScene.", authoring);
                    return;
                }

                var entity = GetEntity(TransformUsageFlags.Dynamic);
                DynamicBuffer<ProxyColliderElement> buffer = default;
                var added = 0;
                var authoringTransform = authoring.transform;

                foreach (var collider in colliders)
                {
                    if (collider == null) continue;
                    if (collider.isTrigger && !authoring.IncludeTriggers) continue;

                    if (!StaticColliderBake.TryDescribe(collider, out var element))
                    {
                        Debug.LogWarning($"{StaticColliderBake.LogPrefix} StaticColliderAuthoring on '{authoring.name}': " +
                            $"'{collider.name}' is a {collider.GetType().Name}, which cannot be baked — only BoxCollider, " +
                            "SphereCollider and CapsuleCollider are supported. That collider will NOT block the player in a " +
                            "SubScene; replace it with primitives or keep this prop in a classic additive scene.", collider);
                        continue;
                    }

                    // Dependencies: re-bake when the collider or its placement changes.
                    DependsOn(collider);
                    DependsOn(collider.transform);

                    var local = math.mul(
                        (float4x4)authoringTransform.worldToLocalMatrix,
                        (float4x4)collider.transform.localToWorldMatrix);
                    StaticColliderBake.DecomposeTrs(local, out var position, out var rotation, out var scale);

                    if (StaticColliderBake.IsLossyPlacement(rotation, scale))
                    {
                        Debug.LogWarning($"{StaticColliderBake.LogPrefix} StaticColliderAuthoring on '{authoring.name}': " +
                            $"'{collider.name}' combines a rotation with non-uniform scale relative to the authoring transform. " +
                            "That cannot be reproduced exactly by a child transform, so the proxy will be slightly off. " +
                            "Un-rotate the collider, or make the scale uniform.", collider);
                    }

                    element.LocalPosition = position;
                    element.LocalRotation = rotation;
                    element.LocalScale = scale;
                    element.Layer = collider.gameObject.layer;
                    element.IsTrigger = (byte)(collider.isTrigger ? 1 : 0);

                    if (added == 0) buffer = AddBuffer<ProxyColliderElement>(entity);
                    buffer.Add(element);
                    added++;
                }

                if (added == 0)
                {
                    Debug.LogWarning($"{StaticColliderBake.LogPrefix} StaticColliderAuthoring on '{authoring.name}': " +
                        "found colliders but baked none of them (all were triggers or unsupported types) — " +
                        "nothing will block the player once this prop is baked into a SubScene.", authoring);
                }
            }
        }
    }

    /// <summary>
    /// Pure bake-time helpers, kept static and side-effect free so they can be unit tested without
    /// a SubScene, a bake, or a live world. The placement math here is the part most likely to
    /// regress: getting it wrong silently misplaces every proxy.
    /// </summary>
    public static class StaticColliderBake
    {
        public const string LogPrefix = "[SceneManagement]";

        /// <summary>
        /// Splits a transform matrix into translation, rotation and scale. Negative scale (a mirrored
        /// prop) is folded into the X axis so the rotation stays a proper (right-handed) rotation.
        /// </summary>
        public static void DecomposeTrs(float4x4 m, out float3 position, out quaternion rotation, out float3 scale)
        {
            position = m.c3.xyz;

            var axisX = m.c0.xyz;
            var axisY = m.c1.xyz;
            var axisZ = m.c2.xyz;

            var scaleX = math.length(axisX);
            var scaleY = math.length(axisY);
            var scaleZ = math.length(axisZ);

            // A negative determinant means the basis is mirrored; a quaternion cannot express that,
            // so the flip is attributed to X and the remaining basis stays right-handed.
            if (math.determinant(m) < 0f) scaleX = -scaleX;

            scale = new float3(scaleX, scaleY, scaleZ);

            axisX = math.abs(scaleX) > 1e-8f ? axisX / scaleX : new float3(1f, 0f, 0f);
            axisY = scaleY > 1e-8f ? axisY / scaleY : new float3(0f, 1f, 0f);
            axisZ = scaleZ > 1e-8f ? axisZ / scaleZ : new float3(0f, 0f, 1f);

            rotation = new quaternion(new float3x3(axisX, axisY, axisZ));
        }

        /// <summary>
        /// True when a rotation is combined with non-uniform scale — the one case a single child
        /// TRS cannot reproduce faithfully, because the scale would have to be applied in the
        /// parent's frame rather than the child's.
        /// </summary>
        public static bool IsLossyPlacement(quaternion rotation, float3 scale, float tolerance = 1e-3f)
        {
            var abs = math.abs(scale);
            var maxScale = math.cmax(abs);
            var minScale = math.cmin(abs);
            if (maxScale - minScale <= tolerance) return false; // uniform scale is always exact

            // math.abs(w) ~= 1 means "no rotation" (q and -q are the same rotation).
            return math.abs(math.abs(rotation.value.w) - 1f) > tolerance;
        }

        /// <summary>
        /// Fills in the shape-specific fields for a supported primitive collider. Returns false for
        /// anything else (MeshCollider, TerrainCollider, ...), which the caller reports.
        /// </summary>
        public static bool TryDescribe(Collider collider, out ProxyColliderElement element)
        {
            element = default;
            switch (collider)
            {
                case BoxCollider box:
                    element.Shape = ProxyColliderShape.Box;
                    element.Center = box.center;
                    element.Size = box.size;
                    return true;
                case SphereCollider sphere:
                    element.Shape = ProxyColliderShape.Sphere;
                    element.Center = sphere.center;
                    element.Radius = sphere.radius;
                    return true;
                case CapsuleCollider capsule:
                    element.Shape = ProxyColliderShape.Capsule;
                    element.Center = capsule.center;
                    element.Radius = capsule.radius;
                    element.Height = capsule.height;
                    element.Direction = capsule.direction;
                    return true;
                default:
                    return false;
            }
        }
    }
}
