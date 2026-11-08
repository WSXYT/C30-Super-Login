using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
namespace C30SuperLogin.Desktop;
internal static class Resources
{
    public static void SetResourceReference<T>(this StyledElement target, StyledProperty<T> property, string key) => target.Bind(property, new DynamicResourceExtension(key));
}
