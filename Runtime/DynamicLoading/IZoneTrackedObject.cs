namespace jeanf.scenemanagement
{
    /// <summary>
    /// Marker for components whose GameObject gets a zone assigned at runtime (registered with
    /// <see cref="ObjectZoneTrackingBridge"/> directly or through an adapter). Carries no runtime
    /// behavior — the editor zone-validation tools scan open scenes for implementers to predict
    /// their play-mode assignment before entering play mode.
    /// </summary>
    public interface IZoneTrackedObject { }
}
