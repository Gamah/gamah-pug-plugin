namespace Gpp;

// The parts that are not an engine call: what a bare ".smokes" means, and the line the thrower gets back.
public static class Smokes
{
    // ".smokes", ".smokes on", ".smokes off" - and a bare one is a toggle, because that is what people
    // type the second time.
    public static bool SmokesWanted(string arg, bool alreadyOn)
    {
        return arg.Trim().ToLowerInvariant() switch
        {
            "on" or "1" or "yes" => true,
            "off" or "0" or "no" => false,
            _ => !alreadyOn,
        };
    }

    // One line, their eyes only. Position to a tenth of a unit and angles to a hundredth of a degree: the
    // angles are what a lineup is actually made of, and a hundredth is finer than a mouse can hold.
    public static string SmokeThrowLine(float x, float y, float z, float pitch, float yaw) =>
        $"Threw from {x:0.0} {y:0.0} {z:0.0}  looking {pitch:0.00} {yaw:0.00}";

    public static string SmokePoppedLine(float x, float y, float z) =>
        $"Popped at {x:0.0} {y:0.0} {z:0.0}";
}
