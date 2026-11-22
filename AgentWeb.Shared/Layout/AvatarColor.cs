namespace AgentWeb.Shared.Layout;

// Un colore per persona negli avatar a iniziali: tonalità (0-359) per la variabile CSS --hue.
// Hash fisso (FNV-1a) e non string.GetHashCode, che cambia a ogni processo: prerender sul server e WebAssembly devono dare lo stesso colore.
public static class AvatarColor
{
    public static int Hue(string? name)
    {
        uint h = 2166136261;
        foreach (var c in (name ?? "").ToLowerInvariant()) h = (h ^ c) * 16777619;
        return (int)(h % 360);
    }

    public static string Style(string? name, string? size = null) =>
        size is null ? $"--hue: {Hue(name)}" : $"--size: {size}; --hue: {Hue(name)}";
}
