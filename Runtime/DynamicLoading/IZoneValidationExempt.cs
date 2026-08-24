namespace jeanf.scenemanagement
{
    /// <summary>
    /// Opt-out companion to <see cref="IZoneTrackedObject"/>: implement on components whose
    /// GameObject does receive a runtime zone assignment but whose function does not depend on
    /// where they sit (global managers in the persistent scene — scenario lists, settings pages).
    /// The editor zone-validation tools skip their GameObject entirely; runtime is unchanged.
    /// </summary>
    public interface IZoneValidationExempt { }
}
